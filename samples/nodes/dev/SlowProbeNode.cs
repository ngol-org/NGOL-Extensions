using System;
using System.Threading;
using NodeGraphModLab.NodeAPI;

/// <summary>
/// 長い処理が MCP の待ち（15 秒）とどう噛み合うかを測るための使い捨てノード。
/// 同じ待ち時間を 2 つの型で実行し、呼び出し側から見た違いを判別する。
/// </summary>
[NodeType("ngol.dev.slow_probe", "Dev", "Slow Probe",
    Version = "1.0.1",
    Description = "Waits the requested number of seconds and reports how the call looked from the caller. Two modes: block keeps Execute busy until the wait is over, job returns at once and finishes in the background while reporting progress. Use it to see where a client gives up.")]
[NodePort("seconds", PortDirection.Input, "number",
    Description = "How long to wait, in seconds. Default 20, which is past the 15 seconds an MCP client waits")]
[NodePort("mode", PortDirection.Input, "string",
    Description = "block = Execute stays busy for the whole wait. job = Execute returns at once and the wait happens in the background, with progress on the job. Default block")]
[NodePort("waited_ms", PortDirection.Output, "number",
    Description = "How long the wait actually took. Only meaningful in block mode")]
[NodePort("thread_id", PortDirection.Output, "number",
    Description = "Which thread Execute ran on, as the .NET managed thread id. Useful for telling apart the NGOL loop from a pool thread across several runs of this node. It is not the id the operating system uses, so it cannot be compared with what ngol.dev.exec_thread and ngol.dev.persistent_pulse report, nor with anything read from outside the process")]
[NodePort("result", PortDirection.Output, "string",
    Description = "Human-readable outcome. In job mode it says the work has only started")]
public sealed class SlowProbeNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        double seconds = ToDouble(ctx.GetPortValue("seconds"), 20.0);
        if (seconds < 0.0) seconds = 0.0;
        if (seconds > 300.0) seconds = 300.0;
        int ms = (int)(seconds * 1000.0);

        string mode = (ctx.GetPortValue("mode") as string ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "block";

        int tid = Thread.CurrentThread.ManagedThreadId;
        ctx.SetPortValue("thread_id", (double)tid);

        if (mode == "job")
        {
            // 登録そのものは仕事をしない。Job の器を作るためだけに使う。
            var reg = ctx.RegisterPersistent(new PersistentCallbacks { OnUpdate = () => { } });
            reg.ReportProgress("queued: " + seconds.ToString("F1") + "s");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var start = DateTime.UtcNow;
                try
                {
                    int step = Math.Max(1, ms / 10);
                    int done = 0;
                    while (done < ms)
                    {
                        int slice = Math.Min(step, ms - done);
                        Thread.Sleep(slice);
                        done += slice;
                        reg.ReportProgress("working " + (done / 1000.0).ToString("F1")
                                           + "s / " + seconds.ToString("F1") + "s");
                    }
                    reg.ReportProgress("OK: waited "
                        + (DateTime.UtcNow - start).TotalMilliseconds.ToString("F0") + " ms");
                }
                catch (Exception ex)
                {
                    reg.ReportProgress("ERROR: " + ex.Message);
                }
                finally
                {
                    reg.Cancel();
                }
            });

            ctx.SetPortValue("waited_ms", 0.0);
            ctx.SetPortValue("result",
                "STARTED on thread " + tid + "; the wait runs in the background, read it with check_job_status");
            return;
        }

        var t0 = DateTime.UtcNow;
        Thread.Sleep(ms);
        double waited = (DateTime.UtcNow - t0).TotalMilliseconds;
        ctx.SetPortValue("waited_ms", waited);
        ctx.SetPortValue("result",
            "blocked for " + waited.ToString("F0") + " ms on thread " + tid);
    }

    private static double ToDouble(object value, double fallback)
    {
        if (value == null) return fallback;
        if (value is double d) return d;
        double parsed;
        return double.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
    }
}
