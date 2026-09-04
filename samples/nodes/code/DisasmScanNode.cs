using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// RVA リストを一括逆アセンブルし、フィルタ条件にマッチした関数のみ返す。
/// DisasmNode の call_targets 出力を rva_list に接続して「芋づる絞り込み」に使う。
///
/// フィルタ構文（カンマ区切りで OR）:
///   offset:4c       -> [reg+4Ch] のようなオフセットアクセスを含む
///   calls:0x12340   -> 指定 RVA への call を含む
///   mnemonic:cmp    -> 指定ニーモニックで始まる命令を含む
///   text:foo        -> 逆アセンブルテキストに部分一致（汎用フォールバック）
///
/// 診断:
///   filter に "debug" を追加すると各 RVA のパターン一致詳細をログ出力
///   例: "offset:4c,debug"
///
/// 制約:
///   線形スキャンのため最初の ret/int3 で停止する。複数出口を持つ関数は全体を解析できない場合がある。
///   stop_at_ret オプションなし（stop_at_ret=false による回避不可）。
///   Windows x64 専用（GetModuleHandleA 使用）。
/// </summary>
[NodeType("ngol.code.disasm_scan", "Code", "Disasm Scan",
    Version = "1.0.1",
    Description = "Scan multiple RVAs with iced and return only those matching a filter. Pipe call_targets from DisasmNode to rva_list. Add 'debug' to filter for per-RVA diagnostics.")]
