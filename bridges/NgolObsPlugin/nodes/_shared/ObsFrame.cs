using System;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが描いた 1 枚を扱う。
///
/// 画面を撮るのではなく、ホストに描かせて出来上がった画素をそのまま受け取る。
/// 周りのもの（一覧・題・重なった別の窓）が写らず、プレビューの縮小にも
/// 左右されず、出力する大きさそのままで来る。
///
/// 縮めるのはここでする。ネイティブ側は渡すところまでしかしない--
/// 薄いままにしておけば、見せ方を直すのにホストの再起動が要らない。
/// </summary>
internal static class ObsFrame
{
    /// <summary>控えを引き取る。並びは B,G,R,A。</summary>
    internal static string Take(out byte[] raw, out int width, out int height, out int pitch)
    {
        raw = null;
        int need = ObsNative.Ngol_Obs_TakeFrame(null, 0, out width, out height, out pitch);
        if (need <= 0 || width <= 0 || height <= 0)
            return "nothing has been drawn yet";

        raw = new byte[need];
        if (ObsNative.Ngol_Obs_TakeFrame(raw, raw.Length, out width, out height, out pitch) != need)
        {
            raw = null;
            return "the picture changed size while it was being collected";
        }
        return null;
    }

    /// <summary>幅がこれ以下になるまで縮める倍率。0 を渡すと縮めない。</summary>
    internal static int StepFor(int width, int maxWidth)
    {
        if (maxWidth <= 0) return 1;
        int step = 1;
        while (width / (step + 1) >= maxWidth) step++;
        return step;
    }

    /// <summary>
    /// 縮める。step 個ごとに 1 つ取るのではなく、その範囲を平らにならす。
    /// 間引くだけだと細い線が消えて文字が読めなくなり、
    /// 「小さくても何が描かれたか分かる」という目的を外す。
    ///
    /// 並びは元のまま返す（B,G,R,A のうち先頭 3 つ）。
    /// </summary>
    internal static byte[] Shrink(byte[] src, int width, int height, int pitch, int step,
                                  out int outWidth, out int outHeight, out double lit)
    {
        outWidth = Math.Max(1, width / step);
        outHeight = Math.Max(1, height / step);
        var dst = new byte[outWidth * outHeight * 4];
        long litCount = 0;
        int area = step * step;

        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int c0 = 0, c1 = 0, c2 = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int row = (y * step + dy) * pitch;
                    for (int dx = 0; dx < step; dx++)
                    {
                        int s = row + (x * step + dx) * 4;
                        c0 += src[s];
                        c1 += src[s + 1];
                        c2 += src[s + 2];
                    }
                }
                int d = (y * outWidth + x) * 4;
                dst[d] = (byte)(c0 / area);
                dst[d + 1] = (byte)(c1 / area);
                dst[d + 2] = (byte)(c2 / area);
                dst[d + 3] = 255;
                if (dst[d] != 0 || dst[d + 1] != 0 || dst[d + 2] != 0) litCount++;
            }
        }

        lit = (double)litCount / (outWidth * outHeight);
        return dst;
    }
}
