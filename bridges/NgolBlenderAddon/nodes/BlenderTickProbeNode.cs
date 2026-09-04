using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MonoMod.RuntimeDetour;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// **ネイティブフックで、ホストのメインスレッドを掴めるか**を確かめる。
///
/// 動機: NGOL の `MainThreadDispatch` はキューに積むだけで、
///    吸い出すのは `Tick()` を呼んだスレッド。`EnableDirectMode=true` の今、
///    それは **NGOL 自前のスレッド**であって Blender のメインスレッドではない。
///    => ホストのメインループから呼ばれる関数にフックを刺せば、
///      **そのコールバックはホストのメインスレッドで走る**はず。ここではそれを測る。
///
/// 測り方（言葉ではなく突き合わせで示す）:
///   1) スレッドを列挙し、**最も早く作られたもの＝メインスレッド**の id を取る
///   2) フックのコールバックで `GetCurrentThreadId()` を記録する
///   3) **一致すればメインスレッドを掴めている**
///   4) さらに、NGOL 側のスレッドから積んだ仕事が
///      **そのコールバックで実行される**（＝ディスパッチが成立する）ことも記録する
///
/// ここで得られるのは「メインスレッドである」ことだけ。
///     **`bpy` を安全に触ってよい地点かどうかは別問題**--
///     描画の途中でシーンを書き換えるのは Blender が禁じている。
///     この試作は**記録しかしない**。`bpy` は呼ばない。
/// </summary>
[NodeType("blender.tick.probe", "Blender", "Main Thread Tick Probe",
    Version = "1.0.0",
    Description = "Hook a function the host calls from its own main loop and check, by comparing thread ids, whether the callback really lands on the host's main thread. It also drains a queue there, so work handed over from an NGOL thread can be shown to run on the main one. It only records: no host API is called from the callback, because being on the main thread is not the same as being at a point where touching the host's data is allowed.")]
