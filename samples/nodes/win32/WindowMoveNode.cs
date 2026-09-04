using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// 汎用デバッグノード(プロジェクト非依存): ウィンドウの位置と大きさを変える。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// 手で動かせない相手（枠を持たない・最前面に固定されている・自動的に位置を戻す）を
// 決まった場所へ置くために使う。検証を再現可能にするには、毎回同じ位置と大きさに
// してから測る必要がある。
[NodeType(
    "ngol.win32.window_move",
    "Win32",
    "Window Move",
    Version = "1.0.1",
    Description =
        "Move or resize a window, including one that cannot be dragged by hand. "
      + "Position and size are given for the part that is actually drawn: a window's stored rectangle "
      + "includes invisible resize borders, so asking for a position without accounting for them leaves a "
      + "visible gap. The correction is applied from the difference measured on the window itself. "
      + "Every value is optional - leave one out to keep it as it is. The window before and after is "
      + "reported so the caller can see what actually changed.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Process that owns the window (0 = every process)")]
[NodePort("windowTitleContains", PortDirection.Input, "string", Description = "Title of the window, case-insensitive substring. The window has to be identified uniquely, otherwise nothing is moved")]
[NodePort("x", PortDirection.Input, "number", Description = "Left edge of the drawn window, in screen pixels. Leave out to keep the current position")]
[NodePort("y", PortDirection.Input, "number", Description = "Top edge of the drawn window, in screen pixels. Leave out to keep the current position")]
[NodePort("width", PortDirection.Input, "number", Description = "Width of the drawn window. Leave out to keep the current size")]
[NodePort("height", PortDirection.Input, "number", Description = "Height of the drawn window. Leave out to keep the current size")]
[NodePort("compensateBorders", PortDirection.Input, "boolean", Description = "Treat the given values as the drawn area rather than the stored rectangle. Default true. Set false to work in raw coordinates")]
[NodePort("moved", PortDirection.Output, "boolean", Description = "true when the window was asked to move and the request went through")]
[NodePort("before", PortDirection.Output, "string", Description = "Drawn area before, as x,y WxH")]
[NodePort("after", PortDirection.Output, "string", Description = "Drawn area after. Compare with what was asked: an application may refuse or clamp a size")]
[NodePort("borders", PortDirection.Output, "string", Description = "The invisible border measured on this window, as left,top,right,bottom. Non-zero values are what the correction accounts for")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was moved. Empty on success")]
public sealed class WindowMoveNode : INode
{
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static string Describe(NgolWindowFind.WindowInfo w) =>
        $"{w.FrameLeft},{w.FrameTop} {w.FrameWidth}x{w.FrameHeight}";

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

        if (!NgolWindowFind.FindOne(query, out var window, out string problem)) { Fail(ctx, problem); return; }

        // 見えない枠の厚み。生の矩形と描かれている矩形の差そのもの。
        int borderLeft = window.FrameLeft - window.Left;
        int borderTop = window.FrameTop - window.Top;
        int borderRight = window.Right - window.FrameRight;
        int borderBottom = window.Bottom - window.FrameBottom;

        ctx.SetPortValue("borders", $"{borderLeft},{borderTop},{borderRight},{borderBottom}");
        ctx.SetPortValue("before", Describe(window));

        bool compensate = ctx.GetPortValue("compensateBorders") is not bool c || c;
        if (!window.FrameBoundsAvailable) compensate = false;

        bool hasX = ctx.GetPortValue("x") is double;
        bool hasY = ctx.GetPortValue("y") is double;
        bool hasW = ctx.GetPortValue("width") is double;
        bool hasH = ctx.GetPortValue("height") is double;

        if (!hasX && !hasY && !hasW && !hasH)
        {
            // 何も指定されていないなら動かさない。現状の報告だけ返す。
            ctx.SetPortValue("moved", false);
            ctx.SetPortValue("after", Describe(window));
            ctx.SetPortValue("reason", "nothing to change: give at least one of x, y, width or height");
            return;
        }

        int x = hasX ? (int)(double)ctx.GetPortValue("x")! : window.FrameLeft;
        int y = hasY ? (int)(double)ctx.GetPortValue("y")! : window.FrameTop;
        int w = hasW ? (int)(double)ctx.GetPortValue("width")! : window.FrameWidth;
        int h = hasH ? (int)(double)ctx.GetPortValue("height")! : window.FrameHeight;

        if (compensate)
        {
            x -= borderLeft;
            y -= borderTop;
            w += borderLeft + borderRight;
            h += borderTop + borderBottom;
        }

        uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (!hasX && !hasY) flags |= SWP_NOMOVE;
        if (!hasW && !hasH) flags |= SWP_NOSIZE;

        bool ok = SetWindowPos(window.Handle, IntPtr.Zero, x, y, Math.Max(w, 1), Math.Max(h, 1), flags);
        int error = ok ? 0 : Marshal.GetLastWin32Error();

        var now = NgolWindowFind.Describe(window.Handle);
        ctx.SetPortValue("moved", ok);
        ctx.SetPortValue("after", Describe(now));
        ctx.SetPortValue("reason", ok ? "" : $"the window refused to move (win32 error {error})");
    }

    static void Fail(IExecutionContext ctx, string reason)
    {
        ctx.SetPortValue("moved", false);
        ctx.SetPortValue("before", "");
        ctx.SetPortValue("after", "");
        ctx.SetPortValue("borders", "");
        ctx.SetPortValue("reason", reason);
    }
}
