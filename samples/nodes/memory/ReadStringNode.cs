using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 文字列を読む。位置はモジュール内の RVA でも、絶対アドレスでも指定できる。
///
/// 絶対アドレスを受けられるので、<c>ngol.mem.read_ptr</c> でポインタを辿った先を
/// そのまま読める--オブジェクトの中に置かれた名前を取り出すような使い方ができる。
/// </summary>
[NodeType("ngol.mem.read_string", "Memory", "Read String",
    Version = "1.1.1",
    Description = "Read a null-terminated string, either at module_base+rva or at an absolute address. wide=true for UTF-16LE (default), false for ASCII.")]
[NodePort("rva",                 PortDirection.Input,  "string",  Description = "RVA hex (e.g. '0x9df6b0'). Ignored when absolute_address_hex is set")]
[NodePort("module",              PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("absolute_address_hex", PortDirection.Input, "string",  Description = "Absolute address hex. When set, rva/module are ignored. Use with ngol.mem.read_ptr to follow a pointer chain")]
[NodePort("wide",                PortDirection.Input,  "boolean", Description = "true=UTF-16LE (default), false=ASCII")]
[NodePort("max_chars",           PortDirection.Input,  "number",  Description = "Max characters to read (default 128)")]
[NodePort("text",                PortDirection.Output, "string",  Description = "Decoded string")]
public sealed class ReadStringNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    public void Execute(IExecutionContext ctx)
    {
        bool wide = true;
        if (ctx.GetPortValue("wide") is bool wb) wide = wb;
        double maxCharsD = 128.0;
        if (ctx.GetPortValue("max_chars") is double mc) maxCharsD = mc;
        int maxChars = Math.Max(1, (int)maxCharsD);

        IntPtr target;
        var absStr = (ctx.GetPortValue("absolute_address_hex") as string ?? "").Trim();
        if (absStr.Length > 0)
        {
            if (!TryParseHex(absStr, out var abs) || abs == 0)
            {
                ctx.Logger.LogError($"[ReadString] invalid absolute_address_hex: {absStr}");
                ctx.SetPortValue("text", "");
                return;
            }
            target = new IntPtr(abs);
        }
        else
        {
            var rvaStr = ctx.GetPortValue("rva") as string ?? "";
            if (!TryParseHex(rvaStr, out var rva))
            {
                ctx.Logger.LogError($"[ReadString] invalid rva: {rvaStr}");
                ctx.SetPortValue("text", "");
                return;
            }

            var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
            var baseAddr = GetModuleHandleA(moduleName);
            if (baseAddr == IntPtr.Zero)
            {
                ctx.Logger.LogWarning($"[ReadString] module not found: {moduleName}");
                ctx.SetPortValue("text", "");
                return;
            }
            target = new IntPtr(baseAddr.ToInt64() + rva);
        }

        // 読めない番地への読み取りは例外にならずプロセスごと落ちる。
        //    読める分だけ取り、そこまでで復号する（末尾まで届かなくても、届いた分は返す）。
        var charSize = wide ? 2 : 1;
        var want = maxChars * charSize;
        var raw = new byte[want];
        var got = NgolSafeMemory.Read(target, raw, 0, want);
        if (got < charSize)
        {
            ctx.Logger.LogWarning($"[ReadString] not readable at 0x{target.ToInt64():x}");
            ctx.SetPortValue("text", "");
            return;
        }

        var usable = got - (got % charSize);
        var text = wide
            ? System.Text.Encoding.Unicode.GetString(raw, 0, usable)
            : System.Text.Encoding.ASCII.GetString(raw, 0, usable);
        var nul = text.IndexOf('\0');
        if (nul >= 0) text = text.Substring(0, nul);
        ctx.SetPortValue("text", text);
        ctx.Logger.LogInfo($"[ReadString] 0x{target.ToInt64():x} wide={wide} -> \"{text}\"");
    }

    static bool TryParseHex(string s, out long value)
    {
        value = 0;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
