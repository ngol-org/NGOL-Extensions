using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// このプロセスのスレッドが **いまどこで止まっているか** を、モジュール単位で言う。
///
/// **Blender が固まっているときに使う。** そのとき `blender.py.run` は使えない--
///    ポンプ自体が Python なので、GIL を待って一緒に止まる。
///    **C# のノードだけが動く。** ここが「対象の中に NGOL が居る」ことの値打ち。
///
/// やること: スレッドを列挙 -> 一時停止 -> 命令ポインタ(RIP)を読む -> 再開 ->
///          その番地がどのモジュールに属するかを引く。
///
/// 自分のスレッドは触らない（止めたら自分が返せなくなる）。
/// 一時停止は必ず対で戻す。`finally` で必ず `ResumeThread` する。
/// </summary>
[NodeType("blender.diag.threads", "Blender", "Where Are The Threads",
    Version = "1.0.0",
    Description = "Say where every thread in this process is currently stopped, by module. Meant for the case where Blender has frozen: the Python side of the bridge cannot answer then, because the pump is Python and waits on the same lock, so this reads the machine state directly instead. Each thread is briefly suspended to read its instruction pointer and resumed again.")]
[NodePort("filter", PortDirection.Input, "string", Description = "Only report threads stopped in a module whose name contains this, e.g. python313. Empty reports every thread")]
[NodePort("threads", PortDirection.Output, "number", Description = "How many threads were examined")]
[NodePort("oldest_tid", PortDirection.Output, "number", Description = "Thread id of the earliest-created thread, which is the main thread")]
[NodePort("oldest_module", PortDirection.Output, "string", Description = "Which module the main thread is stopped in. python313.dll here means it is waiting on the interpreter lock")]
[NodePort("result", PortDirection.Output, "string", Description = "One line per thread: id / instruction pointer / module, oldest first")]
public sealed class BlenderThreadDiagNode : INode
{
    private const uint TH32CS_SNAPTHREAD = 0x00000004;
    private const uint THREAD_ACCESS = 0x0002 /*SUSPEND_RESUME*/ | 0x0008 /*GET_CONTEXT*/
                                     | 0x0040 /*QUERY_INFORMATION*/;
    private const uint CONTEXT_AMD64 = 0x00100000;
    private const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x1;
    private const int CONTEXT_SIZE = 1232;   // x64 の CONTEXT
    private const int OFF_CONTEXTFLAGS = 0x30;
    private const int OFF_RIP = 0xF8;
    // RIP からモジュールを引く。自前でモジュール一覧を持たなくてよい。
    private const uint FROM_ADDRESS = 0x00000004;
    private const uint UNCHANGED_REFCOUNT = 0x00000002;

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
    private static extern uint SuspendThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr thread, IntPtr context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit,
                                              out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(IntPtr module, StringBuilder name, int size);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private sealed class ThreadInfo
    {
        public uint Tid;
        public long Created;
        public ulong Rip;
        public string Module = "";
        public string Note = "";
    }

    public void Execute(IExecutionContext ctx)
    {
        string filter = (ctx.GetPortValue("filter") as string ?? "").Trim();
        uint myPid = GetCurrentProcessId();
        uint myTid = GetCurrentThreadId();

        var found = new List<ThreadInfo>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            ctx.SetPortValue("result",
                "Could not enumerate threads (GetLastError=" + Marshal.GetLastWin32Error() + ")");
            return;
        }

