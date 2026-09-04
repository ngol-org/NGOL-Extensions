using System;
using System.Linq;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 任意の長さのバイト列を読む。
///
/// ngol.code.disasm はバイト列を出すが、それは命令として復号できる場所に限られる。
///   構造体・配列・テーブルのようなデータ領域は復号すると意味の無い命令になるため、
///   「そのまま並びを見る」手段が別に要る。
///
/// bytes_hex は ngol.code.aob_scan の pattern と ngol.hook.patch_bytes の bytes_hex に
///   そのまま渡せる形（空白区切りの16進）で返す--読んだものを探す・書き戻す鎖が閉じる。
/// hex_dump は目で読むためのもので、機械に渡すなら bytes_hex を使う。
/// </summary>
[NodeType("ngol.mem.read_bytes", "Memory", "Read Bytes",
    Version = "1.0.1",
    Description = "Read a run of raw bytes at module+rva or at an absolute address. ngol.code.disasm only shows bytes "
      + "where they decode as instructions, so this is the way to look at data - structs, arrays, lookup tables. "
      + "bytes_hex comes back space-separated, ready to paste into ngol.code.aob_scan's pattern or "
      + "ngol.hook.patch_bytes' bytes_hex; hex_dump is the side-by-side hex/ASCII view for reading by eye. Reads stop "
      + "at the first unreadable page, so read_length below length means the run ended there rather than the read "
      + "failing.")]
[NodePort("module",               PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("rva",                  PortDirection.Input,  "string", Description = "RVA hex (e.g. '0x6136c0')")]
[NodePort("absolute_address_hex", PortDirection.Input,  "string", Description = "Pre-resolved absolute address. Takes priority over module/rva when non-empty. Use with ngol.mem.read_ptr to follow a pointer chain")]
[NodePort("length",               PortDirection.Input,  "number", Description = "Bytes to read (default 64, max 4096)")]
[NodePort("bytes_hex",            PortDirection.Output, "string", Description = "Space-separated hex, accepted as-is by ngol.code.aob_scan and ngol.hook.patch_bytes")]
[NodePort("hex_dump",             PortDirection.Output, "string", Description = "16 bytes per line with address and ASCII column, for reading by eye")]
[NodePort("read_length",          PortDirection.Output, "number", Description = "Bytes actually read. Below length means an unreadable page ended the run")]
[NodePort("address_hex",          PortDirection.Output, "string", Description = "The absolute address that was read, whichever way it was given")]
[NodePort("result",               PortDirection.Output, "string", Description = "How many bytes were read and from where, or the reason nothing was read. Fewer bytes than asked for means the readable range ended there")]
public sealed class ReadBytesNode : INode
{
    // 1回の出力に載る量の上限。これ以上は文字列として扱いにくく、
    //   広い範囲を見たいなら aob_scan / disasm_scan のような「探す」側のノードが向く。
    private const int MaxLength = 4096;
    private const int BytesPerLine = 16;

    public void Execute(IExecutionContext ctx)
    {
        var moduleName  = NgolModuleDefault.Resolve(ReadString(ctx, "module", ""));
        var rvaHex      = ReadString(ctx, "rva", "");
        var absoluteHex = ReadString(ctx, "absolute_address_hex", "");
        var length      = ctx.GetPortValue("length") is double d && d >= 1 ? (int)d : 64;
        if (length > MaxLength) length = MaxLength;

        var useAbsolute = !string.IsNullOrWhiteSpace(absoluteHex);
        if (!useAbsolute && string.IsNullOrWhiteSpace(rvaHex))
        {
            SetOutputs(ctx, "", "", 0, "", "rva is empty (and no absolute_address_hex given)");
            return;
        }

        if (!NgolAddressResolve.TryResolveTarget(useAbsolute, moduleName, rvaHex, absoluteHex, out var target, out var resolveError))
        {
            SetOutputs(ctx, "", "", 0, "", resolveError);
            return;
        }

        var addressHex = "0x" + target.ToInt64().ToString("x");

        // 読めない番地へ触ると例外ではなくプロセスごと落ちる。NgolSafeMemory が
        //    読める分だけ写して止まるので、届いた分をそのまま返す。
        var buf = new byte[length];
        var got = NgolSafeMemory.Read(target, buf, 0, length);
        if (got <= 0)
        {
            SetOutputs(ctx, "", "", 0, addressHex, $"not readable at {addressHex}");
            return;
        }

        var bytesHex = string.Join(" ", buf.Take(got).Select(b => b.ToString("x2")));
        var dump = BuildDump(target.ToInt64(), buf, got);
        var note = got < length ? $" (unreadable from +0x{got:x})" : "";

        // 読んだ内容が載ったままゴミになると、ngol.mem.value_scan がこのバッファ自身を
        //   候補として拾う。手放す前に消す。
        Array.Clear(buf, 0, buf.Length);

        SetOutputs(ctx, bytesHex, dump, got, addressHex, $"{got} byte(s) at {addressHex}{note}");
    }

    private static string BuildDump(long baseAddress, byte[] data, int count)
    {
        var sb = new StringBuilder();
        for (int lineStart = 0; lineStart < count; lineStart += BytesPerLine)
        {
            var lineLength = Math.Min(BytesPerLine, count - lineStart);
            sb.Append($"0x{baseAddress + lineStart:x}  ");

            for (int i = 0; i < BytesPerLine; i++)
                sb.Append(i < lineLength ? data[lineStart + i].ToString("x2") + " " : "   ");

            sb.Append(' ');
            for (int i = 0; i < lineLength; i++)
            {
                var b = data[lineStart + i];
                // 表示できない文字は '.' に置く--桁が揃っていないと並びが読めない。
                sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ReadString(IExecutionContext ctx, string name, string fallback)
        => ctx.GetPortValue(name) as string ?? ctx.GetParam<string>(name) ?? fallback;

    private static void SetOutputs(IExecutionContext ctx, string bytesHex, string dump, int readLength,
                                    string addressHex, string result)
    {
        ctx.SetPortValue("bytes_hex", bytesHex);
        ctx.SetPortValue("hex_dump", dump);
        ctx.SetPortValue("read_length", (double)readLength);
        ctx.SetPortValue("address_hex", addressHex);
        ctx.SetPortValue("result", result);
    }
}
