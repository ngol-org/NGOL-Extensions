using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストが抱えているソースを並べる。
///
/// types を立てると、代わりに「作れる種類」の一覧が返る。
/// ソースを足すノードは種類の識別子を要求するので、そちらを推測せずここから引く。
/// </summary>
[NodeType("obs.source.list", "OBS", "List Sources",
    Version = "1.0.0",
    Description = "Lists the sources the host holds. Raise types instead and it lists the kinds of source that can be created: the node that adds a source wants one of those identifiers, and they are worth taking from here rather than guessing.")]
[NodePort("types", PortDirection.Input, "boolean", Description = "List creatable source kinds instead of existing sources")]
[NodePort("include_scenes", PortDirection.Input, "boolean", Description = "Also list scenes, which are sources too")]
[NodePort("names", PortDirection.Output, "string", Description = "One name per line")]
[NodePort("ids", PortDirection.Output, "string", Description = "One identifier per line, lined up with names")]
[NodePort("count", PortDirection.Output, "number", Description = "How many entries came back")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer, sizes and audio state included")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSourceListNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        bool wantTypes = ctx.GetPortValue("types") is bool t && t;
        bool withScenes = ctx.GetPortValue("include_scenes") is bool s && s;

        var request = wantTypes
            ? new ObsNative.Request("source.types")
            : new ObsNative.Request("source.list").With("include_scenes", withScenes);

        using var reply = ObsNative.Call(request);
        ctx.SetPortValue("json", reply.Raw);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        string array = wantTypes ? "types" : "sources";
        int count = reply.Count(array);
        ctx.SetPortValue("names", reply.Column(array, wantTypes ? "display_name" : "name"));
        ctx.SetPortValue("ids", reply.Column(array, "id"));
        ctx.SetPortValue("count", (double)count);
        ctx.SetPortValue("result", count + (wantTypes ? " kind(s) can be created" : " source(s) in the host"));
    }
}
