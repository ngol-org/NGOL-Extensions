using NodeGraphModLab.NodeAPI;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;

namespace NodeGraphModLab.CustomNodes;

// フック前の安全チェック。
//
// フックを設置する前に、対象 RVA の命令パターンから危険な形を洗い出す。
//
// 判定ロジック:
//   [DANGER] 先頭 TRAMPOLINE_SIZE バイト内に RIP 相対命令 or LOCK 命令がある
//            -> 退避したコードを別の場所で動かせない（RIP 相対は行き先がずれ、LOCK は割り込めない）
//   [WARN]   先頭 N バイトは安全だが、関数本体に LOCK 命令（GC バリア）がある
//            -> 退避そのものは成立するが、実行時にアクセス違反で落ちることがある
//   [SAFE]   上記に該当しない
//
// 判定と、どういう仕掛け方なら通るかの対応:
//   SAFE  : 先頭を決め打ちの幅で書き換える単純な方式でも通る
//   WARN  : 単純な方式は避ける。命令の切れ目を解釈して退避する方式なら通る
//   DANGER: 同上。加えて RIP 相対命令の行き先を退避先に合わせて直す必要がある
//
// 制約:
//   線形スキャンのため最初の ret/int3 で停止する。LOCK 命令が最初の ret より後にある場合は WARN を出せない。
//   Windows x64 専用（GetModuleHandleA 使用）。
[NodeType("ngol.hook.safety_check", "Hook", "Hook Safety Check",
    Version = "1.0.1",
    Description = "Decide whether a native function can be hooked at its entry, before installing anything. " +
                  "The first bytes are relocated into a trampoline, so a LOCK prefix or a RIP-relative operand " +
                  "among them breaks once moved. Reports SAFE / WARN / DANGER with the offending instructions.")]
