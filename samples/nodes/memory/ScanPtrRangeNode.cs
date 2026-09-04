using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// address_hexからoffset_start..offset_end(step ずつ)の範囲を8バイトずつ読み、
/// 値が対象モジュールの.text相当範囲(モジュールイメージ範囲)に入っているものを列挙する。
/// 公開されていない構造体のオフセットを総当たりで見つける用途。
///
/// 任意の番地を調べるノードなので、途中に読めない所があっても諦めずページ単位で飛ばして続ける。
/// </summary>
[NodeType("ngol.mem.scan_ptr_range", "Memory", "Scan Pointer Range",
    Version = "1.1.2",
    Description = "Scan address_hex+[offset_start..offset_end step step] for 8-byte values that look like code pointers (fall within the given module's image range). "
                + "Unreadable pages inside the range are skipped, not treated as the end. "
                + "Set async=true to scan in the background and read the result on a second run.")]
[NodePort("address_hex", PortDirection.Input, "string", Description = "Base address as hex string")]
[NodePort("offset_start", PortDirection.Input, "number", Description = "Start offset (default 0)")]
[NodePort("offset_end", PortDirection.Input, "number", Description = "End offset exclusive (default 0x200)")]
[NodePort("step", PortDirection.Input, "number", Description = "Step in bytes (default 8). Offsets are always offset_start + k*step, whatever the scan is split into")]
[NodePort("module", PortDirection.Input, "string", Description = "Module name to check range against, empty = main EXE")]
[NodePort("max_hits", PortDirection.Input, "number", Description = "Max candidates to return (default: 100). Reaching it sets truncated=true - there are more past that point")]
[NodePort("async", PortDirection.Input, "boolean", Description = "true = start the scan in the background and return immediately with done=false. Run again with the same inputs to read the result (default: false)")]
[NodePort("chunk_bytes", PortDirection.Input, "number", Description = "Bytes read per host update when async=true (default: 1048576 = 1MB). Smaller keeps the host smoother, down to 4096 - anything below that is raised to it")]
[NodePort("restart", PortDirection.Input, "boolean", Description = "true = discard a finished background scan and run it again (default: false). Without it, re-running returns the same result")]
[NodePort("hits", PortDirection.Output, "string", Description = "JSON array of {offset, value_hex} for candidates in-range")]
[NodePort("hit_count", PortDirection.Output, "number", Description = "Number of candidates found")]
[NodePort("scanned_bytes", PortDirection.Output, "number", Description = "Bytes actually read. Less than (offset_end - offset_start) means unreadable pages were skipped inside the range")]
[NodePort("truncated", PortDirection.Output, "boolean", Description = "true = stopped at max_hits, so more candidates exist beyond what is listed")]
[NodePort("done", PortDirection.Output, "boolean", Description = "false only while an async scan is still running")]
[NodePort("progress_rva", PortDirection.Output, "string", Description = "Offset the scan has reached")]
[NodePort("self_dropped", PortDirection.Output, "number", Description = "Hits discarded because they pointed into this scan's own working buffer. NGOL runs inside the target process, so a scan can find its own copy of the data. Non-zero means the range overlaps the scanner's memory - run it twice and trust only what appears both times")]
public sealed class ScanPtrRangeNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    public void Execute(IExecutionContext ctx)
    {
        var addrHex = (ctx.GetPortValue("address_hex") as string ?? "0").Trim();
        double startD = 0, endD = 0x200, stepD = 8;
        if (ctx.GetPortValue("offset_start") is double s) startD = s;
        if (ctx.GetPortValue("offset_end") is double e) endD = e;
        if (ctx.GetPortValue("step") is double st) stepD = st;
        var moduleName = ctx.GetPortValue("module") as string;

        double maxRaw = 100;
        if (ctx.GetPortValue("max_hits") is double mv) maxRaw = mv;
        var maxHits = Math.Max(1, (int)maxRaw);

        var async = ctx.GetPortValue("async") is bool ab && ab;
        var restart = ctx.GetPortValue("restart") is bool rb && rb;
        double chunkRaw = 1 << 20;
        if (ctx.GetPortValue("chunk_bytes") is double cv) chunkRaw = cv;

        if (!long.TryParse(addrHex.Replace("0x", "").Replace("0X", ""), System.Globalization.NumberStyles.HexNumber, null, out var baseAddr))
        {
            ctx.Logger.LogError($"[ScanPtrRange] Failed to parse address_hex: {addrHex}");
            SetEmpty(ctx);
            return;
        }

        IntPtr hModule = string.IsNullOrEmpty(moduleName) ? GetModuleHandleA(null) : GetModuleHandleA(moduleName);
        long lo = 0, hi = 0;
        if (hModule != IntPtr.Zero && GetModuleInformation(GetCurrentProcess(), hModule, out var info, (uint)Marshal.SizeOf<MODULEINFO>()))
        {
            lo = info.lpBaseOfDll.ToInt64();
            hi = lo + info.SizeOfImage;
        }

        var step = Math.Max(1, (long)stepD);
        var startOff = (long)startD;
        var size = (long)endD - startOff;
        if (size <= 0)
        {
            ctx.Logger.LogWarning($"[ScanPtrRange] empty range: offset_start=0x{startOff:X} offset_end=0x{(long)endD:X}");
            SetEmpty(ctx);
            return;
        }

        var outcome = NgolChunkedScan.Run(ctx, new NgolChunkedScan.Request
        {
            Name = "ScanPtrRange",
            KeySuffix = addrHex + "|" + (moduleName ?? "") + "|" + step.ToString("x"),
            BaseAddress = new IntPtr(baseAddr),
            StartRva = startOff,
            Size = size,
            MaxHits = maxHits,
            Async = async,
            Restart = restart,
            ChunkBytes = (int)Math.Max(4096, chunkRaw),
            // 8 バイト値が切れ目をまたぐことがあるので、その分だけ重ねて読む。
            Overlap = 7,
            // 途中に穴があっても先を見る。ここが他の走査ノードと違う。
            SkipUnreadable = true,
            Scan = (buf, len, chunkStart, usable, sink) => Match(buf, len, chunkStart, usable, sink, startOff, step, lo, hi),
        });

        NgolChunkedScan.Emit(ctx, outcome, "hits", "hit_count");

        if (!outcome.Done)
        {
            ctx.Logger.LogInfo($"[ScanPtrRange] background scan running, reached 0x{outcome.ProgressRva:X}");
            return;
        }

        ctx.Logger.LogInfo($"[ScanPtrRange] {outcome.HitCount} candidate(s) found, scanned {outcome.ScannedBytes} byte(s)"
            + NgolChunkedScan.DescribeLimits(outcome, maxHits));
    }

    static long Match(byte[] buf, int len, long chunkStartOff, int usable, NgolChunkedScan.Sink sink,
        long startOff, long step, long lo, long hi)
    {
        // step を変えても調べる位置がずれないよう、offset_start から数える。
        var phase = (chunkStartOff - startOff) % step;
        var first = phase == 0 ? 0 : (int)(step - phase);

        for (long i = first; i < usable && i + 8 <= len; i += step)
        {
            var value = BitConverter.ToInt64(buf, (int)i);
            if (lo != 0 && value >= lo && value < hi)
            {
                if (!sink.Add(chunkStartOff + i, $"{{\"offset\":\"0x{(chunkStartOff + i):X}\",\"value_hex\":\"0x{value:X}\"}}")) break;
            }
        }
        return usable;
    }

    static void SetEmpty(IExecutionContext ctx)
    {
        ctx.SetPortValue("hits", "[]");
        ctx.SetPortValue("hit_count", 0.0);
        ctx.SetPortValue("scanned_bytes", 0.0);
        ctx.SetPortValue("truncated", false);
        ctx.SetPortValue("done", true);
        ctx.SetPortValue("progress_rva", "0x0");
        ctx.SetPortValue("self_dropped", 0.0);
    }
}
