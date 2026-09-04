using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定モジュールの.textセクション全体を線形デコードし、RIP相対オペランド
/// (LEA/MOV/CALL/JMP等)の計算先アドレスがtarget_rvaと一致する命令を全て列挙する。
/// DisasmNodeの「単一RVAを渡された分だけ逆アセンブル」を拡張し、
/// 「あるRVAを参照している命令をコードセクション全体から総当たりで探す」
/// （静的解析ツールの相互参照に相当）をインプロセスで行う。
///
/// 出力:
///   hits      -> {instrRva, targetRva, mnemonic} のJSON配列
///   hit_count -> hits の件数
///
/// 主な使い方:
///   文字列リテラルやグローバル変数のRVAが分かっていて、それを参照しているコード
///   （＝呼び出し元関数）を静的解析ツールなしで一発特定したい場合に使う。
///   同じモジュールに対して複数のtarget_rvaを繰り返し調べたい場合は、
///   毎回全体スキャンし直す本ノードより ngol.code.xref_index_build
///   （バックグラウンドで一度だけ全体をインデックス化）+ ngol.code.xref_lookup
///   （インデックスを即座に逆引き）の組み合わせの方が効率的。
///
/// 制約:
///   RIP相対メモリオペランドと直接分岐(NearBranch64)のみ検出。デコンパイラの
///   レジスタ値伝播由来の間接参照（デコンパイラが追加で検出するもの）は対象外。
///   命令をデコードして探すため、データセクションからの参照は構造上見つからない
///   （例外テーブル(.pdata)・vtable・関数ポインタ表・リロケーションなど）。
///   「参照が0件」は「コードから参照されていない」であって「どこからも参照されていない」ではない。
///   Windows x64専用（GetModuleHandleA使用）。
/// </summary>
[NodeType("ngol.code.xref_find", "Code", "Find Xrefs",
    Version = "1.1.3",
    Description = "Scan a module's code range for instructions whose RIP-relative operand or direct branch resolves to target_rva. Instruction-level only: references from data sections (exception tables, vtables, function-pointer arrays) are out of scope by construction. "
                + "Set async=true to scan in the background and read the result on a second run.")]
