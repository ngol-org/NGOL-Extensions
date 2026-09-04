using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// PE ファイルの素性を、ディスク上のファイルとして読む。
///
/// このノードは、他のノードと同じく NGOL が動いているホストの中で実行される。
///   動いていなくてよいのは「調べる相手」の方で、NGOL 自身ではない。
///   ここで読むのはメモリ上のモジュールではなく、指定したパスのファイルである。
///
/// 他の code.* は読み込み済みのモジュールを対象にするため、まだ起動していない実行ファイルは
/// 調べられない。どの経路で入れるか（プロキシ DLL / マネージド向けの仕組み）、静的に解析して
/// 意味があるか、エントリポイントより前に走るコードがあるか--これらは相手を起動する前に
/// 決まっているので、起動を待つ理由が無い。
///
/// 読み取った値は「起動したあと、そのプロセスをどう扱うか」の事前知識になる。
///
///   エントロピーが高い区画がある
///       -> ファイル上のコードは圧縮・暗号化されている。展開は起動後に起きるので、
///         ファイルの中身とメモリの中身が一致しない。disasm はメモリ側で行う。
///   TLS コールバックがある
///       -> エントリポイントより前に走るコードがある。そこには後から入れない。
///   マネージド（CLR ヘッダを持つ）
///       -> ランタイムを実行時に読み込むため、プロキシ DLL の前提が成立しない。
///         起動後も、ネイティブの逆アセンブルが届くのはランタイム本体の側だけ。
///   ビット数
///       -> 一致しないローダー・フックは読み込まれない。
///   署名がある
///       -> ファイルを書き換えると起動しなくなりうる。実行中のフックの方を選ぶ。
///   区画が書き込み可能か・実行可能か
///       -> 起動後にどこへ手を入れられるかの目安。
///
/// 出すのは事実だけ。どう扱うべきかは判定しない。
/// </summary>
[NodeType("ngol.code.pe_info", "Code", "PE Info",
    Version = "1.0.2",
    Description = "Read what a PE file on disk is: machine type, sections with their entropy, TLS callbacks, whether "
      + "it is managed, and how many exports it has. The target does not have to be running.")]
[NodePort("path", PortDirection.Input, "string", IsRequired = true, Description = "PE file to read (.exe / .dll)")]
[NodePort("text", PortDirection.Output, "string", Description = "Everything that was read, as a report")]
[NodePort("machine", PortDirection.Output, "string", Description = "x64 / x86 / arm64 / ...")]
[NodePort("is_managed", PortDirection.Output, "boolean", Description = "true when the file carries a CLR header (a .NET assembly)")]
[NodePort("tls_callbacks", PortDirection.Output, "number", Description = "How many callbacks run before the entry point")]
[NodePort("max_entropy", PortDirection.Output, "number", Description = "Highest entropy of any section. Close to 8 suggests packing or encryption - it is a hint, not a verdict")]
public sealed class PeInfoNode : INode
{
    /// <summary>圧縮・暗号化を疑う目安。断定はできない（データだけの区画も高く出る）。</summary>
    private const double HighEntropy = 7.2;

    public void Execute(IExecutionContext ctx)
    {
        var path = ctx.GetPortValue("path") as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ctx.SetPortValue("text", "file not found: " + path);
            return;
        }

        var b = File.ReadAllBytes(path);
        var sb = new StringBuilder();

        var peOff = BitConverter.ToInt32(b, 0x3C);
        if (BitConverter.ToUInt32(b, peOff) != 0x00004550)
        {
            ctx.SetPortValue("text", "not a PE file");
            return;
        }

        var machine = BitConverter.ToUInt16(b, peOff + 4);
        var machineName = machine == 0x8664 ? "x64" : machine == 0x14c ? "x86"
                        : machine == 0xaa64 ? "arm64" : "0x" + machine.ToString("x");
        var numSections = BitConverter.ToUInt16(b, peOff + 6);
        var timestamp = BitConverter.ToUInt32(b, peOff + 8);
        var characteristics = BitConverter.ToUInt16(b, peOff + 22);
        var isDll = (characteristics & 0x2000) != 0;

