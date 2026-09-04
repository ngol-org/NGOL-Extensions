using System;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// このプロセスに読み込まれているモジュールを、大きい順に一覧する。
///
/// 出すのは事実だけ--名前・置き場所・ベース・大きさ。
/// どれが目的のコードを持つかは判定しない。
///   実行ファイルが起動用の殻でしかない構成もあれば、実行ファイル自身が本体の構成もあり、
///   同梱ライブラリの方を調べたい場合もある。どれに当たるかはノードには分からず、
///   分かるのは「何がどれだけの大きさで読み込まれているか」までである。
///
/// 他の解析ノードは `module` が空のときプロセスの主モジュールを対象にする。
/// 走査を始める前にこのノードを通せば、その既定で狙いどおりかを自分の目で確かめられる。
///
/// 一覧が全部とは限らない。列挙している最中に別のスレッドが読み込むと取りこぼしうるため、
///   その場合は `complete` を false にして報告する。黙って縮めると
///   「出てこない」が「読み込まれていない」に見えてしまう。
///
/// データとして読み込まれたモジュール（実行対象ではなく資源としてのマップ）は
///   そもそも列挙 API が返さないため、この一覧には現れない。
///
/// 実装が Win32 のみなのは意図的:
/// 動的コンパイルされるノードが参照できるアセンブリはホスト依存で変わるため、
/// 呼び出し側と同じ依存の範囲（kernel32 / psapi）に留めている。
/// </summary>
[NodeType("ngol.code.module_list", "Code", "Module List",
    Version = "1.1.1",
    Description = "List the modules loaded in this process, largest first, with path, base address and image size, plus which module the other nodes target when their `module` port is left empty. Reports facts only and does not guess which module holds the code you are after - that depends on the layout of the program being analysed. Run it before scanning to check that the default target is the one you meant. If the list could not be captured in full (another thread loaded a module while it was being enumerated) `complete` is false - treat a missing name as unknown rather than as absent. Modules mapped as data rather than as code are never listed.")]
[NodePort("max", PortDirection.Input, "number", Description = "Maximum entries to show in the report (default 30). All are counted regardless")]
[NodePort("name_filter", PortDirection.Input, "string", Description = "Only include modules whose name contains this text (case-insensitive). Empty = all")]
[NodePort("min_size_mb", PortDirection.Input, "number", Description = "Only include modules at least this large, in MB (default 0)")]
[NodePort("count", PortDirection.Output, "number", Description = "Number of modules matched. A lower bound when `complete` is false")]
[NodePort("complete", PortDirection.Output, "boolean", Description = "false = the module list could not be captured in full, so a name missing from the list is not evidence that it is not loaded")]
[NodePort("largest_name", PortDirection.Output, "string", Description = "Name of the largest matched module. This is just the largest one - it is not a claim about which module holds the code you want")]
[NodePort("default_target", PortDirection.Output, "string", Description = "Module other nodes use when their `module` port is left empty")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable table")]
public sealed class ModuleListNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        int max = ctx.GetPortValue("max") is double md ? (int)md : 30;
        if (max < 1) max = 1;
        var filter = (ctx.GetPortValue("name_filter") as string ?? "").Trim();
        double minMb = ctx.GetPortValue("min_size_mb") is double sd ? sd : 0;
        long minBytes = (long)(minMb * 1024 * 1024);

        var all = NgolModuleDefault.List(4096, out var truncated);
        var matched = new System.Collections.Generic.List<NgolModuleDefault.ModuleEntry>();
        foreach (var m in all)
        {
            if (m.Size < minBytes) continue;
            if (filter.Length > 0 && m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            matched.Add(m);
        }

        var defaultTarget = NgolModuleDefault.Resolve(null);

        if (matched.Count == 0)
        {
            ctx.SetPortValue("count", 0.0);
            ctx.SetPortValue("complete", !truncated);
            ctx.SetPortValue("largest_name", "");
            ctx.SetPortValue("default_target", defaultTarget);
            ctx.SetPortValue("result", all.Count == 0
                ? "Could not enumerate modules on this host."
                : $"No module matched (of {all.Count} loaded).");
            return;
        }

        var sb = new StringBuilder();
        sb.Append(NgolModuleDefault.FormatList(matched, max, truncated));

        // 既定の対象が何で、どれだけの大きさかを添える。
        // どれが目的のコードを持つかは推測しない。並んだ大きさを見て利用者が決める。
        //   実行ファイルが起動用の殻でしかない構成もあれば、実行ファイル自身が本体の構成もあり、
        //   同梱ライブラリの方を調べたい場合もある。どれかはノードには分からない。
        if (defaultTarget.Length > 0)
        {
            sb.Append("\ndefault target when `module` is empty: ").Append(defaultTarget);
            foreach (var m in all)
            {
                if (!string.Equals(m.Name, defaultTarget, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append("  (").Append(FormatSize(m.Size)).Append(')');
                break;
            }
            sb.Append('\n');
        }

        ctx.SetPortValue("count", (double)matched.Count);
        ctx.SetPortValue("complete", !truncated);
        ctx.SetPortValue("largest_name", matched[0].Name);
        ctx.SetPortValue("default_target", defaultTarget);
        ctx.SetPortValue("result", sb.ToString());
        ctx.Logger.LogInfo($"[ModuleList] {matched.Count} module(s), largest={matched[0].Name}"
            + (truncated ? " (INCOMPLETE)" : ""));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("F2") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        return bytes + " B";
    }
}
