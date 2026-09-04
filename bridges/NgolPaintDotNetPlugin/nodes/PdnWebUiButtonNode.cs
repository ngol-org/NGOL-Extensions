using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストの右上のメニュー帯へ項目を 1 つ足す。押すとメニューが開き、そこから編集画面を開いたり、
/// 保存してあるグラフを名指しで開いたりできる。enabled=false で外れ、ホストは元どおりになる。
///
/// 帯は WinForms の ToolStrip なので、<c>Items</c> へ足せばホストが並べてくれる。
/// 位置も大きさも計算しない。ホストがアイコンを増減しても並びは向こうが面倒を見る。
/// 見た目もホストの renderer が描くため、隣の項目と揃う。
///
/// 開く先は設定値ではなく、NGOL が実際に開いた口を聞く。
/// 設定したポートが使用中なら NGOL は空きへ移るため、設定値では繋がらないことがある。
///
/// メニューにノードを全部並べることはしない。100 本を超えており、選べる一覧にならない。
/// 出すのはこのホスト向けのものだけで、それ以外は編集画面の仕事にする。
///
/// ホストの UI に触るので、追加も削除もホストの UI スレッドで行う。
///
/// enabled=true は、既に在っても作り直す。足した項目は足した時点のコードを握るので、
/// このファイルを直して読み込み直しただけでは、帯の中は古いほうが動き続ける。
/// </summary>
[NodeType("pdn.ui.webui_button", "Paint.NET", "WebUI Menu",
    Version = "1.11.0",
    Description = "Add an item to the host's own toolbar strip: open the node graph editor, open a saved graph by "
                + "name, or run one of this bridge's nodes. The item is registered with the strip, so the host lays it "
                + "out and draws it like its own. Nothing is written to disk and the host is back to normal once it is "
                + "removed.")]
[NodePort("enabled", PortDirection.Input, "boolean",
    Description = "true (default) = put the item there. false = take it away")]
[NodePort("status", PortDirection.Output, "string",
    Description = "\"added\" / \"replaced\" / \"removed\" / \"not there\", or the step of the walk that could not be taken")]
[NodePort("port", PortDirection.Output, "number",
    Description = "The port the menu will open. 0 when NGOL is not serving, in which case nothing is added")]
public sealed class PdnWebUiButtonNode : INode
{
    /// <summary>足した項目を後で見分けるための名前。ホストの項目とぶつからない綴りにする。</summary>
    private const string ItemName = "NgolWebUiButton";

    /// <summary>ホストの右上のメニュー帯。</summary>
    private const string StripName = "PdnAuxMenu";

    /// <summary>ノードが最後の結果を控える鍵の末尾。頭はノードの ID そのもの。</summary>
    private const string ResultKeySuffix = ".result";

    /// <summary>
    /// メニューへ出すノード。全部並べても選べないので、このホスト向けだけにする。
    ///
    /// 何かを始めるものは、止める側も同じ並びに置く。
    /// メニューから始められて止められないと、外す手が編集画面にしか無くなる。
    /// 入力を 1 つ渡せるので、同じノードを違う値で 2 度並べれば足りる。
    /// </summary>
    private static readonly (string Id, string Label, string Port, object? Value)[] OwnNodes =
    {
        ("pdn.app.info",     "App info",              "", null),
        ("pdn.plugins.list", "Plugin inventory",      "", null),
        ("pdn.doc.info",     "Document info",         "", null),
        ("pdn.tools.remember_layout", "Remember tool layout", "enabled", true),
        ("pdn.tools.remember_layout", "Stop remembering",     "enabled", false),
        ("pdn.fx.extend", "Sharpen per channel",       "effect", "sharpen"),
        ("pdn.fx.extend", "Motion blur per channel",   "effect", "motion_blur"),
        ("pdn.fx.extend", "Gaussian blur per channel", "effect", "gaussian_blur"),
        ("pdn.fx.extend", "Frosted glass per channel", "effect", "frosted_glass"),
        ("pdn.fx.extend", "Undo every patch",          "effect", "off"),
    };

    public void Execute(IExecutionContext ctx)
    {
        var enabled = !(ctx.GetPortValue("enabled") is bool b) || b;

        var port = ServerPort();
        ctx.SetPortValue("port", (double)port);

        var main = MainForm();
        if (main == null) { ctx.SetPortValue("status", "the host has no main window yet"); return; }

        var status = "";
        main.Invoke(new Action(() => status = Apply(ctx, main, enabled, port)));
        ctx.SetPortValue("status", status);
    }

    private static string Apply(IExecutionContext ctx, Form main, bool enabled, int port)
    {
        if (FindByName(main, StripName) is not ToolStrip strip) return "the host's menu strip was not found";

        var existing = strip.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Name == ItemName);

