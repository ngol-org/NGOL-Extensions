using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが持っているシーンを、名前で引けるようにする。
///
/// 切り替えるノードは名前を要求するので、先にこれで実在する名前を得ておく。
/// 推測した名前を渡して「無い」と言われる往復が要らなくなる。
/// </summary>
[NodeType("obs.scene.list", "OBS", "List Scenes",
    Version = "1.0.0",
    Description = "Names every scene the host holds. The switching node wants a name, so take the real ones from here rather than guessing and being told the name does not exist.")]
[NodePort("names", PortDirection.Output, "string", Description = "One scene name per line, in the host's own order")]
[NodePort("count", PortDirection.Output, "number", Description = "How many scenes there are")]
[NodePort("current_scene", PortDirection.Output, "string", Description = "The one on air right now")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer, sizes included")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSceneListNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        using var reply = ObsNative.Call("scene.list");
        ctx.SetPortValue("json", reply.Raw);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        int count = reply.Count("scenes");
        ctx.SetPortValue("names", reply.Column("scenes", "name"));
        ctx.SetPortValue("count", (double)count);
        ctx.SetPortValue("current_scene", reply.Text("current_scene"));
        ctx.SetPortValue("result", count + " scene(s); '" + reply.Text("current_scene") + "' is on air");
    }
}
