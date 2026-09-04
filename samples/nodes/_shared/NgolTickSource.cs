using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// NGOL の更新（Tick）の駆動元を、ノード側から後付けで差し替える。
///
/// direct mode は「ホストの更新ループへ乗れないとき」の代替で、専用スレッドが一定間隔で
/// 自分を回す。そのため更新の速さを決めるのは設定値であり、ホストが実際に絵を出す間隔とは
/// 無関係になる。ここでは駆動元を奪い、描画が待つ周期でそのまま Tick を回す。
/// 呼ぶ内容は direct mode と同一（Tick はドレインループの本体と同じ処理）で、時計だけが変わる。
///
/// 参照している NGOL Core の非公開メンバ（Core 改修時はここを確認する）:
///   - NgolRuntime._drainThread   （フィールド）direct mode の駆動スレッド
///   - NgolRuntime.DrainLoop()    （メソッド）  そのスレッドの本体。戻すときに作り直す
///   - NgolRuntime.Tick()         （public）    駆動ループの本体と同じ処理
/// いずれも見つからなければ何もせず、理由を報告する。
/// 実体は <see cref="NgolRuntimeFind"/> が実行文脈から参照をたどって見つける。
/// 保持者の名前で当てにいくと、名前が合っていても中身が空の写しを掴むことがある。
///
/// 制約:
///   - 掴んだあとは必ず <see cref="Unbind"/> で戻すこと。戻さないと更新する主体が居なくなり、
///     グラフの実行も永続ノードも止まる。
///   - 掴む/戻すの操作は、対象のスレッド自身から行ってはならない。<see cref="Execute"/> も
///     永続ノードのコールバックも、止めようとしているそのスレッド上で走っている。
///   - ホットリロードで世代が変わっても古い駆動スレッドが残らないよう、稼働中の記録は
///     AppDomain へ置く（入れ物は framework の型だけで作る）。
///   - ホスト固有の初期化コールバック（DirectModeDrainSetup）は、戻すときに再度呼ばれる。
/// </summary>
internal static class NgolTickSource
{
    private const string StateKey = "ngol.demo.tick_source";

    /// <summary>描画側が「この周回は垂直同期で待った」と申告するための旗。</summary>
    internal static volatile bool FrameWaited;

    internal static bool Active
    {
        get { var s = Load(); return s != null && ((StrongBox<bool>)s[0]).Value; }
    }

    /// <summary>
    /// 駆動元を差し替える。呼び出し元のスレッドは止めない（別スレッドで行う）。
    /// start には実行文脈をそのまま渡す。そこから参照をたどって本体を見つける。
    /// </summary>
    internal static void RequestBind(object start, Action<string> report)
    {
        var t = new Thread(() => { try { Bind(start, report); } catch (Exception ex) { report?.Invoke("bind failed: " + ex.Message); } });
        t.IsBackground = true;
        t.Name = "NGOL-TickSource-Bind";
        t.Start();
    }

    /// <summary>元の駆動元へ戻す。呼び出し元のスレッドが駆動スレッド自身でも安全。</summary>
    internal static void RequestUnbind(Action<string> report)
    {
        var t = new Thread(() => { try { Unbind(report); } catch (Exception ex) { report?.Invoke("unbind failed: " + ex.Message); } });
        t.IsBackground = true;
        t.Name = "NGOL-TickSource-Unbind";
        t.Start();
    }

