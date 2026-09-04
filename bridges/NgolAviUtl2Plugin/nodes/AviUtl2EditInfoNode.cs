using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// いまの編集の状態を読む。
///
/// 画面の解像度・フレームレート・カーソルの位置のほか、
/// ホストが編集中なのか再生中なのか出力中なのかが分かる。
///
/// 「操作したら再生が止まった」のような話は、止まったことを目で見るしかないと
/// 思われがちだが、状態そのものを読めば画面を見ずに確かめられる。
/// </summary>
[NodeType("aviutl.edit.info", "AviUtl2", "Edit Info",
    Version = "1.0.0",
    Description = "Reads what the host is doing and where the cursor is: scene size, frame rate, current frame and layer, and whether the host is editing, playing a preview or writing a file. Answers questions like 'did that stop playback' without watching the screen.")]
[NodePort("state", PortDirection.Output, "string", Description = "editing / playing / writing, or unknown with the raw number")]
[NodePort("stateCode", PortDirection.Output, "number", Description = "The raw number the host answered with. -1 when it would not answer")]
[NodePort("frame", PortDirection.Output, "number", Description = "Where the cursor is, counted from 0. The host's UI counts from 1")]
[NodePort("layer", PortDirection.Output, "number", Description = "Selected layer, counted from 0")]
[NodePort("values", PortDirection.Output, "string", Description = "Everything that was read, one per line as name=value")]
[NodePort("result", PortDirection.Output, "string", Description = "What happened")]
public sealed class AviUtl2EditInfoNode : INode
{
    // disasm-verified: Ngol_GetEditInfo RVA 0x84c0 / 引数2個（rcx=64bit ポインタ / edx=32bit、
    // [rsp+X] からの引数読み取りは無い）/ 戻り値は eax の 32bit
    [DllImport("NgolForAviUtl2.aux2")]
    private static extern int Ngol_GetEditInfo(byte[] outUtf8, int outLen);

    public void Execute(IExecutionContext ctx)
    {
        ctx.SetPortValue("state", "");
        ctx.SetPortValue("stateCode", -1d);
        ctx.SetPortValue("frame", 0d);
        ctx.SetPortValue("layer", 0d);
        ctx.SetPortValue("values", "");

        try
        {
            var buffer = new byte[8 * 1024];
            int written = Ngol_GetEditInfo(buffer, buffer.Length);
            if (written <= 0)
            {
                ctx.SetPortValue("result", "no editing handle yet. It appears once a project is open");
                return;
            }

            int end = Array.IndexOf(buffer, (byte)0, 0, Math.Min(written, buffer.Length));
            if (end < 0) end = Math.Min(written, buffer.Length);
            string text = Encoding.UTF8.GetString(buffer, 0, end).TrimEnd('\n');

            var read = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                int at = line.IndexOf('=');
                if (at > 0) read[line.Substring(0, at)] = line.Substring(at + 1);
            }

            double Number(string key) =>
                read.TryGetValue(key, out string? v) && double.TryParse(v, out double d) ? d : 0;

            double code = read.TryGetValue("edit_state", out string? s)
                && double.TryParse(s, out double c) ? c : -1;

            string state = code switch
            {
                0 => "editing",
                1 => "playing",
                2 => "writing",
                _ => "unknown (" + code + ")",
            };

            ctx.SetPortValue("state", state);
            ctx.SetPortValue("stateCode", code);
            ctx.SetPortValue("frame", Number("frame"));
            ctx.SetPortValue("layer", Number("layer"));
            ctx.SetPortValue("values", text);
            ctx.SetPortValue("result", "the host is " + state
                + ", cursor at frame " + Number("frame") + " layer " + Number("layer"));
        }
        catch (DllNotFoundException)
        {
            ctx.SetPortValue("result", "the bridge module is not loaded in this process");
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
        }
    }
}
