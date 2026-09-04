using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NodeGraphModLab.NodeAPI;

/// <summary>
/// 永続ノードが「どのスレッドで」「どれくらいの間隔で」回るのかを測る。
///
/// ガイドには OnUpdate は「毎フレーム（メインスレッド）」とあるが、
/// それは Unity のようにホストが NGOL を毎フレーム呼ぶ場合の話。
/// ホストが NGOL を呼ばない構成（EnableDirectMode=true）では、
/// 回しているのは NGOL 自前のスレッドで、間隔は directModeIntervalMs で決まる。
/// どちらなのかは推測せず、id と時刻で測る。
///
/// ホスト固有の要素は無い。どのホストでもそのまま使える。
/// </summary>
[NodeType("ngol.dev.persistent_pulse", "Dev", "Persistent Pulse",
    Version = "1.0.1",
    Description = "Register a per-frame callback and measure what that actually means on this host: which thread it runs on, how often it fires, and whether live parameters from the WebUI reach it. The guide describes OnUpdate as running each frame on the main thread, which holds when the host drives NGOL itself; where NGOL drives its own loop instead the answer is different, so this measures rather than assumes. It only records - no host API is touched.")]
[NodePort("start", PortDirection.Input, "bool", Description = "true registers the callback if it is not already running. false just reads what has been measured so far")]
[NodePort("live_key", PortDirection.Input, "string", Description = "Live parameter key to read on every tick, so WebUI tuning can be seen arriving. Default pulse.value")]
[NodePort("registered", PortDirection.Output, "bool", Description = "true when the callback is registered and ticking")]
[NodePort("ticks", PortDirection.Output, "number", Description = "How many times the callback has fired")]
[NodePort("tick_thread", PortDirection.Output, "number", Description = "Operating system thread id the callback runs on. This is the id a debugger and any tool outside the process shows, not the .NET managed id ngol.dev.slow_probe and ngol.dev.tick_source report")]
[NodePort("exec_thread", PortDirection.Output, "number", Description = "Operating system thread id this Execute ran on, for contrast")]
[NodePort("main_thread", PortDirection.Output, "number", Description = "Operating system thread id of the host's main thread, found as the earliest-created one")]
[NodePort("on_main_thread", PortDirection.Output, "bool", Description = "true when the callback runs on the host's main thread")]
[NodePort("interval_ms", PortDirection.Output, "number", Description = "Milliseconds between the last two ticks")]
[NodePort("rate_hz", PortDirection.Output, "number", Description = "Ticks per second, worked out from the interval")]
[NodePort("live_value", PortDirection.Output, "string", Description = "What the live parameter held on the most recent tick")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable summary of what the per-frame callback turned out to be here")]
public sealed class PersistentPulseNode : INode
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32First(IntPtr snapshot, IntPtr entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32Next(IntPtr snapshot, IntPtr entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit,
                                              out long kernel, out long user);

    // ホットリロードで作り直されても生き残る置き場所。
    // 普通の static にすると、ノードを差し替えた瞬間に
    // 回り続けているコールバックが「前の版の入れ物」を触り、こちらから何も見えなくなる。
    private static T Keep<T>(string key, Func<T> factory) where T : class
    {
        var v = AppDomain.CurrentDomain.GetData(key) as T;
        if (v == null) { v = factory(); AppDomain.CurrentDomain.SetData(key, v); }
        return v;
    }

    // 0:ticks 1:tickThread 2:lastStamp 3:intervalTicks 4:registered
    private static long[] State { get { return Keep("NgolPulse_state", () => new long[5]); } }
    private static string[] LiveSlot { get { return Keep("NgolPulse_live", () => new string[1]); } }

    public void Execute(IExecutionContext ctx)
    {
        bool start = false;
        object startValue = ctx.GetPortValue("start");
        if (startValue is bool) start = (bool)startValue;

        string liveKey = ctx.GetPortValue("live_key") as string;
        if (string.IsNullOrEmpty(liveKey)) liveKey = "pulse.value";

        uint execThread = GetCurrentThreadId();
        uint mainThread = FindMainThread();

        long[] state = State;

        if (start && Interlocked.Read(ref state[4]) == 0)
        {
            Interlocked.Exchange(ref state[4], 1);
            Interlocked.Exchange(ref state[0], 0);

            string key = liveKey;
            ctx.RegisterPersistent(new PersistentCallbacks
            {
                OnStart = () =>
                {
                    Interlocked.Exchange(ref state[2], Stopwatch.GetTimestamp());
                },
                OnUpdate = () =>
                {
                    // 例外を漏らすと Job が Failed になり、登録ごと止まる（fail-fast）。
                    // 測るだけのノードで止まられると意味が無いので、ここで握る。
                    try
                    {
                        long now = Stopwatch.GetTimestamp();
                        long prev = Interlocked.Exchange(ref state[2], now);
                        if (prev != 0) Interlocked.Exchange(ref state[3], now - prev);
                        Interlocked.Increment(ref state[0]);
                        Interlocked.Exchange(ref state[1], GetCurrentThreadId());

                        // WebUI からのライブ調整が届いているかを、値そのもので見る。
                        LiveSlot[0] = Convert.ToString(ctx.GetLiveParam<double>(key, -1.0));
                    }
                    catch (Exception)
                    {
                        // 一過性の失敗で止めない。
                    }
                },
                OnStop = () =>
                {
                    Interlocked.Exchange(ref state[4], 0);
                },
            });
        }

        long ticks = Interlocked.Read(ref state[0]);
        long tickThread = Interlocked.Read(ref state[1]);
        long intervalTicks = Interlocked.Read(ref state[3]);
        bool registered = Interlocked.Read(ref state[4]) != 0;

        double intervalMs = intervalTicks > 0
            ? (intervalTicks * 1000.0 / Stopwatch.Frequency)
            : 0.0;
        double rateHz = intervalMs > 0.0 ? (1000.0 / intervalMs) : 0.0;
        bool onMain = tickThread != 0 && tickThread == mainThread;

        var report = new StringBuilder();
        report.Append("registered   : ").Append(registered).Append('\n');
        report.Append("ticks        : ").Append(ticks).Append('\n');
        report.Append("exec thread  : ").Append(execThread).Append("   (this Execute)\n");
        report.Append("tick thread  : ").Append(tickThread == 0 ? "(not yet)" : tickThread.ToString()).Append('\n');
        report.Append("main thread  : ").Append(mainThread).Append('\n');
        report.Append("interval     : ").Append(intervalMs.ToString("0.###")).Append(" ms  (")
              .Append(rateHz.ToString("0.##")).Append(" Hz)\n");
        report.Append("live value   : ").Append(LiveSlot[0] == null ? "(none)" : LiveSlot[0]).Append('\n');
        report.Append(onMain
            ? "the per-frame callback runs on the host's MAIN thread\n"
            : "the per-frame callback does NOT run on the host's main thread; it is NGOL's own loop\n");

        ctx.SetPortValue("registered", registered);
        ctx.SetPortValue("ticks", (double)ticks);
        ctx.SetPortValue("tick_thread", (double)tickThread);
        ctx.SetPortValue("exec_thread", (double)execThread);
        ctx.SetPortValue("main_thread", (double)mainThread);
        ctx.SetPortValue("on_main_thread", onMain);
        ctx.SetPortValue("interval_ms", intervalMs);
        ctx.SetPortValue("rate_hz", rateHz);
        ctx.SetPortValue("live_value", LiveSlot[0] == null ? "" : LiveSlot[0]);
        ctx.SetPortValue("result", report.ToString());
    }

    private static uint FindMainThread()
    {
        const uint TH32CS_SNAPTHREAD = 0x00000004;
        const uint THREAD_QUERY_INFORMATION = 0x0040;
        const int ENTRY_SIZE = 28;

        uint pid = GetCurrentProcessId();
        uint best = 0;
        long earliest = long.MaxValue;

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;
        IntPtr entry = Marshal.AllocHGlobal(ENTRY_SIZE);
        try
        {
            Marshal.WriteInt32(entry, 0, ENTRY_SIZE);
            bool more = Thread32First(snapshot, entry);
            while (more)
            {
                uint tid = (uint)Marshal.ReadInt32(entry, 8);
                uint owner = (uint)Marshal.ReadInt32(entry, 12);
                Marshal.WriteInt32(entry, 0, ENTRY_SIZE);
                more = Thread32Next(snapshot, entry);
                if (owner != pid) continue;

                IntPtr handle = OpenThread(THREAD_QUERY_INFORMATION, false, tid);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    long created;
                    long exit;
                    long kernel;
                    long user;
                    if (GetThreadTimes(handle, out created, out exit, out kernel, out user)
                        && created < earliest)
                    {
                        earliest = created;
                        best = tid;
                    }
                }
                finally { CloseHandle(handle); }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            CloseHandle(snapshot);
        }
        return best;
    }
}