[NodePort("module", PortDirection.Input, "string", Description = "Module holding the function to hook, e.g. opengl32.dll")]
[NodePort("rva", PortDirection.Input, "string", Description = "RVA of the function, hex, e.g. 0x41a50. Check it with ngol.hook.safety_check first")]
[NodePort("enabled", PortDirection.Input, "boolean", Description = "true installs the hook, false uninstalls it")]
[NodePort("enqueue_probe", PortDirection.Input, "boolean", Description = "Also hand one piece of work to the queue from this node's thread, so the thread it actually runs on can be compared")]
[NodePort("hit_count", PortDirection.Output, "number", Description = "How many times the hook has been reached")]
[NodePort("hook_thread", PortDirection.Output, "number", Description = "Thread id the callback last ran on")]
[NodePort("main_thread", PortDirection.Output, "number", Description = "Thread id of the earliest-created thread, which is the host's main thread")]
[NodePort("caller_thread", PortDirection.Output, "number", Description = "Thread id this node itself is running on, for contrast")]
[NodePort("drained_thread", PortDirection.Output, "number", Description = "Thread id the queued work actually ran on")]
[NodePort("on_main_thread", PortDirection.Output, "boolean", Description = "true when the callback thread and the main thread are the same")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable verdict, or the reason it failed")]
public sealed class BlenderTickProbeNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

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

    // 対象関数のシグネチャ。x64 では引数が多めでも害は無い（呼ばれる側は使わない分を見ない）。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr HookDelegate(IntPtr a0, IntPtr a1, IntPtr a2);

    private delegate IntPtr HookWithOriginal(HookDelegate orig, IntPtr a0, IntPtr a1, IntPtr a2);

    // ホットリロードで作り直されても生き残る置き場所。
    //    ここを普通の static にすると、ノードを差し替えた瞬間に
    //      フックが「前の版の入れ物」を触り続け、こちらからは何も見えなくなる。
    private static T Keep<T>(string key, Func<T> factory) where T : class
    {
        var v = AppDomain.CurrentDomain.GetData(key) as T;
        if (v == null) { v = factory(); AppDomain.CurrentDomain.SetData(key, v); }
        return v;
    }

    private static ConcurrentDictionary<long, NativeHook> Hooks
        => Keep("NgolTickProbe_hooks", () => new ConcurrentDictionary<long, NativeHook>());

    private static System.Collections.Generic.List<GCHandle> Pinned
        => Keep("NgolTickProbe_pins", () => new System.Collections.Generic.List<GCHandle>());

    /// <summary>(hits, lastThreadId, drainedThreadId) を 1 つの箱で持つ。</summary>
    private static long[] Counters
        => Keep("NgolTickProbe_counters", () => new long[3]);

    private static ConcurrentQueue<Action> Queue
        => Keep("NgolTickProbe_queue", () => new ConcurrentQueue<Action>());

    public void Execute(IExecutionContext ctx)
    {
        string module = (ctx.GetPortValue("module") as string ?? "").Trim();
        if (module.Length == 0) module = "opengl32.dll";
        string rvaText = (ctx.GetPortValue("rva") as string ?? "").Trim();
        bool enabled = ctx.GetPortValue("enabled") is bool b && b;
        bool enqueue = ctx.GetPortValue("enqueue_probe") is bool e && e;

        uint callerThread = GetCurrentThreadId();
        ctx.SetPortValue("caller_thread", (double)callerThread);

        uint mainThread = FindMainThread();
        ctx.SetPortValue("main_thread", (double)mainThread);

        var report = new StringBuilder();

        long abs = 0;
        if (rvaText.Length > 0)
        {
            IntPtr baseAddr = GetModuleHandleW(module);
            if (baseAddr == IntPtr.Zero)
            {
                ctx.SetPortValue("result", module + " is not loaded in this process");
                return;
            }
            long rva = ParseHex(rvaText);
            if (rva == 0)
            {
                ctx.SetPortValue("result", "Could not read rva: " + rvaText);
                return;
            }
            abs = baseAddr.ToInt64() + rva;
        }

        try
        {
            if (enabled && abs != 0 && !Hooks.ContainsKey(abs))
            {
                Install(abs);
                report.Append("hooked @ 0x").Append(abs.ToString("X")).Append('\n');
            }
            else if (!enabled && abs != 0 && Hooks.TryRemove(abs, out var existing))
            {
                try { existing.Dispose(); } catch { /* 解除できなくても報告は返す */ }
                report.Append("unhooked @ 0x").Append(abs.ToString("X")).Append('\n');
            }
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", "Failed to install the hook: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        if (enqueue)
        {
            // NGOL 側のスレッドから積む。走る場所が変われば、ディスパッチが成立している。
            Queue.Enqueue(() => Slot.Write(Counters, 2, GetCurrentThreadId()));
            report.Append("queued 1 piece of work from thread ").Append(callerThread).Append('\n');
        }

        long hits = Interlocked.Read(ref Counters[0]);
        long hookThread = Interlocked.Read(ref Counters[1]);
        long drained = Interlocked.Read(ref Counters[2]);

        ctx.SetPortValue("hit_count", (double)hits);
        ctx.SetPortValue("hook_thread", (double)hookThread);
        ctx.SetPortValue("drained_thread", (double)drained);

        bool onMain = hookThread != 0 && hookThread == mainThread;
        ctx.SetPortValue("on_main_thread", onMain);

        report.Append("main thread   : ").Append(mainThread).Append('\n');
        report.Append("caller thread : ").Append(callerThread).Append("  (this node)\n");
        report.Append("hook thread   : ").Append(hookThread == 0 ? "(not called yet)" : hookThread.ToString()).Append('\n');
        report.Append("drained thread: ").Append(drained == 0 ? "(not run yet)" : drained.ToString()).Append('\n');
        report.Append("hits          : ").Append(hits).Append('\n');
        report.Append(onMain
            ? "match. The hook callback runs on the host's main thread\n"
            : "no match. Either not called yet, or called from a different thread\n");

        ctx.SetPortValue("result", report.ToString());
    }

    private static void Install(long abs)
    {
        HookWithOriginal hook = (orig, a0, a1, a2) =>
        {
            // ここはホストのメインスレッド（のはず）。**重いことをしない。**
            //    記録と、積まれた仕事を少しだけ捌くこと以外はしない。
            Interlocked.Increment(ref Counters[0]);
            Slot.Write(Counters, 1, GetCurrentThreadId());

            // 溜まっていても一度に全部やらない。ホストの描画を止めない。
            for (int i = 0; i < 4 && Queue.TryDequeue(out var work); i++)
            {
                try { work(); }
                catch { /* 1 件の失敗で描画を巻き込まない */ }
            }

            return orig(a0, a1, a2);
        };

        // 委譲が回収されるとフック先が消えてプロセスごと落ちる。固定する。
        Pinned.Add(GCHandle.Alloc(hook, GCHandleType.Normal));
        Hooks[abs] = new NativeHook((IntPtr)abs, hook);
    }

    /// <summary>最も早く作られたスレッド＝メインスレッド。</summary>
    private static uint FindMainThread()
    {
        const uint TH32CS_SNAPTHREAD = 0x00000004;
        const uint THREAD_QUERY = 0x0040;
        const int ENTRY_SIZE = 28;

        uint myPid = GetCurrentProcessId();
        uint best = 0;
        long bestCreated = long.MaxValue;

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
                if (owner != myPid) continue;

                IntPtr handle = OpenThread(THREAD_QUERY, false, tid);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (GetThreadTimes(handle, out long created, out _, out _, out _)
                        && created < bestCreated)
                    {
                        bestCreated = created;
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

    private static long ParseHex(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        return long.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>long[] の 1 要素へ安全に書く小道具。
    /// 名前を Volatile にしない--System.Threading.Volatile と紛れて読み違える。</summary>
    private static class Slot
    {
        internal static void Write(long[] array, int index, long value)
            => Interlocked.Exchange(ref array[index], value);
    }
}
