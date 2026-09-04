using System;
using System.Collections.Generic;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// サイコロの絵と形を組み立てる。ここはグラフィックスの API に一切触れないので、
/// どの描画方式からでもそのまま使える。
///
/// 絵は外部ファイルを持たず、その場で画素を並べる。サンプルが .cs だけで完結する。
/// </summary>
internal static class DiceGeometry
{
    // --- 絵 ---

    internal const int FaceCount = 6;
    internal const int FacePixels = 128;
    internal const int AtlasWidth = FacePixels * FaceCount;
    internal const int AtlasHeight = FacePixels;

    // 目の中心を置く格子の、面の端からの距離（面の一辺に対する割合）。添字は目の数 - 1。
    // 二の目は対角に 2 つしか無いので、他と同じ格子に置くと外へ張り出して見える。
    // そこだけ内側へ寄せてある。一の目は面の真ん中に 1 つなので、この値は効かない。
    private static readonly float[] PipMargins = { 0.22f, 0.26f, 0.22f, 0.22f, 0.22f, 0.22f };

    // 目 1 つの半径（面の一辺に対する割合）。添字は目の数 - 1。
    // 和式は目の容積を数に反比例させ、どの面も削れる量が同じになるようにしてある
    // （重心が偏らないための工夫）。深さを揃えるなら半径は 1/sqrtn に比例するので、
    // 三以降はその値をそのまま使っている。
    // 一の目は法則から外れて大きく彫ってあり、二の目は面に 2 つしか無いぶん大きく見えるので、
    // その 2 つだけ実物に寄せてある。
    private static readonly float[] PipRadii = { 0.170f, 0.105f, 0.098f, 0.085f, 0.076f, 0.069f };

    // 彫り込みの底が占める割合。ここから外は斜面として陰影を付ける。
    private const float PipFloor = 0.45f;
    // 斜面での明るさの増減。掛け算ではなく足し引きにする。掛け算だと暗い色ほど差が出ず、
    // 黒い目と赤い目で効きがそろわない。
    // なお陰影はテクスチャに焼いてあるので、立体が回っても光の向きは面に貼り付いたまま。
    // 向きまで合わせるには面ごとの向きを使ったライティングが要る。
    private const float PipRelief = 0.24f;

    // 目の位置は 3x3 の格子で表す。番号は左上から右下へ 0..8。
    //   0 1 2
    //   3 4 5
    //   6 7 8
    // 目の数ごとに使う位置。左右対称なので短い表で済む。
    private static readonly int[][] PipPositions =
    {
        new[] { 4 },
        new[] { 0, 8 },
        new[] { 0, 4, 8 },
        new[] { 0, 2, 6, 8 },
        new[] { 0, 2, 4, 6, 8 },
        new[] { 0, 2, 3, 5, 6, 8 },
    };

    // 面の並びは BuildDice の面順に対応する。向かい合う面の和が 7 になるよう割り当てる。
    private static readonly int[] FacePips = { 1, 6, 2, 5, 3, 4 };

    private static readonly float[][] FaceColors =
    {
        new[] { 0.93f, 0.93f, 0.90f }, new[] { 0.93f, 0.93f, 0.90f },
        new[] { 0.90f, 0.90f, 0.87f }, new[] { 0.90f, 0.90f, 0.87f },
        new[] { 0.87f, 0.87f, 0.84f }, new[] { 0.87f, 0.87f, 0.84f },
    };

    /// <summary>
    /// 6 面を横に並べた 1 枚の絵を組み立てる。面 i は x が [i*128, (i+1)*128) の範囲。
    /// 目は円なので、中心からの距離だけで内側かどうかが決まる。
    /// 縁を少しなめらかにして、拡大したときの階段を目立たなくする。
    /// </summary>
    internal static byte[] BuildAtlas()
    {
        var px = new byte[AtlasWidth * AtlasHeight * 4];

        for (var face = 0; face < FaceCount; face++)
        {
            var bg = FaceColors[face];
            var n = FacePips[face];
            var pips = PipPositions[n - 1];
            // 一の目だけ赤い。日本のサイコロの特徴で、どの面かが一目で分かる。
            var pipColor = n == 1 ? new[] { 0.78f, 0.13f, 0.13f } : new[] { 0.12f, 0.12f, 0.14f };
            var radius = FacePixels * PipRadii[n - 1];

            // 外側の目の中心は端から margin、中央の目はちょうど真ん中。その間を等分する。
            var margin = PipMargins[n - 1];
            var origin = FacePixels * margin;
            var step = FacePixels * (1f - margin * 2f) * 0.5f;

            for (var y = 0; y < FacePixels; y++)
            for (var x = 0; x < FacePixels; x++)
            {
                var cover = 0f;
                var relief = 0f;
                foreach (var pos in pips)
                {
                    var cx = origin + (pos % 3) * step;
                    var cy = origin + (pos / 3) * step;
                    var dx = x + 0.5f - cx;
                    var dy = y + 0.5f - cy;
                    var d = (float)Math.Sqrt(dx * dx + dy * dy);
                    // 半径の内側を 1、外へ 1 画素かけて 0 まで落とす
                    var c = Math.Min(Math.Max(radius + 0.5f - d, 0f), 1f);
                    if (c <= cover) continue;
                    cover = c;

                    // 目は塗ってあるのではなく彫ってある。外周は斜面になるので、
                    // 光を背ける側が暗く、光を向く側は明るく残る。光は左上から当てた想定。
                    // くぼみ全体もわずかに落として、底が奥にあるように見せる。
                    var slope = Math.Min(Math.Max((d / radius - PipFloor) / (1f - PipFloor), 0f), 1f);
                    var toLight = -(dx + dy) / (radius * 1.4142f);
                    relief = slope * (PipRelief * toLight - 0.05f);
                }

                var o = ((y * AtlasWidth) + face * FacePixels + x) * 4;
                for (var ch = 0; ch < 3; ch++)
                    px[o + ch] = ToByte(bg[ch] + (pipColor[ch] + relief - bg[ch]) * cover);
                px[o + 3] = 255;
            }
        }

        return px;
    }

