using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// module_base + rva の関数を引数0個で呼び、戻り値(RAX)を2通りで返す。
///
/// 出力:
///   result_hex -> RAX 全体を16進文字列で返す（ポインタを返す関数向け）
///   result_int -> RAX の下位32bitを int32 として返す（件数・状態値を返す getter 向け）
///
/// 呼び出し規約上 RAX の上位32bitは int を返す関数では未定義なので、int として読むときは
/// 下位32bitだけを取る。どちらを読むかは呼ぶ側が決められるよう両方出す。
///
/// 制約: 呼び出し規約・引数の有無を誤ると呼び出し先の状態を破壊しクラッシュする。
/// 副作用がなく安全と判断できる関数にのみ使うこと。
/// </summary>
[NodeType("ngol.proc.call_fn0", "Proc", "Call Native Fn (0 args)",
    Version = "1.1.1",
    Description = "Call a native function at module_base+rva with zero arguments. Returns the full 64-bit RAX as hex and its low 32 bits as an integer. Use only for known-safe functions.")]
[NodePort("rva",         PortDirection.Input,  "string", Description = "RVA hex (e.g. '0x3655200')")]
[NodePort("module",      PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("result_hex",  PortDirection.Output, "string", Description = "Return value (RAX) as hex string. Empty on failure")]
[NodePort("result_int",  PortDirection.Output, "number", Description = "Low 32 bits of the return value as int32. 0 on failure")]
public sealed class CallPtrFunctionNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr FnPtr0();

    public void Execute(IExecutionContext ctx)
    {
        var rvaStr = ctx.GetPortValue("rva") as string ?? "";
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));

        long rva;
        try
        {
            var s = rvaStr.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            rva = long.Parse(s, System.Globalization.NumberStyles.HexNumber);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[CallFn0] parse error: {ex.Message}");
            SetFailure(ctx);
            return;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[CallFn0] module not found: {moduleName}");
            SetFailure(ctx);
            return;
        }

        var target = new IntPtr(baseAddr.ToInt64() + rva);
        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<FnPtr0>(target);
            long raw = fn().ToInt64();
            ctx.SetPortValue("result_hex", $"0x{raw:x}");
            ctx.SetPortValue("result_int", (double)unchecked((int)raw));
            ctx.Logger.LogInfo($"[CallFn0] rva=0x{rva:x} -> 0x{raw:x} ({unchecked((int)raw)})");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[CallFn0] call failed: {ex.Message}");
            SetFailure(ctx);
        }
    }

    static void SetFailure(IExecutionContext ctx)
    {
        ctx.SetPortValue("result_hex", "");
        ctx.SetPortValue("result_int", 0.0);
    }
}
