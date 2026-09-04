using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// 汎用デバッグノード(プロジェクト非依存): ウィンドウの指定した位置の色を読む。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// 「画面を見て確かめる」を「値で確かめる」に置き換えるための道具。
// 目視は人にしか出来ず、画像を外へ出すことにもなるが、色の数値ならその両方を避けられる。
//
// 既定はウィンドウ自身に描かせる方式にしてある。画面から読む方式は、手前に別の
// ウィンドウがあるとその中身を読んでしまうため、明示的に選ばせる。
[NodeType(
    "ngol.win32.pixel_colors",
    "Win32",
    "Pixel Colors",
    Version = "1.0.1",
    Description =
        "Read the colour at given points of a window, so that what is on screen can be checked as numbers "
      + "instead of by looking at it. Points are normalised to the client area, so they stay valid when the "
      + "window is resized. Two ways of reading are offered: asking the window to draw itself, which never "
      + "picks up anything outside that window, and reading the composited screen, which shows exactly what "
      + "a person would see but also whatever is on top of it. The first is the default. "
      + "Reading the screen while another window overlaps a sampled point stops instead of returning that "
      + "window's pixels, unless allowOccluded is set.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Process that owns the window (0 = every process). Titles collide across applications, so prefer setting this")]
[NodePort("windowTitleContains", PortDirection.Input, "string", Description = "Title of the window, case-insensitive substring")]
[NodePort("pointsJson", PortDirection.Input, "string", IsRequired = true, Description = "[[u,v], ...] where u and v are 0..1 within the client area. [[0.5,0.5]] is the centre")]
[NodePort("method", PortDirection.Input, "string", Description = "printwindow (default: the window draws itself, nothing outside it can appear) / screen (what is actually composited, including anything on top) / auto (printwindow, falling back to screen when it comes out entirely black)")]
[NodePort("allowOccluded", PortDirection.Input, "boolean", Description = "With method=screen, read even when another window covers a sampled point, which puts that window's colours in the result. Default false = stop instead")]
[NodePort("colorsJson", PortDirection.Output, "string", Description = "[{\"r\":0,\"g\":0,\"b\":0}, ...] in the order the points were given, each 0..255. Empty array when nothing was read")]
[NodePort("pointsPx", PortDirection.Output, "string", Description = "The pixel coordinates actually sampled, as [[x,y], ...] within the client area. Check these when a colour looks wrong")]
[NodePort("usedMethod", PortDirection.Output, "string", Description = "Which way the pixels were obtained. Differs from the request when auto fell back")]
[NodePort("clientSize", PortDirection.Output, "string", Description = "Client area size as WxH. A zero size means the window is minimised and nothing can be read")]
[NodePort("occludedBy", PortDirection.Output, "string", Description = "Title of the window covering a sampled point, when that stopped the read")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was read. Empty on success")]
public sealed class PixelColorsNode : INode
{
    const uint SRCCOPY = 0x00CC0020;
    const uint PW_RENDERFULLCONTENT = 0x00000002;
    const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    static List<(double U, double V)> ParsePoints(string json, out string error)
    {
        error = "";
        var points = new List<(double, double)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pair in doc.RootElement.EnumerateArray())
            {
                if (pair.GetArrayLength() < 2) { error = "each point needs two numbers"; return points; }
                points.Add((pair[0].GetDouble(), pair[1].GetDouble()));
            }
        }
        catch (Exception ex) { error = "pointsJson is not [[u,v], ...]: " + ex.Message; }
        return points;
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

        var method = (ctx.GetPortValue("method") as string ?? "printwindow").Trim().ToLowerInvariant();
        if (method.Length == 0) method = "printwindow";
        bool allowOccluded = ctx.GetPortValue("allowOccluded") is bool a && a;

        var points = ParsePoints(ctx.GetPortValue("pointsJson") as string ?? "[]", out string parseError);
        if (parseError.Length > 0) { Fail(ctx, parseError); return; }
        if (points.Count == 0) { Fail(ctx, "no points were given"); return; }

        if (!NgolWindowFind.FindOne(query, out var window, out string problem)) { Fail(ctx, problem); return; }

        int w = window.ClientWidth, h = window.ClientHeight;
        ctx.SetPortValue("clientSize", w + "x" + h);
        if (w <= 0 || h <= 0) { Fail(ctx, "the client area has no size (the window is probably minimised)"); return; }

        // 正規化座標を画素へ。端は範囲内へ寄せる（1.0 がちょうど外側になるため）。
        var px = new List<(int X, int Y)>();
        foreach (var (u, v) in points)
        {
            int x = (int)Math.Round(u * (w - 1));
            int y = (int)Math.Round(v * (h - 1));
            px.Add((Math.Min(Math.Max(x, 0), w - 1), Math.Min(Math.Max(y, 0), h - 1)));
        }

        var pxJson = new StringBuilder("[");
        for (int i = 0; i < px.Count; i++)
        {
            if (i > 0) pxJson.Append(',');
            pxJson.Append('[').Append(px[i].X).Append(',').Append(px[i].Y).Append(']');
        }
        pxJson.Append(']');
        ctx.SetPortValue("pointsPx", pxJson.ToString());

        // 画面から読む場合、手前に別のウィンドウがあるとその中身を読む。
        // 取ってから捨てるのでは遅いので、読む前に調べる。
        if (method == "screen" && !allowOccluded)
        {
            foreach (var p in px)
            {
                var screenPoint = new POINT { X = p.X, Y = p.Y };
                ClientToScreen(window.Handle, ref screenPoint);
                var top = GetAncestor(WindowFromPoint(screenPoint), GA_ROOT);
                if (top != IntPtr.Zero && top != window.Handle)
                {
                    var other = NgolWindowFind.Describe(top);
                    ctx.SetPortValue("occludedBy", other.Title.Length > 0 ? other.Title : other.ClassName);
                    Fail(ctx, "another window covers a sampled point; set allowOccluded to read it anyway");
                    return;
                }
            }
        }

        var colors = Sample(window.Handle, w, h, px, method, out string used, out string error);
        if (error.Length > 0) { Fail(ctx, error); return; }

        var json = new StringBuilder("[");
        for (int i = 0; i < colors.Count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"r\":").Append(colors[i].R)
                .Append(",\"g\":").Append(colors[i].G)
                .Append(",\"b\":").Append(colors[i].B).Append('}');
        }
        json.Append(']');

        ctx.SetPortValue("colorsJson", json.ToString());
        ctx.SetPortValue("usedMethod", used);
        ctx.SetPortValue("occludedBy", "");
        ctx.SetPortValue("reason", "");
    }

    static List<(int R, int G, int B)> Sample(
        IntPtr hwnd, int w, int h, List<(int X, int Y)> px, string method,
        out string used, out string error)
    {
        used = "";
        error = "";
        var colors = new List<(int, int, int)>();

        bool wantPrint = method is "printwindow" or "auto";
        if (!wantPrint && method != "screen") { error = "method must be printwindow, screen or auto"; return colors; }

        if (wantPrint)
        {
            colors = Grab(hwnd, w, h, px, fromScreen: false);
            used = "printwindow";

            // 合成に乗らない描画をしているウィンドウは真っ黒で返る。
            // それが本当に黒い画面なのかは区別できないので、auto のときだけ画面から読み直す。
            bool allBlack = true;
            foreach (var c in colors) if (c.Item1 != 0 || c.Item2 != 0 || c.Item3 != 0) { allBlack = false; break; }
            if (!allBlack || method != "auto") return colors;
        }

        colors = Grab(hwnd, w, h, px, fromScreen: true);
        used = method == "auto" ? "screen (printwindow came out black)" : "screen";
        return colors;
    }

    static List<(int R, int G, int B)> Grab(IntPtr hwnd, int w, int h, List<(int X, int Y)> px, bool fromScreen)
    {
        var colors = new List<(int, int, int)>();

        // PrintWindow はウィンドウ全体を描くので、受け皿も全体の大きさで用意し、
        // クライアント領域の原点ぶんずらして読む。クライアントの大きさで受けると
        // 枠の分だけ内容が切れ、指定した位置と違う画素を読むことになる。
        var info = NgolWindowFind.Describe(hwnd);
        int surfaceW = fromScreen ? w : Math.Max(info.Width, 1);
        int surfaceH = fromScreen ? h : Math.Max(info.Height, 1);

        int offsetX = 0, offsetY = 0;
        if (!fromScreen)
        {
            var origin = new POINT { X = 0, Y = 0 };
            ClientToScreen(hwnd, ref origin);
            offsetX = origin.X - info.Left;
            offsetY = origin.Y - info.Top;
        }

        IntPtr sourceDc = GetDC(IntPtr.Zero);          // 画面全体。合成された結果が入っている
        IntPtr memDc = CreateCompatibleDC(sourceDc);
        IntPtr bitmap = CreateCompatibleBitmap(sourceDc, surfaceW, surfaceH);
        IntPtr previous = SelectObject(memDc, bitmap);

        try
        {
            if (fromScreen)
            {
                var origin = new POINT { X = 0, Y = 0 };
                ClientToScreen(hwnd, ref origin);
                BitBlt(memDc, 0, 0, w, h, sourceDc, origin.X, origin.Y, SRCCOPY);
            }
            else
            {
                PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
            }

            foreach (var p in px)
            {
                uint value = GetPixel(memDc, p.X + offsetX, p.Y + offsetY);
                colors.Add(((int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF)));
            }
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, sourceDc);
        }

        return colors;
    }

    static void Fail(IExecutionContext ctx, string reason)
    {
        ctx.SetPortValue("colorsJson", "[]");
        ctx.SetPortValue("usedMethod", "");
        ctx.SetPortValue("reason", reason);
    }
}
