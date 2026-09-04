using System;
using System.Linq;
using System.Reflection;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストそのものに素性を聞く。ネイティブの口を 1 つも経由しない。
///
/// 届き方が 2 通り混ざっているのがこのノードの見どころ。
///   - ウィンドウの題は、ホストが読み込んでいる公開 API から直に
///   - 効果の一覧は、ホスト本体のアセンブリにあり参照できないので反射で
/// 対象の一部は参照でき、一部は反射でしか届かない。ホストが変わっても構図は同じ。
///
/// ホストの UI を触るものは、ホストの UI スレッドで実行する。
///   NGOL は自前のスレッドで動くので、ここから主ウィンドウへ渡し直している。
/// </summary>
[NodeType("pdn.app.info", "Paint.NET", "App Info",
    Version = "1.2.0",
    Description = "Ask the host itself what it is: version, main window title, how many effects it has registered, "
                + "and whether this bridge is one of them. Reads only - nothing about the host is changed.")]
[NodePort("host_version", PortDirection.Output, "string",
    Description = "Host version, taken from the loaded host assembly rather than from a file on disk")]
[NodePort("window_title", PortDirection.Output, "string",
    Description = "Title of the host's main window, or an empty string when no window is up yet")]
[NodePort("effect_count", PortDirection.Output, "number",
    Description = "How many effects the host has registered, built-in ones included. 0 when the list could not be reached")]
[NodePort("bridge_registered", PortDirection.Output, "boolean",
    Description = "True when the bridge assembly is loaded and sits in one of the folders the host scans for plugins - "
                + "the proof that it got in through the official mechanism rather than by injection")]
[NodePort("bridge_path", PortDirection.Output, "string",
    Description = "Where the host loaded the bridge from, or why it could not be found")]
[NodePort("status", PortDirection.Output, "string",
    Description = "\"ok\" or the reason a value could not be reached. Each value is reported separately, so one failure does not hide the others")]
public sealed class PdnAppInfoNode : INode
{
    /// <summary>この bridge のアセンブリ名。ホストがどこから読んだかをこれで辿る。</summary>
    private const string BridgeAssemblyName = "NgolForPaintDotNet";

    /// <summary>ホストがプラグインを探すフォルダの名前。この 3 つ以外は走査されない。</summary>
    private static readonly string[] PluginFolders = { "Effects", "FileTypes", "Shapes" };

    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>最後の結果を控える鍵。ノードの ID に .result を付けたもの。</summary>
    private const string ResultKey = "pdn.app.info.result";

    public void Execute(IExecutionContext ctx)
    {
        var notes = new StringBuilder();

        var version = HostVersion(notes);
        var title = MainWindowTitle(notes);
        var effects = Effects(notes);
        var count = effects?.Length ?? 0;
        var (registered, path) = BridgeOrigin();
        var status = notes.Length == 0 ? "ok" : notes.ToString().TrimEnd();

        ctx.SetPortValue("host_version", version);
        ctx.SetPortValue("window_title", title);
        ctx.SetPortValue("effect_count", (double)count);
        ctx.SetPortValue("bridge_registered", registered);
        ctx.SetPortValue("bridge_path", path);
        ctx.SetPortValue("status", status);

        // 実行の応答は 1 回きりなので、後から読む側のために控える。
        ctx.Store.Set(ResultKey,
            $"host version  : {version}\n"
          + $"window title  : {title}\n"
          + $"effects       : {count}\n"
          + $"loaded by the host from a plugin folder : {registered}\n"
          + $"bridge path   : {path}\n"
          + $"status        : {status}");
    }

    private static string HostVersion(StringBuilder notes)
    {
        // ディスク上のファイルではなく、いま読み込まれているアセンブリに聞く。
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "PaintDotNet.Core", StringComparison.OrdinalIgnoreCase));
        if (asm == null)
        {
            notes.AppendLine("host_version: the host assembly is not loaded in this process");
            return "";
        }
        return asm.GetName().Version?.ToString() ?? "";
    }

    private static string MainWindowTitle(StringBuilder notes)
    {
        try
        {
            // 型で参照せず反射で辿る。ノードのコンパイルは参照の集合がホストごとに違うため。
            var formsAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "System.Windows.Forms", StringComparison.OrdinalIgnoreCase));
            var appType = formsAsm?.GetType("System.Windows.Forms.Application");
            var openForms = appType?.GetProperty("OpenForms", AnyStatic)?.GetValue(null);
            if (openForms == null)
            {
                notes.AppendLine("window_title: no window collection to read");
                return "";
            }

            string title = "";
            foreach (var form in (System.Collections.IEnumerable)openForms)
            {
                var type = form.GetType();
                // 題を読むだけでも、そのウィンドウを持つスレッドへ渡す。UI の状態を他のスレッドから触らない。
                var invoke = type.GetMethod("Invoke", new[] { typeof(Delegate) });
                Func<string> read = () =>
                    type.GetProperty("Text")?.GetValue(form) as string ?? "";
                title = invoke != null
                    ? (string)invoke.Invoke(form, new object[] { read })!
                    : read();
                if (!string.IsNullOrEmpty(title)) break;
            }
            return title;
        }
        catch (Exception ex)
        {
            notes.AppendLine("window_title: " + ex.GetType().Name + " " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// この bridge をホストがどこから読んだか。
    ///
    /// 効果の一覧では見分けられない。この bridge は効果の型を 1 つも置かないため、
    /// 効果メニューにも一覧にも出ない。読まれた事実は、載っているアセンブリの
    /// 置き場所が、ホストが走査するフォルダかどうかで見る。
    /// </summary>
    private static (bool Registered, string Path) BridgeOrigin()
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, BridgeAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (asm == null) return (false, "the bridge assembly is not loaded");

        string location;
        try { location = asm.Location; } catch { location = ""; }
        if (location.Length == 0) return (false, "the bridge assembly has no file");

        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(location) ?? "");
        var scanned = PluginFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        return (scanned, location);
    }

    private static string[]? Effects(StringBuilder notes)
    {
        try
        {
            // 効果の一覧はホスト本体のアセンブリにあり、プラグインからは参照できない。
            var hostAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "paintdotnet", StringComparison.OrdinalIgnoreCase));
            var collType = hostAsm?.GetType("PaintDotNet.Effects.EffectsCollection");
            var instance = collType?.GetProperty("Instance", AnyStatic)?.GetValue(null);
            if (instance == null)
            {
                notes.AppendLine("effects: the host's effect list could not be reached");
                return null;
            }

            // 一覧は遅延構築で、開かれるまで空のことがある。読む前に組ませる。
            collType!.GetMethod("EnsureInitialized", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(instance, null);

            if (collType.GetProperty("EffectInfos")?.GetValue(instance) is not System.Collections.IEnumerable infos)
            {
                notes.AppendLine("effects: the list is not enumerable");
                return null;
            }

            return infos.Cast<object>()
                .Select(info => info?.GetType().GetProperty("Name")?.GetValue(info) as string ?? "")
                .ToArray();
        }
        catch (Exception ex)
        {
            notes.AppendLine("effects: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }
}
