using System;
using System.Linq;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定アドレスから型付きの値を読む。ngol.mem.read_ptr（8バイト固定）と
/// ngol.mem.read_string の間を埋める--int32/int64/float/double を直接扱いたい場合に使う。
///
/// 同じ幅の整数と浮動小数点の両方の解釈を必ず返す。
///   走査する側から見ると 4 バイトの整数と float は区別がつかず、
///   型を変えて読み直す往復が要る--それを 1 回で済ませる。
/// </summary>
[NodeType("ngol.mem.read_value", "Memory", "Read Value",
    Version = "1.1.1",
    Description = "Read a typed value (int32/int64/float/double) at address_hex+offset. Fills the gap between "
      + "ngol.mem.read_ptr (fixed 8 bytes) and ngol.mem.read_string. The same bytes are always reported both ways - "
      + "as an integer and as a floating point number of the same width - because a 4-byte integer and a float are "
      + "indistinguishable to a scan, and guessing wrong costs a read. bytes_hex carries the exact bytes, which "
      + "matters for 64-bit integers whose value does not survive being reported as a number.")]
[NodePort("address_hex", PortDirection.Input, "string", Description = "Absolute address as hex string")]
[NodePort("offset",      PortDirection.Input, "number", Description = "Byte offset added to address_hex (default 0)")]
[NodePort("value_type",  PortDirection.Input, "string", Description = "int32 | int64 | float | double (default int32). Only picks which reading goes to value_number - both readings are always returned")]
[NodePort("value_number",PortDirection.Output, "number", Description = "The value read as value_type")]
[NodePort("value_as_int",   PortDirection.Output, "number", Description = "The same bytes as an integer of the same width (int32 for 4 bytes, int64 for 8). Beyond 2^53 read bytes_hex instead")]
[NodePort("value_as_float", PortDirection.Output, "number", Description = "The same bytes as a floating point number of the same width (float for 4 bytes, double for 8)")]
[NodePort("bytes_hex",   PortDirection.Output, "string", Description = "The exact bytes read, space-separated. Lossless - use this when the reading as a number is not precise enough")]
[NodePort("readable",    PortDirection.Output, "boolean", Description = "false when the address could not be read. The value outputs are then 0, which is why this is reported separately")]
[NodePort("result",      PortDirection.Output, "string", Description = "The address, the value as value_type, the same bytes as an integer and as a floating point number, and the raw bytes - or the reason nothing was read")]
public sealed class ReadValueNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var addrHex = ctx.GetPortValue("address_hex") as string ?? "";
        var offset   = ctx.GetPortValue("offset") is double o ? (long)o : 0L;
        var type     = (ctx.GetPortValue("value_type") as string ?? "int32").Trim().ToLowerInvariant();

        if (!NgolAddressResolve.TryParseHex(addrHex, out var baseAddr))
        {
            SetOutputs(ctx, 0, 0, 0, "", false, $"address_hex could not be read: '{addrHex}'");
            return;
        }
        var target = new IntPtr(unchecked((long)baseAddr) + offset);

        var size = NgolValueCodec.SizeOf(type);
        if (size == 0)
        {
            SetOutputs(ctx, 0, 0, 0, "", false, $"unknown value_type: '{type}' (use int32/int64/float/double)");
            return;
        }

        var buf = new byte[size];
        if (NgolSafeMemory.Read(target, buf, 0, size) < size)
        {
            SetOutputs(ctx, 0, 0, 0, "", false, $"not readable at 0x{target.ToInt64():x}");
            return;
        }

        var value = NgolValueCodec.Decode(type, buf);
        // 同じバイト列を整数としても浮動小数点としても返す。どちらであるかは
        //   バイト列からは決まらないので、選ばずに両方を出して利用者に見せる。
        var intType = NgolValueCodec.IntTypeOfSize(size);
        var floatType = NgolValueCodec.FloatTypeOfSize(size);
        var asInt = NgolValueCodec.Decode(intType, buf);
        var asFloat = NgolValueCodec.Decode(floatType, buf);
        // 64bit 整数は number に載せると値が変わる。元のバイト列も返して逃げ道を残す。
        var hex = string.Join(" ", buf.Select(b => b.ToString("x2")));

        // 読んだ値が載ったままゴミになると、ngol.mem.value_scan がこのバッファ自身を
        //   「その値を持つ番地」として拾ってしまう。手放す前に消す。
        Array.Clear(buf, 0, buf.Length);
        SetOutputs(ctx, value, asInt, asFloat, hex, true,
            $"0x{target.ToInt64():x} = {value} ({type}) | as {intType} {asInt} | as {floatType} {asFloat} | bytes {hex}");
    }

    private static void SetOutputs(IExecutionContext ctx, double value, double asInt, double asFloat,
                                    string bytesHex, bool readable, string result)
    {
        ctx.SetPortValue("value_number", value);
        ctx.SetPortValue("value_as_int", asInt);
        ctx.SetPortValue("value_as_float", asFloat);
        ctx.SetPortValue("bytes_hex", bytesHex);
        ctx.SetPortValue("readable", readable);
        ctx.SetPortValue("result", result);
    }
}
