using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストがいま何をしているかを、画面を見ずに読む。
///
/// 出力する大きさ・実際に出ている枚数・配信や録画に入っているか・
/// どのシーンが番組に出ているかまで、まとめて 1 回で返る。
/// 操作を行うノードの前後でこれを読めば、効いたかどうかを目視に頼らず確かめられる。
/// </summary>
[NodeType("obs.info", "OBS", "Host Info",
    Version = "1.0.0",
    Description = "Reads what the host is doing without looking at the screen: the size it renders at, the frames it is actually producing, whether it is streaming or recording, and which scene is on air. Read it before and after an action and the effect can be confirmed without watching the window.")]
[NodePort("obs_version", PortDirection.Output, "string", Description = "Host version string")]
[NodePort("current_scene", PortDirection.Output, "string", Description = "Scene that is on air")]
[NodePort("preview_scene", PortDirection.Output, "string", Description = "Scene held in preview; empty unless studio mode is on")]
[NodePort("base_width", PortDirection.Output, "number", Description = "Canvas width")]
[NodePort("base_height", PortDirection.Output, "number", Description = "Canvas height")]
[NodePort("output_width", PortDirection.Output, "number", Description = "Width that leaves the host")]
[NodePort("output_height", PortDirection.Output, "number", Description = "Height that leaves the host")]
[NodePort("fps", PortDirection.Output, "number", Description = "Frame rate the host is set to")]
[NodePort("active_fps", PortDirection.Output, "number", Description = "Frame rate it is actually producing")]
[NodePort("lagged_frames", PortDirection.Output, "number", Description = "Frames the renderer could not keep up with")]
[NodePort("streaming", PortDirection.Output, "boolean", Description = "true while streaming")]
[NodePort("recording", PortDirection.Output, "boolean", Description = "true while recording")]
[NodePort("recording_paused", PortDirection.Output, "boolean", Description = "true while a recording is held")]
[NodePort("replay_buffer", PortDirection.Output, "boolean", Description = "true while the replay buffer runs")]
[NodePort("virtualcam", PortDirection.Output, "boolean", Description = "true while the virtual camera runs")]
[NodePort("studio_mode", PortDirection.Output, "boolean", Description = "true when preview and program are separate")]
[NodePort("profile", PortDirection.Output, "string", Description = "Profile in use")]
[NodePort("scene_collection", PortDirection.Output, "string", Description = "Scene collection in use")]
[NodePort("record_path", PortDirection.Output, "string", Description = "Where a recording would be written")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer, for fields not broken out above")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsInfoNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        using var reply = ObsNative.Call("info");
        ctx.SetPortValue("json", reply.Raw);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("obs_version", reply.Text("obs_version"));
        ctx.SetPortValue("current_scene", reply.Text("current_scene"));
        ctx.SetPortValue("preview_scene", reply.Text("preview_scene"));
        ctx.SetPortValue("base_width", reply.Number("base_width"));
        ctx.SetPortValue("base_height", reply.Number("base_height"));
        ctx.SetPortValue("output_width", reply.Number("output_width"));
        ctx.SetPortValue("output_height", reply.Number("output_height"));
        ctx.SetPortValue("fps", reply.Number("fps"));
        ctx.SetPortValue("active_fps", reply.Number("active_fps"));
        ctx.SetPortValue("lagged_frames", reply.Number("lagged_frames"));
        ctx.SetPortValue("streaming", reply.Bool("streaming"));
        ctx.SetPortValue("recording", reply.Bool("recording"));
        ctx.SetPortValue("recording_paused", reply.Bool("recording_paused"));
        ctx.SetPortValue("replay_buffer", reply.Bool("replay_buffer"));
        ctx.SetPortValue("virtualcam", reply.Bool("virtualcam"));
        ctx.SetPortValue("studio_mode", reply.Bool("studio_mode"));
        ctx.SetPortValue("profile", reply.Text("profile"));
        ctx.SetPortValue("scene_collection", reply.Text("scene_collection"));
        ctx.SetPortValue("record_path", reply.Text("record_path"));

        ctx.SetPortValue("result",
            reply.Text("obs_version") + ", "
            + reply.Number("output_width") + "x" + reply.Number("output_height") + " at "
            + reply.Number("active_fps").ToString("0.0") + "fps, showing '"
            + reply.Text("current_scene") + "'");
    }
}
