using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが読み込んだプラグインを棚卸しする。自分もその表に出る。
///
/// 2 つの見方を突き合わせる。
///   - 管理側: どのアセンブリが読み込まれ、外から見える型と中に在る型がいくつあるか
///   - ネイティブ側: そのファイルがメモリへどれだけ載っているか
/// どちらか片方では「重いのは誰か」も「同じものが二重に載っていないか」も出ない。
///
/// 同じ名前で版が違うものが同居していると、後から読まれた側は使われない。
/// 拡張を重ねて入れる仕組みでは、これが不具合の原因になりやすいので、別の出力で名指しする。
/// </summary>
[NodeType("pdn.plugins.list", "Paint.NET", "Plugin Inventory",
    Version = "1.3.0",
    Description = "Take stock of what the host has loaded: assembly name, version, how many types it exposes out of "
                + "how many it holds, and how much of the file is mapped into memory. This bridge appears in the same "
                + "table. Names that are present more than once with different versions are listed separately, "
                + "because only one of them is used.")]
[NodePort("filter", PortDirection.Input, "string",
    Description = "Case-insensitive substring the assembly name must contain. Empty = every assembly loaded from the host's plugin folders")]
[NodePort("include_host", PortDirection.Input, "boolean",
    Description = "Also list the host's own assemblies, not just plugins. Default false")]
[NodePort("table", PortDirection.Output, "string",
    Description = "One line per assembly: name, version, types as public/total, mapped KB, path. A plugin can carry "
                + "zero public types and still work, so the second number is the one that says whether it holds anything")]
[NodePort("count", PortDirection.Output, "number",
    Description = "How many assemblies the table has")]
[NodePort("duplicate_names", PortDirection.Output, "string",
    Description = "Names loaded more than once with different versions, comma separated. Empty when there are none")]
[NodePort("status", PortDirection.Output, "string",
    Description = "\"ok\" or the reason a column could not be filled. Columns are independent, so one failure does not empty the table")]
public sealed class PdnPluginsNode : INode
{
    /// <summary>最後の結果を控える鍵。ノードの ID に .result を付けたもの。</summary>
    private const string ResultKey = "pdn.plugins.list.result";

    /// <summary>ホストがプラグインを探すフォルダの名前。この 3 つ以外は走査されない。</summary>
    private static readonly string[] PluginFolders = { "Effects", "FileTypes", "Shapes" };

    public void Execute(IExecutionContext ctx)
    {
        var filter = ctx.GetPortValue("filter") as string ?? "";
        var includeHost = ctx.GetPortValue("include_host") is bool b && b;
        var notes = new StringBuilder();

        var mapped = MappedSizes(notes);
        var pluginDirs = PluginDirectories(notes);

        var rows = new List<Row>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string path;
            try { path = asm.Location; } catch { path = ""; }
            if (path.Length == 0) continue;   // 動的に生成されたものはファイルを持たない

            var name = asm.GetName().Name ?? "";
            if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!includeHost && !IsUnder(path, pluginDirs)) continue;

            var (exported, total) = TypeCounts(asm);
            rows.Add(new Row(
                name,
                asm.GetName().Version?.ToString() ?? "",
                exported,
                total,
                mapped.TryGetValue(path, out var kb) ? kb : -1,
                path));
        }

        rows.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));

        var table = new StringBuilder();
        foreach (var r in rows)
        {
            table.AppendLine(string.Format("{0,-40} {1,-16} types={2,-9} mapped={3,7} {4}",
                r.Name, r.Version, Count(r.Exported) + "/" + Count(r.Total),
                r.MappedKb < 0 ? "?" : r.MappedKb + "KB", r.Path));
        }

        var dupes = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Version).Distinct().Count() > 1)
            .Select(g => g.Key);

        var text = table.ToString().TrimEnd();
        var dupeNames = string.Join(", ", dupes);
        var status = notes.Length == 0 ? "ok" : notes.ToString().TrimEnd();

        ctx.SetPortValue("table", text);
        ctx.SetPortValue("count", (double)rows.Count);
        ctx.SetPortValue("duplicate_names", dupeNames);
        ctx.SetPortValue("status", status);

        // 実行の応答は 1 回きりなので、後から読む側のために控える。
        ctx.Store.Set(ResultKey,
            $"assemblies : {rows.Count}\n"
          + $"same name, different version : {(dupeNames.Length == 0 ? "none" : dupeNames)}\n"
          + $"status     : {status}\n\n{text}");
    }

    private readonly record struct Row(string Name, string Version, int Exported, int Total, long MappedKb, string Path);

    private static string Count(int n) => n < 0 ? "?" : n.ToString();

    /// <summary>ホストが走査するフォルダ。ここから読まれたものをプラグインとみなす。</summary>
    private static List<string> PluginDirectories(StringBuilder notes)
    {
        var dirs = new List<string>();
        try
        {
            var hostAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "paintdotnet", StringComparison.OrdinalIgnoreCase));
            var collType = hostAsm?.GetType("PaintDotNet.Effects.EffectsCollection");
            var instance = collType?.GetProperty("Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            var enumerate = collType?.GetMethod("EnumeratePluginAssemblyPaths",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (instance != null && enumerate != null &&
                enumerate.Invoke(instance, null) is IEnumerable paths)
            {
                foreach (var p in paths)
                {
                    var dir = System.IO.Path.GetDirectoryName(p as string ?? "");
                    if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        dirs.Add(dir!);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            notes.AppendLine("plugin folders: " + ex.GetType().Name + " " + ex.Message);
        }

        // 効果の一覧が答えるのは効果の置き場所だけ。ホストはこの 3 つを走査するので、
        // 読み込まれているアセンブリの置き場所からも拾う。
        // 効果を 1 つも持たないプラグインは、こちらでしか見つからない。
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string path;
            try { path = asm.Location; } catch { continue; }
            if (path.Length == 0) continue;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) continue;
            var name = System.IO.Path.GetFileName(dir);
            if (!PluginFolders.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (!dirs.Contains(dir!, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir!);
        }

        if (dirs.Count == 0) notes.AppendLine("plugin folders: none were found");
        return dirs;
    }

    private static bool IsUnder(string path, List<string> dirs)
    {
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        return dirs.Any(d => dir.StartsWith(d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ファイルがメモリへどれだけ載っているか。管理側からは見えないので、モジュールの一覧に聞く。</summary>
    private static Dictionary<string, long> MappedSizes(StringBuilder notes)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                if (!string.IsNullOrEmpty(m.FileName)) map[m.FileName] = m.ModuleMemorySize / 1024;
            }
        }
        catch (Exception ex)
        {
            notes.AppendLine("mapped size: " + ex.GetType().Name + " " + ex.Message);
        }
        return map;
    }

    /// <summary>
    /// 外から見える型の数と、中に在る型の数。読み込めない型があっても、そこで止めずに数える。
    /// 外向きが 0 でも中身が空とは限らない。ホストは公開されていない型からも効果を見つける。
    /// </summary>
    private static (int Exported, int Total) TypeCounts(Assembly asm)
    {
        var exported = -1;
        try { exported = asm.GetExportedTypes().Length; }
        catch (ReflectionTypeLoadException ex) { exported = ex.Types.Count(t => t != null && t.IsPublic); }
        catch { }

        try { return (exported, asm.GetTypes().Length); }
        catch (ReflectionTypeLoadException ex) { return (exported, ex.Types.Count(t => t != null)); }
        catch { return (exported, -1); }
    }
}