[NodePort("rva",          PortDirection.Input,  "string", Description = "RVA hex of the function to check (e.g. '0x12340')")]
[NodePort("module",           PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("verdict",          PortDirection.Output, "string", Description = "SAFE / WARN / DANGER, or 'ERROR: ...' when the address could not be resolved")]
[NodePort("trampoline_issues",PortDirection.Output, "string", Description = "LOCK-prefixed instructions inside the first 14 bytes, one per line. Moving them verbatim produces a broken trampoline, so any entry here makes the verdict DANGER")]
[NodePort("lock_in_body",     PortDirection.Output, "string", Description = "LOCK-prefixed instructions in the rest of the function, one per line. With a clean trampoline zone these make the verdict WARN rather than DANGER")]
[NodePort("rip_in_trampoline",PortDirection.Output, "string", Description = "RIP-relative instructions inside the first 14 bytes, one per line. Their operand no longer points at the same place once moved, so any entry here makes the verdict DANGER")]
[NodePort("detail",           PortDirection.Output, "string", Description = "The whole report: address, trampoline zone size, how many instructions were analysed, the verdict and the instructions behind it")]
public class HookSafetyCheckNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    // NativeDetour が書き換える最大バイト数（x64 絶対 JMP = 14 bytes）
    const int TRAMPOLINE_SIZE = 14;

    sealed class BufOutput : FormatterOutput
    {
        readonly StringBuilder _sb = new();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Flush() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    public void Execute(IExecutionContext ctx)
    {
        var rvaStr     = (string?)ctx.GetPortValue("rva") ?? "";
        var moduleName = NgolModuleDefault.Resolve((string?)ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));

        if (string.IsNullOrWhiteSpace(rvaStr))
        {
            ctx.SetPortValue("verdict", "ERROR: rva is empty");
            return;
        }

        long rva;
        try
        {
            var s = rvaStr.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            rva = long.Parse(s, System.Globalization.NumberStyles.HexNumber);
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("verdict", $"ERROR: parse failed - {ex.Message}");
            return;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.SetPortValue("verdict", $"ERROR: module not found: {moduleName}");
            return;
        }

        // 関数全体を読み取り（最大 64KB）
        const int MAX_BYTES = 65536;
        var targetAddr = baseAddr.ToInt64() + rva;
        var bytes      = new byte[MAX_BYTES];
        Marshal.Copy((IntPtr)targetAddr, bytes, 0, MAX_BYTES);

        var reader  = new ByteArrayCodeReader(bytes);
        var decoder = Iced.Intel.Decoder.Create(64, reader);
        decoder.IP  = (ulong)targetAddr;

        var formatter = new NasmFormatter();
        var fmtOut    = new BufOutput();

        var trampolineIssues = new List<string>(); // 先頭14バイト内の危険命令
        var ripInTrampoline  = new List<string>(); // 先頭14バイト内の RIP 相対命令
        var lockInBody       = new List<string>(); // 関数全体の LOCK 命令

        ulong endIP = decoder.IP + MAX_BYTES;
        int instrCount = 0;

        while (decoder.IP < endIP)
        {
            var instr = decoder.Decode();
            if (instr.Code == Code.INVALID) break;

            var offset    = (long)instr.IP - targetAddr; // 関数先頭からのバイトオフセット
            var instrRva  = (long)instr.IP - baseAddr.ToInt64();
            formatter.Format(instr, fmtOut);
            var instrText = $"RVA:0x{instrRva:x} (+0x{offset:x})  {fmtOut.Flush()}";

            bool inTrampoline = offset < TRAMPOLINE_SIZE;

            // LOCK プレフィックス検出
            if (instr.HasLockPrefix)
            {
                lockInBody.Add(instrText);
                if (inTrampoline)
                    trampolineIssues.Add($"[LOCK] {instrText}");
            }

            // RIP 相対メモリアクセス（トランポリン内で壊れる）
            if (inTrampoline && instr.IsIPRelativeMemoryOperand)
                ripInTrampoline.Add($"[RIP-rel] {instrText}");

            instrCount++;
            if (instr.FlowControl == FlowControl.Return ||
                instr.Code == Code.Int3) break;
        }

        // 判定
        string verdict;
        var detail = new StringBuilder();
        detail.AppendLine($"RVA: 0x{rva:x}  module: {moduleName}");
        detail.AppendLine($"Trampoline zone: first {TRAMPOLINE_SIZE} bytes");
        detail.AppendLine($"Instructions analyzed: {instrCount}");
        detail.AppendLine();

        if (trampolineIssues.Count > 0 || ripInTrampoline.Count > 0)
        {
            verdict = "DANGER";
            detail.AppendLine("DANGER: Trampoline zone contains unsafe instructions.");
            detail.AppendLine("  Relocating these bytes verbatim produces a broken trampoline.");
            if (trampolineIssues.Count > 0)
            {
                detail.AppendLine($"  LOCK in trampoline zone ({trampolineIssues.Count}):");
                trampolineIssues.ForEach(l => detail.AppendLine("    " + l));
            }
            if (ripInTrampoline.Count > 0)
            {
                detail.AppendLine($"  RIP-relative in trampoline zone ({ripInTrampoline.Count}):");
                ripInTrampoline.ForEach(l => detail.AppendLine("    " + l));
            }
        }
        else if (lockInBody.Count > 0)
        {
            verdict = "WARN";
            detail.AppendLine("WARN: Trampoline zone is clean, but function body contains LOCK instructions.");
            detail.AppendLine("  These are GC barriers. Overwriting a fixed number of leading bytes MAY");
            detail.AppendLine("  fault at runtime. Use a detour that decodes instruction boundaries.");
            detail.AppendLine($"  LOCK instructions in body ({lockInBody.Count}):");
            lockInBody.ForEach(l => detail.AppendLine("    " + l));
        }
        else
        {
            verdict = "SAFE";
            detail.AppendLine("SAFE: No LOCK prefix or RIP-relative instructions detected.");
            detail.AppendLine("  The leading bytes can be relocated as they are.");
        }

        ctx.SetPortValue("verdict",           verdict);
        ctx.SetPortValue("trampoline_issues", string.Join("\n", trampolineIssues));
        ctx.SetPortValue("lock_in_body",      string.Join("\n", lockInBody));
        ctx.SetPortValue("rip_in_trampoline", string.Join("\n", ripInTrampoline));
        ctx.SetPortValue("detail",            detail.ToString());
        ctx.Logger.LogInfo($"[HookSafetyCheck] RVA:0x{rva:x} -> {verdict} " +
                           $"(trampoline_issues={trampolineIssues.Count}, lock_in_body={lockInBody.Count})");
    }
}
