using System;
using System.Globalization;
using System.Linq;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定番地の命令バイトを直接書き換える。フック（先頭にジャンプを差し込む）とは別の介入方法で、
/// 関数の途中の1命令だけを潰す・まだ一度も呼ばれていない箇所を変えることができる。
///
/// 書く前に必ず元のバイトを控える。ngol.hook.patch_revert で戻せるのはこのノードが
///   同じプロセス内で控えた分だけ--プロセスを再起動すると控えは失われる。
///
/// 命令の途中に着地すると、後続の命令がバイト単位でずれて壊れる。
///   ngol.code.disasm で命令の境界を確認してから使うこと（bytes_hex の長さが
///   ちょうど1命令分になるように選ぶのが最も安全）。
/// このノードは「何を書くか」を判断しない。ニーモニックからバイト列への変換は
///   含まないので、16 進のバイト列を直接渡すこと。
/// </summary>
[NodeType("ngol.hook.patch_bytes", "Hook", "Patch Bytes",
    Version = "1.1.1",
    Description =
        "Overwrite instruction bytes at an address directly, instead of installing a hook. Useful for patching a "
      + "single instruction mid-function or an address that may never be reached by a hook's entry point. The "
      + "original bytes are saved before writing so ngol.hook.patch_revert can restore them - but only within this "
      + "process; the save does not survive a restart. Landing mid-instruction corrupts the following bytes; check "
      + "instruction boundaries with ngol.code.disasm first. Does not assemble mnemonics - bytes_hex must already be "
      + "the raw bytes to write. Set nop_instructions instead of bytes_hex to neutralise whole instructions: the node "
      + "decodes that many instructions at the address, so the fill always ends on an instruction boundary.")]
