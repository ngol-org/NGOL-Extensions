using System;
using System.Reflection;
using System.Threading;
using NodeGraphModLab.CustomNodes;
using NodeGraphModLab.NodeAPI;

/// <summary>
/// direct mode の更新を回している主体を、ノード側へ一時的に引き取る。
/// 引き取りは必ず期限付きで、期限が来たら自分で返す。
/// </summary>
[NodeType("ngol.dev.tick_source", "Dev", "Tick Source",
    Version = "1.1.0",
    Description = "Takes over the loop that drives NGOL in direct mode, runs it from this node for a limited time, then hands it back. Handing back is on a timer, so a forgotten release cannot leave the host without a driver. Use it to see whether the update rate is set by the interval in the configuration or by something the node chooses. What is driving is found by following references from this node's own execution context, so it works whatever the host calls the object it keeps NGOL in.")]
[NodePort("mode", PortDirection.Input, "string",
    Description = "status reads what is happening without changing anything. take borrows the loop for the requested seconds. release hands it back at once. Default status")]
[NodePort("seconds", PortDirection.Input, "number",
    Description = "How long take keeps the loop before handing it back on its own. Default 15, capped at 120. The cap is deliberate: a borrowed loop that is never returned stops execution and every persistent node")]
[NodePort("interval_ms", PortDirection.Input, "number",
    Description = "How long this node sleeps between turns of the loop while it holds it. Default 5. The configured value for the built-in loop is usually 50, so a smaller number here is what makes the change visible")]
[NodePort("driving", PortDirection.Output, "bool",
    Description = "true while this node is the one turning the loop")]
[NodePort("turns", PortDirection.Output, "number",
    Description = "How many turns this node has taken since it borrowed the loop")]
[NodePort("driver_thread", PortDirection.Output, "number",
    Description = "Managed thread id of the loop this node is running, 0 when it holds nothing. Compare it with builtin_thread, not with the operating system ids ngol.dev.exec_thread and ngol.dev.persistent_pulse report")]
[NodePort("builtin_thread", PortDirection.Output, "number",
    Description = "Managed thread id of the built-in loop as it stands now, 0 when there is none. It is read from a thread object rather than from the running thread, and the operating system id of another thread cannot be obtained at all, so this one can never be anything else")]
[NodePort("stopped_builtin", PortDirection.Output, "bool",
    Description = "Whether the built-in loop actually stopped when asked. false means two loops were running, which is wasteful but safe")]
[NodePort("seconds_left", PortDirection.Output, "number",
    Description = "How long before the loop is handed back on its own")]
[NodePort("result", PortDirection.Output, "string",
    Description = "Human-readable outcome, or the reason nothing was done")]
public sealed class TickSourceNode : INode
{
    // 世代をまたいで残す。入れ物は framework の型だけで作る。
    private const string StopKey = "ngol.diag.ticksource.stop";
    private const string StatKey = "ngol.diag.ticksource.stat";

    // stat: [0]=driving(0/1) [1]=turns [2]=driverThreadId [3]=stoppedBuiltin(0/1) [4]=deadlineUtcTicks
    private static long[] Stat()
    {
        var v = AppDomain.CurrentDomain.GetData(StatKey) as long[];
        if (v == null || v.Length < 5)
        {
            v = new long[5];
            AppDomain.CurrentDomain.SetData(StatKey, v);
        }
        return v;
    }

