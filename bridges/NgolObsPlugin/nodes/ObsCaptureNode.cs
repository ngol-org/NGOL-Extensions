using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが描いている絵を 1 枚、そのまま画像にする。
///
/// 画面を撮るのとは別物で、こちらの方が上でもある。画面から取ると
/// プレビューの縮小になり、周りに在るもの（一覧・題・開いている場所）まで残り、
/// 他の窓が重なっていれば相手が写る。この道にはそのどれも無い。
///
/// 出来上がるのは出力する大きさそのままなので、確かめるだけなら大きすぎる。
/// 縮める割合はここで決める。
/// </summary>
[NodeType("obs.capture", "OBS", "Capture Output",
    Version = "1.2.0",
    Description = "Turns what the host is drawing into an image. This is not a screen grab and is better than one: a grab gives the shrunken preview, keeps whatever surrounds it, and catches any window sitting on top, none of which happens here. What comes out is the full output size, which is more than a check needs, so the shrinking happens here.")]
[NodePort("output_path", PortDirection.Input, "string", IsRequired = true, Description = "Where to write the image (.png)")]
[NodePort("source", PortDirection.Input, "string", Description = "Scene or source to draw. Empty means whatever is on air")]
[NodePort("max_width", PortDirection.Input, "number", Description = "Shrink until the width is at most this, keeping the shape. Default 640. 0 leaves it at the output size, which costs several times more to look at")]
[NodePort("saved", PortDirection.Output, "boolean", Description = "true when an image was written")]
[NodePort("saved_path", PortDirection.Output, "string", Description = "Where the image went")]
[NodePort("width", PortDirection.Output, "number", Description = "Width of the image that was written")]
[NodePort("height", PortDirection.Output, "number", Description = "Height of the image that was written")]
[NodePort("source_width", PortDirection.Output, "number", Description = "Width the host drew at, before shrinking")]
[NodePort("source_height", PortDirection.Output, "number", Description = "Height the host drew at, before shrinking")]
[NodePort("match_color", PortDirection.Input, "string", Description = "A colour to count, as RRGGBB. It answers how much of the picture is that colour, which is how a change can be judged without anyone looking at it. Empty means do not count")]
[NodePort("match_tolerance", PortDirection.Input, "number", Description = "How far each of red, green and blue may differ and still count. Default 48. Encoding moves colours further than it looks: a channel asked for as 0 came back as 39 after a round trip through video, so a tight window finds nothing at all while the colour is plainly there")]
[NodePort("top_colors", PortDirection.Output, "string", Description = "The colours that take up the most of the picture, most first, as RRGGBB=share on one line each. It exists so match_color can be chosen from what is actually there instead of guessed: asking for a colour the picture does not hold reads exactly like nothing being drawn. Fully see-through pixels are left out, near colours are grouped, and very dark ones are dropped so the backing does not always come first")]
[NodePort("match_ratio", PortDirection.Output, "number", Description = "Share of pixels close enough to match_color. 0 when nothing was asked for")]
[NodePort("lit_ratio", PortDirection.Output, "number", Description = "Share of pixels that are not pure black. Near 0 means nothing was drawn, which is also what a hidden source looks like")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the whole thing took")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsCaptureNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string outputPath = (ctx.GetPortValue("output_path") as string ?? "").Trim();
        string source = (ctx.GetPortValue("source") as string ?? "").Trim();
        int maxWidth = ctx.GetPortValue("max_width") is double m ? (int)m : 640;
        string matchColor = (ctx.GetPortValue("match_color") as string ?? "").Trim().TrimStart('#');
        int tolerance = ctx.GetPortValue("match_tolerance") is double tol ? (int)tol : 48;

        var watch = Stopwatch.StartNew();
        ctx.SetPortValue("saved", false);
        ctx.SetPortValue("saved_path", "");
        ctx.SetPortValue("width", 0d);
        ctx.SetPortValue("height", 0d);
        ctx.SetPortValue("source_width", 0d);
        ctx.SetPortValue("source_height", 0d);
        ctx.SetPortValue("lit_ratio", 0d);
        ctx.SetPortValue("match_ratio", 0d);
        ctx.SetPortValue("top_colors", "");
        ctx.SetPortValue("elapsed_ms", 0d);

        try
        {
            if (outputPath.Length == 0)
            {
                ctx.SetPortValue("result", "give a path to write to");
                return;
            }

            // 名前を渡さなかったときは、いま出ているシーンをホストに教えてもらう。
            if (source.Length == 0)
            {
                using var current = ObsNative.Call("current_scene_name");
                source = current.Text("name");
                if (source.Length == 0)
                {
                    ctx.SetPortValue("result", "the host does not report a scene on air");
                    return;
                }
            }

            using (var drawn = ObsNative.Call(new ObsNative.Request("capture").With("name", source)))
            {
                if (!drawn.Ok)
                {
                    ctx.SetPortValue("result", drawn.Error);
                    return;
                }
            }

            string problem = ObsFrame.Take(out var raw, out int w, out int h, out int pitch);
            if (problem != null)
            {
                ctx.SetPortValue("result", problem);
                return;
            }

            ctx.SetPortValue("source_width", (double)w);
            ctx.SetPortValue("source_height", (double)h);

            int step = ObsFrame.StepFor(w, maxWidth);
            var shrunk = ObsFrame.Shrink(raw, w, h, pitch, step, out int outW, out int outH, out double lit);
            ctx.SetPortValue("width", (double)outW);
            ctx.SetPortValue("height", (double)outH);
            ctx.SetPortValue("lit_ratio", lit);

            double matched = CountColour(shrunk, outW, outH, matchColor, tolerance);
            ctx.SetPortValue("match_ratio", matched);
            ctx.SetPortValue("top_colors", TopColours(shrunk, outW, outH));

            // 画像を組み立てる側は先頭を指す値で受け取る。動かないように留めてから渡す。
            byte[] png;
            var hold = GCHandle.Alloc(shrunk, GCHandleType.Pinned);
            try
            {
                // ホストが渡してくるのは B,G,R,A。縮めても並びはそのまま。
                png = NgolPng.Build(hold.AddrOfPinnedObject(), outW, outH, (uint)(outW * 4),
                                    bottomUp: false, swapRedBlue: false);
            }
            finally { hold.Free(); }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, png);

            ctx.SetPortValue("saved", true);
            ctx.SetPortValue("saved_path", outputPath);
            string extra = matchColor.Length == 6
                ? ", " + (matched * 100).ToString("0.0") + "% of it is that colour" : "";
            ctx.SetPortValue("result", "'" + source + "' drawn at " + w + "x" + h
                + ", written as " + outW + "x" + outH + "; "
                + (lit * 100).ToString("0.0") + "% of it holds something" + extra);
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            watch.Stop();
            ctx.SetPortValue("elapsed_ms", watch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 画面の多くを占めている色を、多い順に返す。
    ///
    /// 効いたかを色の割合で判定する使い方では、まず「どの色で測るか」が要る。
    /// 絵に無い色を選ぶと、何も描かれていないのと同じ答えが返り、読み違える。
    ///
    /// 3 つ落とす。どれも固定にしてある--選べるようにすると、選び方でまた外す。
    ///   透けている所（重ねる素材では大半がここ。数えると意味が消える）
    ///   近い色の違い（符号化で動くので、厳密に数えると 1 画素ずつ別の色になる）
    ///   暗すぎるもの（地の黒が常に 1 位になるのを避ける）
    ///
    /// ここが返す割合と、その色で数え直した割合は一致しないことがある。
    /// こちらは色を枠へ入れて数え、あちらは中心からの距離で数えるため、
    /// 階調が連続している所（灰色の濃淡など）では隣の枠まで拾う。
    /// はっきりした色では一致する（実測: 灰色で 13.8% 対 47.1%、白で 3.6% 対 3.6%）。
    /// </summary>
    static string TopColours(byte[] pixels, int width, int height, int take = 5)
    {
        const int Step = 24;        // 近い色をまとめる幅
        const int DarkFloor = 90;   // 3 色の合計がこれ未満なら地とみなす
        const byte SeeThrough = 40; // これ以下は透けているとみなす

        var counts = new System.Collections.Generic.Dictionary<int, int>();
        int total = width * height;
        int kept = 0;
        for (int i = 0; i < total; i++)
        {
            int p = i * 4;
            if (pixels[p + 3] <= SeeThrough) continue;
            int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
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
            // まとめた枠の真ん中を返す。端を返すと、その色では実際に当たらない。
            int r = Math.Min(255, ((key >> 16) & 0xFF) * Step + Step / 2);
            int g = Math.Min(255, ((key >> 8) & 0xFF) * Step + Step / 2);
            int b = Math.Min(255, (key & 0xFF) * Step + Step / 2);
            sb.Append(r.ToString("x2")).Append(g.ToString("x2")).Append(b.ToString("x2"))
              .Append('=').Append(((double)order[i].Value / total).ToString("0.000"))
              .Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// その色に十分近い画素が何割あるかを返す。
    ///
    /// 厳密に一致する画素を数えても意味が無い。符号化と縮小を通ると色は少しずれるので、
    /// 元の値と完全に同じ画素は、載っていても 1 つも見つからないことがある。
    ///
    /// 渡ってくる並びは B,G,R,A。
    /// </summary>
    static double CountColour(byte[] pixels, int width, int height, string rrggbb, int tolerance)
    {
        if (rrggbb.Length != 6) return 0d;
        int want;
        if (!int.TryParse(rrggbb, System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out want))
            return 0d;

        int wantR = (want >> 16) & 0xFF, wantG = (want >> 8) & 0xFF, wantB = want & 0xFF;
        if (tolerance < 0) tolerance = 0;

        long hits = 0;
        int total = width * height;
        for (int i = 0; i < total; i++)
        {
            int p = i * 4;
            if (Math.Abs(pixels[p + 2] - wantR) <= tolerance
             && Math.Abs(pixels[p + 1] - wantG) <= tolerance
             && Math.Abs(pixels[p] - wantB) <= tolerance) hits++;
        }
        return (double)hits / total;
    }
}
