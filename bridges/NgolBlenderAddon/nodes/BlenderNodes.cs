using System.IO;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// =====================================================================================
// Blender を触るノード一式。
//
// **アドオン側は「NGOL を載せる」と「メインスレッドに乗せる」までしかしない。**
//    何をするかはここと、Python の土台 (Nodes/CustomNodes/py/ngol_blender.py) が決める。
//    => どちらもホットリロードで回るので、**Blender の再起動なしに機能を増やせる。**
//
// ここは NGOL のスレッドで走る。bpy はメインスレッド専用なので、
//    すべて BlenderBridge 経由でアドオンのポンプに走らせてもらう。
//
// 口の形は NGOL の OBS ブリッジに揃えてある（要求も答えも JSON、失敗は ok=false と error）。
//
// シーンの構造を厚く扱うノードはここに作らない--それは公式 Blender MCP
//    (projects.blender.org/lab/blender_mcp) の領分で、あちらのほうが厚く保守もされている。
//    ここに要るのは「**NGOL 側の操作が効いたか**」を確かめる分だけ。
// =====================================================================================


/// <summary>受け口が生きているかだけを確かめる。切り分けの入口。</summary>
[NodeType("blender.ping", "Blender", "Ping Blender",
    Version = "1.0.0",
    Description = "Check that Blender is answering the bridge, and nothing else. Use it first when another Blender node fails: if this does not answer either, the problem is the bridge or the add-on, not the node.")]
[NodePort("alive", PortDirection.Output, "boolean", Description = "true when Blender answered")]
[NodePort("blender_version", PortDirection.Output, "string", Description = "Version string Blender reports")]
[NodePort("pid", PortDirection.Output, "number", Description = "Process id that answered. It should match the NGOL process id, because NGOL runs inside Blender")]
[NodePort("background", PortDirection.Output, "boolean", Description = "true when Blender runs without a window; screen capture does not work then")]
[NodePort("ms", PortDirection.Output, "number", Description = "How long the round trip took")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderPingNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        using var reply = BlenderBridge.CallPy("ping", null, 8.0);

        ctx.SetPortValue("alive", reply.Ok);
        ctx.SetPortValue("ms", reply.Milliseconds);
        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("blender_version", reply.Text("blender"));
        ctx.SetPortValue("pid", reply.Number("pid"));
        ctx.SetPortValue("background", reply.Bool("background"));
        ctx.SetPortValue("result",
            "Blender " + reply.Text("blender") + " answered from pid "
            + reply.Number("pid") + " in " + reply.Milliseconds + "ms");
    }
}


/// <summary>
/// Blender のメインスレッドで Python をそのまま走らせる。
///
/// **これが土台。** 個別のノードを増やす前に、まずここで書いて確かめられる。
///    Text Box ノードを繋げば、WebUI 上で文面を書き換えて即実行できる。
/// </summary>
[NodeType("blender.py.run", "Blender", "Run Python",
    Version = "1.0.2",
    Description = "Run Python on Blender's main thread. bpy, bmesh, mathutils, math, json and os are already in scope, args holds the values wired into this node, and whatever is left in a variable named result comes back. Wire a Text Box into code and the script can be edited in the WebUI and re-run without restarting anything. WARNING: the script runs with everything Blender can reach, so it can delete work and read files.")]
[NodePort("code", PortDirection.Input, "string", Description = "The Python to run. Put what you want back into a variable named result")]
[NodePort("arg_text", PortDirection.Input, "string", Description = "Optional value handed to the script as args['text']")]
[NodePort("arg_number", PortDirection.Input, "number", Description = "Optional value handed to the script as args['number']")]
[NodePort("timeout", PortDirection.Input, "number", Description = "Seconds to wait before giving up. Default 30. An MCP client gives up on its own after 15 seconds, so a longer script looks like a failure there even though it keeps running and finishes inside Blender. Pass async when calling run_node and the full outputs come back through check_job_status instead, with no change to this node")]
[NodePort("ok", PortDirection.Output, "boolean", Description = "true when the script finished without raising")]
[NodePort("stdout", PortDirection.Output, "string", Description = "Whatever the script printed")]
[NodePort("result_json", PortDirection.Output, "string", Description = "The value left in result, as JSON")]
[NodePort("ms", PortDirection.Output, "number", Description = "How long the script took inside Blender")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the traceback")]
public sealed class BlenderRunPythonNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string code = BlenderBridge.ToText(ctx.GetPortValue("code"));
        double timeout = BlenderBridge.ToDouble(ctx.GetPortValue("timeout"), 30.0);
        if (timeout <= 0) timeout = 30.0;

        ctx.SetPortValue("ok", false);

        if (code.Length == 0)
        {
            ctx.SetPortValue("result", "code is empty. Connect a Text Box, or write it directly");
            return;
        }

        var args = new BlenderBridge.Args()
            .Set("text", BlenderBridge.ToText(ctx.GetPortValue("arg_text")))
            .Set("number", BlenderBridge.ToDouble(ctx.GetPortValue("arg_number")));

        using var reply = BlenderBridge.Run(code, args, timeout, "py.run");

        ctx.SetPortValue("stdout", reply.Stdout);
        ctx.SetPortValue("ms", reply.Milliseconds);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("ok", true);
        ctx.SetPortValue("result_json", reply.ResultJson());
        ctx.SetPortValue("result", "ran in " + reply.Milliseconds + "ms");
    }
}