    public void Execute(IExecutionContext ctx)
    {
        string mode = (ctx.GetPortValue("mode") as string ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "status";

        var found = NgolRuntimeFind.Find(ctx);
        object runtime = found.Runtime;
        if (runtime == null)
        {
            Report(ctx, 0, found.Explain() + "; nothing was touched");
            return;
        }

        var fDrain = runtime.GetType().GetField("_drainThread",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var mTick = runtime.GetType().GetMethod("Tick",
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        var mLoop = runtime.GetType().GetMethod("DrainLoop",
            BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

        // 名前が変わっていたら何もしない。誤動作より不動作を選ぶ。
        if (fDrain == null || mTick == null || mLoop == null)
        {
            Report(ctx, fDrain == null ? 0 : ThreadIdOf(fDrain.GetValue(runtime)),
                "runtime internals not found (_drainThread / Tick / DrainLoop); nothing was touched");
            return;
        }

        if (mode == "status")
        {
            Report(ctx, ThreadIdOf(fDrain.GetValue(runtime)), "status only; nothing was touched");
            return;
        }

        if (mode == "release")
        {
            bool had = Release(runtime, fDrain, mLoop);
            Report(ctx, ThreadIdOf(fDrain.GetValue(runtime)),
                had ? "handed the loop back" : "nothing was held; the built-in loop is in place");
            return;
        }

        if (mode != "take")
        {
            Report(ctx, ThreadIdOf(fDrain.GetValue(runtime)), "unknown mode: " + mode);
            return;
        }

        double seconds = ToDouble(ctx.GetPortValue("seconds"), 15.0);
        if (seconds < 1.0) seconds = 1.0;
        if (seconds > 120.0) seconds = 120.0;
        int intervalMs = (int)ToDouble(ctx.GetPortValue("interval_ms"), 5.0);
        if (intervalMs < 1) intervalMs = 1;
        if (intervalMs > 1000) intervalMs = 1000;

        // 前の世代が持ったままなら必ず先に返す。
        Release(runtime, fDrain, mLoop);

        var tick = (Action)Delegate.CreateDelegate(typeof(Action), runtime, mTick);
        var running = new bool[1] { true };
        var stat = Stat();
        stat[1] = 0;
        stat[4] = DateTime.UtcNow.AddSeconds(seconds).Ticks;

        // 引き取りも返却も、ここではない別スレッドで行う。
        // グラフ経由で実行された場合、この Execute 自体が止めたいスレッドの上にいる。
        var worker = new Thread(() =>
        {
            bool stopped = StopBuiltin(fDrain.GetValue(runtime));
            stat[2] = Thread.CurrentThread.ManagedThreadId;
            stat[3] = stopped ? 1 : 0;
            stat[0] = 1;

            var deadline = new DateTime(stat[4], DateTimeKind.Utc);
            while (running[0] && DateTime.UtcNow < deadline)
            {
                try { tick(); } catch { }   // 1 周の失敗で駆動を止めない
                stat[1]++;
                Thread.Sleep(intervalMs);
            }

            stat[0] = 0;
            stat[2] = 0;
            // 止まりきらなくても必ず立て直す。駆動元がゼロの時間を作らない。
            StartBuiltin(runtime, fDrain, mLoop);
            AppDomain.CurrentDomain.SetData(StopKey, null);
        })
        { IsBackground = true, Name = "NGOL-Drain-Borrowed" };

        AppDomain.CurrentDomain.SetData(StopKey, (Action)(() => { running[0] = false; }));
        worker.Start();

        Thread.Sleep(Math.Max(50, intervalMs * 4));   // 立ち上がりを見てから報告する
        Report(ctx, ThreadIdOf(fDrain.GetValue(runtime)),
            "holding the loop for " + seconds.ToString("F0") + "s at " + intervalMs
            + "ms per turn; it is handed back on its own when the time is up");
    }

    // ---- 引き取り・返却 -------------------------------------------------------

    private static bool StopBuiltin(object thread)
    {
        var th = thread as Thread;
        if (th == null || !th.IsAlive) return true;
        try
        {
            th.Interrupt();          // 待機中の割り込みで break する
            return th.Join(2000);
        }
        catch { return false; }
    }

    private static void StartBuiltin(object runtime, FieldInfo fDrain, MethodInfo mLoop)
    {
        try
        {
            var old = fDrain.GetValue(runtime) as Thread;
            if (old != null && old.IsAlive) return;   // 生きているなら二重に立てない

            var body = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), runtime, mLoop);
            var th = new Thread(body) { IsBackground = true, Name = "NGOL-Drain" };
            fDrain.SetValue(runtime, th);
            th.Start();
        }
        catch { }
    }

    private static bool Release(object runtime, FieldInfo fDrain, MethodInfo mLoop)
    {
        var stop = AppDomain.CurrentDomain.GetData(StopKey) as Action;
        if (stop == null)
        {
            StartBuiltin(runtime, fDrain, mLoop);   // 念のため。既に生きていれば何もしない
            return false;
        }
        stop();
        AppDomain.CurrentDomain.SetData(StopKey, null);

        for (int i = 0; i < 40 && Stat()[0] != 0; i++) Thread.Sleep(50);
        StartBuiltin(runtime, fDrain, mLoop);
        return true;
    }

    // ---- 探す -----------------------------------------------------------------

    // 探す実装は NgolRuntimeFind に置いてある。ここで名前を当てにいかない理由もそこに書いた。

    // ---- 報告 -----------------------------------------------------------------

    private void Report(IExecutionContext ctx, int builtinThread, string message)
    {
        var stat = Stat();
        double left = 0.0;
        if (stat[0] != 0 && stat[4] != 0)
        {
            left = (new DateTime(stat[4], DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds;
            if (left < 0.0) left = 0.0;
        }
        ctx.SetPortValue("driving", stat[0] != 0);
        ctx.SetPortValue("turns", (double)stat[1]);
        ctx.SetPortValue("driver_thread", (double)stat[2]);
        ctx.SetPortValue("builtin_thread", (double)builtinThread);
        ctx.SetPortValue("stopped_builtin", stat[3] != 0);
        ctx.SetPortValue("seconds_left", Math.Round(left, 1));
        ctx.SetPortValue("result", message);
    }

    private static int ThreadIdOf(object thread)
    {
        var th = thread as Thread;
        return th != null && th.IsAlive ? th.ManagedThreadId : 0;
    }

    private static double ToDouble(object value, double fallback)
    {
        if (value == null) return fallback;
        if (value is double d) return d;
        double parsed;
        return double.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
    }
}
