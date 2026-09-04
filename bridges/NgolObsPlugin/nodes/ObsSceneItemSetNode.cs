using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// シーンの中の 1 枠を、表示・位置・拡大率・回転で動かす。
///
/// 繋がなかった項目は触らない。位置だけ動かしたいときに、
/// 拡大率まで既定値へ戻ってしまうことがない。
/// 何を書き換えたかは changed に並ぶので、渡したつもりが渡っていない取りこぼしに気づける。
/// </summary>
[NodeType("obs.sceneitem.set", "OBS", "Set Scene Item",
    Version = "1.0.0",
    Description = "Moves one slot in a scene by visibility, position, scale or rotation. Anything left unconnected is left alone, so nudging the position cannot quietly reset the scale, and changed lists what was actually written - which is where a value that never arrived shows up.")]
[NodePort("scene", PortDirection.Input, "string", Description = "Scene holding the item. Empty means the one on air")]
[NodePort("name", PortDirection.Input, "string", Description = "Item to move, by source name")]
[NodePort("item_id", PortDirection.Input, "number", Description = "Item to move, by slot number. Wins over name when both are given")]
[NodePort("visible", PortDirection.Input, "boolean", Description = "Show or hide it")]
[NodePort("locked", PortDirection.Input, "boolean", Description = "Lock or unlock it")]
[NodePort("x", PortDirection.Input, "number", Description = "Left edge, in canvas pixels")]
[NodePort("y", PortDirection.Input, "number", Description = "Top edge, in canvas pixels")]
[NodePort("scale_x", PortDirection.Input, "number", Description = "Horizontal scale. 1 is original size")]
[NodePort("scale_y", PortDirection.Input, "number", Description = "Vertical scale. 1 is original size")]
[NodePort("rotation", PortDirection.Input, "number", Description = "Rotation in degrees")]
[NodePort("applied", PortDirection.Output, "boolean", Description = "true when the host accepted it")]
[NodePort("changed", PortDirection.Output, "string", Description = "Which fields were written, one per line")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSceneItemSetNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var request = new ObsNative.Request("sceneitem.set")
            .With("scene", (ctx.GetPortValue("scene") as string ?? "").Trim())
            .With("name", (ctx.GetPortValue("name") as string ?? "").Trim());

        // 繋がれていない入力は既定値の 0 や false で届く。
        // それを「渡された」と扱うと、触るつもりのない項目まで書き換わる。
        if (ctx.GetPortValue("item_id") is double id && id > 0) request.With("item_id", (long)id);
        if (ctx.GetPortValue("visible") is bool visible) request.With("visible", visible);
        if (ctx.GetPortValue("locked") is bool locked) request.With("locked", locked);
        if (ctx.GetPortValue("x") is double x) request.With("x", x);
        if (ctx.GetPortValue("y") is double y) request.With("y", y);
        if (ctx.GetPortValue("scale_x") is double sx) request.With("scale_x", sx);
        if (ctx.GetPortValue("scale_y") is double sy) request.With("scale_y", sy);
        if (ctx.GetPortValue("rotation") is double rot) request.With("rotation", rot);

        using var reply = ObsNative.Call(request);
        ctx.SetPortValue("json", reply.Raw);
        ctx.SetPortValue("applied", reply.Ok);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        string changed = reply.Column("changed", "field");
        ctx.SetPortValue("changed", changed);
        ctx.SetPortValue("result", changed.Length == 0
            ? "'" + reply.Text("name") + "' was found, but nothing was given to change"
            : "'" + reply.Text("name") + "': " + changed.Replace("\n", ", ") + " written");
    }
}
