using System;
using System.Diagnostics;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 描かれた絵を、ファイルにせずそのまま別のプロセスへ渡す。
///
/// 書き出して渡す道（aviutl.edit.capture_movie）との違いは、渡したものが
/// 固まるかどうか。あちらは 1 本の素材になるので、作り直すまで相手の画は変わらない。
/// こちらは置き換えるたびに相手の次の 1 枚が変わるので、
/// 手元で直しながらその場で相手に出したいときに向く。
///
/// 逆に、決まった演出を繰り返すだけなら書き出す道のほうが軽い。
/// 相手はファイルを読むだけでよく、こちらが動いている必要がない。
///
/// 置いた絵は誰が読んでもよい。読む側を名指ししない。
/// </summary>
[NodeType("aviutl.edit.share_canvas", "AviUtl2", "Share Canvas",
    Version = "1.0.0",
    Description = "Hands the drawn picture straight to another process instead of writing a file. Writing a file fixes what was handed over, so the other side keeps showing it until a new file is made; this replaces the picture in place, so the other side's next frame changes. That suits working on something and showing it as it goes, while a set piece played over and over is cheaper as a file, because then the other side only reads it and this need not be running at all. Whoever reads it is not named here.")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Name of the place to put it in. The reading side is given the same name")]
[NodePort("frame", PortDirection.Input, "number", Description = "Frame to draw. Default 0")]
[NodePort("max_width", PortDirection.Input, "number", Description = "Shrink until the width is at most this, keeping the shape. Default 960. 0 leaves it at the output size, which costs several times more to hand over every time")]
[NodePort("keep_alpha", PortDirection.Input, "boolean", Description = "Carry the see-through part across. Off means every pixel arrives solid, which hides whatever is behind it on the other side")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to wait for the drawing to finish. Default 5000")]
[NodePort("shared", PortDirection.Output, "boolean", Description = "true when a picture was put there")]
[NodePort("width", PortDirection.Output, "number", Description = "Width of what was handed over")]
[NodePort("height", PortDirection.Output, "number", Description = "Height of what was handed over")]
[NodePort("source_width", PortDirection.Output, "number", Description = "Width the host drew at, before shrinking")]
[NodePort("source_height", PortDirection.Output, "number", Description = "Height the host drew at, before shrinking")]
[NodePort("sequence", PortDirection.Output, "number", Description = "Goes up every time. The reading side uses it to tell a new picture from the one it already has")]
[NodePort("lit_ratio", PortDirection.Output, "number", Description = "Share of pixels that are not pure black. Near 0 means nothing was drawn on that frame, which is also what a hidden layer looks like")]
[NodePort("top_colors", PortDirection.Output, "string", Description = "Colours taking up the most of the picture, most first, as RRGGBB=share on one line each. It is what lets this side and the reading side be compared without anyone looking at a screen")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the whole thing took")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2ShareCanvasNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        int frame = ctx.GetPortValue("frame") is double f ? (int)f : 0;
        int maxWidth = ctx.GetPortValue("max_width") is double m ? (int)m : 960;
        int timeout = ctx.GetPortValue("timeout_ms") is double t ? (int)t : 5000;
        bool keepAlpha = ctx.GetPortValue("keep_alpha") is bool k && k;

        var watch = Stopwatch.StartNew();
        ctx.SetPortValue("shared", false);
        ctx.SetPortValue("width", 0d);
        ctx.SetPortValue("height", 0d);
        ctx.SetPortValue("source_width", 0d);
        ctx.SetPortValue("source_height", 0d);
        ctx.SetPortValue("sequence", 0d);
        ctx.SetPortValue("lit_ratio", 0d);
        ctx.SetPortValue("top_colors", "");
        ctx.SetPortValue("elapsed_ms", 0d);

        try
        {
            if (name.Length == 0)
            {
                ctx.SetPortValue("result", "give a name for the place to put it in");
                return;
            }

            string problem = AviUtl2Frame.Take(frame, timeout, out var raw,
                                               out int w, out int h, out int pitch);
            if (problem != null)
            {
                ctx.SetPortValue("result", problem);
                return;
            }

            ctx.SetPortValue("source_width", (double)w);
            ctx.SetPortValue("source_height", (double)h);

            int step = AviUtl2Frame.StepFor(w, maxWidth);
            var shrunk = AviUtl2Frame.Shrink(raw, w, h, pitch, step, 4,
                                             out int outW, out int outH, out double lit,
                                             keepAlpha);
            ctx.SetPortValue("width", (double)outW);
            ctx.SetPortValue("height", (double)outH);
            ctx.SetPortValue("lit_ratio", lit);

            // ここは R,G,B,A で並んでいる。受け取る側は B,G,R,A で読む。
            // 入れ替えないと赤と青が入れ替わったまま相手の画に出る--
            // 絵は出るので「動いた」と読めてしまい、気づくのが遅れる。
            SwapRedBlue(shrunk);

            problem = NgolSharedFrame.Write(name, shrunk, outW, outH, outW * 4, out uint sequence);
            if (problem != null)
            {
                ctx.SetPortValue("result", problem);
                return;
            }

            ctx.SetPortValue("shared", true);
            ctx.SetPortValue("sequence", (double)sequence);
            ctx.SetPortValue("top_colors", NgolSharedFrame.TopColours(shrunk));
            ctx.SetPortValue("result", "frame " + frame + " drawn at " + w + "x" + h
                + ", handed over as " + outW + "x" + outH + " to '" + name + "'; "
                + (lit * 100).ToString("0.0") + "% of it holds something");
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

    static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte r = pixels[i];
            pixels[i] = pixels[i + 2];
            pixels[i + 2] = r;
        }
    }
}