        var optOff = peOff + 24;
        var optSize = BitConverter.ToUInt16(b, peOff + 20);
        var plus = BitConverter.ToUInt16(b, optOff) == 0x20B;
        var entryRva = BitConverter.ToUInt32(b, optOff + 16);
        var subsystem = BitConverter.ToUInt16(b, optOff + (plus ? 68 : 68));
        var dirOff = optOff + (plus ? 112 : 96);

        sb.AppendLine($"kind      : {machineName} {(isDll ? "DLL" : "EXE")}  subsystem={SubsystemName(subsystem)}");
        sb.AppendLine($"entry     : RVA 0x{entryRva:x}");
        sb.AppendLine($"timestamp : {StampText(timestamp)}");

        // --- セクション ---
        var sections = new List<(string Name, uint Va, uint VSize, uint RSize, uint Raw, uint Flags)>();
        var sectOff = optOff + optSize;
        for (int i = 0; i < numSections; i++)
        {
            var s = sectOff + i * 40;
            var name = Encoding.ASCII.GetString(b, s, 8).TrimEnd('\0', ' ');
            sections.Add((name,
                BitConverter.ToUInt32(b, s + 12), BitConverter.ToUInt32(b, s + 8),
                BitConverter.ToUInt32(b, s + 16), BitConverter.ToUInt32(b, s + 20),
                BitConverter.ToUInt32(b, s + 36)));
        }

        int Offset(uint rva)
        {
            foreach (var s in sections)
            {
                if (rva < s.Va) continue;
                var d = rva - s.Va;
                if (d >= Math.Max(s.VSize, s.RSize)) continue;
                if (d >= s.RSize) return -1;
                var o = (long)s.Raw + d;
                return o >= 0 && o < b.Length ? (int)o : -1;
            }
            return -1;
        }

        double maxEntropy = 0;
        sb.AppendLine();
        sb.AppendLine("sections:");
        sb.AppendLine("  name       virtKB    rawKB      entropy  attr");
        foreach (var s in sections)
        {
            var e = s.RSize == 0 ? 0 : Entropy(b, (int)s.Raw, (int)Math.Min(s.RSize, (uint)Math.Max(0, b.Length - (int)s.Raw)));
            if (e > maxEntropy) maxEntropy = e;
            var attr = ((s.Flags & 0x20000000) != 0 ? "X" : "-")
                     + ((s.Flags & 0x80000000) != 0 ? "W" : "-")
                     + ((s.Flags & 0x40000000) != 0 ? "R" : "-");
            var mark = e >= HighEntropy ? "  high" : "";
            sb.AppendLine($"  {s.Name,-10} {s.VSize / 1024,7} {s.RSize / 1024,7}   {e,8:F2}  {attr}{mark}");
        }

        // --- マネージドか（データディレクトリ 14 = CLR ヘッダ） ---
        var clrRva = BitConverter.ToUInt32(b, dirOff + 14 * 8);
        var isManaged = clrRva != 0;

        // --- TLS コールバック（データディレクトリ 9）---
        // エントリポイントより前に走る。注入もデバッガも、ここより後にしか入れない。
        //
        // 「ディレクトリが無い」と「ディレクトリはあるがコールバックが 0 個」を区別すること。
        //   前者は TLS を使っていない。後者は TLS データは使うが入口前のコードは無い、という意味で、
        //   別の道具が「TLS Directory あり」と言うのはこの状態も含む。
        var tlsRva = BitConverter.ToUInt32(b, dirOff + 9 * 8);
        var hasTlsDirectory = tlsRva != 0;
        var tlsCount = 0;
        if (tlsRva != 0)
        {
            var t = Offset(tlsRva);
            if (t >= 0)
            {
                // TLS ディレクトリの AddressOfCallBacks は「読み込み後の番地」で入っている。
                // 画像基準を引いて RVA に戻してから辿る。
                var imageBase = plus ? BitConverter.ToUInt64(b, optOff + 24) : BitConverter.ToUInt32(b, optOff + 28);
                var cbField = plus ? BitConverter.ToUInt64(b, t + 24) : BitConverter.ToUInt32(b, t + 12);
                if (cbField > imageBase)
                {
                    var listOff = Offset((uint)(cbField - imageBase));
                    var step = plus ? 8 : 4;
                    while (listOff >= 0 && listOff + step <= b.Length)
                    {
                        var v = plus ? BitConverter.ToUInt64(b, listOff) : BitConverter.ToUInt32(b, listOff);
                        if (v == 0) break;
                        tlsCount++;
                        listOff += step;
                        if (tlsCount > 256) break;      // 壊れた表で回り続けない
                    }
                }
            }
        }