/// <summary>
/// Python の土台を読み直す。
/// これが無いと、`.py` を直しても **黙って古い実装が動き続ける**。
/// </summary>
[NodeType("blender.py.reload", "Blender", "Reload Python Module",
    Version = "1.0.0",
    Description = "Re-import the Python modules under Nodes/CustomNodes/py so an edit to them takes effect. Python remembers what it has already imported, so without this a changed file keeps running its old code and the change looks like it did nothing. Blender does not need restarting.")]
[NodePort("names", PortDirection.Input, "string", Description = "Module names to re-import, comma separated. Default ngol_blender")]
[NodePort("reloaded", PortDirection.Output, "string", Description = "One line per module saying whether it was imported, reloaded, or failed")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderReloadPythonNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string names = BlenderBridge.ToText(ctx.GetPortValue("names"), "ngol_blender");

        const string code =
            "names = [n.strip() for n in args['names'].split(',') if n.strip()]\n" +
            "result = {'ok': True, 'reloaded': reload_modules(names)}\n";

        using var reply = BlenderBridge.Run(code, new BlenderBridge.Args().Set("names", names),
                                            20.0, "py.reload");

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("reloaded", reply.Lines("reloaded"));
        ctx.SetPortValue("result", "reloaded: " + names);
    }
}


/// <summary>
/// いま Blender に何件あるかを数で読む。
/// シーンの構造を説明するノードではない。答えるのは「さっきの操作は効いたか」だけ。
/// </summary>
[NodeType("blender.scene.stat", "Blender", "Scene Stat",
    Version = "1.0.0",
    Description = "Count what the Blender file holds right now, so an action can be confirmed by numbers instead of by looking at the window. Run it before and after a change and compare. It answers how many, not how the scene is arranged: for the collection tree and data-block detail, run the official Blender MCP alongside this.")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Optional name prefix to count separately, e.g. NGOL. Leave empty to count everything only")]
[NodePort("scene_name", PortDirection.Output, "string", Description = "Name of the active scene")]
[NodePort("blend_file", PortDirection.Output, "string", Description = "Path of the .blend file, or (unsaved)")]
[NodePort("object_count", PortDirection.Output, "number", Description = "How many objects exist in this file")]
[NodePort("mesh_count", PortDirection.Output, "number", Description = "How many of them are meshes")]
[NodePort("matched", PortDirection.Output, "number", Description = "How many names started with the prefix")]
[NodePort("active_object", PortDirection.Output, "string", Description = "Name of the active object, empty when there is none")]
[NodePort("frame", PortDirection.Output, "number", Description = "Current frame")]
[NodePort("by_type", PortDirection.Output, "string", Description = "Object count broken down by type, as JSON")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable one-line summary, or the reason it failed")]
public sealed class BlenderSceneStatNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var args = new BlenderBridge.Args()
            .Set("prefix", BlenderBridge.ToText(ctx.GetPortValue("prefix")));

        using var reply = BlenderBridge.CallPy("scene_stat", args);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("scene_name", reply.Text("scene_name"));
        ctx.SetPortValue("blend_file", reply.Text("blend_file"));
        ctx.SetPortValue("object_count", reply.Number("object_count"));
        ctx.SetPortValue("mesh_count", reply.Number("mesh_count"));
        ctx.SetPortValue("matched", reply.Number("matched"));
        ctx.SetPortValue("active_object", reply.Any("active_object"));
        ctx.SetPortValue("frame", reply.Number("frame"));
        ctx.SetPortValue("by_type", reply.Any("by_type"));

        ctx.SetPortValue("result",
            "scene '" + reply.Text("scene_name") + "': " + reply.Number("object_count")
            + " objects (" + reply.Number("mesh_count") + " mesh), matched "
            + reply.Number("matched") + ", frame " + reply.Number("frame"));
    }
}