        if (!enabled)
        {
            if (existing == null) return "not there";
            Take(strip, existing);
            return "removed";
        }

        if (port <= 0) return "NGOL is not serving a port yet; nothing added";

        // 既に在っても作り直す。項目は足した時点のコードを握ったままなので、
        // このファイルを直して読み込み直しても、入れ替えなければ古いほうが動き続ける。
        var replaced = existing != null;
        if (replaced) Take(strip, existing!);

        var item = new ToolStripDropDownButton
        {
            Name = ItemName,
            // 絵だけ。文字を出すと帯の中でこの項目だけ幅が広くなる。
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            Image = Glyph(16, strip.ForeColor),
            ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "NGOL",
            ShowDropDownArrow = false,
        };

        // 中身は開かれるたびに組み直す。保存したグラフも待ち受け先も、
        // 足した時点の姿ではなく、開いた瞬間の姿を見せるため。
        item.DropDownOpening += (_, _) =>
        {
            item.DropDownItems.Clear();
            foreach (var entry in BuildMenu(ctx)) item.DropDownItems.Add(entry);
        };

        // 設定と ? はホストの並びの末尾に居る。その手前へ入れて、末尾の 2 つを動かさない。
        var settings = FieldValue(strip, "showSettingsButton") as ToolStripItem;
        var at = settings != null ? strip.Items.IndexOf(settings) : strip.Items.Count;
        if (at < 0) at = strip.Items.Count;

        // 大きさは隣の項目から写す。自前に任せると数画素狭くなって並びが揃わない。
        if (strip.Items.Count > 0)
        {
            item.AutoSize = false;
            item.Size = strip.Items[strip.Items.Count - 1].Size;
        }

        // 帯は中身ちょうどの幅で固定されており（AutoSize=false・CanOverflow=false）、
        // 足しただけでは末尾の項目が幅からはみ出して描かれなくなる。
        var before = strip.Width;
        strip.Items.Insert(at, item);
        strip.Width = before + item.Width;

