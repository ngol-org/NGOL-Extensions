using System;
using System.Collections.Generic;
using System.Linq;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ngol.mem.value_scan（または前回の value_next）が残した候補を、いま現在の値で絞り込む。
///
/// condition:
///   equals      ... target_value と一致するものだけ残す
///   changed     ... 前回スキャン時から値が変わったものだけ残す
///   unchanged   ... 変わっていないものだけ残す
///   increased   ... 増えたものだけ残す
///   decreased   ... 減ったものだけ残す
///
/// 候補集合は AppDomain 上のセッションで持つ（NgolScanSession）。呼ぶたびに生き残った
///   候補だけで置き換わる。
/// 絞り込みは元に戻せない。条件を誤って 0 件にしたら value_scan からやり直すことになる。
/// </summary>
[NodeType("ngol.mem.value_next", "Memory", "Value Next",
    Version = "1.1.2",
    Description = "Narrow down a ngol.mem.value_scan session by re-reading each candidate's current value and "
      + "filtering: equals / changed / unchanged / increased / decreased. Repeated narrowing is how you find the "
      + "address of a value you cannot type in exactly - one you only know went up, went down, or stayed the same. "
      + "Each call replaces the session with the survivors and cannot be undone: narrowing to zero means starting "
      + "over from ngol.mem.value_scan.")]
[NodePort("session_id",   PortDirection.Input,  "string", Description = "Session from ngol.mem.value_scan")]
[NodePort("condition",    PortDirection.Input,  "string", Description = "equals | changed | unchanged | increased | decreased (default changed)")]
[NodePort("target_value", PortDirection.Input,  "number", Description = "Used when condition=equals")]
[NodePort("tolerance",    PortDirection.Input,  "number", Description = "Match tolerance for float/double (default 0.01)")]
[NodePort("match_count",      PortDirection.Output, "number", Description = "Candidates that survived this round. The session now holds exactly these")]
[NodePort("previous_count",   PortDirection.Output, "number", Description = "Candidates the session held before this round. Unchanged from match_count means the condition narrowed nothing")]
[NodePort("sample_addresses", PortDirection.Output, "string", Description = "Up to 20 surviving addresses, comma-separated hex")]
[NodePort("result",           PortDirection.Output, "string", Description = "How many of the previous candidates matched the condition, and which condition that was")]
public sealed class ValueNextNode : INode
{
    private static readonly string[] Conditions = { "equals", "changed", "unchanged", "increased", "decreased" };

    public void Execute(IExecutionContext ctx)
    {
        var sessionId = (ctx.GetPortValue("session_id") as string ?? "").Trim();
        var condition = (ctx.GetPortValue("condition") as string ?? "changed").Trim().ToLowerInvariant();
        var target    = ctx.GetPortValue("target_value") is double t ? t : 0.0;
        var tolerance = ctx.GetPortValue("tolerance") is double tol ? tol : 0.01;

        if (sessionId.Length == 0)
        {
            SetOutputs(ctx, 0, 0, "", "session_id is empty (run ngol.mem.value_scan first)");
            return;
        }
        // 知らない条件を「1 件も残らなかった」と区別する。既定へ落とすと、綴り違いが
        //   絞り込みの成果に見えてしまう。
        if (Array.IndexOf(Conditions, condition) < 0)
        {
            SetOutputs(ctx, 0, 0, "", $"unknown condition: '{condition}' (use {string.Join(" / ", Conditions)})");
            return;
        }
        if (!NgolScanSession.TryLoad(sessionId, out var type, out var addrs, out var oldValues))
        {
            var live = NgolScanSession.ListSessions();
            var known = live.Length == 0 ? "(none)" : string.Join(", ", live);
            SetOutputs(ctx, 0, 0, "",
                $"unknown session_id: '{sessionId}'. Sessions live in memory only and the oldest are dropped. Available: {known}");
            return;
        }

        var size = NgolValueCodec.SizeOf(type);
        var survivorAddrs = new List<long>();
        var survivorValues = new List<double>();
        var buf = new byte[size];

        for (int i = 0; i < addrs.Length; i++)
        {
            if (NgolSafeMemory.Read(new IntPtr(addrs[i]), buf, 0, size) < size)
                continue; // 読めなくなった候補は落とす（対象が解放された等）

            var newValue = NgolValueCodec.DecodeAt(type, buf, 0);
            var oldValue = oldValues[i];
            bool keep = condition switch
            {
                "equals"    => type is "float" or "double" ? Math.Abs(newValue - target) <= tolerance : newValue == target,
                "changed"   => newValue != oldValue,
                "unchanged" => newValue == oldValue,
                "increased" => newValue > oldValue,
                "decreased" => newValue < oldValue,
                _ => false,
            };
            if (keep) { survivorAddrs.Add(addrs[i]); survivorValues.Add(newValue); }
        }
        // 読み取りバッファに探している値が載ったままゴミになると、そのバッファ自身が
        //   次の value_scan で候補として拾われる。手放す前に消す。
        Array.Clear(buf, 0, buf.Length);

        NgolScanSession.Save(sessionId, type, survivorAddrs.ToArray(), survivorValues.ToArray());

        var sample = string.Join(", ", survivorAddrs.Take(20).Select(x => "0x" + x.ToString("x")));
        SetOutputs(ctx, survivorAddrs.Count, addrs.Length, sample,
            $"{survivorAddrs.Count} of {addrs.Length} candidate(s) matched '{condition}'");
    }

    private static void SetOutputs(IExecutionContext ctx, int count, int previousCount, string sample, string result)
    {
        ctx.SetPortValue("match_count", (double)count);
        ctx.SetPortValue("previous_count", (double)previousCount);
        ctx.SetPortValue("sample_addresses", sample);
        ctx.SetPortValue("result", result);
    }
}
