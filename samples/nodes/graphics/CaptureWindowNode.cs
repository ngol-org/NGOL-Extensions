using System;
using NodeGraphModLab.NodeAPI;

// 汎用デバッグノード(プロジェクト非依存): 指定タイトルのウィンドウをキャプチャして画像に保存する。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// 実装は WindowCapture.cs へ切り出してある(撮る瞬間を決める側が自分で撮れるようにするため。
// 理由はそちらのコメント参照)。このノードは単発で撮りたいときの入口。
[NodeType(
    "ngol.gfx.capture_window",
    "Graphics",
    "Capture Window",
    Version = "1.0.1",
    Description =
        "Save one window's contents to an image file, without touching the target application. "
      + "Captures the named window only, never the screen behind it: if more than one window matches the title it "
      + "stops without capturing, and if another window is on top it stops as well unless allowOccluded is set. "
      + "Always read matchedTitle and occluded before looking at the image.")]
[NodePort("windowTitleContains", PortDirection.Input, "string", IsRequired = true, Description = "Substring of the window title, case-insensitive")]
[NodePort("processId", PortDirection.Input, "number", Description = "Restrict to windows of this process (0 = any). Titles can collide with other applications, so prefer setting this")]
[NodePort("outputPath", PortDirection.Input, "string", IsRequired = true, Description = "Where to write the image (.png / .bmp)")]
[NodePort("method", PortDirection.Input, "string", Description = "printwindow (window only) / desktopdc (reads the screen where the window sits) / auto (default: printwindow, falling back to desktopdc if it comes out black)")]
[NodePort("allowOccluded", PortDirection.Input, "boolean", Description = "true captures through desktopdc even when another window overlaps, which puts that window's pixels in the image. Default false = stop instead")]
[NodePort("saved", PortDirection.Output, "boolean", Description = "true when an image was written")]
[NodePort("matchedTitle", PortDirection.Output, "string", Description = "Title of what was actually captured. Check this before looking at the image")]
[NodePort("ambiguous", PortDirection.Output, "string", Description = "Candidate list when several windows matched and nothing was captured. Non-empty means no image was taken")]
[NodePort("savedPath", PortDirection.Output, "string", Description = "Where the image really went (differs from outputPath when the extension was changed)")]
[NodePort("usedMethod", PortDirection.Output, "string", Description = "Which method was actually used")]
[NodePort("occluded", PortDirection.Output, "boolean", Description = "Whether another window overlaps the target")]
[NodePort("occludedBy", PortDirection.Output, "string", Description = "Title of the overlapping window")]
[NodePort("nonBlackRatio", PortDirection.Output, "number", Description = "Share of non-black pixels (0..1). Near 0 means the capture came out empty")]
[NodePort("width", PortDirection.Output, "number", Description = "Client area width")]
[NodePort("height", PortDirection.Output, "number", Description = "Client area height")]
public sealed class CaptureWindowNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var titleContains = ctx.GetPortValue("windowTitleContains") as string ?? "";
        var outPath = ctx.GetPortValue("outputPath") as string ?? "";
        var method = ctx.GetPortValue("method") as string ?? "auto";
        var allowOccludedValue = ctx.GetPortValue("allowOccluded");
        var allowOccluded = allowOccludedValue != null && Convert.ToBoolean(allowOccludedValue);
        var pidValue = ctx.GetPortValue("processId");
        var pid = pidValue == null ? 0 : Convert.ToInt32(pidValue);

        var r = WindowCapture.Capture(titleContains, outPath, method, allowOccluded, pid);

        ctx.SetPortValue("matchedTitle", r.MatchedTitle ?? "");
        ctx.SetPortValue("ambiguous", r.Ambiguous ?? "");
        ctx.SetPortValue("saved", r.Saved);
        ctx.SetPortValue("savedPath", r.SavedPath ?? "");
        ctx.SetPortValue("usedMethod", r.UsedMethod);
        ctx.SetPortValue("occluded", r.Occluded);
        ctx.SetPortValue("occludedBy", r.OccludedBy);
        ctx.SetPortValue("nonBlackRatio", (double)r.NonBlack);
        ctx.SetPortValue("width", (double)r.Width);
        ctx.SetPortValue("height", (double)r.Height);
    }
}
