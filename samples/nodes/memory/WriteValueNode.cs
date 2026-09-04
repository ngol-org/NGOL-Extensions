using System;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定アドレスへ型付きの値を書く。ngol.mem.value_scan / value_next で見つけた番地への
/// 書き戻しに使う想定。このノードは判定を持たない--書いていい番地かどうかは
/// 利用者が決めること（対象プロセスの状態を直接変更する）。
/// </summary>
[NodeType("ngol.mem.write", "Memory", "Write Value",
    Version = "1.0.1",
    Description = "Write a typed value (int32/int64/float/double) at address_hex+offset. This changes the target "
      + "process's state directly - there is no undo. Typically used after ngol.mem.value_scan/value_next to write "
      + "back to an address that was found by searching for its value.")]
[NodePort("address_hex", PortDirection.Input, "string", Description = "Absolute address as hex string")]
[NodePort("offset",      PortDirection.Input, "number", Description = "Byte offset added to address_hex (default 0)")]
[NodePort("value_type",  PortDirection.Input, "string", Description = "int32 | int64 | float | double (default int32)")]
[NodePort("value",       PortDirection.Input, "number", Description = "Value to write")]
[NodePort("written",     PortDirection.Output, "boolean", Description = "true when the write succeeded")]
[NodePort("result",      PortDirection.Output, "string", Description = "What was written and where, or the reason nothing was")]
public sealed class WriteValueNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var addrHex = ctx.GetPortValue("address_hex") as string ?? "";
        var offset   = ctx.GetPortValue("offset") is double o ? (long)o : 0L;
        var type     = (ctx.GetPortValue("value_type") as string ?? "int32").Trim().ToLowerInvariant();
        var value    = ctx.GetPortValue("value") is double v ? v : 0.0;

        if (!NgolAddressResolve.TryParseHex(addrHex, out var baseAddr))
        {
            SetOutputs(ctx, false, $"address_hex could not be read: '{addrHex}'");
            return;
        }
        var target = new IntPtr(unchecked((long)baseAddr) + offset);

        var bytes = NgolValueCodec.Encode(type, value);
        if (bytes.Length == 0)
        {
            SetOutputs(ctx, false, $"unknown value_type: '{type}' (use int32/int64/float/double)");
            return;
        }

        var ok = NgolSafeMemory.Write(target, bytes);
        // 書いた値が載ったままゴミになると、ngol.mem.value_scan がこのバッファ自身を
        //   「その値を持つ番地」として拾ってしまう。手放す前に消す。
        Array.Clear(bytes, 0, bytes.Length);
        if (!ok)
        {
            SetOutputs(ctx, false, $"write failed at 0x{target.ToInt64():x}");
            return;
        }
        SetOutputs(ctx, true, $"wrote {type} {value} at 0x{target.ToInt64():x}");
    }

    private static void SetOutputs(IExecutionContext ctx, bool written, string result)
    {
        ctx.SetPortValue("written", written);
        ctx.SetPortValue("result", result);
    }
}
