using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// AOB（Array of Bytes）パターンスキャン。'??'/'?' をワイルドカードとして扱う。
/// 対象が更新されてRVAがずれても、バイトパターンなら同じ関数を見つけ直せる
/// （ハードコードRVAより保守性が高い）。module パラメータで任意のモジュールを対象にできる。
/// </summary>
[NodeType("ngol.code.aob_scan", "Code", "AOB Scan",
    Version = "1.1.2",
    Description = "Scan a module for a byte pattern (wildcards as '??'). More update-resilient than hardcoded RVAs. "
                + "Set async=true to scan in the background and read the result on a second run.")]
[NodePort("module",      PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("pattern",     PortDirection.Input,  "string", IsRequired = true, Description = "Space-separated hex bytes, '??' as wildcard (e.g. '48 8B 05 ?? ?? ?? ??')")]
[NodePort("search_size", PortDirection.Input,  "number", Description = "Bytes to scan from module base (default: 0x2000000). Scanning stops early at the first unreadable page")]
[NodePort("max_matches", PortDirection.Input,  "number", Description = "Max matches to return (default: 10). Reaching it sets truncated=true - there are more matches past that point")]
[NodePort("async",       PortDirection.Input,  "boolean", Description = "true = start the scan in the background and return immediately with done=false. Run again with the same inputs to read the result (default: false)")]
[NodePort("chunk_bytes", PortDirection.Input,  "number", Description = "Bytes scanned per host update when async=true (default: 4194304 = 4MB). Smaller keeps the host smoother, down to 4096 - anything below that is raised to it")]
[NodePort("restart",     PortDirection.Input,  "boolean", Description = "true = discard a finished background scan and run it again (default: false). Without it, re-running returns the same result")]
[NodePort("matches_rva", PortDirection.Output, "string", Description = "JSON array of matched RVA hex strings")]
[NodePort("match_count", PortDirection.Output, "number", Description = "How many matches were found. Same length as matches_rva")]
[NodePort("scanned_bytes", PortDirection.Output, "number", Description = "Bytes actually scanned. Less than search_size means the readable range ended there")]
[NodePort("truncated",   PortDirection.Output, "boolean", Description = "true = stopped at max_matches, so more matches exist beyond what is listed")]
[NodePort("done",        PortDirection.Output, "boolean", Description = "false only while an async scan is still running")]
[NodePort("progress_rva", PortDirection.Output, "string", Description = "RVA the scan has reached")]
[NodePort("self_dropped", PortDirection.Output, "number", Description = "Hits discarded because they pointed into this scan's own working buffer. NGOL runs inside the target process, so a scan can find its own copy of the data. Non-zero means the range overlaps the scanner's memory - run it twice and trust only what appears both times")]
public sealed class AobScanNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string moduleName);

    public void Execute(IExecutionContext ctx)
    {
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
        var patternStr = ctx.GetPortValue("pattern") as string ?? ctx.GetParam<string>("pattern") ?? "";

        double sizeRaw = 0x2000000;
        if (ctx.GetPortValue("search_size") is double sv) sizeRaw = sv;
        var searchSize = (long)Math.Max(4096, Math.Min(sizeRaw, 0x8000000));

        double maxRaw = 10;
        if (ctx.GetPortValue("max_matches") is double mv) maxRaw = mv;
        var maxMatches = Math.Max(1, (int)maxRaw);

        var async = ctx.GetPortValue("async") is bool ab && ab;
        var restart = ctx.GetPortValue("restart") is bool rb && rb;
        double chunkRaw = 4 << 20;
        if (ctx.GetPortValue("chunk_bytes") is double cv) chunkRaw = cv;

        if (string.IsNullOrWhiteSpace(patternStr))
        {
            ctx.Logger.LogWarning("[AobScan] pattern is empty");
            SetEmpty(ctx);
            return;
        }

        List<byte?> pattern;
        try { pattern = ParsePattern(patternStr); }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[AobScan] invalid pattern: {ex.Message}");
            SetEmpty(ctx);
            return;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[AobScan] module not found: {moduleName}");
            SetEmpty(ctx);
            return;
        }

        var outcome = NgolChunkedScan.Run(ctx, new NgolChunkedScan.Request
        {
            Name = "AobScan",
            KeySuffix = moduleName + "|" + patternStr,
            BaseAddress = baseAddr,
            StartRva = 0,
            Size = searchSize,
            MaxHits = maxMatches,
            Async = async,
            Restart = restart,
            ChunkBytes = (int)Math.Max(4096, chunkRaw),
            // 境界をまたぐ一致を落とさないため、パターンの長さぶん手前まで重ねて読む。
            Overlap = pattern.Count - 1,
            Scan = (buf, len, chunkStartRva, usable, sink) => Match(buf, len, chunkStartRva, usable, sink, pattern),
        });

        NgolChunkedScan.Emit(ctx, outcome, "matches_rva", "match_count");

        if (!outcome.Done)
        {
            ctx.Logger.LogInfo($"[AobScan] {moduleName}: background scan running, reached 0x{outcome.ProgressRva:x}");
            return;
        }

        ctx.Logger.LogInfo($"[AobScan] {moduleName}: {outcome.HitCount} match(es) in {outcome.ScannedBytes} of {searchSize} requested bytes"
            + NgolChunkedScan.DescribeLimits(outcome, maxMatches));
    }

    static long Match(byte[] buf, int len, long chunkStartRva, int usable, NgolChunkedScan.Sink sink, List<byte?> pattern)
    {
        var patLen = pattern.Count;
        for (int i = 0; i < usable && i + patLen <= len; i++)
        {
            var ok = true;
            for (int j = 0; j < patLen; j++)
            {
                var pb = pattern[j];
                if (pb.HasValue && buf[i + j] != pb.Value) { ok = false; break; }
            }
            if (ok && !sink.Add(chunkStartRva + i, $"\"0x{(chunkStartRva + i):x}\"")) break;
        }
        return usable;
    }

    static List<byte?> ParsePattern(string s)
    {
        var tokens = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<byte?>(tokens.Length);
        foreach (var t in tokens)
        {
            if (t == "??" || t == "?" || t == "**")
                result.Add(null);
            else
                result.Add(Convert.ToByte(t, 16));
        }
        if (result.Count == 0) throw new ArgumentException("empty pattern");
        return result;
    }

    static void SetEmpty(IExecutionContext ctx)
    {
        ctx.SetPortValue("matches_rva", "[]");
        ctx.SetPortValue("match_count", 0.0);
        ctx.SetPortValue("scanned_bytes", 0.0);
        ctx.SetPortValue("truncated", false);
        ctx.SetPortValue("done", true);
        ctx.SetPortValue("progress_rva", "0x0");
        ctx.SetPortValue("self_dropped", 0.0);
    }
}
