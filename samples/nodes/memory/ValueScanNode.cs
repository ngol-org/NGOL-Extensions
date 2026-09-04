using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定した値を持つ番地を、書き込み可能なメモリ領域から総当たりで集める（最初のスキャン）。
/// 見つかった候補は ngol.mem.value_next で絞り込む。
///
/// 候補集合は AppDomain に置く（永続ストアではない）。理由は NgolScanSession を参照。
///
/// よくある値（0 や 1）を指定すると候補は数千万件になる。実測で 1MB あたり約 16 万件。
///   そのため max_matches で必ず上限を掛け、超えたら matches_truncated=true で打ち切る。
///   打ち切られた結果から絞り込んでも、探している番地が既に捨てられている可能性がある。
///
/// 対象がゲームの値であっても、C# のフィールドとして持つランタイム（Mono 等）では
///   ネイティブのメモリスキャンが値に届かないことがある。その場合は 0 件が正しい結果でありうる。
/// 命令が置かれる実行可能領域は対象外（値スキャンの対象ではない）。
///
/// このスキャンは対象と同じプロセスの中で走るため、走査そのものが対象のメモリを増やす。
///   自分の走査バッファは結果から除いてあるが、実行のたびに数件の差が出ることはある。
///   2 回続けて走査して両方に出た番地だけを信用すると確実。
/// </summary>
[NodeType("ngol.mem.value_scan", "Memory", "Value Scan",
    Version = "1.2.1",
    Description = "Search writable process memory for addresses holding a specific value (int32/int64/float/double). "
      + "Results are kept as a session (session_id) for ngol.mem.value_next to narrow down; sessions live in memory "
      + "only and the oldest are dropped automatically. Only writable, committed regions are scanned - not the "
      + "process's code. Compare scanned_mb against total_writable_mb before believing a low match count: large "
      + "applications hold several GB of writable memory, so with the default max_scan_mb the scan stops long before "
      + "the end and zero matches means 'not found yet', not 'not present'. Common values such as 0 produce tens of "
      + "millions of hits, so max_matches always applies and matches_truncated reports when it was reached. The scan "
      + "runs inside the target process, so a few unstable hits can appear in its own heap: trust addresses that show "
      + "up in two consecutive scans. On runtimes where state lives in managed fields rather than native memory, zero "
      + "matches can be the correct result.")]
[NodePort("session_id",   PortDirection.Input,  "string", Description = "Empty = start a new session (one is generated and returned)")]
[NodePort("value_type",   PortDirection.Input,  "string", Description = "int32 | int64 | float | double (default int32)")]
[NodePort("target_value", PortDirection.Input,  "number", Description = "Value to search for")]
[NodePort("tolerance",    PortDirection.Input,  "number", Description = "Match tolerance for float/double (default 0.01). Ignored for integers")]
[NodePort("alignment",    PortDirection.Input,  "number", Description = "Address step in bytes (default 4). Values are almost always 4-byte aligned; set 1 to scan every offset, which is far slower")]
[NodePort("max_scan_mb",  PortDirection.Input,  "number", Description = "Stop after scanning this many MB of writable memory (default 512)")]
[NodePort("max_matches",  PortDirection.Input,  "number", Description = "Stop after collecting this many matches (default 1000000)")]
[NodePort("session_id_out",     PortDirection.Output, "string", Description = "The session these results were stored under - the value of session_id when one was given, otherwise the generated one. Hand it to ngol.mem.value_next")]
[NodePort("match_count",        PortDirection.Output, "number", Description = "How many addresses held the value. Reaching max_matches sets matches_truncated, so there are more past that point")]
[NodePort("scanned_mb",         PortDirection.Output, "number", Description = "Writable memory actually read. Compare with total_writable_mb - less means the rest was never examined, so 0 matches does not mean the value is absent")]
[NodePort("total_writable_mb",  PortDirection.Output, "number", Description = "All writable memory in the process. scanned_mb below this means the scan did not reach the end - raise max_scan_mb")]
[NodePort("truncated",          PortDirection.Output, "boolean", Description = "true = max_scan_mb was reached before all writable memory was scanned")]
[NodePort("matches_truncated",  PortDirection.Output, "boolean", Description = "true = max_matches was reached, so the address you are looking for may have been discarded")]
[NodePort("sample_addresses",   PortDirection.Output, "string", Description = "Up to 20 matching addresses, comma-separated hex")]
[NodePort("result",             PortDirection.Output, "string", Description = "How many matches, out of how much of the writable memory. Says explicitly when the scan stopped early and what to raise")]
public sealed class ValueScanNode : INode
{
    // 領域を丸ごと確保せず、この大きさで区切って読む。
    //   領域は数百MBになりうるので、丸ごと確保すると大きなオブジェクトが毎回積み上がる。
    private const int ChunkSize = 4 * 1024 * 1024;

