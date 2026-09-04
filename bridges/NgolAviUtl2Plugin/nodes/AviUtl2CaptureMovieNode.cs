using System;
using System.Diagnostics;
using System.IO;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 描かれた絵を続けて受け取り、そのまま動画にする。
///
/// ホストの出力機能を通らない。通ると、置き場を尋ねる窓が開いて操作が要り、
/// 符号化されない形で書き出されるので 3 秒で数百 MB になる。
/// ここでは 1 枚ずつ受け取って符号化する側へ流すので、途中に大きなものが出来ない。
///
/// 音は入らない。音まで要るならホストの出力機能を使う。
///
/// 符号化そのものはしない。何をどう符号化するかは渡す引数で決まるので、
/// 変えるのにこちらを作り直さなくてよい。
/// </summary>
[NodeType("aviutl.edit.capture_movie", "AviUtl2", "Capture Movie",
    Version = "1.2.0",
    Description = "Takes the drawn frames one after another and turns them into a movie, without going through the host's own file output. That route opens a window asking where to save and writes without compressing, which runs to hundreds of megabytes for a few seconds. Here each frame is handed straight to the encoder, so nothing large is ever written in between. No audio: use the host's own output when sound is needed.")]
[NodePort("output_path", PortDirection.Input, "string", IsRequired = true, Description = "Where to write the movie (.mp4)")]
[NodePort("frame_from", PortDirection.Input, "number", Description = "First frame. Default 0")]
[NodePort("frame_to", PortDirection.Input, "number", Description = "Last frame, inclusive. Default 60")]
[NodePort("fps", PortDirection.Input, "number", Description = "Frames per second of the result. Default 30. Match the project unless the movie is meant to run at another speed")]
[NodePort("max_width", PortDirection.Input, "number", Description = "Shrink until the width is at most this. Default 640. 0 = the full output size, which is far more than a check needs")]
[NodePort("encoder_path", PortDirection.Input, "string", Description = "The program that does the encoding. Default ffmpeg, found on PATH")]
[NodePort("encoder_args", PortDirection.Input, "string", Description = "What to hand it, after the input. Default is H.264 on the CPU, which works anywhere. A GPU-specific encoder is faster where available, for example -c:v h264_nvenc -preset p4 -cq 23 -pix_fmt yuv420p {out} on an NVIDIA GPU. The placeholders {w} {h} {fps} {out} are filled in")]
[NodePort("keep_alpha", PortDirection.Input, "boolean", Description = "false (default) fills every pixel in solid. Raise it to carry the see-through parts across, which is what lets the result be laid over something else instead of covering it. The default encoding then becomes VP9 in a .webm, because the usual one cannot hold see-through at all")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to wait for each frame to be drawn. Default 5000")]
[NodePort("saved", PortDirection.Output, "boolean", Description = "true when a movie was written")]
[NodePort("saved_path", PortDirection.Output, "string", Description = "Where it went")]
[NodePort("frames", PortDirection.Output, "number", Description = "How many frames went in")]
[NodePort("width", PortDirection.Output, "number", Description = "Width of the movie")]
[NodePort("height", PortDirection.Output, "number", Description = "Height of the movie")]
[NodePort("lit_ratio", PortDirection.Output, "number", Description = "Share of pixels that are not pure black, over the whole run. Near 0 means nothing was drawn anywhere")]
[NodePort("lit_by_frame", PortDirection.Output, "string", Description = "The same share taken frame by frame, thinned to about a dozen points, as frame:share. A run where nothing moves holds the same number throughout, so this tells a still picture from a moving one without looking at either")]
[NodePort("bytes", PortDirection.Output, "number", Description = "Size of the result")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the whole thing took")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2CaptureMovieNode : INode
{
    // 既定は CPU で符号化する形にしてある。どの機械でも動くのはこちらだけで、
    //   GPU 側の符号化は積んでいるものが違えば名前も引数も変わる。
    //   速くはなるが、置き換わるのは全体の一部でしかない（実測の大半は
    //   1 枚ずつ描いて受け取る側で、符号化はその後ろに隠れる）。
    const string DefaultArgs = "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \"{out}\"";

    // 透ける度合いを持てる形。VP9 の yuva420p は OBS Studio が素で読む。
    //   遅いので、長いものを既定でこちらにしない（数秒の素材向け）。
    const string AlphaArgs = "-c:v libvpx-vp9 -pix_fmt yuva420p -b:v 0 -crf 30 -row-mt 1 \"{out}\"";

    public void Execute(IExecutionContext ctx)
    {
        string outputPath = (ctx.GetPortValue("output_path") as string ?? "").Trim();
        int from = ctx.GetPortValue("frame_from") is double a ? (int)a : 0;
        int to = ctx.GetPortValue("frame_to") is double b ? (int)b : 60;
        int fps = ctx.GetPortValue("fps") is double f ? (int)f : 30;
        int maxWidth = ctx.GetPortValue("max_width") is double m ? (int)m : 640;
        string encoder = (ctx.GetPortValue("encoder_path") as string ?? "ffmpeg").Trim();
        string argsTemplate = (ctx.GetPortValue("encoder_args") as string ?? "").Trim();
        bool keepAlpha = ctx.GetPortValue("keep_alpha") is bool ka && ka;
        int timeout = ctx.GetPortValue("timeout_ms") is double t ? (int)t : 5000;
        if (encoder.Length == 0) encoder = "ffmpeg";
        if (argsTemplate.Length == 0) argsTemplate = keepAlpha ? AlphaArgs : DefaultArgs;

        var watch = Stopwatch.StartNew();
        ctx.SetPortValue("saved", false);
        ctx.SetPortValue("saved_path", "");
        ctx.SetPortValue("frames", 0d);
        ctx.SetPortValue("width", 0d);
        ctx.SetPortValue("height", 0d);
        ctx.SetPortValue("lit_ratio", 0d);
        ctx.SetPortValue("lit_by_frame", "");
        ctx.SetPortValue("bytes", 0d);
        ctx.SetPortValue("elapsed_ms", 0d);

        Process encoderProcess = null;
        try
        {
            if (outputPath.Length == 0)
            {
                ctx.SetPortValue("result", "give a path to write to");
                return;
            }
            if (to < from)
            {
                ctx.SetPortValue("result", "frame_to is before frame_from");
                return;
            }

            // 1 枚目で大きさが決まる。符号化する側は最初に大きさを知らないと始められない。
            string problem = AviUtl2Frame.Take(from, timeout, out var raw, out int w, out int h, out int pitch);
            if (problem != null)
            {
                ctx.SetPortValue("result", problem);
                return;
            }

            int step = AviUtl2Frame.StepFor(w, maxWidth);
            int bpp = keepAlpha ? 4 : 3;
            var first = AviUtl2Frame.Shrink(raw, w, h, pitch, step, bpp, out int outW, out int outH, out double lit, keepAlpha);
            ctx.SetPortValue("width", (double)outW);
            ctx.SetPortValue("height", (double)outH);

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            encoderProcess = StartEncoder(encoder, argsTemplate, outW, outH, fps, outputPath, keepAlpha);
            if (encoderProcess == null)
            {
                ctx.SetPortValue("result", "could not start '" + encoder + "'");
                return;
            }

            var stream = encoderProcess.StandardInput.BaseStream;
            stream.Write(first, 0, first.Length);

            int count = 1;
            double litTotal = lit;
            // 1 枚ごとの明るさを控える。全体の平均だけでは、動いているものと
            //   止まっているものが同じ数になってしまう。
            var litEach = new System.Collections.Generic.List<double> { lit };
            for (int frame = from + 1; frame <= to; frame++)
            {
                problem = AviUtl2Frame.Take(frame, timeout, out raw, out w, out h, out pitch);
                if (problem != null)
                {
                    ctx.SetPortValue("result", problem + " (stopped after " + count + " frame(s))");
                    break;
                }
                var bytes = AviUtl2Frame.Shrink(raw, w, h, pitch, step, bpp, out int fw, out int fh, out lit, keepAlpha);
                if (fw != outW || fh != outH)
                {
                    ctx.SetPortValue("result", "the picture changed size at frame " + frame
                        + " (stopped after " + count + " frame(s))");
                    break;
                }
                stream.Write(bytes, 0, bytes.Length);
                litTotal += lit;
                litEach.Add(lit);
                count++;
            }

            // 入り口を閉じるのが「もう来ない」の合図。閉じないと相手は待ち続ける。
            encoderProcess.StandardInput.Close();
            if (!encoderProcess.WaitForExit(60000))
            {
                try { encoderProcess.Kill(); } catch { }
                ctx.SetPortValue("result", "the encoder did not finish within 60s and was stopped");
                return;
            }

            ctx.SetPortValue("frames", (double)count);
            ctx.SetPortValue("lit_ratio", litTotal / count);
            ctx.SetPortValue("lit_by_frame", Thin(litEach, from));

            if (!File.Exists(outputPath))
            {
                ctx.SetPortValue("result", "the encoder exited with " + encoderProcess.ExitCode
                    + " and wrote nothing. Check encoder_args");
                return;
            }

            long size = new FileInfo(outputPath).Length;
            ctx.SetPortValue("bytes", (double)size);
            ctx.SetPortValue("saved", size > 0);
            ctx.SetPortValue("saved_path", outputPath);
            ctx.SetPortValue("result", count + " frame(s) at " + outW + "x" + outH + " -> " + size + " bytes");
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (encoderProcess != null) encoderProcess.Dispose();
            watch.Stop();
            ctx.SetPortValue("elapsed_ms", watch.Elapsed.TotalMilliseconds);
        }
    }

    // 全部返すと、聞かれた答えより桁違いに大きくなる。形が分かる数だけに絞る。
    static string Thin(System.Collections.Generic.List<double> values, int firstFrame)
    {
        const int Points = 12;
        var sb = new System.Text.StringBuilder();
        int n = values.Count;
        int take = Math.Min(Points, n);
        for (int k = 0; k < take; k++)
        {
            int i = take == 1 ? 0 : k * (n - 1) / (take - 1);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(firstFrame + i).Append(':').Append(values[i].ToString("0.0000"));
        }
        return sb.ToString();
    }

    // 出力は受け取らない。受け取ると、相手が書き続けてこちらが読まない間に
    //    両方が止まる形になりうる。=> 送る側だけを繋ぐ。
    static Process StartEncoder(string encoder, string argsTemplate,
                                int width, int height, int fps, string outputPath, bool keepAlpha)
    {
        string head = "-y -loglevel error -f rawvideo -pix_fmt " + (keepAlpha ? "rgba" : "rgb24")
                    + " -s " + width + "x" + height + " -r " + fps + " -i -";
        string tail = argsTemplate
            .Replace("{w}", width.ToString())
            .Replace("{h}", height.ToString())
            .Replace("{fps}", fps.ToString())
            .Replace("{out}", outputPath);

        var info = new ProcessStartInfo
        {
            FileName = encoder,
            Arguments = head + " " + tail,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        return Process.Start(info);
    }
}
