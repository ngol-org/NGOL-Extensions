using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// シーンへソースを足す・外す。
///
/// 種類の識別子は obs.source.list の types から引く。
/// 設定は種類ごとに違うので、足した直後に obs.source.settings で読めば形が分かる。
///
/// 外すのはシーンからで、他のシーンに同じソースが置かれていればそちらは残る。
/// </summary>
[NodeType("obs.source.edit", "OBS", "Add or Remove Source",
    Version = "1.0.0",
    Description = "Puts a source into a scene or takes one out. Kind identifiers come from the types listing, and since settings differ per kind, reading the new source's settings straight afterwards shows the shape it wants. Removing takes it out of that scene only - copies in other scenes stay.")]
[NodePort("scene", PortDirection.Input, "string", Description = "Scene to change. Empty means the one on air")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Name of the source to add or remove")]
[NodePort("id", PortDirection.Input, "string", Description = "Kind of source to create, e.g. text_gdiplus_v3 or color_source_v3")]
[NodePort("settings", PortDirection.Input, "string", Description = "JSON object handed to the new source")]
[NodePort("remove", PortDirection.Input, "boolean", Description = "Take the named item out of the scene instead of adding")]
[NodePort("applied", PortDirection.Output, "boolean", Description = "true when the host accepted it")]
[NodePort("item_id", PortDirection.Output, "number", Description = "Slot number of what was added")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSourceEditNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string scene = (ctx.GetPortValue("scene") as string ?? "").Trim();
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        string id = (ctx.GetPortValue("id") as string ?? "").Trim();
        string settings = (ctx.GetPortValue("settings") as string ?? "").Trim();
        bool remove = ctx.GetPortValue("remove") is bool r && r;

        ctx.SetPortValue("applied", false);

        if (name.Length == 0)
        {
            ctx.SetPortValue("result", "give the name of a source");
            return;
        }
        if (!remove && id.Length == 0)
        {
            ctx.SetPortValue("result", "give the kind of source to create; obs.source.list with types lists them");
            return;
        }

        var request = new ObsNative.Request(remove ? "source.remove" : "source.add")
            .With("scene", scene).With("name", name);
        if (!remove)
        {
            request.With("id", id);
            if (settings.Length > 0) request.With("settings", settings);
        }

        using var reply = ObsNative.Call(request);
        ctx.SetPortValue("json", reply.Raw);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("applied", true);
        ctx.SetPortValue("item_id", reply.Number("item_id"));
        ctx.SetPortValue("result", remove
            ? "'" + reply.Text("removed") + "' taken out of '" + reply.Text("scene") + "'"
            : "'" + name + "' added to '" + reply.Text("scene") + "' as slot " + reply.Number("item_id"));
    }
}