[NodePort("target_rva",     PortDirection.Input,  "string",  Description = "Target RVA hex (e.g. '0x9df7a0')")]
[NodePort("scan_start_rva", PortDirection.Input,  "string",  Description = "Start RVA of scan range (default: '0x1000')")]
[NodePort("scan_size",      PortDirection.Input,  "number",  Description = "Bytes to scan from scan_start_rva (default: 0x9ce751, i.e. typical .text size)")]
[NodePort("module",         PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module")]
[NodePort("max_hits",       PortDirection.Input,  "number",  Description = "Max references to return (default: 50). Each entry carries a mnemonic, so this is much smaller than the other scan nodes - a larger value gets cut off in transit. Reaching it sets truncated=true. For the full list, build an index with ngol.code.xref_index_build and read it with ngol.code.xref_lookup")]
[NodePort("async",          PortDirection.Input,  "boolean", Description = "true = start the scan in the background and return immediately with done=false. Run again with the same inputs to read the result (default: false)")]
[NodePort("chunk_bytes",    PortDirection.Input,  "number",  Description = "Bytes decoded per host update when async=true (default: 262144 = 256KB). Decoding costs more per byte than a byte compare, so this default is smaller than the other scan nodes. The floor is 4096 - anything below that is raised to it")]
[NodePort("restart",        PortDirection.Input,  "boolean", Description = "true = discard a finished background scan and run it again (default: false). Without it, re-running returns the same result")]
[NodePort("hits",           PortDirection.Output, "string",  Description = "JSON array of {instrRva, targetRva, mnemonic}")]
[NodePort("hit_count",      PortDirection.Output, "number",  Description = "Number of matching instructions")]
[NodePort("scanned_bytes",  PortDirection.Output, "number",  Description = "Bytes actually scanned. Less than scan_size means the readable range ended there")]
[NodePort("truncated",      PortDirection.Output, "boolean", Description = "true = stopped at max_hits, so more references exist beyond what is listed")]
[NodePort("done",           PortDirection.Output, "boolean", Description = "false only while an async scan is still running")]
[NodePort("progress_rva",   PortDirection.Output, "string",  Description = "RVA the scan has reached")]
[NodePort("self_dropped", PortDirection.Output, "number", Description = "Hits discarded because they pointed into this scan's own working buffer. NGOL runs inside the target process, so a scan can find its own copy of the data. Non-zero means the range overlaps the scanner's memory - run it twice and trust only what appears both times")]
public sealed class FindXrefsNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    sealed class BufOutput : FormatterOutput
    {
        readonly StringBuilder _sb = new StringBuilder();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Flush() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    public void Execute(IExecutionContext ctx)
    {
        var targetRvaStr = ctx.GetPortValue("target_rva") as string ?? "";
        var scanStartStr = (ctx.GetPortValue("scan_start_rva") as string) ?? "0x1000";
        double scanSizeD = 0x9ce751;
        if (ctx.GetPortValue("scan_size") is double sd) scanSizeD = sd;
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));

        // 1 件が約 110 バイトあるので、他の走査ノードより小さくする。
        // 大きくすると出力が経路の上限で切られ、件数すら読めなくなる（実測）。
        double maxRaw = 50;
        if (ctx.GetPortValue("max_hits") is double mv) maxRaw = mv;
        var maxHits = Math.Max(1, (int)maxRaw);

        var async = ctx.GetPortValue("async") is bool ab && ab;
        var restart = ctx.GetPortValue("restart") is bool rb && rb;
        // デコードは 1 バイトあたりが重いので、既定のチャンクは他の走査ノードより小さい。
        double chunkRaw = 256 * 1024;
        if (ctx.GetPortValue("chunk_bytes") is double cv) chunkRaw = cv;

        long targetRva, scanStartRva;
        try
        {
            targetRva = ParseHex(targetRvaStr);
            scanStartRva = ParseHex(scanStartStr);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[FindXrefs] parse error: {ex.Message}");
            SetEmpty(ctx);
            return;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[FindXrefs] module not found: {moduleName}");
            SetEmpty(ctx);
            return;
        }

        var scanSize = (long)scanSizeD;
        long absoluteTargetAddr = baseAddr.ToInt64() + targetRva;
        long moduleBase = baseAddr.ToInt64();

        var outcome = NgolChunkedScan.Run(ctx, new NgolChunkedScan.Request
        {
            Name = "FindXrefs",
            KeySuffix = moduleName + "|" + targetRva.ToString("x"),
            BaseAddress = baseAddr,
            StartRva = scanStartRva,
            Size = scanSize,
            MaxHits = maxHits,
            Async = async,
            Restart = restart,
            ChunkBytes = (int)Math.Max(4096, chunkRaw),
            // x64 命令の最大長は 15 バイト。チャンクの切れ目をまたぐ命令が途中で切れて
            // 誤ってデコードされないよう、末尾に少し余分に読む。
            Overlap = 16,
            Scan = (buf, len, chunkStart, usable, sink) =>
                Decode(buf, len, chunkStart, usable, sink, moduleBase, targetRva, absoluteTargetAddr),
        });

        NgolChunkedScan.Emit(ctx, outcome, "hits", "hit_count");

        if (!outcome.Done)
        {
            ctx.Logger.LogInfo($"[FindXrefs] target_rva=0x{targetRva:x}: background scan running, reached 0x{outcome.ProgressRva:x}");
            return;
        }

        ctx.Logger.LogInfo($"[FindXrefs] target_rva=0x{targetRva:x} scanned 0x{outcome.ScannedBytes:x} of 0x{scanSize:x} requested bytes from 0x{scanStartRva:x}, {outcome.HitCount} hit(s)"
            + NgolChunkedScan.DescribeLimits(outcome, maxHits));
    }

    /// <summary>
    /// 1 チャンクぶんをデコードして対象への参照を拾う。
    /// 戻り値はデコーダが実際に到達した位置。命令長が可変なので、次のチャンクは
    /// 名目の切れ目ではなくここから始めないと命令の途中から読むことになる。
    /// </summary>
    static long Decode(byte[] buf, int len, long chunkStartRva, int usable, NgolChunkedScan.Sink sink,
        long moduleBase, long targetRva, long absoluteTargetAddr)
    {
        var reader = new ByteArrayCodeReader(buf, 0, len);
        var decoder = Iced.Intel.Decoder.Create(64, reader);
        decoder.IP = (ulong)(moduleBase + chunkStartRva);
        var endIP = decoder.IP + (ulong)usable;

        var formatter = new NasmFormatter();
        var fmtOut = new BufOutput();

        while (decoder.IP < endIP)
        {
            var instr = decoder.Decode();
            if (instr.Code == Code.INVALID) continue;

            var matched = false;

            // RIP相対メモリオペランドを持つ命令(LEA/MOV/CMP等)をチェック
            for (int i = 0; i < instr.OpCount && !matched; i++)
            {
                if (instr.GetOpKind(i) == OpKind.Memory && instr.IsIPRelativeMemoryOperand
                    && (long)instr.IPRelativeMemoryAddress == absoluteTargetAddr)
                    matched = true;
            }

            // NearBranch (CALL/JMP rel32)も対象RVAならヒットとする
            if (!matched && instr.Op0Kind == OpKind.NearBranch64
                && (long)instr.NearBranch64 - moduleBase == targetRva)
                matched = true;

            if (matched)
            {
                var instrRva = (long)instr.IP - moduleBase;
                formatter.Format(instr, fmtOut);
                var mnem = fmtOut.Flush();
                if (!sink.Add(instrRva, $"{{\"instrRva\":\"0x{instrRva:x}\",\"targetRva\":\"0x{targetRva:x}\",\"mnemonic\":{EscapeJson(mnem)}}}"))
                    break;
            }
        }

        return (long)decoder.IP - moduleBase - chunkStartRva;
    }

    static long ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }

    static string EscapeJson(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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
