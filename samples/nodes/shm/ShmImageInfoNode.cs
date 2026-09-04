using System;
using System.Diagnostics;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 名前を付けた場所に置かれている絵を覗く。
///
/// この口が要るのは、絵が相手の画に出ないときに
/// 「置けていない」のか「読めていない」のかが、そのままでは分からないから。
/// ここは置き場だけを見るので、置いた側とも読んだ側とも独立に答えが出る。
///
/// 通し番号は置き直すたびに進む。同じ番号が返り続けるなら、
/// 置く側が止まっている。番号が奇数なら、ちょうど書いている最中。
///
/// どのホストでも動く。相手のアプリケーションを何も知らない。
/// </summary>
[NodeType("ngol.shm.image_info", "Shared", "Shared Frame Info",
    Version = "1.0.1",
    Description = "Looks at the picture sitting in a named place. This exists because when a picture does not reach the other side, nothing tells you whether it was never put there or never picked up; this looks only at the place itself, so it answers independently of both. The count goes up every time the picture is replaced, so an unchanging count means whoever puts it there has stopped, and an odd one means it is mid-write. Works on any host and knows nothing about the application it runs in.")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Name of the place to look at")]
[NodePort("read_pixels", PortDirection.Input, "boolean", Description = "Also read the picture itself so its colours can be counted. Costs the size of the picture, so it is off by default")]
[NodePort("found", PortDirection.Output, "boolean", Description = "true when a picture is there")]
[NodePort("width", PortDirection.Output, "number", Description = "Width of the picture")]
[NodePort("height", PortDirection.Output, "number", Description = "Height of the picture")]
[NodePort("stride", PortDirection.Output, "number", Description = "Bytes per row")]
[NodePort("sequence", PortDirection.Output, "number", Description = "Goes up each time the picture is replaced. The same value twice means nobody is putting anything there; an odd value means it is being written right now")]
[NodePort("byte_count", PortDirection.Output, "number", Description = "How many bytes the picture takes")]
[NodePort("top_colors", PortDirection.Output, "string", Description = "Colours taking up the most of the picture, most first, as RRGGBB=share on one line each. Only filled in when the picture was read. It is what lets the two sides be compared without anyone looking at a screen. Colours are put into buckets 24 wide and reported as the middle of the bucket, so ff0000 comes back as fc0c0c - compare the bucket, not the exact value. Nearly transparent pixels and very dark ones are left out of the count but still counted in the total, so the shares can add up to less than 1")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long this took")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ShmImageInfoNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        bool wantPixels = ctx.GetPortValue("read_pixels") is bool b && b;

        var watch = Stopwatch.StartNew();
        ctx.SetPortValue("found", false);
        ctx.SetPortValue("width", 0d);
        ctx.SetPortValue("height", 0d);
        ctx.SetPortValue("stride", 0d);
        ctx.SetPortValue("sequence", 0d);
        ctx.SetPortValue("byte_count", 0d);
        ctx.SetPortValue("top_colors", "");
        ctx.SetPortValue("elapsed_ms", 0d);

        try
        {
            NgolSharedFrame.Info info;
            string problem;
            byte[] pixels = null;

            if (wantPixels) problem = NgolSharedFrame.ReadPixels(name, out pixels, out info);
            else problem = NgolSharedFrame.ReadInfo(name, out info);

            if (problem != null)
            {
                ctx.SetPortValue("result", problem);
                return;
            }

            ctx.SetPortValue("found", true);
            ctx.SetPortValue("width", (double)info.Width);
            ctx.SetPortValue("height", (double)info.Height);
            ctx.SetPortValue("stride", (double)info.Stride);
            ctx.SetPortValue("sequence", (double)info.Sequence);
            ctx.SetPortValue("byte_count", (double)info.ByteCount);

            if (pixels != null)
                ctx.SetPortValue("top_colors", NgolSharedFrame.TopColours(pixels));

            ctx.SetPortValue("result", "'" + name + "' holds " + info.Width + "x" + info.Height
                + ", replaced " + (info.Sequence / 2) + " time(s)"
                + ((info.Sequence & 1u) != 0u ? ", being written right now" : ""));
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
