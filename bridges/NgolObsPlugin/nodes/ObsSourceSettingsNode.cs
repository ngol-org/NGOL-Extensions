using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ソースの設定を、そのソース自身の言葉で読み書きする。
///
/// 設定の項目名は種類ごとに違い、こちらで表を持っても種類が増えれば古くなる。
/// だから項目を並べず、ホストが持っている形をそのまま渡す。
/// 何が書けるかは、まず読んで返ってきた形を見れば分かる。
///
/// 書いたあとは必ず読み戻した結果を返すので、受け取られなかった項目は
/// 「書いたのに変わっていない」として見える。
/// </summary>
[NodeType("obs.source.settings", "OBS", "Source Settings",
    Version = "1.0.0",
    Description = "Reads and writes a source's settings in the source's own vocabulary. Field names differ per kind of source and a table kept here would go stale, so the host's own shape is passed through untouched - read first and the shape shows what can be written. A write always returns the settings read back, so a field the source ignored shows up as unchanged.")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Source to read or write")]
[NodePort("settings", PortDirection.Input, "string", Description = "JSON object to merge in. Leave empty to only read")]
[NodePort("applied", PortDirection.Output, "boolean", Description = "true when a write was accepted")]
[NodePort("settings_json", PortDirection.Output, "string", Description = "The settings as they stand now")]
[NodePort("source_id", PortDirection.Output, "string", Description = "What kind of source it is")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSourceSettingsNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        string settings = (ctx.GetPortValue("settings") as string ?? "").Trim();

        ctx.SetPortValue("applied", false);

        if (name.Length == 0)
        {
            ctx.SetPortValue("result", "give the name of a source");
            return;
        }

        bool writing = settings.Length > 0;
        var request = new ObsNative.Request(writing ? "source.settings.set" : "source.settings.get")
            .With("name", name);
        if (writing) request.With("settings", settings);

        using var reply = ObsNative.Call(request);

        if (!reply.Ok)
        {
            ctx.SetPortValue("result", reply.Error);
            return;
        }

        ctx.SetPortValue("applied", writing);
        ctx.SetPortValue("settings_json", reply.Text("settings"));
        ctx.SetPortValue("source_id", reply.Text("id"));
        ctx.SetPortValue("result", writing
            ? "'" + name + "' updated; the settings above are what it holds now"
            : "'" + name + "' read");
    }
}
