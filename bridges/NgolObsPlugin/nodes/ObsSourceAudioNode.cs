using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ソースの音を読む・変える。
///
/// 音量は倍率と dB の両方で返る。ホストの画面に出ているのは dB のほうなので、
/// 目で見えている値と突き合わせられる。書くときも dB を渡せる。
/// </summary>
[NodeType("obs.source.audio", "OBS", "Source Audio",
    Version = "1.0.0",
    Description = "Reads and changes the sound of a source. Volume comes back both as a multiplier and in dB, and dB is what the host shows on screen, so the number here can be lined up with the one visible there. dB can be written too.")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Source to read or change")]
[NodePort("muted", PortDirection.Input, "boolean", Description = "Mute or unmute it")]
[NodePort("volume_db", PortDirection.Input, "number", Description = "Volume in dB, the way the host shows it. 0 is unchanged level, -100 is silence")]
[NodePort("apply", PortDirection.Input, "boolean", Description = "Write the values above. Without this the node only reads")]
[NodePort("is_muted", PortDirection.Output, "boolean", Description = "Mute state after the call")]
[NodePort("volume", PortDirection.Output, "number", Description = "Volume as a multiplier")]
[NodePort("db", PortDirection.Output, "number", Description = "Volume in dB")]
[NodePort("has_audio", PortDirection.Output, "boolean", Description = "false when this source makes no sound at all")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSourceAudioNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        bool apply = ctx.GetPortValue("apply") is bool a && a;

        if (name.Length == 0)
        {
            ctx.SetPortValue("result", "give the name of a source");
            return;
        }

        ObsNative.Request request;
        if (apply)
        {
            // 書く経路でだけ値を積む。読むだけのつもりで無音にしてしまわないため。
            request = new ObsNative.Request("source.audio.set").With("name", name);
            if (ctx.GetPortValue("muted") is bool muted) request.With("muted", muted);
            if (ctx.GetPortValue("volume_db") is double db) request.With("volume_db", db);
        }
        else
        {
            request = new ObsNative.Request("source.audio.get").With("name", name);
        }

        using var reply = ObsNative.Call(request);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("is_muted", reply.Bool("muted"));
        ctx.SetPortValue("volume", reply.Number("volume"));
        ctx.SetPortValue("db", reply.Number("volume_db"));
        ctx.SetPortValue("has_audio", reply.Bool("has_audio"));
        ctx.SetPortValue("result", "'" + name + "' is "
            + (reply.Bool("muted") ? "muted" : "audible")
            + " at " + reply.Number("volume_db").ToString("0.0") + " dB");
    }
}
