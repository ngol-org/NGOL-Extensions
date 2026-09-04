using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定モジュールの生メモリを線形バイト検索し、指定文字列(UTF-16LE or ASCII)が
/// 出現する全RVAを列挙する。ngol.code.xref_index_buildが「あるRVAへの参照元」を
/// 調べるのに対し、本ノードは「文字列そのものがバイナリ中に存在するか・どこにあるか」を
/// 調べる。読み取り範囲はデフォルトでPEヘッダーのSizeOfImageから自動算出するため、
/// 巨大なメインEXE全体もmodule=""(GetModuleHandleA(NULL)=メインモジュール)一発で走査できる。
///
/// 出力:
///   hits      -> 出現RVAのJSON配列(16進文字列)
///   hit_count -> hits の件数
///
/// 主な使い方:
///   ある文字列(クラス名・アセットパス等)がメインEXE/特定モジュールのどこかに
///   存在するかを高速に確認したい場合に使う。見つかったRVAは
///   ngol.mem.read_string で内容確認、周辺は ngol.code.disasm で
///   逆アセンブルして参照元コードを追う、という流れで使うと効率的。
///
/// 制約:
///   単純なバイト列一致検索のため、圧縮・暗号化されたデータ内の文字列は検出できない。
///   Windows x64専用（GetModuleHandleA使用）。
/// </summary>
[NodeType("ngol.code.find_string", "Code", "Find String",
    Version = "1.1.3",
    Description = "Linear byte-search a module's memory for all occurrences of search_text (UTF-16LE or ASCII). Scan range defaults to the module's full SizeOfImage (read from its PE header), so module=\"\" scans the main EXE in one call. "
                + "Set async=true to scan in the background and read the result on a second run.")]