    private static byte ToByte(float v) => (byte)Math.Min(Math.Max(v * 255f + 0.5f, 0f), 255f);

    // --- 形 ---

    internal const int FloatsPerVertex = 8;             // 位置 3 ・色 3 ・テクスチャ座標 2
    /// 末尾に置いた単位クアッドの頂点数。
    internal const uint QuadVertexCount = 6;

    /// 角と辺の丸めの半径。立方体の半径に対する割合。
    private const float RoundRadius = 0.18f;
    /// 面を一辺あたり何分割するか。多いほど丸みがなめらかになる。
    private const int FaceSubdiv = 14;

    /// <summary>
    /// 角と辺を丸めたサイコロの頂点（位置・色・テクスチャ座標）。末尾に数字表示用の板が付く。
    ///
    /// 立方体の表面を格子に切り、各点を丸めた表面へ移す。移し方は 1 つの式で済む。
    /// 内側のひとまわり小さい箱へ落とした点を中心に、そこから半径ぶん押し出す。
    /// 面の上では法線の向きへ押し出すだけ、辺では円柱、角では球になり、
    /// 面・辺・角を場合分けせずに同じ扱いで書ける。
    ///
    /// 丸めても凸のままなので、深度バッファは要らず背面カリングだけで正しく見える。
    ///
    /// 巻き順は背面カリングの向きと一致していなければならない。手で並べると向きが揃わず、
    /// 揃っていない面だけが裏返って見える（描画は成功し、例外もログも出ない）。
    /// ここでは多角形と法線を渡し、向きが合っているかを機械的に確かめてから三角形にする。
    ///
    /// テクスチャ座標は丸める前の面の上で決める。丸みの部分では絵が少し詰まるが、
    /// 本物も塗りが角へ回り込んでいるので、そのほうが近い。
    /// </summary>
    internal static float[] Build()
    {
        const float h = 0.5f;
        const float r = h * RoundRadius;

        // 面の中心方向と、その面を張る 2 軸
        var faces = new[]
        {
            (n: new[] { 0f, 0f, -1f }, u: new[] { 1f, 0f, 0f }, v: new[] { 0f, 1f, 0f }),
            (n: new[] { 0f, 0f, 1f }, u: new[] { -1f, 0f, 0f }, v: new[] { 0f, 1f, 0f }),
            (n: new[] { -1f, 0f, 0f }, u: new[] { 0f, 0f, 1f }, v: new[] { 0f, 1f, 0f }),
            (n: new[] { 1f, 0f, 0f }, u: new[] { 0f, 0f, -1f }, v: new[] { 0f, 1f, 0f }),
            (n: new[] { 0f, -1f, 0f }, u: new[] { 1f, 0f, 0f }, v: new[] { 0f, 0f, 1f }),
            (n: new[] { 0f, 1f, 0f }, u: new[] { 1f, 0f, 0f }, v: new[] { 0f, 0f, -1f }),
        };

        var data = new List<float>(FaceCount * FaceSubdiv * FaceSubdiv * 6 * FloatsPerVertex);

        for (var i = 0; i < faces.Length; i++)
        {
            var f = faces[i];
            var u = f.u; var v = f.v;
            if (Dot(Cross(u, v), f.n) < 0f) { var t = u; u = v; v = t; }

            // 面ローカルの (s, t) から、丸める前の立方体表面の点を作る
            float[] Flat(float s, float t) => new[]
            {
                f.n[0] * h + u[0] * s + v[0] * t,
                f.n[1] * h + u[1] * s + v[1] * t,
                f.n[2] * h + u[2] * s + v[2] * t,
            };
            // 面の区画へ。縦は上下が逆になるので符号を反転する。
            float[] Uv(float s, float t) => new[] { (i + (s / h + 1f) * 0.5f) / FaceCount, (1f - t / h) * 0.5f };

            for (var gy = 0; gy < FaceSubdiv; gy++)
            for (var gx = 0; gx < FaceSubdiv; gx++)
            {
                float Coord(int g) => -h + 2f * h * g / FaceSubdiv;
                float s0 = Coord(gx), s1 = Coord(gx + 1);
                float t0 = Coord(gy), t1 = Coord(gy + 1);

                var poly = new[]
                {
                    RoundToBox(Flat(s0, t0), h, r), RoundToBox(Flat(s1, t0), h, r),
                    RoundToBox(Flat(s1, t1), h, r), RoundToBox(Flat(s0, t1), h, r),
                };
                var uv = new[] { Uv(s0, t0), Uv(s1, t0), Uv(s1, t1), Uv(s0, t1) };

                // 丸めた後の向きは、内側の箱から見た押し出しの向きそのもの。
                var normal = BoxNormal(Flat((s0 + s1) * 0.5f, (t0 + t1) * 0.5f), h, r);
                AddPolygon(data, poly, normal, uv);
            }
        }

        // 画面に数字を描くための単位クアッドを末尾に置く。xy が 0..1 の板で、
        // 描くときに定数の行列で位置と大きさを与える。
        // テクスチャは面の隅を指す。目は中ほどにしか無いので、そこは必ず地の色になる。
        // 巻き順はサイコロの面と揃えないと背面として捨てられる。
        float[][] quad =
        {
            new[] { 0f, 0f }, new[] { 1f, 1f }, new[] { 1f, 0f },
            new[] { 0f, 0f }, new[] { 0f, 1f }, new[] { 1f, 1f },
        };
        foreach (var q in quad)
        {
            data.Add(q[0]); data.Add(q[1]); data.Add(0f);
            data.Add(1f); data.Add(1f); data.Add(1f);
            data.Add(0.01f); data.Add(0.01f);
        }

        return data.ToArray();
    }