        // THREADENTRY32: dwSize / cntUsage / th32ThreadID / th32OwnerProcessID / ...
        const int ENTRY_SIZE = 28;
        IntPtr entry = Marshal.AllocHGlobal(ENTRY_SIZE);
        // CONTEXT は 16 バイト境界に載せる必要がある。多めに取って自分で合わせる。
        IntPtr contextRaw = Marshal.AllocHGlobal(CONTEXT_SIZE + 16);
        try
        {
            long aligned = (contextRaw.ToInt64() + 15) & ~15L;
            IntPtr context = new IntPtr(aligned);

            Marshal.WriteInt32(entry, 0, ENTRY_SIZE);
            bool more = Thread32First(snapshot, entry);
            while (more)
            {
                uint tid = (uint)Marshal.ReadInt32(entry, 8);
                uint ownerPid = (uint)Marshal.ReadInt32(entry, 12);
                Marshal.WriteInt32(entry, 0, ENTRY_SIZE);
                more = Thread32Next(snapshot, entry);

                if (ownerPid != myPid) continue;

                var info = new ThreadInfo { Tid = tid };

                IntPtr handle = OpenThread(THREAD_ACCESS, false, tid);
                if (handle == IntPtr.Zero)
                {
                    // 生成時刻が取れないものは並びの基準に混ぜない。
                    //    0 のままにすると「最も古い＝メインスレッド」の判定を壊す。
                    info.Created = long.MaxValue;
                    info.Note = "Cannot open (GetLastError=" + Marshal.GetLastWin32Error() + ")";
                    found.Add(info);
                    continue;
                }

                if (tid == myTid)
                {
                    // 自分は止めない（止めたら返せなくなる）。
                    //    ただし**生成時刻は取る**--取らないと 0 のまま先頭に並び、
                    //      自分をメインスレッドだと報告してしまう。
                    try
                    {
                        if (GetThreadTimes(handle, out long selfCreated, out _, out _, out _))
                            info.Created = selfCreated;
                        else
                            info.Created = long.MaxValue;
                    }
                    finally { CloseHandle(handle); }
                    info.Note = "(the thread running this node; not suspended)";
                    found.Add(info);
                    continue;
                }

                try
                {
                    if (GetThreadTimes(handle, out long created, out _, out _, out _))
                        info.Created = created;
                    else
                        info.Created = long.MaxValue;

                    if (SuspendThread(handle) == uint.MaxValue)
                    {
                        info.Note = "Cannot suspend (GetLastError=" + Marshal.GetLastWin32Error() + ")";
                    }
                    else
                    {
                        try
                        {
                            for (int i = 0; i < CONTEXT_SIZE; i++) Marshal.WriteByte(context, i, 0);
                            Marshal.WriteInt32(context, OFF_CONTEXTFLAGS, unchecked((int)CONTEXT_CONTROL));
                            if (GetThreadContext(handle, context))
                            {
                                info.Rip = (ulong)Marshal.ReadInt64(context, OFF_RIP);
                                info.Module = ModuleOf((IntPtr)(long)info.Rip);
                            }
                            else
                            {
                                info.Note = "Cannot get context (GetLastError="
                                            + Marshal.GetLastWin32Error() + ")";
                            }
                        }
                        finally
                        {
                            // 何があっても必ず戻す。止めっぱなしにしない。
                            ResumeThread(handle);
                        }
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
                found.Add(info);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            Marshal.FreeHGlobal(contextRaw);
            CloseHandle(snapshot);
        }

        // 最も早く作られたスレッドがメインスレッド。
        found.Sort((a, b) => a.Created.CompareTo(b.Created));

        var report = new StringBuilder();
        int shown = 0;
        foreach (var t in found)
        {
            if (filter.Length > 0 &&
                t.Module.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            shown++;
            report.Append(shown == 1 && filter.Length == 0 ? "MAIN " : "     ")
                  .Append("tid=").Append(t.Tid)
                  .Append("  rip=0x").Append(t.Rip.ToString("x"))
                  .Append("  ").Append(t.Module.Length > 0 ? t.Module : "(outside any module)")
                  .Append(t.Note.Length > 0 ? "  " + t.Note : "")
                  .Append('\n');
        }

        ctx.SetPortValue("threads", (double)found.Count);
        if (found.Count > 0)
        {
            ctx.SetPortValue("oldest_tid", (double)found[0].Tid);
            ctx.SetPortValue("oldest_module", found[0].Module);
        }
        ctx.SetPortValue("result", report.ToString());
    }

    private static string ModuleOf(IntPtr address)
    {
        if (!GetModuleHandleExW(FROM_ADDRESS | UNCHANGED_REFCOUNT, address, out IntPtr module))
            return "";
        var name = new StringBuilder(512);
        int n = GetModuleFileNameW(module, name, name.Capacity);
        if (n <= 0) return "";
        string full = name.ToString(0, n);
        int slash = full.LastIndexOf('\\');
        return slash >= 0 ? full.Substring(slash + 1) : full;
    }
}
