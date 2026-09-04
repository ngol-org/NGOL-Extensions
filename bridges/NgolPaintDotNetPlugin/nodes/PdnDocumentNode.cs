using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// いま開いている画像の素性を返す。読むだけで、画像にも編集中の状態にも触らない。
///
/// ここへ届く道は公開 API ではない。ホストのウィンドウの中を名前で辿り、
/// そこから作業領域と画像へ入る。
///   MainForm -> appWorkspace -> ActiveDocumentWorkspace -> Document
/// 各段が見つからなければ、どこで止まったかを status に出して空で返す。
/// 版が変わって名前が動いたときに、黙って 0 を返さないため。
///
/// ホストの UI が持つものを読むので、実行はホストの UI スレッドへ渡す。
/// </summary>
[NodeType("pdn.doc.info", "Paint.NET", "Document Info",
    Version = "1.1.0",
    Description = "Report the image the host currently has open: size, layer count, layer names, and how many images "
                + "are open in total. Reads only - neither the image nor the editing state is touched.")]
[NodePort("width", PortDirection.Output, "number",
    Description = "Width of the active image in pixels. 0 when nothing is open or the path could not be walked")]
[NodePort("height", PortDirection.Output, "number",
    Description = "Height of the active image in pixels. 0 when nothing is open or the path could not be walked")]
[NodePort("layer_count", PortDirection.Output, "number",
    Description = "How many layers the active image has")]
[NodePort("layer_names", PortDirection.Output, "string",
    Description = "Layer names from bottom to top, comma separated")]
[NodePort("open_documents", PortDirection.Output, "number",
    Description = "How many images are open in the host, the active one included")]
[NodePort("status", PortDirection.Output, "string",
    Description = "\"ok\", \"no document\" when nothing is open, or the step of the walk that could not be taken")]
public sealed class PdnDocumentNode : INode
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>最後の結果を控える鍵。ノードの ID に .result を付けたもの。</summary>
    private const string ResultKey = "pdn.doc.info.result";

    public void Execute(IExecutionContext ctx)
    {
        var status = "ok";
        double width = 0, height = 0, layers = 0, docs = 0;
        var names = "";

        var main = MainForm();
        if (main == null)
        {
            Fill(ctx, 0, 0, 0, "", 0, "the host has no main window yet");
            return;
        }

        main.Invoke(new Action(() =>
        {
            var workspace = FindByName(main, "appWorkspace");
            if (workspace == null) { status = "the host's work area was not found"; return; }

            if (Get(workspace, "DocumentWorkspaces") is ICollection all) docs = all.Count;

            var active = Get(workspace, "ActiveDocumentWorkspace");
            if (active == null) { status = "no document"; return; }

            var document = Get(active, "Document");
            if (document == null) { status = "no document"; return; }

            width = ToNumber(Get(document, "Width"));
            height = ToNumber(Get(document, "Height"));

            if (Get(document, "Layers") is IEnumerable list)
            {
                var each = list.Cast<object>().ToArray();
                layers = each.Length;
                names = string.Join(", ", each.Select(l => Get(l, "Name") as string ?? ""));
            }
        }));

        Fill(ctx, width, height, layers, names, docs, status);
    }

    private static void Fill(IExecutionContext ctx, double w, double h, double layers, string names, double docs, string status)
    {
        ctx.SetPortValue("width", w);
        ctx.SetPortValue("height", h);
        ctx.SetPortValue("layer_count", layers);
        ctx.SetPortValue("layer_names", names);
        ctx.SetPortValue("open_documents", docs);
        ctx.SetPortValue("status", status);

        // 実行の応答は 1 回きりなので、後から読む側のために控える。
        ctx.Store.Set(ResultKey,
            $"size           : {w} x {h}\n"
          + $"layers         : {layers}\n"
          + $"layer names    : {names}\n"
          + $"open documents : {docs}\n"
          + $"status         : {status}");
    }

    private static Form? MainForm()
    {
        foreach (Form f in Application.OpenForms)
        {
            if (f.Name == "MainForm") return f;
        }
        return null;
    }

    private static object? Get(object owner, string name)
    {
        try { return owner.GetType().GetProperty(name, All)?.GetValue(owner); }
        catch { return null; }
    }

    private static double ToNumber(object? v)
        => v is IConvertible c ? Convert.ToDouble(c) : 0;

    private static Control? FindByName(Control root, string name)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Name == name) return c;
            var found = FindByName(c, name);
            if (found != null) return found;
        }
        return null;
    }
}
