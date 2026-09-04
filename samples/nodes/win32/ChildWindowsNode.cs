using System;
using System.Collections.Generic;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// 汎用デバッグノード(プロジェクト非依存): ウィンドウの中のコントロールを階層で読み出す。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// メニューと同じ発想で、画面を見ずに UI の構造を読む。どのコントロールを対象にするかを
// 目で決めるのではなく、クラス名・識別子・位置・文字から選べるようにする。
[NodeType(
    "ngol.win32.child_windows",
    "Win32",
    "Child Windows",
    Version = "1.0.1",
    Description =
        "Walk the controls inside a window and report the tree: class, control id, position, size and text. "
      + "Lets a caller pick a control by what it is instead of by looking at the screen. "
      + "Text of a control in another process cannot be read the ordinary way, so it is requested from the "
      + "control itself with a timeout; a control that does not answer is reported rather than shown as empty. "
      + "Applications that draw their own interface have no child windows at all: childCount 0 with a parent "
      + "found means there is nothing here to read, which is different from not finding the window.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Process that owns the parent window (0 = every process)")]
[NodePort("windowTitleContains", PortDirection.Input, "string", Description = "Title of the parent window, case-insensitive substring. Leave empty only when processId picks exactly one window")]
[NodePort("classContains", PortDirection.Input, "string", Description = "Only report controls whose class name contains this, case-insensitive. Empty = all")]
[NodePort("textContains", PortDirection.Input, "string", Description = "Only report controls whose text contains this, case-insensitive. Empty = all")]
[NodePort("maxDepth", PortDirection.Input, "number", Description = "How deep to walk (default 4). Deeply nested interfaces produce a lot of lines")]
[NodePort("textTimeoutMs", PortDirection.Input, "number", Description = "How long to wait for one control to hand over its text (default 200). Raise it only if the application is slow, since every control costs this at worst")]
[NodePort("controls", PortDirection.Output, "string", Description = "One control per line, indented by depth: class / id / position and size / text")]
[NodePort("count", PortDirection.Output, "number", Description = "How many controls were reported after filtering")]
[NodePort("childCount", PortDirection.Output, "number", Description = "How many controls exist in total, before filtering. Zero with a parent found means the application draws its own interface")]
[NodePort("parentTitle", PortDirection.Output, "string", Description = "Title of the window that was walked. Check this before reading the tree")]
[NodePort("unansweredControls", PortDirection.Output, "number", Description = "Controls that did not answer the request for their text within the timeout. Different from a control that answered and simply has no text, which is normal for containers")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was reported. Empty when something was")]
public sealed class ChildWindowsNode : INode
{
    sealed class Walker
    {
        public string ClassFilter = "";
        public string TextFilter = "";
        public int MaxDepth = 4;
        public uint TextTimeout = 200;
        public readonly StringBuilder Out = new StringBuilder();
        public int Total;
        public int Reported;
        public int Silent;

        public bool Wanted(string cls, string text)
        {
            if (ClassFilter.Length > 0 &&
                cls.IndexOf(ClassFilter, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (TextFilter.Length > 0 &&
                text.IndexOf(TextFilter, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }
    }

    const int TextLimit = 160;

    static string Shorten(string text)
    {
        if (text.Length == 0) return "";
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= TextLimit ? text : text.Substring(0, TextLimit) + " ...";
    }

    static void Walk(IntPtr parent, Walker walker, int depth)
    {
        if (depth > walker.MaxDepth) return;

        foreach (var child in NgolWindowFind.Children(parent))
        {
            walker.Total++;

            var info = NgolWindowFind.Describe(child);
            string text = NgolWindowFind.TextOf(child, walker.TextTimeout, out bool answered);
            if (!answered) walker.Silent++;

            // 一行に収める。記録を持つコントロールは全文が数十 KB になることがあり、
            // 構造を読む用途では邪魔になる。
            text = Shorten(text);

            if (walker.Wanted(info.ClassName, text))
            {
                walker.Reported++;
                walker.Out.Append(new string(' ', depth * 2))
                    .Append('[').Append(info.ClassName).Append(']');

                int id = NgolWindowFind.ControlIdOf(child);
                if (id != 0) walker.Out.Append("  id ").Append(id);

                walker.Out.Append("  ").Append(info.Left).Append(',').Append(info.Top)
                    .Append(' ').Append(info.Width).Append('x').Append(info.Height);

                if (!info.Visible) walker.Out.Append("  hidden");
                if (text.Length > 0) walker.Out.Append("  ").Append(text);

                walker.Out.Append('\n');
            }

            Walk(child, walker, depth + 1);
        }
    }

    public void Execute(IExecutionContext ctx)
    {
        var query = new NgolWindowFind.Query
        {
            ProcessId = ctx.GetPortValue("processId") is double pid && pid > 0 ? (uint)pid : 0,
            TitleContains = ctx.GetPortValue("windowTitleContains") as string ?? "",
            ClassContains = "",
            VisibleOnly = true,
            TopLevelOnly = true,
        };

        // 親が 1 つに決まらないまま辿ると、別のウィンドウの中身を返してしまう。
        if (!NgolWindowFind.FindOne(query, out var parent, out string problem))
        {
            ctx.SetPortValue("controls", "");
            ctx.SetPortValue("count", 0d);
            ctx.SetPortValue("childCount", 0d);
            ctx.SetPortValue("parentTitle", "");
            ctx.SetPortValue("unansweredControls", 0d);
            ctx.SetPortValue("reason", problem);
            return;
        }

        var walker = new Walker
        {
            ClassFilter = (ctx.GetPortValue("classContains") as string ?? "").Trim(),
            TextFilter = (ctx.GetPortValue("textContains") as string ?? "").Trim(),
            MaxDepth = ctx.GetPortValue("maxDepth") is double d && d >= 1 ? (int)d : 4,
            TextTimeout = ctx.GetPortValue("textTimeoutMs") is double t && t >= 1 ? (uint)t : 200,
        };

        Walk(parent.Handle, walker, 0);

        string reason = "";
        if (walker.Reported == 0)
        {
            reason = walker.Total == 0
                ? "the window has no child windows (the application draws its own interface)"
                : "controls exist but none matched the class or text filter";
        }

        ctx.SetPortValue("controls", walker.Out.ToString());
        ctx.SetPortValue("count", (double)walker.Reported);
        ctx.SetPortValue("childCount", (double)walker.Total);
        ctx.SetPortValue("parentTitle", parent.Title);
        ctx.SetPortValue("unansweredControls", (double)walker.Silent);
        ctx.SetPortValue("reason", reason);
    }
}
