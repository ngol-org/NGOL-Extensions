using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 画像ごとにツールウィンドウの置き場所を覚え、その画像へ戻ったときに戻す。
///
/// ホストはこれを持っていない。覚える場所も無いので、位置は NGOL のキー・値ストアへ書く。
/// ストアは再起動をまたぐので、次に立ち上げたときも同じ配置に戻る。
///
/// 覗きに行くのではなく、ホスト自身のイベントに乗る。使うのは
/// <c>ActiveDocumentWorkspaceChanging</c>（素の EventHandler）。
/// 対になる <c>...Changed</c> はホスト独自のデリゲート型で、しかもその型引数が
/// 別アセンブリの internal な型なので、こちらから名指しで書けない。
/// 切り替わったあとの処理は <c>BeginInvoke</c> へ回せば足りる。
///
/// 画像の見分けには開いているファイルのパスを使う。まだ保存していない画像は
/// パスを持たないので、覚える対象から外す。
///
/// 止めるまで載り続けるので、外す口を必ず用意する（enabled=false）。
/// 世代をまたぐ状態は AppDomain に置く。ノードの static はホットリロードで作り直され、
/// 古い世代の購読が外せないまま残る。
/// </summary>
[NodeType("pdn.tools.remember_layout", "Paint.NET", "Remember Tool Layout",
    Version = "1.0.0",
    Description = "Remember where the floating tool windows sit for each open image, and put them back when that "
                + "image becomes active again. The host has no such feature. Positions are kept in the key-value "
                + "store, so they survive a restart. Run again with enabled=false to stop; the host is left as it was.")]
[NodePort("enabled", PortDirection.Input, "boolean",
    Description = "true (default) = start remembering. false = stop and unsubscribe")]
[NodePort("status", PortDirection.Output, "string",
    Description = "\"watching\" / \"already watching\" / \"stopped\" / \"not watching\", or the step that could not be taken")]
[NodePort("switch_count", PortDirection.Output, "number",
    Description = "Image switches seen since it started")]
[NodePort("remembered", PortDirection.Output, "string",
    Description = "One line per image that has a stored layout")]
public sealed class PdnToolLayoutNode : INode
{
    /// <summary>置き場所を書く鍵の頭。後ろに画像のパスが付く。</summary>
    private const string LayoutKeyPrefix = "pdn.tools.layout.";

    /// <summary>最後の結果を控える鍵。ノードの ID に .result を付けたもの。</summary>
    private const string ResultKey = "pdn.tools.remember_layout.result";

    /// <summary>
    /// 世代をまたいで持つもの。入れ物は framework の型にする。
    /// 自分で定義した型を入れると、次の世代からは別の型に見えて取り出せない。
    /// </summary>
    private const string StateKey = "pdn.tools.remember_layout.state.v1";

    private const string EventName = "ActiveDocumentWorkspaceChanging";
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public void Execute(IExecutionContext ctx)
    {
        var enabled = !(ctx.GetPortValue("enabled") is bool b) || b;

        var main = MainForm();
        if (main == null) { Report(ctx, "the host has no main window yet", 0); return; }

        var status = "";
        var count = 0;
        main.Invoke(new Action(() =>
        {
            if (!enabled)
            {
                count = Count();
                // 止めるときも今の置き場所を控える。次に開いたとき同じ配置で始められる。
                Remember(ctx, main);
                status = Detach() ? "stopped" : "not watching";
                return;
            }

            if (State() != null) { count = Count(); status = "already watching"; return; }
            status = Attach(ctx, main);
            Remember(ctx, main);
        }));

        Report(ctx, status, count);
    }

    private void Report(IExecutionContext ctx, string status, int count)
    {
        var lines = ctx.Store.Keys(LayoutKeyPrefix)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => k.Substring(LayoutKeyPrefix.Length) + "  ->  " + (ctx.Store.Get<string>(k) ?? ""))
            .ToArray();
        var remembered = lines.Length == 0 ? "(nothing stored yet)" : string.Join("\n", lines);

