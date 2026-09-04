using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// シーンの中に何が並んでいるかを読む。
///
/// 同じソースが複数のシーンに置かれることがあるので、位置や表示は
/// ソースではなく「シーンの中の 1 枠」に付いている。動かす側のノードは
/// その枠を名前か番号で指すので、ここで番号を得ておくと同名でも迷わない。
/// </summary>
[NodeType("obs.sceneitem.list", "OBS", "List Scene Items",
    Version = "1.0.0",
    Description = "Reads what is stacked inside a scene. The same source can sit in several scenes, so position and visibility belong to the slot in the scene rather than to the source; the moving node points at a slot by name or by number, and taking the number from here keeps two identical names apart.")]
[NodePort("scene", PortDirection.Input, "string", Description = "Scene to look inside. Empty means the one on air")]
[NodePort("names", PortDirection.Output, "string", Description = "One item name per line, front of the stack first")]
[NodePort("item_ids", PortDirection.Output, "string", Description = "One slot number per line, lined up with names")]
[NodePort("visible_flags", PortDirection.Output, "string", Description = "true/false per line, lined up with names")]
[NodePort("count", PortDirection.Output, "number", Description = "How many items are in the scene")]
[NodePort("scene_name", PortDirection.Output, "string", Description = "Scene that was actually read")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer, position and scale included")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSceneItemListNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string scene = (ctx.GetPortValue("scene") as string ?? "").Trim();

        using var reply = ObsNative.Call(new ObsNative.Request("sceneitem.list").With("scene", scene));
        ctx.SetPortValue("json", reply.Raw);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        int count = reply.Count("items");
        ctx.SetPortValue("names", reply.Column("items", "name"));
        ctx.SetPortValue("item_ids", reply.Column("items", "item_id"));
        ctx.SetPortValue("visible_flags", reply.Column("items", "visible"));
        ctx.SetPortValue("count", (double)count);
        ctx.SetPortValue("scene_name", reply.Text("scene"));
        ctx.SetPortValue("result", "'" + reply.Text("scene") + "' holds " + count + " item(s)");
    }
}
