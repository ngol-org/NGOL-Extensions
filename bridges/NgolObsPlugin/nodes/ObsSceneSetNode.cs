using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 番組に出すシーンを変える。
///
/// スタジオモードのときは preview を立てると、出さずにプレビュー側へ置ける。
/// 置いたものを出すのは transition。
///
/// 切り替えた後の状態を読み戻して返すので、効いたかどうかを画面で確かめなくてよい。
/// </summary>
[NodeType("obs.scene.set", "OBS", "Set Scene",
    Version = "1.0.0",
    Description = "Changes which scene goes out. With studio mode on, raise preview to stage a scene without putting it out, and use transition to push what is staged. The state after the change is read back, so nothing has to be confirmed by eye.")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Scene to switch to")]
[NodePort("preview", PortDirection.Input, "boolean", Description = "Set the preview side instead of program. Needs studio mode")]
[NodePort("transition", PortDirection.Input, "boolean", Description = "After setting, push preview to program")]
[NodePort("applied", PortDirection.Output, "boolean", Description = "true when the host accepted it")]
[NodePort("current_scene", PortDirection.Output, "string", Description = "What is on air after the change")]
[NodePort("preview_scene", PortDirection.Output, "string", Description = "What is in preview after the change")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSceneSetNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        bool preview = ctx.GetPortValue("preview") is bool p && p;
        bool transition = ctx.GetPortValue("transition") is bool t && t;

        ctx.SetPortValue("applied", false);

        if (name.Length == 0)
        {
            ctx.SetPortValue("result", "give the name of a scene");
            return;
        }

        using (var reply = ObsNative.Call(new ObsNative.Request("scene.set")
                                          .With("name", name).With("preview", preview)))
        {
            if (!reply.Ok)
            {
                ctx.SetPortValue("result", reply.Error);
                return;
            }
        }

        if (transition)
        {
            using var pushed = ObsNative.Call(new ObsNative.Request("control").With("action", "transition"));
            if (!pushed.Ok)
            {
                ctx.SetPortValue("result", "the scene was set but the transition was refused: " + pushed.Error);
                return;
            }
        }

        // 効いたことは、頼んだ側でなくホストの側から読み戻して確かめる。
        using var after = ObsNative.Call("info");
        ctx.SetPortValue("current_scene", after.Text("current_scene"));
        ctx.SetPortValue("preview_scene", after.Text("preview_scene"));

        string landed = preview && !transition ? after.Text("preview_scene") : after.Text("current_scene");
        bool applied = landed == name;
        ctx.SetPortValue("applied", applied);
        ctx.SetPortValue("result", applied
            ? "'" + name + "' is now on " + (preview && !transition ? "preview" : "air")
            : "the host was asked for '" + name + "' but reports '" + landed + "'");
    }
}