/// <summary>何が在るかを平らな行で読む。前後で突き合わせるための形。</summary>
[NodeType("blender.object.list", "Blender", "List Objects",
    Version = "1.0.0",
    Description = "List objects as flat rows, each with position, rotation, scale, vertex count and material. The rows are flat and sorted by name on purpose, so two runs can be compared line by line to see exactly what an action changed.")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Only list names starting with this. Empty lists everything")]
[NodePort("type", PortDirection.Input, "string", Description = "Only list this object type, e.g. MESH / CAMERA / LIGHT. Empty lists every type")]
[NodePort("limit", PortDirection.Input, "number", Description = "How many rows to return at most, 1-500. Default 50")]
[NodePort("matched", PortDirection.Output, "number", Description = "How many matched the filter, before the limit")]
[NodePort("shown", PortDirection.Output, "number", Description = "How many rows are in the output")]
[NodePort("names", PortDirection.Output, "string", Description = "Matching names, one per line")]
[NodePort("rows_json", PortDirection.Output, "string", Description = "The rows as JSON, with position, rotation, scale, verts and material")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderObjectListNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var args = new BlenderBridge.Args()
            .Set("prefix", BlenderBridge.ToText(ctx.GetPortValue("prefix")))
            .Set("type", BlenderBridge.ToText(ctx.GetPortValue("type")))
            .Set("limit", BlenderBridge.ToDouble(ctx.GetPortValue("limit"), 50));

        using var reply = BlenderBridge.CallPy("list_objects", args);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("matched", reply.Number("matched"));
        ctx.SetPortValue("shown", reply.Number("shown"));
        ctx.SetPortValue("names", reply.Column("objects", "name"));
        ctx.SetPortValue("rows_json", reply.Any("objects"));
        ctx.SetPortValue("result",
            reply.Number("matched") + " matched, showing " + reply.Number("shown"));
    }
}


/// <summary>目に見えるものを作る。数・半径・大きさを変えると画面が明確に変わる。</summary>
[NodeType("blender.object.spawn", "Blender", "Spawn Ring",
    Version = "1.0.0",
    Description = "Create meshes arranged on a ring, so a change to any parameter is obvious on screen. One mesh is shared between the copies, which keeps a high count cheap, while each copy gets its own material so they can be told apart in the solid viewport. Wire a Scene Stat before and after to confirm the effect by numbers as well.")]
[NodePort("shape", PortDirection.Input, "string", Description = "cube / sphere / icosphere / cone / cylinder / monkey. Default cube")]
[NodePort("count", PortDirection.Input, "number", Description = "How many to make, 1-500. Default 8. With 1 it sits at the origin instead of on the ring")]
[NodePort("radius", PortDirection.Input, "number", Description = "Radius of the ring. Default 4")]
[NodePort("size", PortDirection.Input, "number", Description = "Size of one object. Default 1")]
[NodePort("height", PortDirection.Input, "number", Description = "Z the ring sits at. Default 0")]
[NodePort("wave", PortDirection.Input, "number", Description = "How far the ring waves up and down. 0 keeps it flat")]
[NodePort("spin", PortDirection.Input, "number", Description = "Rotate the whole ring, in degrees")]
[NodePort("rainbow", PortDirection.Input, "boolean", Description = "true gives every copy its own hue. Default true")]
[NodePort("color", PortDirection.Input, "string", Description = "Colour used when rainbow is off, as RRGGBB hex")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Name prefix. Clear Objects removes by this. Default NGOL")]
[NodePort("created", PortDirection.Output, "number", Description = "How many were made")]
[NodePort("total_objects", PortDirection.Output, "number", Description = "Objects in the file after this ran")]
[NodePort("names", PortDirection.Output, "string", Description = "Names that were made, one per line")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderSpawnNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var args = new BlenderBridge.Args()
            .Set("shape", BlenderBridge.ToText(ctx.GetPortValue("shape"), "cube"))
            .Set("count", BlenderBridge.ToDouble(ctx.GetPortValue("count"), 8))
            .Set("radius", BlenderBridge.ToDouble(ctx.GetPortValue("radius"), 4))
            .Set("size", BlenderBridge.ToDouble(ctx.GetPortValue("size"), 1))
            .Set("height", BlenderBridge.ToDouble(ctx.GetPortValue("height"), 0))
            .Set("wave", BlenderBridge.ToDouble(ctx.GetPortValue("wave"), 0))
            .Set("spin", BlenderBridge.ToDouble(ctx.GetPortValue("spin"), 0))
            .Set("rainbow", BlenderBridge.ToBool(ctx.GetPortValue("rainbow"), true))
            .Set("color", BlenderBridge.ToText(ctx.GetPortValue("color")))
            .Set("prefix", BlenderBridge.ToText(ctx.GetPortValue("prefix"), "NGOL"));

        using var reply = BlenderBridge.CallPy("spawn_ring", args, 60.0);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("created", reply.Number("created"));
        ctx.SetPortValue("total_objects", reply.Number("total_objects"));
        ctx.SetPortValue("names", reply.Lines("names"));
        ctx.SetPortValue("result",
            "made " + reply.Number("created") + " " + reply.Text("shape")
            + " in " + reply.Milliseconds + "ms; the file now holds "
            + reply.Number("total_objects") + " objects");
    }
}


