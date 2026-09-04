using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが描いた 1 枚を受け取る。
///
/// 描画を頼むと、出来上がった画素がプラグイン側へ渡ってくる。画素はその場でしか
/// 有効でないのでプラグインが写して控え、こちらは控えを引き取る。
/// 画面を撮る必要は無く、撮るより上でもある（周りのものが写らない・
/// 他の窓の重なりに左右されない・出力する大きさそのまま）。
///
/// 縮めるのはここでする。ホスト側は渡すところまでしかしない。
/// </summary>
internal static class AviUtl2Frame
{
    // disasm-verified 2026-08-20: エクスポート RVA 0x9ee0 は 0x6510 への jmp。
    //   実体のプロローグは push rbx + sub rsp,60h でずれ 0x68。[rsp+X] の読み取りは
    //   すべて X<=0x50 なので局所変数だけで、スタック経由の引数は無い。
    //   ecx は書き換えられないまま call r9 の第 1 引数へ素通しされる。
    //   => 引数 1 個（ecx=32bit）／ 戻り値は movzx eax,bl で al の 8bit。
    //   RVA は作り直すと動く（実測 0x8570 -> 0x9ee0）。名前で解決すること。
    [DllImport("NgolForAviUtl2.aux2")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool Ngol_RenderScene(int frame);

    // signature-owned: Ngol_TakeFrame は本ブリッジの plugin.cpp が定義している。
    //   int Ngol_TakeFrame(unsigned char*, int, int*, int*, int*, unsigned int*)
    //   置き場を渡さずに呼べば、要る大きさだけが返る。他の口と同じ作法。
    [DllImport("NgolForAviUtl2.aux2")]
    internal static extern int Ngol_TakeFrame(byte[] outBytes, int outLen,
                                              out int width, out int height, out int pitch, out uint seq);

    /// <summary>1 枚描かせて受け取る。取れなければ理由を文で返す。</summary>
    internal static string Take(int frame, int timeoutMs,
                                out byte[] raw, out int width, out int height, out int pitch)
    {
        raw = null;
        // 描き終わった枚数を先に控える。増えたことで「新しい 1 枚」だと分かる。
        Ngol_TakeFrame(null, 0, out width, out height, out pitch, out uint before);

        if (!Ngol_RenderScene(frame))
            return "the host refused to draw frame " + frame
                 + "; it may be writing a file, or the frame may hold nothing";

        var watch = Stopwatch.StartNew();
        int need = 0;
        uint now = before;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            need = Ngol_TakeFrame(null, 0, out width, out height, out pitch, out now);
            if (now != before && need > 0) break;
            System.Threading.Thread.Sleep(1);
        }
        if (now == before || need <= 0)
            return "drawing frame " + frame + " did not finish within " + timeoutMs + "ms";

        raw = new byte[need];
        if (Ngol_TakeFrame(raw, raw.Length, out width, out height, out pitch, out _) != need
            || width <= 0 || height <= 0)
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
    /// 間引くだけだと細い線が消え、文字が読めなくなる。確かめるための絵なので、
    /// 小さくしても何が描かれたか分かることの方が要る。
    ///
    /// 元は 1 画素 4 バイトの R,G,B,A。返すのは bytesPerPixel で決める。
    ///
    /// keepAlpha を立てると、透ける度合いをホストが渡してきたまま通す。
    /// 立てないと不透明で埋める--重ねる相手が居ない用途ではその方が扱いやすく、
    /// 符号化する側も透過を持てない形が既定だから。
    /// bytesPerPixel が 4 未満なら透ける度合いは置く場所が無いので捨てられる。
    /// </summary>
    internal static byte[] Shrink(byte[] src, int width, int height, int pitch, int step,
                                  int bytesPerPixel, out int outWidth, out int outHeight, out double lit,
                                  bool keepAlpha = false)
    {
        // 縦横は偶数にそろえる。奇数だと、受け取る側が 1 枚あたりの長さを
        //   こちらと違う数で見積もり、途中から全部ずれる（絵が斜めに流れる形で出る）。
        outWidth = Math.Max(2, (width / step) & ~1);
        outHeight = Math.Max(2, (height / step) & ~1);
        var dst = new byte[outWidth * outHeight * bytesPerPixel];
        long litCount = 0;
        int area = step * step;

        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int row = (y * step + dy) * pitch;
                    for (int dx = 0; dx < step; dx++)
                    {
                        int s = row + (x * step + dx) * 4;
                        r += src[s];
                        g += src[s + 1];
                        b += src[s + 2];
                        a += src[s + 3];
                    }
                }
                int d = (y * outWidth + x) * bytesPerPixel;
                dst[d] = (byte)(r / area);
                dst[d + 1] = (byte)(g / area);
                dst[d + 2] = (byte)(b / area);
                if (bytesPerPixel > 3) dst[d + 3] = keepAlpha ? (byte)(a / area) : (byte)255;
                if (dst[d] != 0 || dst[d + 1] != 0 || dst[d + 2] != 0) litCount++;
            }
        }

        lit = (double)litCount / (outWidth * outHeight);
        return dst;
    }
}