        // 広げたぶん左へ寄せないと、右端がウィンドウの外へ出る。
        strip.Left -= item.Width;
        return replaced ? "replaced" : "added";
    }

    /// <summary>項目を外し、そのぶん帯の幅と位置を戻す。</summary>
    private static void Take(ToolStrip strip, ToolStripItem item)
    {
        var w = item.Width;
        strip.Items.Remove(item);
        item.Dispose();
        strip.Width = Math.Max(0, strip.Width - w);
        strip.Left += w;
    }

    // ---------------------------------------------------------------- メニュー

    private static List<ToolStripItem> BuildMenu(IExecutionContext ctx)
    {
        var items = new List<ToolStripItem>();
        var port = ServerPort();

        // ホストは NGOL を知らないので、項目名にも NGOL を入れる。
        // 「WebUI を開く」だけでは、このアプリの中で何のことか分からない。
        items.Add(Item("Open NGOL WebUI", () => OpenEditor(port), ctx.Logger));

        var graphMenu = new ToolStripMenuItem("Open saved graph");
        var graphs = SavedGraphs();
        if (graphs.Count == 0)
        {
            graphMenu.DropDownItems.Add(Disabled("(none saved yet)"));
        }
        else
        {
            foreach (var name in graphs)
            {
                var captured = name;
                graphMenu.DropDownItems.Add(
                    Item(captured, () => OpenGraph(port, captured, ctx.Logger), ctx.Logger));
            }
        }
        items.Add(graphMenu);

        // 全ノードは出さない。ここはこのホスト向けの入口で、一覧は編集画面の仕事。
        var nodeMenu = new ToolStripMenuItem("Run a node");
        foreach (var (id, label, portName, value) in OwnNodes)
        {
            var entry = (Id: id, Label: label, Port: portName, Value: value);
            nodeMenu.DropDownItems.Add(Item(entry.Label,
                () => RunAndShow(ctx, entry.Id, entry.Label, entry.Port, entry.Value), ctx.Logger));
        }
        items.Add(nodeMenu);

        items.Add(new ToolStripSeparator());
        items.Add(Disabled(port > 0 ? "NGOL is listening on port " + port : "NGOL is not serving a port"));
        return items;
    }

    private static ToolStripMenuItem Item(string text, Action onClick, INodeLogger? log = null)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            // ここで投げるとホストの UI スレッドで落ちる。飲むのは必須だが、
            // 飲んだままだと押しても何も起きない状態と区別が付かないので必ず残す。
            try { onClick(); }
            catch (Exception ex) { log?.LogError("[pdn.ui] " + text + ": " + ex); }
        };
        return item;
    }

    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    /// <summary>
    /// ノードを 1 つ走らせ、そのノードが控えた結果をウィンドウに出す。
    ///
    /// 実行そのものは値を返さない（<c>QuickExecuteNode</c> は出力ポートを捨てる）。
    /// 読むのは、ノードが <c>Store</c> へ控えたほう。鍵はノードの ID から機械的に決まる。
    /// </summary>
    private static void RunAndShow(IExecutionContext ctx, string id, string label, string portName, object? value)
    {
        ctx.QuickExecuteNode(id, portName, value);
        var text = ctx.Store.Get<string>(id + ResultKeySuffix);
        ShowResult(label, string.IsNullOrEmpty(text) ? "This node left no result." : text);
    }

    /// <summary>結果を出すウィンドウ。ホストのウィンドウを親にするので、ホストを閉じれば一緒に閉じる。</summary>
    private static void ShowResult(string title, string text)
    {
        var form = new ResultWindow("NGOL - " + title, text);
        form.Show(MainForm());
    }

    /// <summary>
    /// ホスト自身のウィンドウの土台に乗せる。明暗の切り替えと非クライアント領域の描き方が
    /// ホストのウィンドウと同じになり、素の Form のように 1 つだけ浮かない。
    ///
    /// 浮動パレットの型そのもの（FloatingToolForm）は internal なので、
    /// 別アセンブリからは継承できない。公開されているのはその 1 つ下の土台まで。
    /// </summary>
    private sealed class ResultWindow : PaintDotNet.PdnBaseForm
    {
        private readonly TextBox _box;

        public ResultWindow(string title, string text)
        {
            Text = title;
            UseAppThemeColors = true;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 420);
            ShowInTaskbar = false;

            _box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                // 表を崩さないため等幅で。改行はこの部品の作法に合わせて入れ直す。
                Font = new Font("Consolas", 9f),
                Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            };
            Controls.Add(_box);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 中身の色は土台が決めた色から取る。ここを既定のままにすると、
            // ホストが暗い配色のときだけ文字が読めなくなる。
            _box.BackColor = BackColor;
            _box.ForeColor = ForeColor;
            _box.SelectionLength = 0;
        }
    }

    /// <summary>保存してあるグラフの名前。NGOL が書く場所をそのまま読む。</summary>
    private static List<string> SavedGraphs()
    {
        try
        {
            var dir = Path.Combine(NgolRoot(), "Graphs");
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void OpenEditor(int fallbackPort)
    {
        var port = LivePort(fallbackPort);
        if (port <= 0) return;
        try
        {
            // ループバックのみ。外部のアドレスは開かない。
            Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port + "/") { UseShellExecute = true });
        }
        catch
        {
            // 開けなくてもホストを巻き込まない。
        }
    }

    /// <summary>
    /// 保存してあるグラフを編集画面に出す。
    ///
    /// URL では開けない。名前を付けて開くクエリパラメータの方式は廃止されている。
    /// 待ち受けへ open_graph を送ると、繋がっているタブへ押し出される。
    ///
    /// 押し出す先は最後に繋がったタブなので、先にブラウザを開くと取り違える。
    /// こちらが開いたタブが繋がるより先に、既に開いていたタブへ出てしまい、
    /// 押した人が見ているウィンドウは空のまま残る。誰も居ないと分かったときだけ開く。
    ///
    /// 止める合図は「届いた」ではなく「出ている」。タブは繋がった瞬間から宛先になるが、
    /// 頁が組み上がる前に届いた分は受け取り手が居ないまま捨てられる。
    /// </summary>
    private static void OpenGraph(int fallbackPort, string id, INodeLogger? log)
    {
        var port = LivePort(fallbackPort);
        if (port <= 0) return;

        Task.Run(() =>
        {
            try
            {
                if (!Push(port, id)) OpenEditor(port);
            }
            catch (Exception ex) { log?.LogDebug("[pdn.ui] open_graph: " + ex.GetBaseException().Message); }

            for (var i = 0; i < 15; i++)
            {
                try
                {
                    if (ShowsGraph(port, id))
                    {
                        log?.LogInfo("[pdn.ui] the editor is showing " + id);
                        return;
                    }
                    Push(port, id);
                }
                catch (Exception ex) { log?.LogDebug("[pdn.ui] open_graph: " + ex.GetBaseException().Message); }
                Thread.Sleep(600);
            }
            log?.LogWarning("[pdn.ui] no editor tab took the graph: " + id);
        });
    }

    /// <summary>押し出しを 1 度頼み、宛先が居たかを返す。</summary>
    private static bool Push(int port, string id)
    {
        var reply = Ask(port, "{\"type\":\"open_graph\",\"id\":" + JsonSerializer.Serialize(id) + "}",
                        "open_graph_response", 3);
        return reply.Contains("\"delivered\":true");
    }

    /// <summary>編集画面がそのグラフを出しているか。押し出せたことは、開けたことを意味しない。</summary>
    private static bool ShowsGraph(int port, string id)
    {
        var reply = Ask(port, "{\"type\":\"get_canvas_graph\",\"target\":\"latest\",\"timeoutMs\":800}",
                        "get_canvas_graph_response", 4);
        if (reply.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return false;
            foreach (var r in results.EnumerateArray())
            {
                if (r.TryGetProperty("graph", out var g)
                    && g.TryGetProperty("id", out var gid)
                    && string.Equals(gid.GetString(), id, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 待ち受けへ 1 往復。繋いだ直後は別の知らせも流れてくるので、目当ての返事まで読み進める。
    ///
    /// <c>?client=mcp</c> は外せない。付けない接続はブラウザのタブとして数えられ、
    /// 最後に繋いだのはこちらなので、編集画面へ宛てた押し出しを自分で受け取ってしまう。
    /// </summary>
    private static string Ask(int port, string request, string responseType, int seconds)
    {
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws?client=mcp"), cts.Token).GetAwaiter().GetResult();
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(request)),
            WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();

        var buf = new byte[16384];
        var text = new StringBuilder();
        for (var i = 0; i < 64; i++)
        {
            text.Clear();
            WebSocketReceiveResult r;
            // 1 つの知らせが複数に分かれて届くので、終わりまで繋げてから見る。
            do
            {
                r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).GetAwaiter().GetResult();
                if (r.MessageType == WebSocketMessageType.Close) return "";
                text.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            }
            while (!r.EndOfMessage);

            var s = text.ToString();
            if (s.Contains("\"type\":\"" + responseType + "\"")) return s;
        }
        return "";
    }

    /// <summary>押された時点の待ち受け先。稼働中に口が移ることがあるため、その場で聞き直す。</summary>
    private static int LivePort(int fallbackPort)
    {
        var port = ServerPort();
        return port > 0 ? port : fallbackPort;
    }

    // ---------------------------------------------------------------- 絵

    /// <summary>
    /// 項目に載せる絵をその場で描く。外部のファイルを持たないので、配置物が 1 枚で済む。
    /// 大きさは帯が並べている項目に合わせる（22x25 の枠に 16px の絵）。
    ///
    /// 虫眼鏡。色は帯の文字色をそのまま使う。ホストの明暗が入れ替わっても隣の項目と揃う。
    /// 丸を含むのでアンチエイリアスを掛ける。切ると輪が階段になる。
    /// </summary>
    private static Bitmap Glyph(int side, Color color)
    {
        const float left = 1.5f, span = 9.5f, ring = 2f, handle = 3f;

        var bmp = new Bitmap(side, side);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var lens = new Pen(color, ring) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(lens, left, left, span, span);

        // 柄はレンズの縁から出す。中心から縁までは半径なので、斜め 45 度なら半径を sqrt(2) で割る。
        var center = left + span / 2f;
        var edge = span / 2f / (float)Math.Sqrt(2) + ring * 0.2f;
        using var stem = new Pen(color, handle) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(stem, center + edge, center + edge,
                   side - 1 - handle / 2f, side - 1 - handle / 2f);
        return bmp;
    }

    // ---------------------------------------------------------------- 足回り

    /// <summary>NGOL 一式の場所。読み込まれている本体の在処から辿る。</summary>
    private static string NgolRoot()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name != "NodeGraphModLab.Core") continue;
            var dir = Path.GetDirectoryName(asm.Location);
            if (!string.IsNullOrEmpty(dir)) return dir!;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// このホストのブリッジに聞く。ノードは実行時にコンパイルされ、ブリッジのアセンブリへの
    /// 参照を持てないので、型と名前で辿る。設定値ではなく実際に開いた口が返る。
    /// </summary>
    private static int ServerPort()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType("NgolForPaintDotNet.NgolBoot");
                if (type == null) continue;
                var value = type.GetProperty("ServerPort", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (value is int i) return i;
            }
        }
        catch
        {
            // 見つからなければ 0 のまま。何も足さずに理由を返す。
        }
        return 0;
    }

    private static object? FieldValue(object owner, string name)
    {
        try
        {
            return owner.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(owner);
        }
        catch
        {
            return null;
        }
    }

    private static Form? MainForm()
    {
        foreach (Form f in Application.OpenForms)
        {
            if (f.Name == "MainForm") return f;
        }
        return null;
    }

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
