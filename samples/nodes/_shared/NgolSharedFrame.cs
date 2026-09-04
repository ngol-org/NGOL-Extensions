using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 名前を付けた場所へ絵を 1 枚置き、別のプロセスがそのまま読む。
///
/// ファイルに書き出して渡す道もあるが、そちらは 1 本の素材として固まる。
/// こちらは置き換えるたびに読む側の次の 1 枚が変わるので、
/// 手元で作りながら相手の画に出したいときに向く。
///
/// 先頭 64 バイトがヘッダーで、その後ろが画素。並びは B,G,R,A。
///
/// 書く側と読む側は待ち合わせをしない。何もしなければ、
/// 上半分が新しく下半分が古い絵が読まれる。
/// そこで通し番号を画素の書き込みの前後で挟む--
/// 書く前に奇数へ、書き終えたら偶数へ。読む側は前後で番号を見て、
/// 奇数だったか値が変わっていたら、その 1 枚を諦めて前の絵を使う。
/// 待たせない方が正しい。待たせると相手の描画そのものが止まる。
/// </summary>
internal static class NgolSharedFrame
{
    internal const int HeaderBytes = 64;
    internal const uint Magic0 = 0x4C4F474E;   // "NGOL"
    internal const uint Magic1 = 0x004D5246;   // "FRM\0"
    internal const uint Version = 1;
    internal const uint FormatBgra = 0;

    // ヘッダーの並び。読む側（C++）と同じ位置を指していること。
    private const int OffMagic0 = 0;
    private const int OffMagic1 = 4;
    private const int OffVersion = 8;
    private const int OffWidth = 12;
    private const int OffHeight = 16;
    private const int OffStride = 20;
    private const int OffFormat = 24;
    private const int OffSequence = 28;
    private const int OffByteCount = 32;

    /// <summary>置き場の名前。同じ利用者の同じログオンの中だけで通じる。</summary>
    internal static string PathFor(string name)
    {
        return "Local" + "\\" + "ngol.frame." + name;
    }

    internal readonly struct Info
    {
        internal Info(int width, int height, int stride, uint format, uint sequence, int byteCount)
        {
            Width = width; Height = height; Stride = stride;
            Format = format; Sequence = sequence; ByteCount = byteCount;
        }
        internal int Width { get; }
        internal int Height { get; }
        internal int Stride { get; }
        internal uint Format { get; }
        internal uint Sequence { get; }
        internal int ByteCount { get; }
    }

    // 置いた場所を掴んだままにしておく入れ物。
    //
    // ここを離すと置いたものが消える。名前を付けた領域は、
    // それを開いている者が 1 人も居なくなった時点で os が捨てるので、
    // 書き終えて閉じると、読む側が見に来たときにはもう無い。
    //
    // 持ち場は AppDomain に置く。ノードの静的な場所に置くと、
    // ソースを書き直したときに古い側ごと捨てられ、そこで領域も消える。
    private const string HoldKey = "ngol.shared_frame.holds";

    private static System.Collections.Generic.Dictionary<string, MemoryMappedFile> Holds()
    {
        var held = AppDomain.CurrentDomain.GetData(HoldKey)
                   as System.Collections.Generic.Dictionary<string, MemoryMappedFile>;
        if (held == null)
        {
            held = new System.Collections.Generic.Dictionary<string, MemoryMappedFile>();
            AppDomain.CurrentDomain.SetData(HoldKey, held);
        }
        return held;
    }

    private const string SizeKey = "ngol.shared_frame.sizes";

    private static System.Collections.Generic.Dictionary<string, int> Sizes()
    {
        var sizes = AppDomain.CurrentDomain.GetData(SizeKey)
                    as System.Collections.Generic.Dictionary<string, int>;
        if (sizes == null)
        {
            sizes = new System.Collections.Generic.Dictionary<string, int>();
            AppDomain.CurrentDomain.SetData(SizeKey, sizes);
        }
        return sizes;
    }

