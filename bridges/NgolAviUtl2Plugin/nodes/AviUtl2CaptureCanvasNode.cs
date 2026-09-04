using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 描かれた絵を 1 枚、そのまま画像にする。
///
/// ホストは描画を頼まれると、出来上がった画素をそのまま渡してくる。
/// 画面を撮る必要は無く、撮るより上でもある。画面から取ると縮小された
/// プレビューになり、周りに在るもの（一覧・題・開いている場所）まで
/// 画像に残り、他の窓が重なっていれば相手が写る。この道にはそのどれも無い。
///
/// 出来上がるのは出力する大きさそのままなので、確かめるだけなら大きすぎる。
/// 縮める割合はここで決める。ホスト側は渡すところまでしかしない。
///
/// 続けて何枚も要るなら aviutl.edit.capture_movie を使う。
/// </summary>
[NodeType("aviutl.edit.capture_canvas", "AviUtl2", "Capture Canvas",
    Version = "1.1.0",
    Description = "Saves what the host actually drew. The host hands the finished pixels over when it is asked to draw, so nothing is taken from the screen: the image holds the picture and nothing around it, and another window sitting on top changes nothing. What comes back is the full output size, which is more than a check needs, so the shrinking happens here. Use capture_movie when a run of frames is wanted instead of one.")]
[NodePort("output_path", PortDirection.Input, "string", IsRequired = true, Description = "Where to write the image (.png)")]
[NodePort("frame", PortDirection.Input, "number", Description = "Frame to draw. Default 0")]
[NodePort("max_width", PortDirection.Input, "number", Description = "Shrink until the width is at most this, keeping the shape. Default 640. 0 = leave it at the output size, which costs several times more to look at")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to wait for the drawing to finish. Default 5000")]
[NodePort("saved", PortDirection.Output, "boolean", Description = "true when an image was written")]
[NodePort("saved_path", PortDirection.Output, "string", Description = "Where the image went")]
[NodePort("width", PortDirection.Output, "number", Description = "Width of the image that was written")]
[NodePort("height", PortDirection.Output, "number", Description = "Height of the image that was written")]
[NodePort("source_width", PortDirection.Output, "number", Description = "Width the host drew at, before shrinking")]
[NodePort("source_height", PortDirection.Output, "number", Description = "Height the host drew at, before shrinking")]
[NodePort("lit_ratio", PortDirection.Output, "number", Description = "Share of pixels that are not pure black. Near 0 means nothing was drawn on that frame, which is also what a hidden layer looks like")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the whole thing took")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2CaptureCanvasNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string outputPath = (ctx.GetPortValue("output_path") as string ?? "").Trim();
        int frame = ctx.GetPortValue("frame") is double f ? (int)f : 0;
        int maxWidth = ctx.GetPortValue("max_width") is double m ? (int)m : 640;
        int timeout = ctx.GetPortValue("timeout_ms") is double t ? (int)t : 5000;

        var watch = Stopwatch.StartNew();
        ctx.SetPortValue("saved", false);
        ctx.SetPortValue("saved_path", "");
        ctx.SetPortValue("width", 0d);
        ctx.SetPortValue("height", 0d);
        ctx.SetPortValue("source_width", 0d);
        ctx.SetPortValue("source_height", 0d);
        ctx.SetPortValue("lit_ratio", 0d);
        ctx.SetPortValue("elapsed_ms", 0d);

        try
        {
            if (outputPath.Length == 0)
            {
                ctx.SetPortValue("result", "give a path to write to");
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
                                             out int outW, out int outH, out double lit);
            ctx.SetPortValue("width", (double)outW);
            ctx.SetPortValue("height", (double)outH);
            ctx.SetPortValue("lit_ratio", lit);

            // 画像を組み立てる側は先頭を指す値で受け取る。動かないように留めてから渡す。
            byte[] png;
            var hold = GCHandle.Alloc(shrunk, GCHandleType.Pinned);
            try
            {
                png = NgolPng.Build(hold.AddrOfPinnedObject(), outW, outH, (uint)(outW * 4),
                                    bottomUp: false, swapRedBlue: true);
            }
            finally { hold.Free(); }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, png);

            ctx.SetPortValue("saved", true);
            ctx.SetPortValue("saved_path", outputPath);
            ctx.SetPortValue("result", w + "x" + h + " drawn, written as " + outW + "x" + outH
                + "; " + (lit * 100).ToString("0.0") + "% of it holds something");
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
}