        // --- export 数（データディレクトリ 0）---
        var expRva = BitConverter.ToUInt32(b, dirOff + 0 * 8);
        var exportCount = 0;
        string exportDllName = null;
        if (expRva != 0)
        {
            var e = Offset(expRva);
            if (e >= 0)
            {
                exportCount = (int)BitConverter.ToUInt32(b, e + 24);
                var nameOff = Offset(BitConverter.ToUInt32(b, e + 12));
                if (nameOff >= 0)
                {
                    var end = nameOff;
                    while (end < b.Length && b[end] != 0) end++;
                    exportDllName = Encoding.ASCII.GetString(b, nameOff, end - nameOff);
                }
            }
        }

        // --- 署名の有無（データディレクトリ 4）---
        var signed = BitConverter.ToUInt32(b, dirOff + 4 * 8 + 4) != 0;

        sb.AppendLine();
        sb.AppendLine($"managed   : {(isManaged ? "yes (.NET assembly)" : "no")}");
        var tlsText = tlsCount > 0 ? $"{tlsCount} callback(s), each running before the entry point"
                    : hasTlsDirectory ? "directory present but no callbacks - nothing runs before the entry point"
                    : "none";
        sb.AppendLine($"TLS       : {tlsText}");
        sb.AppendLine($"export    : {exportCount}{(exportDllName != null ? $" (internal name {exportDllName})" : "")}");
        sb.AppendLine($"signed    : {(signed ? "yes" : "no")}");
        if (maxEntropy >= HighEntropy)
            sb.AppendLine($"highest entropy {maxEntropy:F2} - possibly packed or encrypted, which limits how much can be read statically");

        ctx.SetPortValue("text", sb.ToString());
        ctx.SetPortValue("machine", machineName);
        ctx.SetPortValue("is_managed", isManaged);
        ctx.SetPortValue("tls_callbacks", tlsCount);
        ctx.SetPortValue("max_entropy", Math.Round(maxEntropy, 2));
    }

    private static string SubsystemName(ushort s) => s switch
    {
        2 => "GUI", 3 => "CUI", _ => s.ToString(),
    };

    /// <summary>
    /// ヘッダーのタイムスタンプ。これを「ビルド時刻」として読んではいけない。
    /// 再現可能なビルドでは、このフィールドには時刻ではなく内容から決まる値が入る。
    /// 未来の日付になっていたらまずそれで、実際の時刻ではない。
    /// </summary>
    private static string StampText(uint stamp)
    {
        if (stamp == 0) return "none";
        var t = DateTimeOffset.FromUnixTimeSeconds(stamp).LocalDateTime;
        var plausible = t > new DateTime(1995, 1, 1) && t <= DateTime.Now.AddDays(1);
        return plausible
            ? $"{t:yyyy-MM-dd HH:mm:ss}"
            : $"0x{stamp:x8} (not readable as a time - a reproducible build puts a content-derived value here)";
    }

    /// <summary>バイトの散らばり具合（0〜8）。8 に近いほど偏りが無い＝圧縮・暗号化の疑い。</summary>
    private static double Entropy(byte[] data, int offset, int length)
    {
        if (length <= 0 || offset < 0 || offset >= data.Length) return 0;
        length = Math.Min(length, data.Length - offset);

        var counts = new int[256];
        for (int i = 0; i < length; i++) counts[data[offset + i]]++;

        double e = 0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            var p = (double)counts[i] / length;
            e -= p * Math.Log(p, 2);
        }
        return e;
    }
}