    /// <summary>
    /// 絵を 1 枚置く。無ければ作り、大きさが足りなければ作り直す。
    /// 戻り値が null なら置けた。null でなければ理由。
    /// </summary>
    internal static string Write(string name, byte[] bgra, int width, int height, int stride,
                                 out uint sequence)
    {
        sequence = 0;
        if (string.IsNullOrEmpty(name)) return "give a name for the place to put it in";
        if (bgra == null || bgra.Length == 0) return "there are no pixels to put";
        if (width <= 0 || height <= 0) return "the size makes no sense";

        int need = HeaderBytes + bgra.Length;
        try
        {
            var holds = Holds();
            var sizes = Sizes();
            holds.TryGetValue(name, out MemoryMappedFile map);
            sizes.TryGetValue(name, out int have);

            // 入りきらなくなったら作り直す。読む側は次に開いたときに新しいほうを掴む。
            if (map != null && have < need)
            {
                map.Dispose();
                map = null;
            }
            if (map == null)
            {
                map = MemoryMappedFile.CreateOrOpen(PathFor(name), need);
                holds[name] = map;
                sizes[name] = need;
            }

            using (var view = map.CreateViewAccessor(0, sizes[name]))
            {
                uint before = view.ReadUInt32(OffSequence);
                // 書いている間だけ奇数にする。読む側はこれを見て、その 1 枚を諦める。
                uint marker = (before | 1u);
                if (marker == before) marker = before + 2u;
                view.Write(OffSequence, marker);

                view.Write(OffMagic0, Magic0);
                view.Write(OffMagic1, Magic1);
                view.Write(OffVersion, Version);
                view.Write(OffWidth, (uint)width);
                view.Write(OffHeight, (uint)height);
                view.Write(OffStride, (uint)stride);
                view.Write(OffFormat, FormatBgra);
                view.Write(OffByteCount, (uint)bgra.Length);
                view.WriteArray(HeaderBytes, bgra, 0, bgra.Length);

                sequence = marker + 1u;
                view.Write(OffSequence, sequence);
                return null;
            }
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// ヘッダーだけを読む。絵が出ないときに、書けていないのか読めていないのかを分けるための口。
    /// 戻り値が null なら読めた。
    /// </summary>
    internal static string ReadInfo(string name, out Info info)
    {
        info = default;
        if (string.IsNullOrEmpty(name)) return "give the name of the place to look at";

        try
        {
            using (var map = MemoryMappedFile.OpenExisting(PathFor(name),
                                                           MemoryMappedFileRights.Read))
            using (var view = map.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read))
            {
                if (view.ReadUInt32(OffMagic0) != Magic0 || view.ReadUInt32(OffMagic1) != Magic1)
                    return "that place holds something else";
                uint version = view.ReadUInt32(OffVersion);
                if (version != Version)
                    return "that place was written by version " + version + ", this reads " + Version;

                info = new Info((int)view.ReadUInt32(OffWidth),
                                (int)view.ReadUInt32(OffHeight),
                                (int)view.ReadUInt32(OffStride),
                                view.ReadUInt32(OffFormat),
                                view.ReadUInt32(OffSequence),
                                (int)view.ReadUInt32(OffByteCount));
                return null;
            }
        }
        catch (System.IO.FileNotFoundException)
        {
            return "nothing has been put there yet";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// 画素を間引いて読む。主要な色を数えるときに使う。
    /// 戻り値が null なら読めた。
    /// </summary>
    internal static string ReadPixels(string name, out byte[] bgra, out Info info)
    {
        bgra = null;
        string problem = ReadInfo(name, out info);
        if (problem != null) return problem;
        if (info.ByteCount <= 0) return "there are no pixels there";

        try
        {
            using (var map = MemoryMappedFile.OpenExisting(PathFor(name),
                                                           MemoryMappedFileRights.Read))
            using (var view = map.CreateViewAccessor(0, HeaderBytes + info.ByteCount,
                                                     MemoryMappedFileAccess.Read))
            {
                // 前後で番号を見る。挟んだ中で変わっていれば、書き換えの途中を読んでいる。
                uint before = view.ReadUInt32(OffSequence);
                if ((before & 1u) != 0u) return "it is being written to right now";

                var buffer = new byte[info.ByteCount];
                view.ReadArray(HeaderBytes, buffer, 0, buffer.Length);

                if (view.ReadUInt32(OffSequence) != before)
                    return "it changed while it was being read";

                bgra = buffer;
                return null;
            }
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>画素のうち、多くを占めている色を多い順に返す。判定を数値で行うための口。</summary>
    internal static string TopColours(byte[] bgra, int take = 5)
    {
        if (bgra == null || bgra.Length < 4) return "";
        const int Step = 24;
        const int DarkFloor = 90;
        const byte SeeThrough = 40;

        var counts = new System.Collections.Generic.Dictionary<int, int>();
        int total = bgra.Length / 4;
        int kept = 0;
        for (int i = 0; i < total; i++)
        {
            int p = i * 4;
            if (bgra[p + 3] <= SeeThrough) continue;
            int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
            if (r + g + b < DarkFloor) continue;
            int key = ((r / Step) << 16) | ((g / Step) << 8) | (b / Step);
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
            kept++;
        }
        if (kept == 0) return "";

        var order = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>>(counts);
        order.Sort((x, y) => y.Value.CompareTo(x.Value));

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < order.Count && i < take; i++)
        {
            int key = order[i].Key;
            int r = Math.Min(255, ((key >> 16) & 0xFF) * Step + Step / 2);
            int g = Math.Min(255, ((key >> 8) & 0xFF) * Step + Step / 2);
            int b = Math.Min(255, (key & 0xFF) * Step + Step / 2);
            sb.Append(r.ToString("x2")).Append(g.ToString("x2")).Append(b.ToString("x2"))
              .Append('=').Append(((double)order[i].Value / total).ToString("0.000"))
              .Append((char)10);
        }
        return sb.ToString().TrimEnd((char)10);
    }
}
