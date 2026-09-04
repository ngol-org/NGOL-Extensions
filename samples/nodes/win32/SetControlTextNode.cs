using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 窓の中の入力欄へ文字を入れる。
///
/// ボタンを押すだけでは越えられない窓がある。保存する場所を尋ねる窓のように、
/// 名前を書いてからでないと進めないものがそれで、押す手だけ持っていても止まる。
///
/// 相手の窓が応答を返せない状態でも、こちらは待たされない。
/// 送るのではなく置いてくる形にしてあるので、詰まっている相手にも届く。
///
/// どの欄に入れるかは番号で指す。番号は窓ごとに違うので、
/// 先に中身を読んで確かめること（読む道具は別に在る）。
/// </summary>
[NodeType("ngol.win32.set_control_text", "Win32", "Set Control Text",
    Version = "1.0.1",
    Description = "Puts text into an input field of another application's window. Pressing buttons alone cannot get past a window that first wants something typed, such as one asking where to save. The target does not have to be answering for this to land, so a window that is holding its own application still takes the text. Which field to fill is named by its number, which differs per window, so read the window's contents first rather than guessing.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Only windows of this process (0 = any). Titles collide across applications, so prefer setting this")]
[NodePort("windowTitleContains", PortDirection.Input, "string", IsRequired = true, Description = "Substring of the window title, case-insensitive")]
[NodePort("controlId", PortDirection.Input, "number", Description = "Number of the field to fill. Read it from the window's contents first")]
[NodePort("className", PortDirection.Input, "string", Description = "Only fields of this class, for example Edit. Empty = any. A field is often wrapped in a box of another class, and it is the inner one that holds the text")]
[NodePort("text", PortDirection.Input, "string", Description = "What to put in. Empty clears the field")]
[NodePort("filled", PortDirection.Output, "boolean", Description = "true when a field took the text")]
[NodePort("readBack", PortDirection.Output, "string", Description = "What the field holds afterwards. Compare it with what was sent: a field that refused looks the same as one that was never found")]
[NodePort("matchCount", PortDirection.Output, "number", Description = "How many fields matched. More than one means nothing was filled")]
[NodePort("candidates", PortDirection.Output, "string", Description = "The fields that matched, as class / number / text. Read this when nothing was filled")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class SetControlTextNode : INode
{
    const uint WM_SETTEXT = 0x000C;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wparam, string lparam,
                                             uint flags, uint timeoutMs, out IntPtr resultOut);

    public void Execute(IExecutionContext ctx)
    {
        int processId = ctx.GetPortValue("processId") is double p ? (int)p : 0;
        string title = (ctx.GetPortValue("windowTitleContains") as string ?? "").Trim();
        int controlId = ctx.GetPortValue("controlId") is double c ? (int)c : 0;
        string className = (ctx.GetPortValue("className") as string ?? "").Trim();
        string text = ctx.GetPortValue("text") as string ?? "";

        ctx.SetPortValue("filled", false);
        ctx.SetPortValue("readBack", "");
        ctx.SetPortValue("matchCount", 0d);
        ctx.SetPortValue("candidates", "");

        if (title.Length == 0)
        {
            ctx.SetPortValue("result", "give part of the window title");
            return;
        }

        var query = new NgolWindowFind.Query
        {
            ProcessId = (uint)processId,
            TitleContains = title,
            VisibleOnly = true,
            TopLevelOnly = true,
        };
        if (!NgolWindowFind.FindOne(query, out var window, out var problem))
        {
            ctx.SetPortValue("result", problem);
            return;
        }

        // 孫まで見る。入力欄は箱の中に入っていることが多い。
        var found = new List<IntPtr>();
        var report = new StringBuilder();
        Walk(window.Handle, found, report, controlId, className);

        ctx.SetPortValue("matchCount", (double)found.Count);
        ctx.SetPortValue("candidates", report.ToString());

        if (found.Count == 0)
        {
            ctx.SetPortValue("result", "no field matched; read the window's contents and check the number");
            return;
        }
        if (found.Count > 1)
        {
            ctx.SetPortValue("result", found.Count + " fields matched, so nothing was filled. Narrow it with className");
            return;
        }

        // 相手が答えられない状態でもこちらが待たされないようにする。
        SendMessageTimeoutW(found[0], WM_SETTEXT, IntPtr.Zero, text,
                            SMTO_ABORTIFHUNG, 1000, out _);

        // 入ったかどうかは読み直して確かめる。断られても送信そのものは成功に見える。
        string after = NgolWindowFind.TextOf(found[0], 500, out bool answered);
        ctx.SetPortValue("readBack", after);
        bool ok = answered && after == text;
        ctx.SetPortValue("filled", ok);
        ctx.SetPortValue("result", ok
            ? "the field now holds what was sent"
            : "the field did not end up holding what was sent; read 'readBack'");
    }

    static void Walk(IntPtr parent, List<IntPtr> found, StringBuilder report,
                     int controlId, string className)
    {
        foreach (var child in NgolWindowFind.Children(parent))
        {
            var info = NgolWindowFind.Describe(child);
            int id = NgolWindowFind.ControlIdOf(child);

            bool idMatches = controlId == 0 || id == controlId;
            bool classMatches = className.Length == 0
                || info.ClassName.IndexOf(className, StringComparison.OrdinalIgnoreCase) >= 0;

            if (idMatches && classMatches)
            {
                found.Add(child);
                report.Append(info.ClassName).Append('\t').Append(id).Append('\t')
                      .Append(NgolWindowFind.TextOf(child, 300, out _)).Append((char)10);
            }

            Walk(child, found, report, controlId, className);
        }
    }
}
