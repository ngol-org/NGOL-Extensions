using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 窓に「閉じてくれ」と伝える。
///
/// 問いかけの窓を、押す当てが無いまま片付けるための手。ボタンを探して押す道は、
/// 自前で描く作りのアプリ（Qt など）では通らない--ボタンが OS の部品ではないため。
/// こちらは窓そのものに伝えるので、中の作りに依らない。
///
/// 問いかけの窓では、これは「取り消す」側にあたる。承諾する側にはならないので、
/// 押し間違えて先へ進んでしまうことがない。
///
/// 伝えるだけで待たない。相手が問いかけの最中でも、こちらは止まらない。
/// </summary>
[NodeType("ngol.win32.window_close", "Win32", "Close Window",
    Version = "1.0.0",
    Description = "Tells a window to close. It is the way to clear a question box when there is no button to press: hunting for the button fails on applications that paint their own (Qt and the like), because the button is not an OS control, while this talks to the window itself and does not care how it is built. On a question box this is the answering-no side, so it cannot accidentally agree to anything. It is posted, not sent, so a modal box does not hold up the caller.")]
[NodePort("process_id", PortDirection.Input, "number", Description = "Process that owns the window (0 = any)")]
[NodePort("title_contains", PortDirection.Input, "string", IsRequired = true, Description = "Title of the window to close, case-insensitive substring. Required, because closing the wrong window is not undoable")]
[NodePort("send", PortDirection.Input, "boolean", Description = "false (default) only reports what would be closed")]
[NodePort("matched", PortDirection.Output, "string", Description = "One matching window per line: handle and title")]
[NodePort("match_count", PortDirection.Output, "number", Description = "How many windows matched")]
[NodePort("sent", PortDirection.Output, "boolean", Description = "true when the request was posted")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was done. Empty when something was")]
public sealed class WindowCloseNode : INode
{
    // system-api: user32 の窓まわり。公開ヘッダーどおりで、版によらず固定。
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    public void Execute(IExecutionContext ctx)
    {
        uint wantProcess = ctx.GetPortValue("process_id") is double p && p > 0 ? (uint)p : 0;
        string title = (ctx.GetPortValue("title_contains") as string ?? "").Trim();
        bool send = ctx.GetPortValue("send") is bool s && s;

        ctx.SetPortValue("sent", false);
        ctx.SetPortValue("match_count", 0d);
        ctx.SetPortValue("matched", "");
        ctx.SetPortValue("reason", "");

        if (title.Length == 0)
        {
            ctx.SetPortValue("reason", "give part of the title; closing an unnamed window cannot be undone");
            return;
        }

        var hits = new List<IntPtr>();
        var lines = new List<string>();
        var text = new StringBuilder(512);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            if (wantProcess != 0)
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner != wantProcess) return true;
            }

            text.Clear();
            GetWindowTextW(hwnd, text, text.Capacity);
            string caption = text.ToString();
            if (caption.Length == 0) return true;
            if (caption.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) return true;

            hits.Add(hwnd);
            lines.Add("0x" + hwnd.ToInt64().ToString("X") + "\t" + caption);
            return true;
        }, IntPtr.Zero);

        ctx.SetPortValue("matched", string.Join("\n", lines));
        ctx.SetPortValue("match_count", (double)hits.Count);

        if (hits.Count == 0)
        {
            ctx.SetPortValue("reason", "no visible window's title contains '" + title + "'");
            return;
        }
        if (!send)
        {
            ctx.SetPortValue("reason", "nothing was closed; raise send once the list above is the right one");
            return;
        }

        foreach (var hwnd in hits) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        ctx.SetPortValue("sent", true);
    }
}
