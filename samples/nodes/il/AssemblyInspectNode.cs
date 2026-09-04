using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// プロセスにロード済みのアセンブリを一覧し、指定した名前・版でのバインドを実際に試す診断ノード。
///
/// 「DLL は置いてあるのに読めていない」「別の版が使われている」の切り分けに使う。
/// アセンブリの版が食い違うと、ロード自体は成功したのに型へ最初に触れた場所で
/// FileNotFoundException になり、エラーメッセージが原因から遠くなる。
/// そういう場面で「実際に何がロードされているか」「その名前と版で引けるのか」を
/// その場で確かめられる。
///
/// ホストや対象アプリの内部構造に一切依存せず、標準ライブラリだけで動く。
/// AssemblyLoadContext は .NET Framework / Mono に無いため意図的に使っていない。
/// </summary>
[NodeType("ngol.il.assembly_inspect", "IL", "Assembly Inspect",
    Version = "1.0.1",
    Description = "List loaded assemblies with their identity and file location, and optionally test whether a given name/version can be bound. Useful for diagnosing version conflicts and 'the DLL is there but not loaded' situations.")]
[NodePort("name_filter", PortDirection.Input, "string", ShowInlineEditor = true,
    Description = "Substring to filter assembly names by (case-insensitive). Empty lists every loaded assembly.")]
[NodePort("probe_name", PortDirection.Input, "string", ShowInlineEditor = true,
    Description = "Assembly simple name to test binding for (e.g. \"MyLib\"). Empty skips the binding test.")]
[NodePort("probe_version", PortDirection.Input, "string", ShowInlineEditor = true,
    Description = "Version to request in the binding test (e.g. \"1.2.3.0\"). Empty tests only the version-less request.")]
[NodePort("report", PortDirection.Output, "string", Description = "Human-readable report")]
[NodePort("count", PortDirection.Output, "number", Description = "Number of assemblies listed")]
public sealed class AssemblyInspectNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var filter = AsString(ctx, "name_filter");
        var probeName = AsString(ctx, "probe_name");
        var probeVersion = AsString(ctx, "probe_version");

        var loaded = CollectLoaded(filter);
        var sb = new StringBuilder();

        sb.Append("=== Loaded assemblies");
        if (!string.IsNullOrEmpty(filter)) sb.Append(" matching \"").Append(filter).Append('"');
        sb.Append(" (").Append(loaded.Count).AppendLine(") ===");

        foreach (var line in loaded) sb.AppendLine("  " + line);

        if (!string.IsNullOrEmpty(probeName))
        {
            sb.AppendLine();
            sb.AppendLine("=== Binding test ===");
            sb.AppendLine("  without version -> " + TryBind(probeName));
            if (!string.IsNullOrEmpty(probeVersion))
            {
                var q = probeName + ", Version=" + probeVersion + ", Culture=neutral, PublicKeyToken=null";
                sb.AppendLine("  Version=" + probeVersion + " -> " + TryBind(q));
            }
        }

        ctx.SetPortValue("report", sb.ToString());
        ctx.SetPortValue("count", (double)loaded.Count);
        ctx.Logger.LogInfo("[AssemblyInspect] listed " + loaded.Count + " assembly/assemblies");
    }

    private static List<string> CollectLoaded(string filter)
    {
        var result = new List<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            AssemblyName name;
            try { name = asm.GetName(); }
            catch { continue; }

            var shortName = name.Name ?? "(unknown)";
            if (!string.IsNullOrEmpty(filter) &&
                shortName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            result.Add(shortName + " " + name.Version + "  <- " + SafeLocation(asm));
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>動的生成アセンブリは Location へのアクセス自体が例外になるため必ず包む。</summary>
    private static string SafeLocation(Assembly asm)
    {
        try
        {
            var loc = asm.Location;
            return string.IsNullOrEmpty(loc) ? "(no file)" : loc;
        }
        catch { return "(unavailable)"; }
    }

    /// <summary>
    /// 実際にバインドを試す。ロード済みのものが返ることを期待しており、
    /// 見つからなければ例外の型名を結果として返す（ノード自体は完走する）。
    /// </summary>
    private static string TryBind(string assemblyString)
    {
        try
        {
            var asm = Assembly.Load(assemblyString);
            return "OK, resolved to " + asm.GetName().Version;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static string AsString(IExecutionContext ctx, string port)
    {
        var v = ctx.GetPortValue(port) ?? ctx.GetParam<object>(port);
        var s = v as string;
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
