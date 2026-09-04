using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定 RVA のネイティブコードを逆アセンブルし、命令テキスト・CALL先・分岐先を返す。
/// 同じプロセスの中で解く。module パラメータでロード済みの任意のモジュールを対象にできる。
///
/// 出力:
///   text           -> 逆アセンブルテキスト（1行1命令、RVA ＋ 生バイト ＋ ニーモニック）
///   call_targets   -> CALL 先 RVA の JSON 配列（DisasmScanNode の rva_list に接続可）
///   branch_targets -> 分岐先 RVA の JSON 配列
///   terminated_by  -> 停止理由: ret | int3 | limit | invalid | unreadable
///
/// 生バイトを出す理由（show_bytes、既定 true）:
///   ・そのバイト列がそのまま ngol.code.aob_scan の検索対象になる。
///     探すノードはあるのに探すバイト列を作る手段が無い、という欠けを埋める。
///   ・ディスク上のファイルの中身と実メモリを突き合わせられる。
///     マップ後に書き換えられた箇所は、こうしないと見つからない。
///   ・並んだバイト数がそのまま命令長になる。
///     ngol.hook.patch_bytes は「命令の境界を確認してから使う」ことを前提にしている。
///   ・ngol.hook.safety_check が SAFE を返した番地が本当にコードなのかを目で確かめられる
///     （あちらは LOCK と RIP 相対しか見ておらず、コードかどうかは判定しない）。
///
/// 主な使い方:
///   call_targets を DisasmScanNode の rva_list に接続して「芋づる解析」を行う。
///   auto_extend=true（デフォルト）: ret 未到達なら 65536 バイトで自動再試行する。
///
/// 制約:
///   線形スキャンのため最初の ret/int3 で停止する。複数出口を持つ関数は全体を解析できない場合がある。
///   stop_at_ret=false で回避できるが、次の関数のコードに流れ込む可能性がある。
///   Windows x64 専用（GetModuleHandleA 使用）。
/// </summary>
[NodeType("ngol.code.disasm", "Code", "Disasm",
    Version = "1.1.1",
    Description = "Disassemble native code at the given RVA. Works on any loaded module. Each line carries the "
      + "instruction's raw bytes as well as its mnemonic, so the listing doubles as a byte dump: paste the bytes into "
      + "ngol.code.aob_scan to find the same code after an update, compare them against the file on disk to spot code "
      + "that was modified after loading, and read off instruction lengths before writing with ngol.hook.patch_bytes.")]
