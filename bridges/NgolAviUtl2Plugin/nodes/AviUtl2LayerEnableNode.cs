using System;
using System.Runtime.InteropServices;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// レイヤーの表示・非表示を読み書きする。
///
/// スクリプト側からは触れない領域なので、ここだけホストの編集 API を通す。
/// 一度消してすぐ戻す pulse は、画面の表示を描き直させるために使う。
/// 描画を起こす render_scene は画像を作るだけで、画面の表示は古いままになる。
/// </summary>
[NodeType("aviutl.edit.layer_enable", "AviUtl2", "Layer Enable",
    Version = "1.0.0",
    Description = "Reads or changes whether a layer is shown. Also used to make the on-screen preview redraw: rendering a frame produces an image but leaves what the user sees untouched, so a script that was just fixed keeps looking broken until something forces a repaint. action=pulse turns the layer off and straight back on, which is the same thing a person does by clicking the layer's eye twice.")]
[NodePort("layer", PortDirection.Input, "number", Description = "Layer number counted from 0. The host's UI counts from 1, so Layer1 on screen is 0 here")]
[NodePort("action", PortDirection.Input, "string", Description = "read (default) / on / off / pulse. pulse turns it off and back on to force a repaint")]
[NodePort("enabled", PortDirection.Output, "boolean", Description = "Whether the layer is shown, after the action was applied")]
[NodePort("known", PortDirection.Output, "boolean", Description = "false when the host would not answer. Do not read 'enabled' in that case")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2LayerEnableNode : INode
{
    // disasm-verified: Ngol_SetLayerEnable RVA 0x9110 / 引数2個（ecx=32bit layer / dl=8bit enable、
    // [rsp+X] からの引数読み取りは無い）/ 戻り値は al の 8bit
    [DllImport("NgolForAviUtl2.aux2")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Ngol_SetLayerEnable(int layer, [MarshalAs(UnmanagedType.U1)] bool enable);

    // disasm-verified: Ngol_GetLayerEnable RVA 0x8d10 / 引数1個（ecx=32bit layer）/
    // 戻り値は eax の 32bit。答えられなかった場合は -1 のまま返る
    [DllImport("NgolForAviUtl2.aux2")]
    private static extern int Ngol_GetLayerEnable(int layer);

    public void Execute(IExecutionContext ctx)
    {
        int layer = ctx.GetPortValue("layer") is double d ? (int)d : 0;
        string action = (ctx.GetPortValue("action") as string ?? "read").Trim().ToLowerInvariant();
        if (action.Length == 0) action = "read";

        try
        {
            switch (action)
            {
                case "read":
                    break;

                case "on":
                case "off":
                    if (!Ngol_SetLayerEnable(layer, action == "on"))
                    {
                        Report(ctx, layer, "the host refused to change layer " + layer);
                        return;
                    }
                    break;

                case "pulse":
                    // 消してから戻す。間を空けないとホストが 1 回の変更として畳んでしまう。
                    if (!Ngol_SetLayerEnable(layer, false))
                    {
                        Report(ctx, layer, "the host refused to hide layer " + layer);
                        return;
                    }
                    Thread.Sleep(30);
                    if (!Ngol_SetLayerEnable(layer, true))
                    {
                        // 消したまま戻せないと利用者の画面から消えたままになる。
                        Report(ctx, layer, "layer " + layer + " was hidden but could not be shown again");
                        return;
                    }
                    break;

                default:
                    Report(ctx, layer, "unknown action '" + action + "'. Use read / on / off / pulse");
                    return;
            }

            Report(ctx, layer, "action '" + action + "' applied to layer " + layer);
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("enabled", false);
            ctx.SetPortValue("known", false);
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void Report(IExecutionContext ctx, int layer, string message)
    {
        int state = Ngol_GetLayerEnable(layer);
        ctx.SetPortValue("known", state >= 0);
        ctx.SetPortValue("enabled", state == 1);
        ctx.SetPortValue("result", state < 0 ? message + " (the host did not report the state)" : message);
    }
}
