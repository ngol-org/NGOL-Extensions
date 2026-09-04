using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 配信・録画・リプレイ・仮想カメラを起こす、止める。
///
/// 頼んだ時点では終わっていない。始まる合図はホストから後で来るので、
/// すぐ返る状態だけを返し、確定させたいときは obs.events で合図を待つ。
///
/// 配信を始める操作だけは、別の入力を立てない限り断る。
/// 他の操作と違って結果が外に出ていき、取り消せないため。
/// 送信先が設定されていなければ実害は無いが、それは設定に依存する話で、
/// このノードの側では設定を当てにしない。
/// </summary>
[NodeType("obs.control", "OBS", "Control Output",
    Version = "1.0.0",
    Description = "Starts and stops streaming, recording, the replay buffer and the virtual camera. None of them are finished at the moment they are asked for - the host says so afterwards - so only the state readable right away is returned, and the events node is where a start is confirmed.")]
[NodePort("allow_streaming", PortDirection.Input, "boolean", Description = "Required before start_streaming will be passed on. Everything a stream sends leaves the machine and cannot be taken back, so it is not something to reach by accident")]
[NodePort("action", PortDirection.Input, "string", IsRequired = true, Description = "start_recording, stop_recording, pause_recording, resume_recording, split_recording, start_streaming, stop_streaming, start_replay, stop_replay, save_replay, start_virtualcam, stop_virtualcam, studio_mode_on, studio_mode_off, transition, screenshot, save")]
[NodePort("accepted", PortDirection.Output, "boolean", Description = "true when the host took the request")]
[NodePort("streaming", PortDirection.Output, "boolean", Description = "Streaming state right after the call")]
[NodePort("recording", PortDirection.Output, "boolean", Description = "Recording state right after the call")]
[NodePort("replay_buffer", PortDirection.Output, "boolean", Description = "Replay buffer state right after the call")]
[NodePort("virtualcam", PortDirection.Output, "boolean", Description = "Virtual camera state right after the call")]
[NodePort("studio_mode", PortDirection.Output, "boolean", Description = "Studio mode state right after the call")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsControlNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string action = (ctx.GetPortValue("action") as string ?? "").Trim();

        ctx.SetPortValue("accepted", false);

        if (action.Length == 0)
        {
            ctx.SetPortValue("result", "give an action; the port description lists them");
            return;
        }

        // 配信だけは、間違って踏んでも通らないようにする。
        if (action == "start_streaming" && !(ctx.GetPortValue("allow_streaming") is bool allow && allow))
        {
            ctx.SetPortValue("result",
                "start_streaming was not passed on: raise allow_streaming to mean it. "
                + "What a stream sends leaves the machine and cannot be recalled");
            return;
        }

        using var reply = ObsNative.Call(new ObsNative.Request("control").With("action", action));

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("accepted", true);
        ctx.SetPortValue("streaming", reply.Bool("streaming"));
        ctx.SetPortValue("recording", reply.Bool("recording"));
        ctx.SetPortValue("replay_buffer", reply.Bool("replay_buffer"));
        ctx.SetPortValue("virtualcam", reply.Bool("virtualcam"));
        ctx.SetPortValue("studio_mode", reply.Bool("studio_mode"));
        ctx.SetPortValue("result", "'" + action + "' was taken; the states above are how things stood immediately after");
    }
}
