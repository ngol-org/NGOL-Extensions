using System;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// 汎用デバッグノード(プロジェクト非依存): ホストのウィンドウを列挙して素性を返す。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// 探索は NgolWindowFind.cs へ切り出してある(撮る・動かす・読むのどれでも同じ手順になるため)。
[NodeType(
    "ngol.win32.window_list",
    "Win32",
    "Window List",
    Version = "1.0.1",
    Description =
        "List the windows of a running application and what each one is: handle, process, UI thread, "
      + "title, class, position and size. Answers which window is the real one when several match a title, "
      + "and which thread owns it - the thread that a UI call has to be made from. "
      + "Reports the drawn bounds separately from the raw rectangle, because the raw one includes "
      + "invisible resize borders, and reports cloaked separately from hidden, because a window sitting on "
      + "another virtual desktop still counts as visible. "
      + "When nothing matches, 'reason' says whether the process had no window at all or the filters "
      + "excluded them, which are different problems.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Only windows of this process (0 = every process)")]
[NodePort("titleContains", PortDirection.Input, "string", Description = "Only windows whose title contains this, case-insensitive. Empty = any")]
[NodePort("classContains", PortDirection.Input, "string", Description = "Only windows whose class name contains this, case-insensitive. Empty = any. Useful when the title is empty or changes")]
[NodePort("visibleOnly", PortDirection.Input, "boolean", Description = "Skip windows that are not visible. Default true")]
[NodePort("includeChildren", PortDirection.Input, "boolean", Description = "Also walk child windows (controls). Default false, which lists top level windows only")]
[NodePort("windows", PortDirection.Output, "string", Description = "One window per line: handle / process / thread / position and size / class / title")]
[NodePort("count", PortDirection.Output, "number", Description = "How many windows matched")]
[NodePort("scanned", PortDirection.Output, "number", Description = "How many top level windows existed before filtering. Zero means nothing was there to look at")]
[NodePort("firstHandleHex", PortDirection.Output, "string", Description = "Handle of the first match, as hex. Empty when nothing matched")]
[NodePort("firstThreadId", PortDirection.Output, "number", Description = "UI thread of the first match. Compare it with the thread a callback runs on to know whether that callback may touch the window")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing matched. Empty when something did")]
public sealed class WindowListNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var query = new NgolWindowFind.Query
        {
            ProcessId = ctx.GetPortValue("processId") is double pid && pid > 0 ? (uint)pid : 0,
            TitleContains = ctx.GetPortValue("titleContains") as string ?? "",
            ClassContains = ctx.GetPortValue("classContains") as string ?? "",
            VisibleOnly = ctx.GetPortValue("visibleOnly") is not bool visible || visible,
            TopLevelOnly = ctx.GetPortValue("includeChildren") is not bool children || !children,
        };

        var outcome = NgolWindowFind.Find(query);

        var sb = new StringBuilder();
        foreach (var w in outcome.Windows)
        {
            // 位置と大きさは描かれている方を出す。生の矩形は見えない枠を含むので、
            // 並べるときに使うと隙間が開く。
            sb.Append("0x").Append(w.Handle.ToInt64().ToString("x"))
              .Append("  pid ").Append(w.ProcessId)
              .Append("  tid ").Append(w.ThreadId)
              .Append("  ").Append(w.FrameLeft).Append(',').Append(w.FrameTop)
              .Append(' ').Append(w.FrameWidth).Append('x').Append(w.FrameHeight)
              .Append("  client ").Append(w.ClientWidth).Append('x').Append(w.ClientHeight);

            if (w.Dpi > 0) sb.Append("  dpi ").Append(w.Dpi);
            if (!w.FrameBoundsAvailable) sb.Append("  raw-rect");
            if (!w.Visible) sb.Append("  hidden");
            if (w.Cloaked) sb.Append("  cloaked");
            if (w.Minimized) sb.Append("  minimized");

            sb.Append("  [").Append(w.ClassName).Append("]  ").Append(w.Title).Append('\n');
        }

        var first = outcome.Windows.Count > 0 ? outcome.Windows[0] : default;

        ctx.SetPortValue("windows", sb.ToString());
        ctx.SetPortValue("count", (double)outcome.Windows.Count);
        ctx.SetPortValue("scanned", (double)outcome.TotalTopLevel);
        ctx.SetPortValue("firstHandleHex",
            outcome.Windows.Count > 0 ? "0x" + first.Handle.ToInt64().ToString("x") : "");
        ctx.SetPortValue("firstThreadId", (double)(outcome.Windows.Count > 0 ? first.ThreadId : 0));
        ctx.SetPortValue("reason", outcome.Explain(query));
    }
}
