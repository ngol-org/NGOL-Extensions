using System;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// 汎用デバッグノード(プロジェクト非依存): ウィンドウが現れて応答するまで待つ。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// 起動したかどうかを、NGOL が応答することで判断してはいけない。組み込み先では
// NGOL のほうが先に立ち上がるため、本体がまだ画面を出していない時点で
// 「起動した」と読んでしまう。ウィンドウが出たことと、それが応答することは
// さらに別で、出た直後は初期化中でメッセージを処理できない時間がある。
[NodeType(
    "ngol.win32.window_wait",
    "Win32",
    "Window Wait",
    Version = "1.0.2",
    Description =
        "Wait until an application's window exists and answers messages. Whether a window has appeared and "
      + "whether it can respond are two different things, and both come later than the moment a plugin inside "
      + "the process starts serving requests, so neither can be inferred from the other. "
      + "The wait is safe to repeat: when it times out the window may simply not be ready yet, and calling "
      + "again continues waiting. Keep one call short enough for whatever is driving it.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Process to wait for (0 = every process). Prefer setting this: another application can hold a window with the same title")]
[NodePort("titleContains", PortDirection.Input, "string", Description = "Title of the window, case-insensitive substring. Empty = any window of the process")]
[NodePort("timeoutMs", PortDirection.Input, "number", Description = "How long this call waits (default 10000). On timeout, call again to keep waiting")]
[NodePort("pollIntervalMs", PortDirection.Input, "number", Description = "How often to look (default 200)")]
[NodePort("requireResponding", PortDirection.Input, "boolean", Description = "Also require that the window answers a message before reporting success. Default true. Set false to return as soon as it exists")]
[NodePort("ready", PortDirection.Output, "boolean", Description = "true when the window met every requested condition")]
[NodePort("appeared", PortDirection.Output, "boolean", Description = "true when a matching window existed at all. false with ready=false means nothing ever showed up")]
[NodePort("responding", PortDirection.Output, "boolean", Description = "true when the window answered. false with appeared=true means it is up but still busy. Only meaningful when requireResponding is true: with it false nothing is asked, so this reports true even for a window that is wedged")]
[NodePort("title", PortDirection.Output, "string", Description = "Title of the window that was found. Titles often change during startup, so this says which state it reached")]
[NodePort("handleHex", PortDirection.Output, "string", Description = "Handle of the window, as hex")]
[NodePort("elapsedMs", PortDirection.Output, "number", Description = "How long this call waited")]
[NodePort("reason", PortDirection.Output, "string", Description = "What was still missing when the wait ended. Empty on success")]
public sealed class WindowWaitNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var query = new NgolWindowFind.Query
        {
            ProcessId = ctx.GetPortValue("processId") is double pid && pid > 0 ? (uint)pid : 0,
            TitleContains = ctx.GetPortValue("titleContains") as string ?? "",
            ClassContains = "",
            VisibleOnly = true,
            TopLevelOnly = true,
        };

        int timeout = ctx.GetPortValue("timeoutMs") is double t && t >= 1 ? (int)t : 10000;
        int interval = ctx.GetPortValue("pollIntervalMs") is double i && i >= 1 ? (int)i : 200;
        bool requireResponding = ctx.GetPortValue("requireResponding") is not bool r || r;

        // Stopwatch instead of Environment.TickCount64: the latter needs .NET Core 3.0 or
        // newer, and this node is compiled by the host it is deployed into. On a Mono host
        // it does not exist, and the node then fails to register at all.
        var started = System.Diagnostics.Stopwatch.StartNew();

        bool appeared = false, responding = false;
        string title = "", handle = "";

        while (true)
        {
            var outcome = NgolWindowFind.Find(query);
            if (outcome.Windows.Count > 0)
            {
                var window = outcome.Windows[0];
                appeared = true;
                title = window.Title;
                handle = "0x" + window.Handle.ToInt64().ToString("x");

                // 出ていることと応答できることは別。起動直後は前者だけが真になる。
                responding = !requireResponding || NgolWindowFind.Responds(window.Handle, (uint)Math.Min(interval, 1000));
                if (responding) break;
            }

            if (started.ElapsedMilliseconds >= timeout) break;
            Thread.Sleep(interval);
        }

        bool ready = appeared && responding;

        ctx.SetPortValue("ready", ready);
        ctx.SetPortValue("appeared", appeared);
        ctx.SetPortValue("responding", responding);
        ctx.SetPortValue("title", title);
        ctx.SetPortValue("handleHex", handle);
        ctx.SetPortValue("elapsedMs", (double)started.ElapsedMilliseconds);
        ctx.SetPortValue("reason", ready ? ""
            : appeared ? "the window is up but did not answer within the timeout"
                       : "no matching window appeared within the timeout");
    }
}