[NodePort("module",               PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("rva",                  PortDirection.Input,  "string", Description = "RVA hex (e.g. 0x12340)")]
[NodePort("absolute_address_hex", PortDirection.Input,  "string", Description = "Pre-resolved absolute address. Takes priority over module/rva when non-empty")]
[NodePort("bytes_hex",            PortDirection.Input,  "string", Description = "Bytes to write, as hex (e.g. '90 90 90' or '909090'). Ignored when nop_instructions is set")]
[NodePort("nop_instructions",     PortDirection.Input,  "number", Description = "Neutralise this many whole instructions with 0x90 instead of writing bytes_hex. The node decodes them to get their exact length, so the patch cannot end mid-instruction")]
[NodePort("applied",              PortDirection.Output, "boolean", Description = "true when the bytes were written")]
[NodePort("original_bytes_hex",   PortDirection.Output, "string", Description = "Bytes that were at the address before this write, saved for patch_revert")]
[NodePort("patched_length",       PortDirection.Output, "number", Description = "Number of bytes written")]
[NodePort("result",               PortDirection.Output, "string", Description = "How many bytes were written and where, or the reason nothing was. Says so when the address had already been patched, since patch_revert restores the first original")]
public sealed class PatchBytesNode : INode
{
    private const string SavedBytesKeyPrefix = "NgolPatchBytesOriginal_";

    public void Execute(IExecutionContext ctx)
    {
        var moduleName  = NgolModuleDefault.Resolve(ReadString(ctx, "module", ""));
        var rvaHex      = ReadString(ctx, "rva", "");
        var absoluteHex = ReadString(ctx, "absolute_address_hex", "");
        var bytesHex    = ReadString(ctx, "bytes_hex", "");
        var nopCount    = ctx.GetPortValue("nop_instructions") is double n && n > 0 ? (int)n : 0;

        var useAbsolute = !string.IsNullOrWhiteSpace(absoluteHex);
        if (!useAbsolute && string.IsNullOrWhiteSpace(rvaHex))
        {
            SetOutputs(ctx, false, "", 0, "rva is empty (and no absolute_address_hex given)");
            return;
        }

        if (!NgolAddressResolve.TryResolveTarget(useAbsolute, moduleName, rvaHex, absoluteHex, out var target, out var resolveError))
        {
            SetOutputs(ctx, false, "", 0, resolveError);
            return;
        }

        byte[] newBytes;
        if (nopCount > 0)
        {
            // 命令を数えて長さを測るので、埋める範囲が命令の途中で終わることが起きない。
            //   手で 0x90 の個数を数えると、そこが最も間違えやすい。
            if (!TryMeasureInstructions(target, nopCount, out var span, out var measureError))
            {
                SetOutputs(ctx, false, "", 0, measureError);
                return;
            }
            newBytes = new byte[span];
            for (int i = 0; i < span; i++) newBytes[i] = 0x90;
        }
        else if (!TryParseBytes(bytesHex, out newBytes, out var byteError))
        {
            SetOutputs(ctx, false, "", 0, byteError);
            return;
        }

        // 書く前に、書く分だけ元バイトを控える。読み取り失敗（=書き込みも失敗するはず）は
        //   ここで検出し、実際に書く前に止める。
        var original = new byte[newBytes.Length];
        var got = NgolSafeMemory.Read(target, original, 0, newBytes.Length);
        if (got < newBytes.Length)
        {
            SetOutputs(ctx, false, "", 0, $"not readable (or not fully readable) at 0x{target.ToInt64():x}");
            return;
        }

        if (!NgolSafeMemory.Write(target, newBytes))
        {
            SetOutputs(ctx, false, "", 0, $"write failed at 0x{target.ToInt64():x}");
            return;
        }

        // 同じ番地へ 2 回目を当てるとき、控えを上書きしてはいけない。
        //   上書きすると控えが「1 回目のパッチ後のバイト列」になり、
        //   patch_revert が元へ戻したつもりで 1 回目のパッチを書き戻してしまう。
        //   この取り違えは戻したあとも静かに残り、後から見ると原因が分からなくなる。
        var key = SavedBytesKeyPrefix + target.ToInt64().ToString("x");
        var alreadySaved = AppDomain.CurrentDomain.GetData(key) as byte[];
        if (alreadySaved == null) AppDomain.CurrentDomain.SetData(key, original);

        var saved = alreadySaved ?? original;
        var origHex = string.Join(" ", saved.Select(b => b.ToString("x2")));
        var note = alreadySaved != null
            ? $" (already patched before; patch_revert will restore the first original: {origHex})"
            : "";
        var how = nopCount > 0 ? $"nop-filled {nopCount} instruction(s) = " : "patched ";
        SetOutputs(ctx, true, origHex, newBytes.Length,
            $"{how}{newBytes.Length} byte(s) at 0x{target.ToInt64():x}{note}");
    }

    /// <summary>
    /// address から instructionCount 個ぶんの命令が占めるバイト数を測る。
    /// 「命令をいくつ潰すか」で指定できるようにするために要る。
    ///   バイト数で指定させると、命令の途中で切れた分だけ後続がずれて壊れる。
    /// </summary>
    private static bool TryMeasureInstructions(IntPtr address, int instructionCount, out int span, out string error)
    {
        span = 0;
        error = string.Empty;

        const int Window = 256;
        var buf = new byte[Window];
        var readable = NgolSafeMemory.Read(address, buf, 0, Window);
        if (readable <= 0)
        {
            error = $"not readable at 0x{address.ToInt64():x}";
            return false;
        }

        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(buf));
        decoder.IP = (ulong)address.ToInt64();
        var endIP = decoder.IP + (ulong)readable;

        for (int i = 0; i < instructionCount; i++)
        {
            if (decoder.IP >= endIP)
            {
                error = $"only {i} instruction(s) fit in the readable range at 0x{address.ToInt64():x}";
                return false;
            }
            var instr = decoder.Decode();
            if (instr.Code == Code.INVALID)
            {
                // そこがコードでないなら、埋めても意味が無いどころか壊す。
                error = $"not an instruction at 0x{instr.IP:x} (decoded as invalid) - is this really code?";
                return false;
            }
            span += instr.Length;
        }
        return true;
    }

    // 空白区切り("90 90 90")と連続16進("909090")の両方を受ける。
    private static bool TryParseBytes(string text, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;
        var s = (text ?? "").Trim();
        if (s.Length == 0) { error = "bytes_hex is empty"; return false; }

        var compact = s.Contains(' ') ? s.Replace(" ", "") : s;
        if (compact.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) compact = compact.Substring(2);
        if (compact.Length % 2 != 0) { error = $"bytes_hex has an odd number of hex digits: '{text}'"; return false; }

        var result = new byte[compact.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(compact.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
            {
                error = $"bytes_hex could not be read: '{text}'";
                return false;
            }
        }
        bytes = result;
        return true;
    }

    private static string ReadString(IExecutionContext ctx, string name, string fallback)
        => ctx.GetPortValue(name) as string ?? ctx.GetParam<string>(name) ?? fallback;

    private static void SetOutputs(IExecutionContext ctx, bool applied, string originalHex, int patchedLength, string result)
    {
        ctx.SetPortValue("applied", applied);
        ctx.SetPortValue("original_bytes_hex", originalHex);
        ctx.SetPortValue("patched_length", (double)patchedLength);
        ctx.SetPortValue("result", result);
    }
}
