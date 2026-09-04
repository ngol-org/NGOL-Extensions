using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定アドレス(+オフセット)から8バイトポインタ値を読み取る汎用診断ノード。
/// ngol.code.module_base と組み合わせ、公開されていない構造体のオフセットを
/// 探り当てる用途を想定（読んだ値がモジュール内を指すかどうかが手掛かりになる）。
/// </summary>
[NodeType("ngol.mem.read_ptr", "Memory", "Read Pointer",
    Version = "1.0.1",
    Description = "Read an 8-byte pointer value at address_hex+offset from the target process memory.")]
[NodePort("address_hex", PortDirection.Input, "string", Description = "Base address as hex string, e.g. '0x1d257bb1888'")]
[NodePort("offset", PortDirection.Input, "number", Description = "Byte offset added to address_hex (default 0)")]
[NodePort("value_hex", PortDirection.Output, "string", Description = "8-byte value read, as hex string")]
[NodePort("in_module_range", PortDirection.Output, "boolean", Description = "true if value_hex falls within the main EXE's image address range (heuristic for 'looks like a code pointer')")]
public sealed class ReadPointerNode : INode
{
    [DllImport("kernel32.dll")]
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
        double offsetD = 0;
        if (ctx.GetPortValue("offset") is double o) offsetD = o;

        if (!long.TryParse(addrHex.Replace("0x", "").Replace("0X", ""), System.Globalization.NumberStyles.HexNumber, null, out var baseAddr))
        {
            ctx.Logger.LogError($"[ReadPointer] Failed to parse address_hex: {addrHex}");
            ctx.SetPortValue("value_hex", "0x0");
            ctx.SetPortValue("in_module_range", false);
            return;
        }

        var target = new IntPtr(baseAddr + (long)offsetD);

        // 読めない番地への Marshal.Read は例外にならずプロセスごと落ちる。
        //    try/catch では守れないので、触る前に読めるかを確かめる。
        var word = new byte[8];
        if (NgolSafeMemory.Read(target, word, 0, 8) < 8)
        {
            ctx.Logger.LogWarning($"[ReadPointer] not readable at 0x{target.ToInt64():X}");
            ctx.SetPortValue("value_hex", "0x0");
            ctx.SetPortValue("in_module_range", false);
            return;
        }
        var value = BitConverter.ToInt64(word, 0);

        ctx.SetPortValue("value_hex", "0x" + value.ToString("X"));

        bool inRange = false;
        var mainModule = GetModuleHandleA(null);
        if (mainModule != IntPtr.Zero && GetModuleInformation(GetCurrentProcess(), mainModule, out var info, (uint)Marshal.SizeOf<MODULEINFO>()))
        {
            long lo = info.lpBaseOfDll.ToInt64();
            long hi = lo + info.SizeOfImage;
            inRange = value >= lo && value < hi;
        }
        ctx.SetPortValue("in_module_range", inRange);
    }
}
