using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 対象が確認画面を出したまま待っていないかを見る。押すこともできる。
///
/// 応答があることは、待っていないことの証拠にならない。別のスレッドで動くものは
/// 画面が止まっている間も平常に応答するので、外からは何も異常が無いように見える。
/// 実際には画面がボタンを押されるのを待っている。
///
/// 判定はモーダルの仕組みそのものを使う。確認画面が出ている間、その持ち主の窓は
/// 無効化される。絵を見なくても、窓が無効かどうかで待ち状態が分かる。
/// </summary>
[NodeType("ngol.win32.modal_state", "Win32", "Modal State",
    Version = "1.1.1",
    Description = "Tells whether a process is sitting on a dialog waiting to be answered, and can answer it. A reply from the process is no proof that it is not waiting - anything running on another thread keeps responding while the window stands still - so this looks at the mechanism instead: while a modal dialog is up, the window that owns it is disabled. Reading and pressing both avoid waiting on the other window's thread, so a stuck target never drags the caller down with it.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Which process to look at. 0 (default) means the process this is running in")]
[NodePort("press", PortDirection.Input, "string", Description = "Text of the button to press, for example OK. Empty = look only")]
[NodePort("reveal", PortDirection.Input, "string", Description = "on = put any dialog that a click cannot reach in front of everything, off = undo that. Empty = leave the order alone. Undo it when done, or the dialog stays in front of every application")]
[NodePort("blocked", PortDirection.Output, "bool", Description = "true when a window is disabled because a dialog is waiting on it")]
[NodePort("dialogs", PortDirection.Output, "string", Description = "One line per dialog, as title / text / buttons")]
[NodePort("dialog_count", PortDirection.Output, "number", Description = "How many dialogs are up")]
[NodePort("pressed", PortDirection.Output, "bool", Description = "true when a button was actually pressed")]
[NodePort("revealed", PortDirection.Output, "number", Description = "How many dialogs were put in front, or taken back out of it. The reach figures in this same run were measured before that happened, so read them again to see the effect")]
[NodePort("windows", PortDirection.Output, "string", Description = "Every top level window of the process, as handle / enabled / how much of it a click would actually reach / position / class / title")]
[NodePort("unseen", PortDirection.Output, "string", Description = "Dialogs that are waiting while a click reaches none of them, and which window to focus to bring each one up. An owned window always rides above its owner, so focusing the owner reveals it")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ModalStateNode : INode
{
    // 標準の確認画面のクラス。持ち主を持たない窓でもこれなら確認画面として扱う。
    const string DialogClass = "#32770";

    public void Execute(IExecutionContext ctx)
    {
        uint pid = ctx.GetPortValue("processId") is double d && d > 0
            ? (uint)d
            : (uint)Process.GetCurrentProcess().Id;
        string press = (ctx.GetPortValue("press") as string ?? "").Trim();
        string reveal = (ctx.GetPortValue("reveal") as string ?? "").Trim().ToLowerInvariant();

        var query = new NgolWindowFind.Query
        {
            ProcessId = pid,
            VisibleOnly = true,
            TopLevelOnly = true,
        };
        var found = NgolWindowFind.Find(query);

        var windows = new StringBuilder();
        var dialogs = new StringBuilder();
        var unseen = new StringBuilder();
        int revealed = 0;
        int dialogCount = 0;
        bool blocked = false;
        bool pressed = false;

        foreach (var window in found.Windows)
        {
            // 見えているかは当たり判定で聞く。様式も座標もクリッピングも答えない。
            double reach = NgolWindowFind.VisibleShare(window.Handle, out string onTop);

            windows.Append("0x").Append(window.Handle.ToInt64().ToString("x"))
                   .Append((char)9).Append(window.Enabled ? "enabled" : "disabled")
                   .Append((char)9).Append("reach ").Append((int)Math.Round(reach * 100)).Append('%')
                   .Append((char)9).Append(window.Left).Append(',').Append(window.Top)
                   .Append((char)9).Append(window.ClassName)
                   .Append((char)9).Append(window.Title).Append((char)10);

            if (!window.Enabled) blocked = true;

            // 持ち主が居るだけでは確認画面ではない。ログ表示のような普通の浮いた窓も
            // 持ち主を持ち、しかも確認画面が出ている間はその持ち主も無効になるので、
            // 「持ち主が無効」だけで数えると一緒に釣れる。
            // 答えを待っているものは、確認画面のクラスを名乗るか、押すものを持っている。
            var owner = NgolWindowFind.OwnerOf(window.Handle);
            if (window.ClassName != DialogClass && !HasButton(window.Handle)) continue;

            dialogCount++;
            // 活性化は奪えないが、手前へ出すことはできる。Z 順の属性なので権利が要らない。
            if (reveal is "on" or "off")
            {
                if (reach <= 0 || reveal == "off")
                {
                    if (NgolWindowFind.KeepOnTop(window.Handle, reveal == "on")) revealed++;
                }
            }

            if (reach <= 0)
            {
                // 前面には出せない。持ち主を選んでもらう方が確実で、しかも正しい。
                // 持ち主を持つ窓は必ず持ち主より上に居るので、持ち主が上がれば一緒に上がる。
                string focusThis = owner != IntPtr.Zero
                    ? NgolWindowFind.Describe(owner).Title
                    : window.Title;

                unseen.Append(window.Title)
                      .Append((char)9).Append(window.Left).Append(',').Append(window.Top)
                      .Append((char)9).Append("behind ").Append(onTop)
                      .Append((char)9).Append("focus ").Append(focusThis).Append((char)10);
            }

            var texts = new List<string>();
            var buttons = new List<(IntPtr Handle, string Text)>();
            Collect(window.Handle, texts, buttons);

            var names = new List<string>();
            foreach (var b in buttons) names.Add(b.Text);

            dialogs.Append(window.Title)
                   .Append((char)9).Append(string.Join(" / ", texts))
                   .Append((char)9).Append(string.Join(" / ", names)).Append((char)10);

            if (press.Length == 0 || pressed) continue;

            foreach (var b in buttons)
            {
                // 表示文字には下線用の & が混ざる。比べる前に落とす。
                if (!string.Equals(b.Text.Replace("&", ""), press, StringComparison.OrdinalIgnoreCase)) continue;
                pressed = NgolWindowFind.ClickAsync(b.Handle);
                break;
            }
        }

        ctx.SetPortValue("blocked", blocked);
        ctx.SetPortValue("unseen", unseen.ToString());
        ctx.SetPortValue("revealed", (double)revealed);
        ctx.SetPortValue("dialogs", dialogs.ToString());
        ctx.SetPortValue("dialog_count", (double)dialogCount);
        ctx.SetPortValue("pressed", pressed);
        ctx.SetPortValue("windows", windows.ToString());
        ctx.SetPortValue("result", found.Windows.Count == 0
            ? found.Explain(query)
            : blocked
                ? (pressed ? $"a dialog was waiting and '{press}' was pressed"
                           : $"a window is disabled; {dialogCount} dialog(s) are up")
                : dialogCount > 0
                    ? $"{dialogCount} window(s) look like dialogs but nothing is disabled"
                    : "nothing is waiting");
    }
    // 窓の中の文字と押すところを集める。
    //
    // 孫まで辿る。上書きするか尋ねる窓のように、ボタンが入れ物の中に
    // 収められている作りがあり、直下だけを見ると「ボタンが 1 つも無い窓」に見える。
    // そうなると一覧に名前が出ないまま、押しても何も起こらない。
    static void Collect(IntPtr parent, List<string> texts, List<(IntPtr Handle, string Text)> buttons)
    {
        foreach (var child in NgolWindowFind.Children(parent))
        {
            var info = NgolWindowFind.Describe(child);
            // 別プロセスのコントロールの文字は問い合わせないと取れない。
            var text = NgolWindowFind.TextOf(child, 300, out bool answered);
            if (answered && text.Length > 0)
            {
                if (string.Equals(info.ClassName, "Button", StringComparison.OrdinalIgnoreCase))
                    buttons.Add((child, text));
                else
                    texts.Add(text);
            }
            Collect(child, texts, buttons);
        }
    }

    // 押すものが 1 つでもあるか。確認画面のクラスを名乗らない窓を見分けるために使う。
    static bool HasButton(IntPtr hwnd)
    {
        foreach (var child in NgolWindowFind.Children(hwnd))
        {
            if (string.Equals(NgolWindowFind.Describe(child).ClassName, "Button",
                              StringComparison.OrdinalIgnoreCase))
                return true;
            if (HasButton(child)) return true;
        }
        return false;
    }
}
