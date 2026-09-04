using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// module_base + rva の関数を引数1個(ポインタ、RCX)・戻り値ポインタ(64bit)として
/// 直接呼び出す。ngol.proc.call_fn0 の1引数版。
/// 「this ポインタ 1 個だけ受け取り、ポインタを返す」単純な形の関数専用。
///
/// 出力:
///   result_hex -> 戻り値(RAX)を16進文字列で返す
///
/// 主な使い方:
///   arg1_hex に対象オブジェクトのポインタ(16進)を渡し、その上で1引数メンバ関数
///   相当のネイティブ関数を呼ぶ。構造体を戻り値にする関数には使えない。実装は
///   隠しの出力ポインタを第1引数として受け取るので、引数の並びがここの想定と食い違う。
///
/// 制約: 対象関数が本当に「RCX=this、戻り値はRAXにポインタ1個」という単純な
/// 形であることを事前にdisasmで確認してから使うこと。構造体を値渡し/値返却する
/// 関数には使えない。
/// </summary>
[NodeType("ngol.proc.call_fn1", "Proc", "Call Native Fn (1 arg)",
    Version = "1.0.2",
    Description = "Call a native function at module_base+rva with one pointer argument (RCX), returning the full 64-bit RAX as a hex string. Use only for simple 'this-only' accessor functions verified via disasm first.")]
[NodePort("rva",       PortDirection.Input,  "string", Description = "RVA hex (e.g. '0x398860')")]
[NodePort("module",    PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("arg1_hex",  PortDirection.Input,  "string", Description = "First argument (RCX) as hex pointer value, e.g. '0x23d055f9880'")]
[NodePort("result_hex", PortDirection.Output, "string", Description = "Return value (RAX) as hex string")]
public sealed class CallPtrFunction1ArgNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr FnPtr1(IntPtr arg1);

    public void Execute(IExecutionContext ctx)
    {
        var rvaStr = ctx.GetPortValue("rva") as string ?? "";
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
        var arg1Str = ctx.GetPortValue("arg1_hex") as string ?? "";

        long rva, arg1;
        try
        {
            rva = ParseHex(rvaStr);
            arg1 = ParseHex(arg1Str);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[CallPtrFn1] parse error: {ex.Message}");
            ctx.SetPortValue("result_hex", "");
            return;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[CallPtrFn1] module not found: {moduleName}");
            ctx.SetPortValue("result_hex", "");
            return;
        }

        var target = new IntPtr(baseAddr.ToInt64() + rva);
        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<FnPtr1>(target);
            var result = fn(new IntPtr(arg1));
            ctx.SetPortValue("result_hex", $"0x{result.ToInt64():x}");
            ctx.Logger.LogInfo($"[CallPtrFn1] rva=0x{rva:x} arg1=0x{arg1:x} -> 0x{result.ToInt64():x}");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[CallPtrFn1] call failed: {ex.Message}");
            ctx.SetPortValue("result_hex", "");
        }
    }

    static long ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }
}
