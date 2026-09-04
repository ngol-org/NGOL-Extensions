using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが「起きた」と言ってきたことを引き取る。
///
/// 起こす側の操作は頼んだ時点では終わっていない。ここへ合図が来たときが
/// 本当に始まった／終わったときで、状態を繰り返し読んで待つより確かで軽い。
///
/// 引き取ると控えから消える。同じ合図を二重に処理しないため。
/// </summary>
[NodeType("obs.events", "OBS", "Poll Host Events",
    Version = "1.0.0",
    Description = "Collects what the host has announced. An action is not finished when it is asked for; the announcement is the moment it truly started or stopped, which is both surer and cheaper than reading the state in a loop. Collecting clears them, so the same announcement is not handled twice.")]
[NodePort("limit", PortDirection.Input, "number", Description = "How many to take at once. Default 100")]
[NodePort("events", PortDirection.Output, "string", Description = "One event name per line, oldest first")]
[NodePort("count", PortDirection.Output, "number", Description = "How many were taken")]
[NodePort("remaining", PortDirection.Output, "number", Description = "How many are still waiting")]
[NodePort("latest", PortDirection.Output, "string", Description = "The newest of the ones taken")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsEventsNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        long limit = ctx.GetPortValue("limit") is double l && l > 0 ? (long)l : 100;

        using var reply = ObsNative.Call(new ObsNative.Request("events.poll").With("limit", limit));

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        string events = reply.Column("events", "event");
        int count = reply.Count("events");
        string latest = "";
        if (events.Length > 0)
        {
            int lastBreak = events.LastIndexOf('\n');
            latest = lastBreak < 0 ? events : events.Substring(lastBreak + 1);
        }

        ctx.SetPortValue("events", events);
        ctx.SetPortValue("count", (double)count);
        ctx.SetPortValue("remaining", reply.Number("remaining"));
        ctx.SetPortValue("latest", latest);
        ctx.SetPortValue("result", count == 0
            ? "nothing has happened since the last look"
            : count + " event(s); the newest is '" + latest + "'");
    }
}