[NodePort("rva_list",      PortDirection.Input,  "string", Description = "JSON array of RVA hex strings (DisasmNode call_targets output)")]
[NodePort("filter",        PortDirection.Input,  "string", Description = "Filter: offset:4c | calls:0x12340 | mnemonic:cmp | text:foo  (comma=OR, add 'debug' for diagnostics)")]
[NodePort("byte_count",    PortDirection.Input,  "number", Description = "Max bytes to read per RVA (default: 4096, max: 65536). Stops early at ret/int3 so short functions don't over-read.")]
[NodePort("module",        PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("matched_rvas",  PortDirection.Output, "string", Description = "JSON array of matched RVA hex strings")]
[NodePort("matched_lines", PortDirection.Output, "string", Description = "Full disasm text for matched RVAs")]
[NodePort("summary",       PortDirection.Output, "string", Description = "One line per scanned RVA: RVA [HIT/---] reason")]
[NodePort("match_count",   PortDirection.Output, "number", Description = "Number of matched RVAs")]
[NodePort("scan_count",    PortDirection.Output, "number", Description = "Total RVAs scanned")]
[NodePort("scanned_bytes", PortDirection.Output, "number", Description = "Bytes actually read across all RVAs. An RVA whose memory is unreadable contributes 0 and is reported as 'not readable' in summary")]
public sealed class DisasmScanNode : INode
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
        var rvaListStr = ctx.GetPortValue("rva_list") as string
                      ?? ctx.GetParam<string>("rva_list") ?? "[]";
        var filterRaw  = ctx.GetPortValue("filter") as string
                      ?? ctx.GetParam<string>("filter") ?? "";

        double rawBytes = 4096.0;
        if (ctx.GetPortValue("byte_count") is double dv) rawBytes = dv;
        var byteCount = Math.Max(16, Math.Min((int)rawBytes, 65536));

        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));

        // "debug" キーワードをフィルタリストから分離
        bool debugMode = false;
        var filterParts = new List<string>();
        foreach (var p in filterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim();
            if (t.Equals("debug", StringComparison.OrdinalIgnoreCase))
                debugMode = true;
            else
                filterParts.Add(t);
        }
        var filters = filterParts.ToArray();

        var rvas = ParseRvaList(rvaListStr);
        if (rvas.Count == 0)
        {
            ctx.Logger.LogWarning("[DisasmScan] rva_list is empty or unparsable");
            SetEmpty(ctx);
            return;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogError($"[DisasmScan] module not found: {moduleName}");
            SetEmpty(ctx);
            return;
        }

        if (debugMode)
        {
            ctx.Logger.LogInfo($"[DisasmScan:debug] filters=[{string.Join("|", filters)}] byteCount={byteCount} rvas={rvas.Count}");
            // 各フィルタのパターンを事前ログ
            foreach (var f in filters)
            {
                if (f.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                {
                    var hexStr = f.Substring(7).Trim().TrimStart('0', 'x');
                    if (long.TryParse(hexStr, NumberStyles.HexNumber, null, out var off))
                        ctx.Logger.LogInfo($"[DisasmScan:debug]   offset filter pattern = '+{off:X}h]'");
                }
            }
        }

        var matchedRvas  = new List<string>();
        var matchedLines = new StringBuilder();
        var summary      = new StringBuilder();

        long scannedBytes = 0;

        foreach (var rva in rvas)
        {
            string callTargets;
            int scannedHere;
            var text = DisasmOne(baseAddr, rva, byteCount, out callTargets, out scannedHere);
            scannedBytes += scannedHere;

            string hitReason = null;
            bool hit = filters.Length == 0;
            foreach (var f in filters)
            {
                if (MatchFilter(f, text, callTargets, out var reason))
                {
                    hit = true;
                    hitReason = reason;
                    break;
                }
            }

            if (debugMode)
            {
                ctx.Logger.LogInfo($"[DisasmScan:debug] 0x{rva:x} textLen={text.Length} calls={callTargets} -> {(hit ? $"HIT ({hitReason})" : "---")}");
                // offset フィルタの場合、実際のパターン検索結果を出す
                foreach (var f in filters)
                {
                    if (f.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                    {
                        var hexStr = f.Substring(7).Trim().TrimStart('0', 'x');
                        if (long.TryParse(hexStr, NumberStyles.HexNumber, null, out var off))
                        {
                            var pat = $"+{off:X}h]";
                            var idx = text.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
                            ctx.Logger.LogInfo($"[DisasmScan:debug]   search '{pat}' -> {(idx >= 0 ? $"found at char {idx}" : "not found")} (text has {text.Length} chars)");
                        }
                    }
                }
            }

            var label = hit ? $"HIT ({hitReason ?? "match"})" : "---";
            summary.AppendLine($"0x{rva:x}  {label}");

            if (hit)
            {
                matchedRvas.Add($"\"0x{rva:x}\"");
                matchedLines.Append($"=== 0x{rva:x} ===\n{text}\n");
            }
        }

        ctx.SetPortValue("matched_rvas",  $"[{string.Join(",", matchedRvas)}]");
        ctx.SetPortValue("matched_lines", matchedLines.ToString());
        ctx.SetPortValue("summary",       summary.ToString());
        ctx.SetPortValue("match_count",   (double)matchedRvas.Count);
        ctx.SetPortValue("scan_count",    (double)rvas.Count);
        ctx.SetPortValue("scanned_bytes", (double)scannedBytes);
        ctx.Logger.LogInfo($"[DisasmScan] scanned={rvas.Count} matched={matchedRvas.Count} bytes={scannedBytes} filter={filterRaw}");
    }

    // ---- helpers ----

    static void SetEmpty(IExecutionContext ctx)
    {
        ctx.SetPortValue("matched_rvas",  "[]");
        ctx.SetPortValue("matched_lines", "");
        ctx.SetPortValue("summary",       "");
        ctx.SetPortValue("match_count",   0.0);
        ctx.SetPortValue("scan_count",    0.0);
        ctx.SetPortValue("scanned_bytes", 0.0);
    }

    static List<long> ParseRvaList(string json)
    {
        var result = new List<long>();
        var s = json.Trim().Trim('[', ']');
        foreach (var part in s.Split(','))
        {
            var t = part.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (long.TryParse(t, NumberStyles.HexNumber, null, out var v))
                result.Add(v);
        }
        return result;
    }

    static string DisasmOne(IntPtr baseAddr, long rva, int byteCount, out string callTargets, out int scannedBytes)
    {
        callTargets = "";
        scannedBytes = 0;
        try
        {
            var ptr   = new IntPtr(baseAddr.ToInt64() + rva);
            var bytes = new byte[byteCount];
            var readable = NgolSafeMemory.Read(ptr, bytes, 0, byteCount);
            scannedBytes = readable;
            if (readable <= 0) return $"RVA:0x{rva:x} not readable";

            var reader    = new ByteArrayCodeReader(bytes);
            var decoder   = Iced.Intel.Decoder.Create(64, reader);
            decoder.IP    = (ulong)ptr.ToInt64();

            var formatter = new NasmFormatter();
            var fmtOut    = new BufOutput();
            var sb        = new StringBuilder();
            var calls     = new List<string>();
            var endIP     = decoder.IP + (ulong)readable;

            while (decoder.IP < endIP)
            {
                var instr = decoder.Decode();
                if (instr.Code == Code.INVALID) break;

                formatter.Format(instr, fmtOut);
                var rvaNow = (long)instr.IP - baseAddr.ToInt64();
                sb.AppendLine($"RVA:0x{rvaNow:x}  {fmtOut.Flush()}");

                if (instr.Op0Kind == OpKind.NearBranch64)
                {
                    var fc = instr.FlowControl;
                    if (fc == FlowControl.Call || fc == FlowControl.IndirectCall)
                        calls.Add($"0x{(long)instr.NearBranch64 - baseAddr.ToInt64():x}");
                }

                // 関数終端で停止（次の関数に踏み込まない）
                if (instr.FlowControl == FlowControl.Return || instr.Code == Code.Int3)
                    break;
            }

            callTargets = string.Join(",", calls);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            callTargets = "";
            return $"[ERROR: {ex.Message}]";
        }
    }

    static bool MatchFilter(string filter, string text, string callTargets, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(filter)) { reason = "empty filter"; return true; }

        if (filter.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
        {
            var hexStr = filter.Substring(7).Trim();
            if (hexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hexStr = hexStr.Substring(2);
            if (long.TryParse(hexStr, NumberStyles.HexNumber, null, out var offset))
            {
                var pattern = $"+{offset:X}h]";
                var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // パターンを含む行を reason に入れる
                    var lineStart = text.LastIndexOf('\n', idx) + 1;
                    var lineEnd   = text.IndexOf('\n', idx);
                    reason = lineEnd > 0
                        ? text.Substring(lineStart, lineEnd - lineStart).Trim()
                        : text.Substring(lineStart).Trim();
                    return true;
                }
            }
            return false;
        }

        if (filter.StartsWith("calls:", StringComparison.OrdinalIgnoreCase))
        {
            var target = filter.Substring(6).Trim();
            if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                target = target.Substring(2);
            if (callTargets.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = $"calls:0x{target}";
                return true;
            }
            return false;
        }

        if (filter.StartsWith("mnemonic:", StringComparison.OrdinalIgnoreCase))
        {
            var mnem = filter.Substring(9).Trim();
            foreach (var line in text.Split('\n'))
            {
                var idx = line.IndexOf("  ", StringComparison.Ordinal);
                if (idx < 0) continue;
                var rest = line.Substring(idx + 2).TrimStart();
                if (rest.StartsWith(mnem + " ", StringComparison.OrdinalIgnoreCase)
                 || rest.StartsWith(mnem + "\r", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(rest.TrimEnd(), mnem, StringComparison.OrdinalIgnoreCase))
                {
                    reason = line.Trim();
                    return true;
                }
            }
            return false;
        }

        if (filter.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
        {
            var substr = filter.Substring(5);
            var idx = text.IndexOf(substr, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var lineStart = text.LastIndexOf('\n', idx) + 1;
                var lineEnd   = text.IndexOf('\n', idx);
                reason = lineEnd > 0
                    ? text.Substring(lineStart, lineEnd - lineStart).Trim()
                    : text.Substring(lineStart).Trim();
                return true;
            }
            return false;
        }

        // prefix なし: テキスト部分マッチ
        {
            var idx = text.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                reason = $"text match: '{filter}'";
                return true;
            }
        }
        return false;
    }
}