/// <summary>作ったものをまとめて動かす。実行するたびに画面が変わる。</summary>
[NodeType("blender.object.move", "Blender", "Move Objects",
    Version = "1.0.0",
    Description = "Rotate, lift and scale every object whose name starts with the prefix, all at once. Running it again keeps moving them, which makes it easy to see on screen that the graph really ran.")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Name prefix to act on. Default NGOL")]
[NodePort("spin", PortDirection.Input, "number", Description = "Degrees to turn the whole ring by, each run. Default 15")]
[NodePort("dz", PortDirection.Input, "number", Description = "How far to lift them, each run")]
[NodePort("scale", PortDirection.Input, "number", Description = "Multiplier applied to their size. 1 leaves it alone")]
[NodePort("moved", PortDirection.Output, "number", Description = "How many were affected")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderMoveNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var args = new BlenderBridge.Args()
            .Set("prefix", BlenderBridge.ToText(ctx.GetPortValue("prefix"), "NGOL"))
            .Set("spin", BlenderBridge.ToDouble(ctx.GetPortValue("spin"), 15))
            .Set("dz", BlenderBridge.ToDouble(ctx.GetPortValue("dz"), 0))
            .Set("scale", BlenderBridge.ToDouble(ctx.GetPortValue("scale"), 1));

        using var reply = BlenderBridge.CallPy("move_prefix", args);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("moved", reply.Number("moved"));
        ctx.SetPortValue("result", "moved " + reply.Number("moved") + " objects");
    }
}


/// <summary>作ったものを片付ける。接頭辞が空のときは何もしない。</summary>
[NodeType("blender.object.clear", "Blender", "Clear Objects",
    Version = "1.1.3",
    Description = "Remove every object whose name starts with any of the given prefixes, together with the meshes and materials nobody else uses. Several prefixes can be listed comma separated, so one run clears a scene that several different nodes have added to. An empty prefix is refused rather than treated as everything, so a blank field cannot empty the scene by accident.")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Name prefixes to remove, comma separated, e.g. NGOL,GRID,CONE,PY. Leave the port unconnected to use NGOL. An explicitly empty value is refused, so clearing the field cannot delete anything")]
[NodePort("removed", PortDirection.Output, "number", Description = "How many objects were removed")]
[NodePort("removed_materials", PortDirection.Output, "number", Description = "How many materials went with them")]
[NodePort("orphan_meshes", PortDirection.Output, "number", Description = "Meshes left with no user. Watch this stay near zero: a number that keeps climbing means something is leaking into the .blend")]
[NodePort("total_objects", PortDirection.Output, "number", Description = "Objects left in the file")]
[NodePort("names", PortDirection.Output, "string", Description = "Names that were removed, one per line")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed. Removing many objects can take longer than the 15 seconds an MCP client waits; the removal still completes inside Blender, and passing async to run_node returns the outputs through check_job_status")]
public sealed class BlenderClearNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        // 空文字は「未指定」ではなく「明示的に空」。ToText は空を既定値へ倒すので、
        // 削除のような取り消せない操作には使えない。
        var args = new BlenderBridge.Args()
            .Set("prefix", BlenderBridge.ToTextKeepEmpty(ctx.GetPortValue("prefix"), "NGOL"));

        using var reply = BlenderBridge.CallPy("clear_prefix", args, 60.0);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("removed", reply.Number("removed"));
        ctx.SetPortValue("removed_materials", reply.Number("removed_materials"));
        ctx.SetPortValue("orphan_meshes", reply.Number("orphan_meshes"));
        ctx.SetPortValue("total_objects", reply.Number("total_objects"));
        ctx.SetPortValue("names", reply.Lines("names"));
        ctx.SetPortValue("result",
            "removed " + reply.Number("removed") + " objects and "
            + reply.Number("removed_materials") + " materials; "
            + reply.Number("total_objects") + " objects left");
    }
}