    /// <summary>
    /// 立方体の表面の点を、角丸の表面へ移す。
    /// 内側のひとまわり小さい箱へ落とした点が丸みの中心で、そこから半径ぶん押し出す。
    /// </summary>
    private static float[] RoundToBox(float[] p, float h, float r)
    {
        var d = OffsetFromInnerBox(p, h, r, out var q, out var len);
        if (len < 1e-6f) return p;
        return new[] { q[0] + d[0] / len * r, q[1] + d[1] / len * r, q[2] + d[2] / len * r };
    }

    /// <summary>丸めた表面のその点での向き。押し出す向きと同じ。</summary>
    private static float[] BoxNormal(float[] p, float h, float r)
    {
        var d = OffsetFromInnerBox(p, h, r, out _, out var len);
        if (len < 1e-6f) return new[] { 0f, 1f, 0f };
        return new[] { d[0] / len, d[1] / len, d[2] / len };
    }

    private static float[] OffsetFromInnerBox(float[] p, float h, float r, out float[] q, out float len)
    {
        var inner = h - r;
        q = new[] { Clamp(p[0], inner), Clamp(p[1], inner), Clamp(p[2], inner) };
        var d = new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] };
        len = (float)Math.Sqrt(Dot(d, d));
        return d;
    }

    private static float Clamp(float v, float limit) => Math.Min(Math.Max(v, -limit), limit);

    /// <summary>
    /// 多角形を三角形の扇に分けて積む。頂点の並びが法線と合っていなければ、その場で反転する。
    /// 向きを手で合わせようとすると、揃っていない面だけが無言で消える。
    /// </summary>
    private static void AddPolygon(List<float> data, float[][] poly, float[] normal, float[][] uv)
    {
        var e1 = new[] { poly[1][0] - poly[0][0], poly[1][1] - poly[0][1], poly[1][2] - poly[0][2] };
        var e2 = new[] { poly[2][0] - poly[0][0], poly[2][1] - poly[0][1], poly[2][2] - poly[0][2] };
        var forward = Dot(Cross(e1, e2), normal) >= 0f;

        var n = poly.Length;
        int Index(int k) => forward ? k : n - 1 - k;

        for (var k = 1; k + 1 < n; k++)
        {
            AddVertex(data, poly, uv, Index(0));
            AddVertex(data, poly, uv, Index(k));
            AddVertex(data, poly, uv, Index(k + 1));
        }
    }

    private static void AddVertex(List<float> data, float[][] poly, float[][] uv, int k)
    {
        data.Add(poly[k][0]); data.Add(poly[k][1]); data.Add(poly[k][2]);
        data.Add(1f); data.Add(1f); data.Add(1f);
        data.Add(uv[k][0]); data.Add(uv[k][1]);
    }

    private static float[] Cross(float[] a, float[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };

    private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    // --- 行列 ---

    /// <summary>
    /// 回転と見え方をまとめた行列。行ベクトル規約。
    /// 世界（回転）・視点（奥へ寄せる）・投影を 1 つにまとめてある。
    /// </summary>
    internal static float[] BuildMvp(float angle, float aspect)
    {
        var cy = (float)Math.Cos(angle); var sy = (float)Math.Sin(angle);
        var cx = (float)Math.Cos(angle * 0.6f); var sx = (float)Math.Sin(angle * 0.6f);

        // Y 回転のあとに X 回転
        var m = new float[16];
        float m00 = cy, m01 = 0, m02 = -sy;
        float m10 = sy * sx, m11 = cx, m12 = cy * sx;
        float m20 = sy * cx, m21 = -sx, m22 = cy * cx;

        const float dist = 3.2f;
        var fovY = 1.0f;                       // ラジアン
        var f = 1.0f / (float)Math.Tan(fovY * 0.5f);
        const float zn = 0.1f, zf = 100f;

        m[0] = m00 * f / aspect; m[1] = m01 * f; m[2] = m02 * zf / (zf - zn); m[3] = m02;
        m[4] = m10 * f / aspect; m[5] = m11 * f; m[6] = m12 * zf / (zf - zn); m[7] = m12;
        m[8] = m20 * f / aspect; m[9] = m21 * f; m[10] = m22 * zf / (zf - zn); m[11] = m22;
        m[12] = 0; m[13] = 0;
        m[14] = dist * zf / (zf - zn) - zn * zf / (zf - zn);
        m[15] = dist;
        return m;
    }

    /// <summary>単位クアッドを、左下が (x, y) で大きさ (w, h) の矩形へ置く行列。</summary>
    internal static float[] RectMatrix(float x, float y, float w, float h, float aspect)
    {
        var m = new float[16];
        m[0] = w / aspect;
        m[5] = h;
        m[10] = 1f;
        m[12] = x / aspect;
        m[13] = y;
        m[15] = 1f;
        return m;
    }

    // --- 数字 ---

    // 7 セグメントの点灯パターン。ビットは a,b,c,d,e,f,g の順。
    //   a=上 / b=右上 / c=右下 / d=下 / e=左下 / f=左上 / g=中
    private static readonly byte[] SegmentMasks =
    {
        0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F,
    };

    /// <summary>
    /// 数字 1 つぶんの矩形を並べて返す。左上が (x, y) で、1 文字の高さが height。
    /// 座標はどれも -1..1 で、描画先が正方形でなければ x 方向だけ aspect で割って形を保つ。
    /// 文字ごとの資材を持たないので、フォントも文字用のテクスチャも要らない。
    /// </summary>
    internal static List<float[]> LayOutNumber(string text, float x, float y, float height, float aspect)
    {
        var rects = new List<float[]>();
        if (string.IsNullOrEmpty(text)) return rects;

        var thickness = height * 0.16f;
        var digitWidth = height * 0.60f;
        var advance = digitWidth + thickness * 1.4f;
        var dotWidth = thickness * 1.6f;

        var cursor = x;
        foreach (var ch in text)
        {
            if (ch == '.')
            {
                rects.Add(RectMatrix(cursor, y - height, dotWidth, thickness, aspect));
                cursor += dotWidth + thickness;
                continue;
            }
            if (ch < '0' || ch > '9') { cursor += advance; continue; }

            var mask = SegmentMasks[ch - '0'];
            var w = digitWidth;
            var half = height * 0.5f;

            if ((mask & 0x01) != 0) rects.Add(RectMatrix(cursor, y - thickness, w, thickness, aspect));                 // a
            if ((mask & 0x02) != 0) rects.Add(RectMatrix(cursor + w - thickness, y - half, thickness, half, aspect));   // b
            if ((mask & 0x04) != 0) rects.Add(RectMatrix(cursor + w - thickness, y - height, thickness, half, aspect)); // c
            if ((mask & 0x08) != 0) rects.Add(RectMatrix(cursor, y - height, w, thickness, aspect));                    // d
            if ((mask & 0x10) != 0) rects.Add(RectMatrix(cursor, y - height, thickness, half, aspect));                 // e
            if ((mask & 0x20) != 0) rects.Add(RectMatrix(cursor, y - half, thickness, half, aspect));                   // f
            if ((mask & 0x40) != 0) rects.Add(RectMatrix(cursor, y - half - thickness * 0.5f, w, thickness, aspect));   // g

            cursor += advance;
        }
        return rects;
    }
}