[NodePort("module",         PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module")]
[NodePort("search_text",    PortDirection.Input,  "string",  Description = "Text to search for")]
[NodePort("wide",           PortDirection.Input,  "boolean", Description = "true=UTF-16LE (default), false=ASCII")]
[NodePort("scan_start_rva", PortDirection.Input,  "string",  Description = "Start RVA (default '0x1000')")]
[NodePort("scan_size",      PortDirection.Input,  "number",  Description = "Bytes to scan. 0 or omitted = auto-detect via PE SizeOfImage")]
[NodePort("max_hits",       PortDirection.Input,  "number",  Description = "Max occurrences to return (default: 200). Reaching it sets truncated=true - there are more past that point")]
[NodePort("async",          PortDirection.Input,  "boolean", Description = "true = start the scan in the background and return immediately with done=false. Run again with the same inputs to read the result (default: false)")]
[NodePort("chunk_bytes",    PortDirection.Input,  "number",  Description = "Bytes scanned per host update when async=true (default: 4194304 = 4MB). Smaller keeps the host smoother, down to 4096 - anything below that is raised to it")]
[NodePort("restart",        PortDirection.Input,  "boolean", Description = "true = discard a finished background scan and run it again (default: false). Without it, re-running returns the same result")]
[NodePort("hits",           PortDirection.Output, "string",  Description = "JSON array of hex RVA strings where search_text was found")]
[NodePort("hit_count",      PortDirection.Output, "number",  Description = "Number of occurrences found")]
[NodePort("scanned_bytes",  PortDirection.Output, "number",  Description = "Bytes actually scanned. Less than scan_size means the readable range ended there")]
[NodePort("truncated",      PortDirection.Output, "boolean", Description = "true = stopped at max_hits, so more occurrences exist beyond what is listed")]
[NodePort("done",           PortDirection.Output, "boolean", Description = "false only while an async scan is still running")]
[NodePort("progress_rva",   PortDirection.Output, "string",  Description = "RVA the scan has reached")]
[NodePort("self_dropped", PortDirection.Output, "number", Description = "Hits discarded because they pointed into this scan's own working buffer. NGOL runs inside the target process, so a scan can find its own copy of the data. Non-zero means the range overlaps the scanner's memory - run it twice and trust only what appears both times")]
public sealed class FindStringOccurrencesNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string moduleName);

    public void Execute(IExecutionContext ctx)
    {
        var moduleName = ctx.GetPortValue("module") as string ?? "";
        var searchText = ctx.GetPortValue("search_text") as string ?? "";
        bool wide = !(ctx.GetPortValue("wide") is bool wb) || wb;
        var scanStartStr = (ctx.GetPortValue("scan_start_rva") as string) ?? "0x1000";
        double scanSizeD = 0;
        if (ctx.GetPortValue("scan_size") is double sd) scanSizeD = sd;

        double maxRaw = 200;
        if (ctx.GetPortValue("max_hits") is double mv) maxRaw = mv;
        var maxHits = Math.Max(1, (int)maxRaw);

        var async = ctx.GetPortValue("async") is bool ab && ab;
        var restart = ctx.GetPortValue("restart") is bool rb && rb;
        double chunkRaw = 4 << 20;
        if (ctx.GetPortValue("chunk_bytes") is double cv) chunkRaw = cv;

        if (string.IsNullOrEmpty(searchText))
        {
            ctx.Logger.LogError("[FindStringOccurrences] search_text is required");
            SetEmpty(ctx);
            return;
        }

        var baseAddr = string.IsNullOrEmpty(moduleName) ? GetModuleHandleA(null) : GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[FindStringOccurrences] module not found: '{moduleName}'");
            SetEmpty(ctx);
            return;
        }

        long scanStartRva = ParseHex(scanStartStr);
        long scanSize = (long)scanSizeD;
        if (scanSize <= 0)
        {
            scanSize = ReadSizeOfImage(baseAddr) - scanStartRva;
            if (scanSize <= 0)
            {
                ctx.Logger.LogError("[FindStringOccurrences] failed to auto-detect SizeOfImage; specify scan_size explicitly");
                SetEmpty(ctx);
                return;
            }
        }

        var pattern = wide ? Encoding.Unicode.GetBytes(searchText) : Encoding.ASCII.GetBytes(searchText);

        var outcome = NgolChunkedScan.Run(ctx, new NgolChunkedScan.Request
        {
            Name = "FindString",
            KeySuffix = moduleName + "|" + (wide ? "w" : "a") + "|" + searchText,
            BaseAddress = baseAddr,
            StartRva = scanStartRva,
            Size = scanSize,
            MaxHits = maxHits,
            Async = async,
            Restart = restart,
            ChunkBytes = (int)Math.Max(4096, chunkRaw),
            // 境界をまたぐ一致を落とさないため、探す文字列の長さぶん重ねて読む。
            Overlap = pattern.Length - 1,
            Scan = (buf, len, chunkStart, usable, sink) => Match(buf, len, chunkStart, usable, sink, pattern),
        });

        NgolChunkedScan.Emit(ctx, outcome, "hits", "hit_count");

        var where = string.IsNullOrEmpty(moduleName) ? "<main exe>" : moduleName;
        if (!outcome.Done)
        {
            ctx.Logger.LogInfo($"[FindStringOccurrences] '{searchText}' in '{where}': background scan running, reached 0x{outcome.ProgressRva:x}");
            return;
        }

        ctx.Logger.LogInfo($"[FindStringOccurrences] '{searchText}' (wide={wide}) in '{where}': {outcome.HitCount} hit(s) across 0x{outcome.ScannedBytes:x} of 0x{scanSize:x} requested bytes from 0x{scanStartRva:x}"
            + NgolChunkedScan.DescribeLimits(outcome, maxHits));
    }

    // ここは配列のまま走査する。ホストによっては ReadOnlySpan<T> が
    //    複数のアセンブリから見えていて、参照が曖昧になりコンパイルが通らない。
    static long Match(byte[] buf, int len, long chunkStartRva, int usable, NgolChunkedScan.Sink sink, byte[] pattern)
    {
        for (int offset = 0; offset < usable && offset + pattern.Length <= len; offset++)
        {
            int k = 0;
            while (k < pattern.Length && buf[offset + k] == pattern[k]) k++;
            if (k == pattern.Length && !sink.Add(chunkStartRva + offset, $"\"0x{(chunkStartRva + offset):x}\"")) break;
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

    static long ReadSizeOfImage(IntPtr baseAddr)
    {
        // DOS header: e_lfanew at offset 0x3C
        var e_lfanew = Marshal.ReadInt32(baseAddr, 0x3C);
        // NT header: Signature(4) + FileHeader(20) -> OptionalHeader starts at e_lfanew+24
        // OptionalHeader(PE32+): SizeOfImage is at offset 56 within OptionalHeader
        var sizeOfImageOffset = e_lfanew + 24 + 56;
        return (uint)Marshal.ReadInt32(baseAddr, sizeOfImageOffset);
    }

    static long ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }
}