/// <summary>Blender が描いている画面をそのまま撮る。</summary>
[NodeType("blender.capture", "Blender", "Capture Window",
    Version = "1.0.0",
    Description = "Ask Blender to write a PNG of its own window. This is the picture Blender itself draws, so nothing else on the desktop gets into it and the window does not have to be in front. Not available in background mode.")]
[NodePort("name", PortDirection.Input, "string", Description = "File name to write, .png is added when missing. Default viewport.png")]
[NodePort("editor_only", PortDirection.Input, "boolean", Description = "true captures only the editor under the cursor instead of the whole window")]
[NodePort("path", PortDirection.Output, "string", Description = "Where the PNG was written")]
[NodePort("width", PortDirection.Output, "number", Description = "Width in pixels")]
[NodePort("height", PortDirection.Output, "number", Description = "Height in pixels")]
[NodePort("bytes", PortDirection.Output, "number", Description = "File size in bytes")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderCaptureNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = BlenderBridge.ToText(ctx.GetPortValue("name"), "viewport.png");
        if (!name.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) name += ".png";
        // 置き場所はこちらで決める。Python 側に「どこへ書くか」を選ばせない。
        string path = Path.Combine(BlenderBridge.NgolRoot(), "blender_bridge", "out",
                                   Path.GetFileName(name));

        var args = new BlenderBridge.Args()
            .Set("path", path)
            .Set("editor_only", BlenderBridge.ToBool(ctx.GetPortValue("editor_only")));

        using var reply = BlenderBridge.CallPy("capture_window", args, 60.0);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("path", reply.Text("path"));
        ctx.SetPortValue("width", reply.Number("width"));
        ctx.SetPortValue("height", reply.Number("height"));
        ctx.SetPortValue("bytes", reply.Number("bytes"));
        ctx.SetPortValue("result",
            reply.Number("width") + "x" + reply.Number("height") + " -> " + reply.Text("path"));
    }
}


/// <summary>
/// cols x rows 個を、中心を原点として gap 間隔で並べる。位置で色を変えるので、
/// 並びが目で確かめられる。実体は Python の土台 `ngol_blender.spawn_grid`。
/// </summary>
[NodeType("blender.object.grid", "Blender", "Spawn Grid",
    Version = "1.0.0",
    Description = "Create meshes arranged on a grid, coloured by their position so the layout can be read at a glance. Use it when the ring is not the shape you want to look at.")]
[NodePort("shape", PortDirection.Input, "string", Description = "cube / sphere / icosphere / cone / cylinder / monkey. Default cube")]
[NodePort("cols", PortDirection.Input, "number", Description = "Columns, 1-60. Default 6")]
[NodePort("rows", PortDirection.Input, "number", Description = "Rows, 1-60. Default 6")]
[NodePort("gap", PortDirection.Input, "number", Description = "Distance between neighbours. Default 2")]
[NodePort("size", PortDirection.Input, "number", Description = "Size of one object. Default 1")]
[NodePort("height", PortDirection.Input, "number", Description = "Z the grid sits at. Default 0")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Name prefix. Clear Objects removes by this. Default GRID")]
[NodePort("created", PortDirection.Output, "number", Description = "How many were made")]
[NodePort("total_objects", PortDirection.Output, "number", Description = "Objects in the file after this ran")]
[NodePort("names", PortDirection.Output, "string", Description = "Names that were made, one per line")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome, or the reason it failed")]
public sealed class BlenderSpawnGridNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var args = new BlenderBridge.Args()
            .Set("shape", BlenderBridge.ToText(ctx.GetPortValue("shape"), "cube"))
            .Set("cols", BlenderBridge.ToDouble(ctx.GetPortValue("cols"), 6))
            .Set("rows", BlenderBridge.ToDouble(ctx.GetPortValue("rows"), 6))
            .Set("gap", BlenderBridge.ToDouble(ctx.GetPortValue("gap"), 2))
            .Set("size", BlenderBridge.ToDouble(ctx.GetPortValue("size"), 1))
            .Set("height", BlenderBridge.ToDouble(ctx.GetPortValue("height"), 0))
            .Set("prefix", BlenderBridge.ToText(ctx.GetPortValue("prefix"), "GRID"));

        using var reply = BlenderBridge.CallPy("spawn_grid", args, 60.0);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("created", reply.Number("created"));
        ctx.SetPortValue("total_objects", reply.Number("total_objects"));
        ctx.SetPortValue("names", reply.Lines("names"));
        ctx.SetPortValue("result",
            "made " + reply.Number("created") + " on a "
            + reply.Number("cols") + "x" + reply.Number("rows") + " grid in "
            + reply.Milliseconds + "ms");
    }
}


