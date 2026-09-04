using System;
using System.IO;
using System.IO.Compression;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 24bit PNG を外部ライブラリなしで書く。標準の deflate だけを使うので、
/// どのランタイムでも同じように動く（画像ライブラリを持ち込まない）。
///
/// BMP より PNG が向く場面がある--BMP を開けない読み取り側があるため、
/// 検証結果を人にも道具にも渡すなら PNG の方が確実。
/// </summary>
internal static class NgolPng
{
    /// <summary>
    /// マップ済みの 32bit 画素から PNG を組み立てる。
    /// <paramref name="rowPitch"/> は行の間隔（幅とは限らない）。
    /// <paramref name="bottomUp"/> は元が最下行から並んでいるとき true。
    /// <paramref name="swapRedBlue"/> は元が R,G,B,A のとき true（PNG は R,G,B の順）。
    /// </summary>
    internal static byte[] Build(IntPtr pData, int width, int height, uint rowPitch,
                                 bool bottomUp, bool swapRedBlue)
    {
        // 走査線ごとに「フィルタ種別」を 1 バイト先頭へ置くのが PNG の規約。0 = フィルタなし。
        var raw = new byte[(width * 3 + 1) * height];
        var srcRow = new byte[width * 4];
        var pos = 0;

        for (int y = 0; y < height; y++)
        {
            int srcY = bottomUp ? (height - 1 - y) : y;
            var addr = new IntPtr(pData.ToInt64() + (long)srcY * rowPitch);
            System.Runtime.InteropServices.Marshal.Copy(addr, srcRow, 0, srcRow.Length);

            raw[pos++] = 0;
            for (int x = 0; x < width; x++)
            {
                int s = x * 4;
                // 元が B,G,R,A なら b=srcRow[s]、R,G,B,A なら r=srcRow[s]。
                byte c0 = srcRow[s], c2 = srcRow[s + 2];
                raw[pos++] = swapRedBlue ? c0 : c2;   // R
                raw[pos++] = srcRow[s + 1];           // G
                raw[pos++] = swapRedBlue ? c2 : c0;   // B
            }
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78); ms.WriteByte(0x9C);   // zlib ヘッダ（deflate / 32KB 窓）
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
            var adler = Adler32(raw);
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
            compressed = ms.ToArray();
        }

        using (var outMs = new MemoryStream())
        {
            outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, width);
            WriteBigEndian(ihdr, 4, height);
            ihdr[8] = 8;    // ビット深度
            ihdr[9] = 2;    // カラータイプ 2 = トゥルーカラー(RGB)
            WriteChunk(outMs, "IHDR", ihdr);
            WriteChunk(outMs, "IDAT", compressed);
            WriteChunk(outMs, "IEND", new byte[0]);
            return outMs.ToArray();
        }
    }

    static void WriteBigEndian(byte[] buf, int offset, int value)
    {
        buf[offset + 0] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    static void WriteChunk(Stream fs, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, data.Length);
        fs.Write(len, 0, 4);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        fs.Write(typeBytes, 0, 4);
        fs.Write(data, 0, data.Length);

        // CRC は「チャンク種別 -> データ」の順に掛ける（長さは含めない）。
        //    順序を逆にすると、署名もサイズも妥当なのに読み込みだけが失敗する。
        var crc = Crc32(data, Crc32(typeBytes, 0xFFFFFFFFu, false), true);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        fs.Write(crcBytes, 0, 4);
    }

    static uint[] s_crcTable;

    static uint Crc32(byte[] data, uint seed, bool finish)
    {
        if (s_crcTable == null)
        {
            s_crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                s_crcTable[n] = c;
            }
        }
        var crc = seed;
        foreach (var b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return finish ? crc ^ 0xFFFFFFFFu : crc;
    }

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