    private static void Bind(object start, Action<string> report)
    {
        Unbind(null);   // 前の世代が残っていても取り替えられるようにする

        var found = NgolRuntimeFind.Find(start);
        var runtime = found.Runtime;
        if (runtime == null) { report?.Invoke(found.Explain()); return; }

        var tickMethod = runtime.GetType().GetMethod("Tick", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (tickMethod == null) { report?.Invoke("Tick() not found"); return; }
        var tick = (Action)Delegate.CreateDelegate(typeof(Action), runtime, tickMethod);

        // 止める対象が最初から居ないのは、direct mode ではない＝ホストが自分で Tick を
        // 呼んでいるということ。そこで駆動を立てると Tick を回す主体が 2 つになり、
        // 同じ更新経路が別々のスレッドから同時に走る。描画を載せているとプロセスごと落ちる。
        StopCoreDrain(runtime, out var reason, out var drainExisted);
        if (!drainExisted)
        {
            report?.Invoke("the host drives Tick itself, so there is no tick source to take");
            return;
        }

        // 停止しきれなくても駆動スレッドは必ず立てる。割り込みだけ残して引き返すと、
        // 割り込みが効いた瞬間に Tick を回す主体がゼロになる。

        var running = new StrongBox<bool>(true);
        var driver = new Thread(() => DriveLoop(tick, running));
        driver.IsBackground = true;
        driver.Name = "NGOL-TickSource";

        Save(new object[] { running, driver, runtime });
        driver.Start();

        report?.Invoke("tick source bound (core drain " + reason + ")");
    }

    private static void DriveLoop(Action tick, StrongBox<bool> running)
    {
        while (running.Value)
        {
            FrameWaited = false;

            try { tick(); }
            catch (Exception) { /* 1 周回の失敗で駆動を止めない */ }

            // 描画が垂直同期で待たなかった周回には何の待ちも無く、そのままでは空回りになる。
            if (!FrameWaited) Thread.Sleep(1);
        }
    }

    private static void Unbind(Action<string> report)
    {
        var state = Load();
        if (state == null) { report?.Invoke("not bound"); return; }

        var running = (StrongBox<bool>)state[0];
        var driver = (Thread)state[1];
        var runtime = state[2];

        running.Value = false;
        if (driver != null && driver != Thread.CurrentThread) driver.Join(2000);
        Save(null);

        StartCoreDrain(runtime, out var reason);
        report?.Invoke("tick source unbound (core drain " + reason + ")");
    }

    // ---- Core の駆動スレッド ----

    /// <summary>
    /// 駆動スレッドを止める。drainExisted は「止める対象が在ったか」で、
    /// false は direct mode ではない（ホストが自分で Tick を呼んでいる）ことを意味する。
    /// </summary>
    private static bool StopCoreDrain(object runtime, out string reason, out bool drainExisted)
    {
        var field = DrainField(runtime);
        var thread = field?.GetValue(runtime) as Thread;
        drainExisted = false;
        if (field == null) { reason = "_drainThread not found"; return false; }
        if (thread == null || !thread.IsAlive) { reason = "already stopped"; return true; }
        drainExisted = true;

        // ドレインループは待機中の割り込みで抜ける作りになっている。
        thread.Interrupt();
        thread.Join(2000);

        reason = thread.IsAlive ? "still alive" : "stopped";
        return !thread.IsAlive;
    }

    private static void StartCoreDrain(object runtime, out string reason)
    {
        var field = DrainField(runtime);
        if (runtime == null || field == null) { reason = "not restored"; return; }

        if (field.GetValue(runtime) is Thread alive && alive.IsAlive) { reason = "already running"; return; }

        var loop = runtime.GetType().GetMethod("DrainLoop", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (loop == null) { reason = "DrainLoop not found"; return; }

        var thread = new Thread((ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), runtime, loop));
        thread.IsBackground = true;
        thread.Name = "NGOL-Drain";
        field.SetValue(runtime, thread);
        thread.Start();
        reason = "restarted";
    }

    private static FieldInfo DrainField(object runtime)
        => runtime?.GetType().GetField("_drainThread", BindingFlags.NonPublic | BindingFlags.Instance);

    // ---- 世代をまたいで持つ記録 ----

    private static object[] Load() => AppDomain.CurrentDomain.GetData(StateKey) as object[];
    private static void Save(object[] state) => AppDomain.CurrentDomain.SetData(StateKey, state);
}