/// <summary>いま開いているファイルの外部参照と未使用データを洗う。</summary>
[NodeType("blender.file.audit", "Blender", "Audit File",
    Version = "1.0.0",
    Description = "Report what this file points at outside itself and what nothing is using: references whose file is gone, paths written absolutely rather than relative to the .blend, packed data, linked libraries, and datablocks with no users. Runs without a window, so it can be pointed at many files in turn.")]
[NodePort("limit", PortDirection.Input, "number",
    Description = "How many rows to list at most, 1-500. Default 50. The counts are of everything; only the listing is cut")]
[NodePort("blend_file", PortDirection.Output, "string",
    Description = "Path of the file being looked at, or (unsaved)")]
[NodePort("saved", PortDirection.Output, "boolean",
    Description = "false for a file that has never been saved. Relative paths cannot be resolved then, so treat the missing count with care")]
[NodePort("external_refs", PortDirection.Output, "number",
    Description = "How many datablocks point at a file outside this one")]
[NodePort("missing", PortDirection.Output, "number",
    Description = "How many of those files are not there. Packed data is not counted, because it does not need the file")]
[NodePort("absolute", PortDirection.Output, "number",
    Description = "How many paths are absolute rather than relative to the .blend. These break as soon as the project is moved to another machine")]
[NodePort("packed", PortDirection.Output, "number",
    Description = "How many are stored inside the .blend itself")]
[NodePort("libraries", PortDirection.Output, "number",
    Description = "How many other .blend files this one links to")]
[NodePort("unused", PortDirection.Output, "number",
    Description = "Datablocks nobody uses. Only meaningful while the file is open: saving drops them, so a freshly loaded file always reports zero")]
[NodePort("missing_names", PortDirection.Output, "string",
    Description = "Names whose file is gone, one per line")]
[NodePort("absolute_names", PortDirection.Output, "string",
    Description = "Names written with an absolute path, one per line")]
[NodePort("unused_names", PortDirection.Output, "string",
    Description = "Unused datablocks as kind:name, one per line")]
[NodePort("listing", PortDirection.Output, "string",
    Description = "One line per external reference, marked ok / packed / MISSING")]
[NodePort("result", PortDirection.Output, "string",
    Description = "Human-readable one-line summary, or the reason it failed")]
public sealed class BlenderFileAuditNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var args = new BlenderBridge.Args()
            .Set("limit", BlenderBridge.ToDouble(ctx.GetPortValue("limit"), 50.0));

        using var reply = BlenderBridge.CallPy("audit_paths", args);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("blend_file", reply.Text("blend_file"));
        ctx.SetPortValue("saved", reply.Bool("saved"));
        ctx.SetPortValue("external_refs", reply.Number("external_refs"));
        ctx.SetPortValue("missing", reply.Number("missing"));
        ctx.SetPortValue("absolute", reply.Number("absolute"));
        ctx.SetPortValue("packed", reply.Number("packed"));
        ctx.SetPortValue("libraries", reply.Number("libraries"));
        ctx.SetPortValue("unused", reply.Number("unused"));
        ctx.SetPortValue("missing_names", reply.Lines("missing_names"));
        ctx.SetPortValue("absolute_names", reply.Lines("absolute_names"));
        ctx.SetPortValue("unused_names", reply.Lines("unused_names"));
        ctx.SetPortValue("listing", reply.Text("listing"));

        ctx.SetPortValue("result",
            reply.Number("external_refs") + " external refs; "
            + reply.Number("missing") + " missing, "
            + reply.Number("absolute") + " absolute, "
            + reply.Number("packed") + " packed; "
            + reply.Number("libraries") + " linked libraries; "
            + reply.Number("unused") + " unused");
    }
}
