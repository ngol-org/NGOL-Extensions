using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストへ現在のシーンのレンダリングを依頼する。
///
/// スクリプトが動くのは描画されるときだけなので、画面が止まっている間は
/// 積んだ式がいつまでも実行されない。ここから描画を起こせば実行される。
/// オブジェクトを作らないため、利用者のプロジェクトは変わらない。
///
/// 依頼はタスクを積むだけで返る。描画が終わったかどうかは戻り値では分から
/// ないので、結果は依頼した相手（式なら Lua Eval の poll_id）で受け取る。
/// </summary>
[NodeType("aviutl.edit.render_scene", "AviUtl2", "Render Scene",
    Version = "1.0.0",
    Description = "Asks the host to render the current scene. Scripts only run while something is being drawn, so a queued expression is never evaluated while the screen sits still: rendering once gets it picked up. No object is created, so the user's project is left untouched. The request only queues a task and returns, so queued=true does not mean the drawing has finished - collect the answer from whoever the work was queued with.")]
[NodePort("frame", PortDirection.Input, "number", Description = "Frame number to render (default 0). The API counts from 0, which is one less than the number shown in the host's UI")]
[NodePort("queued", PortDirection.Output, "boolean", Description = "true when the host accepted the request. false while it is busy writing a file, or when no edit handle exists")]
[NodePort("error", PortDirection.Output, "string", Description = "Empty when the call went through. Otherwise what went wrong")]
public sealed class AviUtl2RenderSceneNode : INode
{
    // disasm-verified: Ngol_RenderScene RVA 0x8570 は 0x5760 への jmp。
    // 実体は引数1個（ecx=32bit をそのまま rendering_scene_video の第1引数へ渡す。
    // rsp 経由の引数読み取りは無い）/ 戻り値は al の 8bit
    [DllImport("NgolForAviUtl2.aux2")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Ngol_RenderScene(int frame);

    // disasm-verified: Ngol_GetEditInfo RVA 0x84c0 / 引数2個（rcx=64bit ポインタ / edx=32bit、
    // [rsp+X] からの引数読み取りは無い）/ 戻り値は eax の 32bit
    [DllImport("NgolForAviUtl2.aux2")]
    private static extern int Ngol_GetEditInfo(byte[] outUtf8, int outLen);

    // ホストが断る条件は公式に「出力中等」とだけ書かれている。
    // 分かる範囲を数値で添えて、次に何を見ればよいかを示す。
    static string ExplainRefusal(int frame)
    {
        var info = ReadEditInfo();
        if (info.Count == 0) return "the host refused and no edit info could be read";

        string state = info.TryGetValue("edit_state", out var s) ? s : "?";
        string frameMax = info.TryGetValue("frame_max", out var m) ? m : "?";

        string stateName = state switch
        {
            "0" => "editing",
            "1" => "playing a preview",
            "2" => "writing a file",
            _ => "unknown (" + state + ")",
        };

        if (int.TryParse(frameMax, out int max) && frame > max)
            return $"frame {frame} is past the last frame that holds an object ({max}); state is {stateName}";

        return $"the host refused while {stateName} (last frame with an object: {frameMax})";
    }

    static Dictionary<string, string> ReadEditInfo()
    {
        var result = new Dictionary<string, string>();
        try
        {
            var probe = new byte[1];
            int need = Ngol_GetEditInfo(probe, probe.Length);
            if (need <= 0) return result;

            var buffer = new byte[need + 16];
            if (Ngol_GetEditInfo(buffer, buffer.Length) > buffer.Length) return result;

            int end = Array.IndexOf(buffer, (byte)0);
            if (end < 0) end = buffer.Length;

            const char newline = (char)10;
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split(newline))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) result[line.Substring(0, eq)] = line.Substring(eq + 1).Trim();
            }
        }
        catch { }
        return result;
    }

    public void Execute(IExecutionContext ctx)
    {
        int frame = ctx.GetPortValue("frame") is double f ? (int)f : 0;
        if (frame < 0) frame = 0;

        try
        {
            bool queued = Ngol_RenderScene(frame);
            ctx.SetPortValue("queued", queued);

            // 断られた理由はこちらには返らない。編集情報を読めば、範囲外なのか
            // 出力中なのかを呼び出し側で区別できる。
            ctx.SetPortValue("error", queued ? "" : ExplainRefusal(frame));
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("queued", false);
            ctx.SetPortValue("error", ex.GetType().Name + ": " + ex.Message);
        }
    }
}
