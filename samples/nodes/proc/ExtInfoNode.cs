using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 拡張パッケージが何を宣言し、拡張が同梱したライブラリのうち実際にどれが載ったかを返す。
///
/// 拡張が配ったライブラリは、ホストが同名のものを先に読み込んでいれば負ける。
///    このとき例外は出ず「型が見つからない」等の形でしか現れないため、
///    載っている版と、その出どころを見る手段が無いと診断できない。
/// </summary>
[NodeType("ngol.proc.ext_info", "Proc", "Extension Info",
    Version = "1.0.2",
    Description = "Report installed extensions with their declared capabilities, and for each library an extension ships, whether that copy is the one actually loaded. Version conflicts are silent at runtime, so they are listed explicitly.")]
[NodePort("filter", PortDirection.Input, "string", Description = "Case-insensitive substring on the assembly name. Empty = every library shipped by an extension. It narrows the shipped libraries, the conflicts and the duplicates - the listing that include_all_loaded adds is never narrowed by it")]
[NodePort("include_all_loaded", PortDirection.Input, "boolean", Description = "Also list every assembly loaded in the process (default false). The list is not narrowed by filter, so on a host with many assemblies it is long")]
[NodePort("ngol_root", PortDirection.Output, "string", Description = "Directory the extension layout was resolved from")]
[NodePort("extensions", PortDirection.Output, "string", Description = "Installed extensions: id, version, declared capabilities and platforms")]
[NodePort("libraries", PortDirection.Output, "string", Description = "Shipped library -> shipped version / loaded version / where the loaded copy came from")]
[NodePort("conflicts", PortDirection.Output, "string", Description = "Libraries where a DIFFERENT VERSION is loaded than the one the extension ships. This is the case that breaks silently")]
[NodePort("conflict_count", PortDirection.Output, "number", Description = "Assemblies loaded in more than one version. Non-zero means a shipped library is not the copy actually in use")]
[NodePort("duplicates", PortDirection.Output, "string", Description = "Same version, loaded from another path. Normal for layouts that place a copy outside the extension directory")]
[NodePort("duplicate_count", PortDirection.Output, "number", Description = "Assemblies loaded more than once at the same version. Harmless in itself, listed because it is easy to mistake for a conflict")]
[NodePort("summary", PortDirection.Output, "string", Description = "Counts on one line: extensions, shipped libraries checked, conflicts and duplicates")]
public sealed class ExtInfoNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var filter = (ctx.GetPortValue("filter") as string ?? "").Trim();
        var includeAll = ctx.GetPortValue("include_all_loaded") is bool b && b;

        var root = ResolveNgolRoot();
        ctx.SetPortValue("ngol_root", root ?? "");

        // 同じ単純名が複数の場所から載ることがあるため、名前 -> 複数件で持つ。
        var loaded = new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = SafeName(asm);
            if (name.Length == 0) continue;
            if (!loaded.TryGetValue(name, out var list)) loaded[name] = list = new List<Assembly>();
            list.Add(asm);
        }

        var extText = new StringBuilder();
        var libText = new StringBuilder();
        var conflictText = new StringBuilder();
        var duplicateText = new StringBuilder();
        int extCount = 0, libCount = 0, conflictCount = 0, duplicateCount = 0;

        var extensionsDir = root == null ? null : Path.Combine(root, "Extensions");
        if (extensionsDir == null || !Directory.Exists(extensionsDir))
        {
            extText.AppendLine("no Extensions directory found");
        }
        else
        {
            foreach (var dir in Directory.EnumerateDirectories(extensionsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                extCount++;
                var manifest = ReadManifest(Path.Combine(dir, "extension.json"));
                extText.AppendLine($"{manifest.Id ?? Path.GetFileName(dir)}  v{manifest.Version ?? "?"}");
                extText.AppendLine($"  capabilities: {(manifest.Capabilities.Count > 0 ? string.Join(", ", manifest.Capabilities) : "(none declared)")}");
                extText.AppendLine($"  platforms   : {(manifest.Platforms.Count > 0 ? string.Join(", ", manifest.Platforms) : "(any)")}");
                extText.AppendLine($"  directory   : {dir}");

                foreach (var file in ShippedAssemblies(dir))
                {
                    var simple = Path.GetFileNameWithoutExtension(file);
                    if (filter.Length > 0 && simple.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    libCount++;
                    var shippedVersion = ReadAssemblyVersion(file);
                    if (shippedVersion == null)
                    {
                        // マネージドの版が読めないもの（ネイティブモジュール等）は比較対象にならない。
                        libText.AppendLine($"{simple,-32} shipped=(native or unreadable)  {file}");
                        continue;
                    }

                    if (!loaded.TryGetValue(simple, out var hits))
                    {
                        libText.AppendLine($"{simple,-32} shipped={shippedVersion}  loaded=(not loaded)");
                        continue;
                    }

                    foreach (var asm in hits)
                    {
                        var loc = SafeLocation(asm);
                        var loadedVersion = asm.GetName().Version?.ToString();
                        var samePath = loc.Length > 0 && string.Equals(Path.GetFullPath(loc), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
                        var sameVersion = string.Equals(shippedVersion, loadedVersion, StringComparison.Ordinal);

                        // 「別の場所から載っている」だけでは異常ではない。同じ版の複製が別の場所に
                        //    置かれる配置はありうる。挙動が変わるのは版が違うときなので、そこだけを
                        //    conflict として数える。両方を同じ扱いにすると、正常な配置でも常に鳴る。
                        var mark = samePath ? "OK  " : (sameVersion ? "COPY" : "DIFF");
                        // 版とパスが別々の出どころを指す行になるため、shipped 側も「どの拡張のものか」を書く。
                        //    片方だけだと、同じ名前の別ファイルの話をしていることが行から読み取れない。
                        var shipper = Path.GetFileName(dir);
                        libText.AppendLine($"{simple,-32} shipped={shippedVersion} (in {shipper})  loaded={loadedVersion}  [{mark}] {(loc.Length > 0 ? loc : "(no location)")}");

                        if (samePath) continue;

                        if (sameVersion)
                        {
                            duplicateCount++;
                            duplicateText.AppendLine($"{simple}  {loadedVersion}");
                            duplicateText.AppendLine($"  shipped here : {file}");
                            duplicateText.AppendLine($"  loaded from  : {(loc.Length > 0 ? loc : "(no location)")}");
                        }
                        else
                        {
                            conflictCount++;
                            conflictText.AppendLine($"{simple}");
                            conflictText.AppendLine($"  extension ships : {shippedVersion}  {file}");
                            conflictText.AppendLine($"  actually loaded : {loadedVersion}  {(loc.Length > 0 ? loc : "(no location)")}");
                        }
                    }
                }
            }
        }

        // 拡張が配っていなくても、同名が複数の場所から載っていれば同じ症状を起こす。
        // ここでも版が割れているものだけを conflict として数える。
        foreach (var pair in loaded.Where(p => p.Value.Count > 1))
        {
            if (filter.Length > 0 && pair.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var versions = pair.Value.Select(a => a.GetName().Version?.ToString() ?? "").Distinct().ToList();
            var target = versions.Count > 1 ? conflictText : duplicateText;
            if (versions.Count > 1) conflictCount++; else duplicateCount++;

            target.AppendLine($"{pair.Key}  (loaded {pair.Value.Count} times)");
            foreach (var asm in pair.Value)
                target.AppendLine($"  {asm.GetName().Version}  {SafeLocation(asm)}");
        }

        if (includeAll)
        {
            libText.AppendLine();
            libText.AppendLine($"--- all loaded assemblies ({loaded.Values.Sum(v => v.Count)}) ---");
            foreach (var pair in loaded.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var asm in pair.Value)
                    libText.AppendLine($"{pair.Key,-40} {asm.GetName().Version,-16} {SafeLocation(asm)}");
        }

        ctx.SetPortValue("extensions", extText.ToString());
        ctx.SetPortValue("libraries", libText.Length > 0 ? libText.ToString() : "(no shipped library matched)");
        ctx.SetPortValue("conflicts", conflictCount > 0 ? conflictText.ToString() : "(none)");
        ctx.SetPortValue("conflict_count", (double)conflictCount);
        ctx.SetPortValue("duplicates", duplicateCount > 0 ? duplicateText.ToString() : "(none)");
        ctx.SetPortValue("duplicate_count", (double)duplicateCount);

        var summary = $"extensions={extCount}  shipped libraries checked={libCount}"
                    + $"  conflicts={conflictCount}  duplicates={duplicateCount}"
                    + (conflictCount > 0 ? "  <- a different VERSION is loaded than the one shipped" : "");
        ctx.SetPortValue("summary", summary);
        ctx.Logger.LogInfo($"[ExtInfo] {summary}");
    }

    /// <summary>
    /// 拡張は配置ルート直下の Extensions/ に置かれる。ルートはノード API の実体がある場所から辿る。
    /// </summary>
    static string ResolveNgolRoot()
    {
        try
        {
            var loc = typeof(INode).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                var dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "Extensions"))) return dir;
            }
        }
        catch { }

        var baseDir = AppContext.BaseDirectory;
        return string.IsNullOrEmpty(baseDir) ? null : baseDir;
    }

    /// <summary>拡張ディレクトリ直下と lib/&lt;tfm&gt;/ の両方が配布物になりうる。</summary>
    static IEnumerable<string> ShippedAssemblies(string extensionDir)
    {
        foreach (var f in SafeFiles(extensionDir)) yield return f;

        var libDir = Path.Combine(extensionDir, "lib");
        if (!Directory.Exists(libDir)) yield break;
        foreach (var tfmDir in Directory.EnumerateDirectories(libDir))
            foreach (var f in SafeFiles(tfmDir)) yield return f;
    }

    static IEnumerable<string> SafeFiles(string dir)
    {
        string[] files;
        try { files = Directory.GetFiles(dir, "*.dll"); }
        catch { yield break; }
        foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
    }

    static string ReadAssemblyVersion(string path)
    {
        // ネイティブ DLL はここで例外になる。版が読めないことは異常ではないので黙って null を返す。
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString(); }
        catch { return null; }
    }

    static string SafeName(Assembly asm)
    {
        try { return asm.GetName().Name ?? ""; } catch { return ""; }
    }

    static string SafeLocation(Assembly asm)
    {
        // 動的に生成されたアセンブリは Location を持たず、実装によっては例外を投げる。
        try { return asm.IsDynamic ? "" : (asm.Location ?? ""); } catch { return ""; }
    }

    sealed class Manifest
    {
        public string Id;
        public string Version;
        public List<string> Capabilities = new List<string>();
        public List<string> Platforms = new List<string>();
    }

    /// <summary>
    /// マニフェストは要素数の少ない固定スキーマなので、必要な項目だけを取り出す。
    /// JSON シリアライザに依存しないのは、ホストのランタイムによっては利用できないため。
    /// </summary>
    static Manifest ReadManifest(string path)
    {
        var m = new Manifest();
        string text;
        try { text = File.ReadAllText(path); }
        catch { return m; }

        m.Id = MatchScalar(text, "id");
        m.Version = MatchScalar(text, "version");
        m.Capabilities.AddRange(MatchArray(text, "capabilities"));
        m.Platforms.AddRange(MatchArray(text, "platforms"));
        return m;
    }

    static string MatchScalar(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static IEnumerable<string> MatchArray(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!m.Success) yield break;
        foreach (Match item in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\""))
            yield return item.Groups[1].Value;
    }
}