[NodePort("rva",               PortDirection.Input,  "string",  Description = "RVA hex (e.g. '0x123456')")]
[NodePort("byte_count",        PortDirection.Input,  "number",  Description = "Bytes to read (default: 256, max: 65536). If auto_extend=true, 65536 is used automatically when ret not found.")]
[NodePort("stop_at_ret",       PortDirection.Input,  "boolean", Description = "Stop at ret/int3 to avoid reading into next function (default: true)")]
[NodePort("auto_extend",       PortDirection.Input,  "boolean", Description = "If ret not found within byte_count, retry once with 65536 bytes (default: true)")]
[NodePort("module",            PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("absolute_address_hex", PortDirection.Input, "string", Description = "Pre-resolved absolute address. Takes priority over module/rva when non-empty. Needed for code that belongs to no module: JIT-compiled methods, trampolines and stubs allocated on the heap. Listing addresses are then absolute")]
[NodePort("show_bytes",        PortDirection.Input,  "boolean", Description = "Include each instruction's raw bytes in the listing (default: true). The byte count is the instruction length. Turn off for very large scans to keep the output small")]
[NodePort("max_lines",         PortDirection.Input,  "number",  Description = "Max listing lines to return (default: 200). Decoding always runs to the end regardless - only the listing is cut, so call_targets and branch_targets stay complete. Raise it to read more of the listing, or turn show_bytes off to fit more lines")]
[NodePort("text",              PortDirection.Output, "string",  Description = "Disassembly listing: RVA + raw bytes + mnemonic per line. Cut at max_lines")]
[NodePort("call_targets",      PortDirection.Output, "string",  Description = "CALL target RVAs as JSON array string")]
[NodePort("branch_targets",    PortDirection.Output, "string",  Description = "Branch target RVAs as JSON array string")]
[NodePort("instruction_count", PortDirection.Output, "number",  Description = "Number of instructions decoded")]
[NodePort("terminated_by",     PortDirection.Output, "string",  Description = "Why decoding stopped: ret | int3 | limit | invalid | unreadable")]
[NodePort("scanned_bytes",     PortDirection.Output, "number",  Description = "Bytes actually read and fed to the decoder. Less than byte_count means the readable range ended there")]
[NodePort("text_truncated",    PortDirection.Output, "boolean", Description = "true = the listing was cut at max_lines. The instructions past that point were still decoded, so call_targets, branch_targets and instruction_count cover the whole range")]
public sealed class DisasmNode : INode
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
        var rvaStr = ctx.GetPortValue("rva") as string ?? ctx.GetParam<string>("rva") ?? "";

        double rawBytes = 256.0;
        if (ctx.GetPortValue("byte_count") is double dv) rawBytes = dv;
        var byteCount = Math.Max(1, Math.Min((int)rawBytes, 65536));

        bool stopAtRet  = ReadBool(ctx, "stop_at_ret",  true);
        bool autoExtend = ReadBool(ctx, "auto_extend",  true);
        bool showBytes  = ReadBool(ctx, "show_bytes",   true);

        // 一覧が長いと出力ごと経路の上限で切られ、呼び出し先の一覧まで失われる。
        // ここで区切っておけば、切れるのは一覧だけで済む。
        double rawLines = 200.0;
        if (ctx.GetPortValue("max_lines") is double lv) rawLines = lv;
        var maxLines = Math.Max(1, (int)rawLines);

        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
        var absoluteStr = (ctx.GetPortValue("absolute_address_hex") as string
                           ?? ctx.GetParam<string>("absolute_address_hex") ?? "").Trim();
        var useAbsolute = absoluteStr.Length > 0;

        if (!useAbsolute && string.IsNullOrWhiteSpace(rvaStr))
        {
            ctx.Logger.LogWarning("[Disasm] rva is empty (and no absolute_address_hex given)");
            SetEmpty(ctx);
            return;
        }

        try
        {
            // どのモジュールにも属さないコード（実行時に生成されたもの・踏み台）は
            // モジュール名では辿り着けない。その場合は番地をそのまま起点にする。
            IntPtr baseAddr;
            long rva;
            if (useAbsolute)
            {
                var a = absoluteStr;
                if (a.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) a = a.Substring(2);
                if (!long.TryParse(a, System.Globalization.NumberStyles.HexNumber, null, out rva))
                {
                    ctx.SetPortValue("text", $"[Disasm] invalid absolute_address_hex: {absoluteStr}");
                    SetEmpty(ctx);
                    return;
                }
                baseAddr = IntPtr.Zero;   // 起点が番地そのものになる。並ぶ番地も絶対値になる
            }
            else
            {
                var s = rvaStr.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                rva = long.Parse(s, System.Globalization.NumberStyles.HexNumber);

                baseAddr = GetModuleHandleA(moduleName);
                if (baseAddr == IntPtr.Zero)
                {
                    ctx.SetPortValue("text", $"[Disasm] module not found: {moduleName}");
                    SetEmpty(ctx);
                    return;
                }
            }

            string termBy;
            string text;
            List<string> calls, branches;
            int count, scanned;
            bool textTruncated;
            DisasmCore(baseAddr, rva, byteCount, stopAtRet, showBytes, maxLines,
                       out text, out calls, out branches, out count, out termBy, out scanned, out textTruncated);

            // auto_extend: ret 未到達で byte_count が上限でない場合、65536 で取り直す
            // 読める範囲がそこで終わっていた場合は取り直しても同じなので伸ばさない。
            if (autoExtend && (termBy == "limit" || termBy == "invalid")
                && byteCount < 65536 && scanned >= byteCount)
            {
                ctx.Logger.LogInfo($"[Disasm] auto_extend: ret not found in {byteCount} bytes, retrying with 65536");
                DisasmCore(baseAddr, rva, 65536, stopAtRet, showBytes, maxLines,
                           out text, out calls, out branches, out count, out termBy, out scanned, out textTruncated);
            }

            ctx.SetPortValue("text",              text);
            ctx.SetPortValue("call_targets",      $"[{string.Join(",", calls)}]");
            ctx.SetPortValue("branch_targets",    $"[{string.Join(",", branches)}]");
            ctx.SetPortValue("instruction_count", (double)count);
            ctx.SetPortValue("terminated_by",     termBy);
            ctx.SetPortValue("scanned_bytes",     (double)scanned);
            ctx.SetPortValue("text_truncated",    textTruncated);
            ctx.Logger.LogInfo($"[Disasm] RVA:0x{rva:x} {count} instr, {calls.Count} calls, terminated_by={termBy}, scanned={scanned}"
                + (textTruncated ? $" -- listing cut at max_lines={maxLines}; call_targets and branch_targets are complete" : ""));
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[Disasm] {ex.Message}");
            ctx.SetPortValue("text", $"Error: {ex.Message}");
            SetEmpty(ctx);
        }
    }

    // ---- helpers ----

    // 7 バイト分（"xx " x 7 - 末尾の空白）。x64 の命令はほとんどここに収まり、
    // 収まらないものは列が伸びるだけで欠けはしない。
    const int BytesColumnWidth = 20;

    static string HexRun(byte[] buf, int offset, int length)
    {
        var sb = new StringBuilder(length * 3);
        for (int i = 0; i < length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(buf[offset + i].ToString("x2"));
        }
        return sb.ToString();
    }

    static void DisasmCore(
        IntPtr baseAddr, long rva, int byteCount, bool stopAtRet, bool showBytes, int maxLines,
        out string text, out List<string> calls, out List<string> branches,
        out int count, out string terminatedBy, out int scannedBytes, out bool textTruncated)
    {
        textTruncated = false;
        terminatedBy = "limit";
        calls    = new List<string>();
        branches = new List<string>();
        count    = 0;

        var targetAddr = new IntPtr(baseAddr.ToInt64() + rva);
        var bytes      = new byte[byteCount];
        // 読める分だけ取る。要求した長さの手前で領域が終わることは普通に起きる。
        var readable   = NgolSafeMemory.Read(targetAddr, bytes, 0, byteCount);
        scannedBytes   = readable;
        if (readable <= 0)
        {
            text = "";
            terminatedBy = "unreadable";
            return;
        }

        var reader    = new ByteArrayCodeReader(bytes);
        var decoder   = Iced.Intel.Decoder.Create(64, reader);
        decoder.IP    = (ulong)targetAddr.ToInt64();

        var formatter = new NasmFormatter();
        var fmtOut    = new BufOutput();
        var sb        = new StringBuilder();
        var endIP     = decoder.IP + (ulong)readable;

        while (decoder.IP < endIP)
        {
            var instr = decoder.Decode();
            if (instr.Code == Code.INVALID) { terminatedBy = "invalid"; break; }

            var rvaNow = (long)instr.IP - baseAddr.ToInt64();

            // 一覧は人が読むもの、呼び出し先は機械が使うもの。
            // 一覧が長いと出力ごと経路の上限で切られ、呼び出し先まで失われる。
            // そこで一覧だけ行数で打ち切り、復号は最後まで続ける。
            // 範囲を狭めて回避することはできない -- 命令の途中から読むと誤って復号する。
            if (count < maxLines)
            {
                formatter.Format(instr, fmtOut);
                if (showBytes)
                {
                    // 並んだバイト数がそのまま命令長になる。逆アセンブラの慣例どおりの体裁。
                    var offset = (int)((long)instr.IP - targetAddr.ToInt64());
                    sb.AppendLine($"RVA:0x{rvaNow:x}  {HexRun(bytes, offset, instr.Length),-BytesColumnWidth}  {fmtOut.Flush()}");
                }
                else
                {
                    sb.AppendLine($"RVA:0x{rvaNow:x}  {fmtOut.Flush()}");
                }
            }
            else if (!textTruncated)
            {
                textTruncated = true;
                sb.AppendLine($"... listing cut at max_lines={maxLines}; decoding continues, so call_targets and branch_targets are complete");
            }
            count++;

            if (instr.Op0Kind == OpKind.NearBranch64)
            {
                var targetRva = (long)instr.NearBranch64 - baseAddr.ToInt64();
                var fc = instr.FlowControl;
                if (fc == FlowControl.Call || fc == FlowControl.IndirectCall)
                    calls.Add($"\"0x{targetRva:x}\"");
                else if (fc == FlowControl.UnconditionalBranch || fc == FlowControl.ConditionalBranch)
                    branches.Add($"\"0x{targetRva:x}\"");
            }

            if (stopAtRet)
            {
                if (instr.FlowControl == FlowControl.Return) { terminatedBy = "ret";  break; }
                if (instr.Code == Code.Int3)                 { terminatedBy = "int3"; break; }
            }
        }

        text = sb.ToString();
    }

    static bool ReadBool(IExecutionContext ctx, string port, bool defaultVal)
    {
        var v = ctx.GetPortValue(port);
        if (v is bool b)   return b;
        if (v is double d) return d != 0;
        if (v is string s) return !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0";
        var p = ctx.GetParam<string>(port);
        if (p != null)     return !p.Equals("false", StringComparison.OrdinalIgnoreCase) && p != "0";
        return defaultVal;
    }

    static void SetEmpty(IExecutionContext ctx)
    {
        ctx.SetPortValue("text",              "");
        ctx.SetPortValue("call_targets",      "[]");
        ctx.SetPortValue("branch_targets",    "[]");
        ctx.SetPortValue("instruction_count", 0.0);
        ctx.SetPortValue("terminated_by",     "");
        ctx.SetPortValue("scanned_bytes",     0.0);
        ctx.SetPortValue("text_truncated",    false);
    }
}

/// <summary>
/// ロード済みモジュールのベースアドレスを取得する。
/// 他のノードが返した絶対アドレスを、モジュール内の位置（RVA）へ直すときに使う。
///
///   RVA = 絶対アドレス - base
///
/// 用途:
///   絶対アドレスしか分からないものを、モジュール内の位置（RVA）に直すとき。
/// </summary>
[NodeType("ngol.code.module_base", "Code", "Module Base",
    Version = "1.0.1",
    Description = "Get the base address of a loaded module. Use to convert absolute addresses to RVA (RVA = abs - base).")]
[NodePort("module",   PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("base_hex", PortDirection.Output, "string", Description = "Base address hex (e.g. '0x7FFD90000000')")]
public sealed class ModuleBaseNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    public void Execute(IExecutionContext ctx)
    {
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[ModuleBase] not found: {moduleName}");
            ctx.SetPortValue("base_hex", "0x0");
            return;
        }

        var hex = $"0x{baseAddr.ToInt64():x}";
        ctx.SetPortValue("base_hex", hex);
        ctx.Logger.LogInfo($"[ModuleBase] {moduleName} base={hex}");
    }
}