        ctx.SetPortValue("status", status);
        ctx.SetPortValue("switch_count", (double)count);
        ctx.SetPortValue("remembered", remembered);
        ctx.Store.Set(ResultKey,
            $"status   : {status}\nswitches : {count}\n\n{remembered}");
    }

    // ---------------------------------------------------------------- 購読

    private static string Attach(IExecutionContext ctx, Form main)
    {
        var workspace = FindByName(main, "appWorkspace");
        if (workspace == null) return "the host's work area was not found";

        var ev = workspace.GetType().GetEvent(EventName, All);
        if (ev == null) return "the host does not expose " + EventName;

        // このイベントは切り替わる前に来る。この時点ではまだ前の画像が現役なので、
        // ここで控えれば「その画像を見ていたときの配置」になる。
        // 戻すのは切り替わったあとなので、行列へ回して順番を待つ。
        EventHandler handler = (_, _) =>
        {
            var state = State();
            if (state == null) return;
            state["count"] = Count() + 1;
            Remember(ctx, main);
            main.BeginInvoke(new Action(() => Restore(ctx, main)));
        };

        try { ev.AddEventHandler(workspace, handler); }
        catch (Exception ex) { return "could not subscribe: " + ex.GetType().Name; }

        var reg = ctx.RegisterPersistent(new PersistentCallbacks { OnStop = () => Detach() });

        AppDomain.CurrentDomain.SetData(StateKey, new Dictionary<string, object?>
        {
            ["workspace"] = workspace,
            ["handler"] = handler,
            ["count"] = 0,
            ["reg"] = reg,
        });
        return "watching";
    }

    private static bool Detach()
    {
        var state = State();
        if (state == null) return false;
        AppDomain.CurrentDomain.SetData(StateKey, null);

        if (state["workspace"] is object ws && state["handler"] is Delegate handler)
        {
            try { ws.GetType().GetEvent(EventName, All)?.RemoveEventHandler(ws, handler); } catch { }
        }
        try { (state["reg"] as IPersistentRegistration)?.Cancel(); } catch { }
        return true;
    }

    private static Dictionary<string, object?>? State()
        => AppDomain.CurrentDomain.GetData(StateKey) as Dictionary<string, object?>;

    private static int Count() => State()?["count"] is int n ? n : 0;

    // ---------------------------------------------------------------- 置き場所

    /// <summary>いま現役の画像に対して、ツールウィンドウの置き場所を控える。</summary>
    private static void Remember(IExecutionContext ctx, Form main)
    {
        var key = ActiveDocumentKey(main);
        if (key == null) return;

        var parts = ToolWindows()
            .Select(f => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5}",
                f.Name, f.Left, f.Top, f.Width, f.Height, f.Visible ? 1 : 0))
            .ToArray();
        if (parts.Length == 0) return;

        try { ctx.Store.Set(LayoutKeyPrefix + key, string.Join(";", parts)); } catch { }
    }

    /// <summary>いま現役の画像について控えてある置き場所があれば、その通りに戻す。</summary>
    private static void Restore(IExecutionContext ctx, Form main)
    {
        var key = ActiveDocumentKey(main);
        if (key == null) return;

        var stored = ctx.Store.Get<string>(LayoutKeyPrefix + key);
        if (string.IsNullOrEmpty(stored)) return;

        var byName = ToolWindows().ToDictionary(f => f.Name, f => f);
        foreach (var part in stored!.Split(';'))
        {
            var f = part.Split(',');
            if (f.Length != 6 || !byName.TryGetValue(f[0], out var form)) continue;
            if (!TryBounds(f, out var bounds)) continue;

            form.Bounds = bounds;
            // 画面の外へ出さない判断はホストが持っている。自分で計算しない。
            if (form is PaintDotNet.PdnBaseForm pdn) pdn.EnsureFormIsOnScreen();
            form.Visible = f[5] == "1";
        }
    }

    private static bool TryBounds(string[] f, out System.Drawing.Rectangle bounds)
    {
        bounds = default;
        if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) return false;
        if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return false;
        if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return false;
        if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (w <= 0 || h <= 0) return false;
        bounds = new System.Drawing.Rectangle(x, y, w, h);
        return true;
    }

    /// <summary>
    /// ホストの浮動ウィンドウ。土台の型がその印になる。
    /// 型そのものは別アセンブリの internal なので、名前で辿る。
    /// </summary>
    private static IEnumerable<Form> ToolWindows()
    {
        foreach (Form f in Application.OpenForms)
        {
            for (var b = f.GetType().BaseType; b != null; b = b.BaseType)
            {
                if (b.Name != "FloatingToolForm") continue;
                yield return f;
                break;
            }
        }
    }

    /// <summary>
    /// 現役の画像を見分ける手がかり。開いているファイルのパスを使う。
    /// まだ保存していない画像はパスを持たないので、覚える対象にしない。
    /// </summary>
    private static string? ActiveDocumentKey(Form main)
    {
        var workspace = FindByName(main, "appWorkspace");
        var active = workspace == null ? null : Get(workspace, "ActiveDocumentWorkspace");
        var path = active == null ? null : Get(active, "FilePath") as string;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    // ---------------------------------------------------------------- 足回り

    private static object? Get(object target, string name)
    {
        try { return target.GetType().GetProperty(name, All)?.GetValue(target); }
        catch { return null; }
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