    public void Execute(IExecutionContext ctx)
    {
        var sessionId = (ctx.GetPortValue("session_id") as string ?? "").Trim();
        if (sessionId.Length == 0) sessionId = "vs" + Guid.NewGuid().ToString("N").Substring(0, 12);

        var type = (ctx.GetPortValue("value_type") as string ?? "int32").Trim().ToLowerInvariant();
        var size = NgolValueCodec.SizeOf(type);
        if (size == 0)
        {
            SetOutputs(ctx, sessionId, 0, 0, 0, false, false, "", $"unknown value_type: '{type}' (use int32/int64/float/double)");
            return;
        }
        var target = ctx.GetPortValue("target_value") is double t ? t : 0.0;
        var tolerance = ctx.GetPortValue("tolerance") is double tol ? tol : 0.01;
        var step = ctx.GetPortValue("alignment") is double a && a >= 1 ? (int)a : 4;
        var maxScanMb = ctx.GetPortValue("max_scan_mb") is double m && m > 0 ? m : 512.0;
        var maxMatches = ctx.GetPortValue("max_matches") is double mm && mm > 0 ? (long)mm : 1000000L;
        var maxScanBytes = (long)(maxScanMb * 1024 * 1024);

        var addresses = new List<long>();
        var values = new List<double>();
        long scannedBytes = 0;
        long totalWritableBytes = 0;
        bool budgetTruncated = false;
        bool matchesTruncated = false;

        // 同じ値がチャンクの境界をまたぐと取りこぼすため、次のチャンクを少しだけ重ねて読む。
        var overlap = size > step ? size - step : 0;

        var buffer = new byte[ChunkSize];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            // 走査バッファ自身の番地。ここに出るヒットは「読んだ内容の写し」であって
            //   対象の状態ではないため、結果から除く（除かないと実行のたびに偽の候補が増える）。
            var bufferStart = pin.AddrOfPinnedObject().ToInt64();
            var bufferEnd = bufferStart + buffer.Length;

            // 全体量と走査量は「同じ 1 回の列挙」から出す。
            //   別々の列挙で測ると、走査中に増えたメモリ（候補配列・GC・他スレッド）が
            //   片方だけに乗り、scanned_mb > total_writable_mb という食い違いが出る。
            //   => 列挙は打ち切らず（全領域を数える）、読むのを予算内に絞る。
            //   こうすると読むのは必ず数えた領域の中なので、scanned <= total が保証される。
            foreach (var region in NgolMemoryRegions.EnumerateWritableRegions(long.MaxValue))
            {
                totalWritableBytes += region.Size;

                // 予算切れ・上限到達後は、読まずに全体量だけ数え続ける。
                if (budgetTruncated || matchesTruncated) continue;

                var regionBase = region.Base.ToInt64();
                long pos = 0;
                while (pos < region.Size && !matchesTruncated)
                {
                    if (scannedBytes >= maxScanBytes) { budgetTruncated = true; break; }

                    var readAt = pos == 0 ? 0 : pos - overlap;
                    var want = (int)Math.Min(ChunkSize, region.Size - readAt);
                    var got = NgolSafeMemory.Read(new IntPtr(regionBase + readAt), buffer, 0, want);
                    if (got <= 0) break;

                    for (int off = 0; off + size <= got; off += step)
                    {
                        if (!NgolValueCodec.MatchesAt(type, buffer, off, target, tolerance)) continue;

                        var addr = regionBase + readAt + off;
                        if (addr >= bufferStart && addr < bufferEnd) continue;

                        addresses.Add(addr);
                        values.Add(NgolValueCodec.DecodeAt(type, buffer, off));
                        if (addresses.Count >= maxMatches) { matchesTruncated = true; break; }
                    }

                    scannedBytes += (readAt + got) - pos;
                    pos = readAt + got;
                    if (got < want) break; // 読めなくなった時点でこの領域は終わり
                }
            }
        }
        finally
        {
            // 手放す前に消す。読んだ内容が載ったままゴミになると、そのバッファ自身が
            //   次回の走査で「対象の値を持つ番地」として拾われる。
            Array.Clear(buffer, 0, buffer.Length);
            pin.Free();
        }

        var totalWritableMb = totalWritableBytes / (1024.0 * 1024.0);

        NgolScanSession.Save(sessionId, type, addresses.ToArray(), values.ToArray());

        var sample = string.Join(", ", addresses.Take(20).Select(x => "0x" + x.ToString("x")));
        var scannedMb = scannedBytes / (1024.0 * 1024.0);
        // 「打ち切った」だけでなく「全体のうちどこまで見たか」を必ず文にも出す。
        //   truncated=true だけでは、あとどれだけ残っているかが分からず「無い」と読まれる。
        var note = matchesTruncated ? " (stopped at max_matches - narrow the value or the range)"
                 : budgetTruncated ? $" (stopped at max_scan_mb - {totalWritableMb - scannedMb:F0} MB of writable memory left unscanned; raise max_scan_mb)"
                 : "";
        SetOutputs(ctx, sessionId, addresses.Count, scannedMb, totalWritableMb, budgetTruncated, matchesTruncated, sample,
            $"{addresses.Count} match(es) in {scannedMb:F1} of {totalWritableMb:F1} MB{note}");
    }

    private static void SetOutputs(IExecutionContext ctx, string sessionId, int count, double scannedMb,
                                    double totalWritableMb, bool truncated, bool matchesTruncated,
                                    string sample, string result)
    {
        ctx.SetPortValue("session_id_out", sessionId);
        ctx.SetPortValue("match_count", (double)count);
        ctx.SetPortValue("scanned_mb", scannedMb);
        ctx.SetPortValue("total_writable_mb", totalWritableMb);
        ctx.SetPortValue("truncated", truncated);
        ctx.SetPortValue("matches_truncated", matchesTruncated);
        ctx.SetPortValue("sample_addresses", sample);
        ctx.SetPortValue("result", result);
    }
}
