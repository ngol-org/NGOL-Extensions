using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定アドレス(絶対値、hex文字列)から数命令だけデコードして表示する軽量ノード。
/// FindXrefsNode.csと同じIced.Intelを流用。候補アドレスが本物の関数プロローグ
/// っぽいか(sub rsp,X / mov [rsp+X],rX / push rX 等)を目視確認する用途。
///
/// 各行に生バイトを添える。ここは「そもそもコードなのか」を疑う場面なので、
///   ニーモニックだけだと、ただのデータを命令として復号した結果を読んでしまう。
///   バイト列が見えていれば、パディング(cc の並び)やポインタ値がそのまま分かる。
/// </summary>
[NodeType("ngol.code.disasm_peek", "Code", "Disasm Peek",
    Version = "1.0.1",
    Description = "Decode a few x64 instructions starting at address_hex (absolute address) using Iced.Intel. For sanity-checking whether a candidate pointer looks like a real function entry. Each line shows the raw bytes as well, so data that merely decodes as instructions can be recognised for what it is.")]
[NodePort("address_hex", PortDirection.Input,  "string",  Description = "Absolute address as hex string, e.g. '0x7ff798018be0'")]
[NodePort("count",         PortDirection.Input,  "number", Description = "Number of instructions to decode (default 8)")]
[NodePort("lines",         PortDirection.Output, "string", Description = "Newline-joined disassembly lines: address + raw bytes + mnemonic")]
[NodePort("scanned_bytes", PortDirection.Output, "number", Description = "Bytes actually read at address_hex. 0 means the address is not readable; less than 128 means the readable range ended there")]
public sealed class DisasmPeekNode : INode
{
    sealed class BufOutput : FormatterOutput
    {
        readonly StringBuilder _sb = new StringBuilder();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public string Flush() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    // 7 バイト分。x64 の命令はほとんどここに収まり、収まらないものは列が伸びるだけ。
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

    public void Execute(IExecutionContext ctx)
    {
        var addrHex = (ctx.GetPortValue("address_hex") as string ?? "0").Trim();
        double countD = 8;
        if (ctx.GetPortValue("count") is double c) countD = c;

        if (!long.TryParse(addrHex.Replace("0x", "").Replace("0X", ""), System.Globalization.NumberStyles.HexNumber, null, out var addr))
        {
            ctx.Logger.LogError($"[DisasmPeek] Failed to parse address_hex: {addrHex}");
            ctx.SetPortValue("lines", "");
            ctx.SetPortValue("scanned_bytes", 0.0);
            return;
        }

        const int bufLen = 128;
        var bytes = new byte[bufLen];
        // 読めない番地への Marshal.Copy は例外にならずプロセスごと落ちる。
        //    try/catch では守れないので、触る前に読めるかを確かめる。
        var readable = NgolSafeMemory.Read(new IntPtr(addr), bytes, 0, bufLen);
        ctx.SetPortValue("scanned_bytes", (double)readable);
        if (readable <= 0)
        {
            ctx.Logger.LogError($"[DisasmPeek] Not readable at 0x{addr:X}");
            ctx.SetPortValue("lines", "");
            return;
        }

        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Iced.Intel.Decoder.Create(64, reader);
        decoder.IP = (ulong)addr;

        var formatter = new NasmFormatter();
        var fmtOut = new BufOutput();
        var lines = new List<string>();

        // 読めた分の外は復号しない。バッファの未読部分は 0 のままで、命令として解釈すると嘘になる。
        var endIP = (ulong)addr + (ulong)readable;
        for (int i = 0; i < (int)countD && decoder.IP < endIP; i++)
        {
            var offset = (int)((long)decoder.IP - addr);
            var instr = decoder.Decode();
            if (instr.Code == Code.INVALID) { lines.Add($"0x{instr.IP:X} (invalid)"); break; }
            formatter.Format(instr, fmtOut);
            lines.Add($"0x{instr.IP:X}  {HexRun(bytes, offset, instr.Length),-BytesColumnWidth}  {fmtOut.Flush()}");
        }

        var result = string.Join("\n", lines);
        ctx.SetPortValue("lines", result);
        ctx.Logger.LogInfo($"[DisasmPeek]\n{result}");
    }
}
