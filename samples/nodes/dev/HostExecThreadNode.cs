using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

/// <summary>
/// ノードが走っているスレッドが、ホストのメインスレッドかどうかを言う。
///
/// NGOL の MainThreadDispatch は「メインスレッド」という名前だが、
///    実際に届くのは Tick() を呼んだスレッド。EnableDirectMode=true のときは
///    NGOL 自前のスレッドであって、ホストのメインスレッドではない。
///    そこからホストの UI API を呼ぶとホストごと落ちる。
///    このノードはその前提を、推測ではなく id の突き合わせで確かめる。
///
/// ホスト固有の要素は無いので、どのホストでもそのまま使える。
/// </summary>
[NodeType("ngol.dev.exec_thread", "Dev", "Where Am I Running",
    Version = "1.0.1",
    Description = "Say whether this node is running on the host's main thread or somewhere else, by comparing thread ids rather than by assuming. NGOL's MainThreadDispatch delivers to whichever thread calls Tick, which in direct mode is NGOL's own thread and not the host's, so calling a host UI API from there takes the host down. Check here before touching anything that belongs to the host's main thread. Nothing host-specific: this works on any host.")]
[NodePort("process_id", PortDirection.Output, "number", Description = "Process this node is running inside")]
[NodePort("exec_thread", PortDirection.Output, "number", Description = "Operating system thread id this node is executing on. This is the id a debugger and any tool outside the process shows, not the .NET managed id ngol.dev.slow_probe and ngol.dev.tick_source report")]
[NodePort("main_thread", PortDirection.Output, "number", Description = "Operating system thread id of the earliest-created thread, which is the host's main thread")]
[NodePort("thread_count", PortDirection.Output, "number", Description = "How many threads the process has right now")]
[NodePort("on_main_thread", PortDirection.Output, "boolean", Description = "true when this node runs on the host's main thread. Expect false in direct mode")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable verdict, including what it means for calling host APIs")]
public sealed class HostExecThreadNode : INode
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

    public void Execute(IExecutionContext ctx)
    {
        uint pid = GetCurrentProcessId();
        uint execThread = GetCurrentThreadId();
        uint mainThread = 0;
        int threadCount = 0;

        // 最も早く作られたスレッド = ホストのメインスレッド。
        // System.Diagnostics.Process.Threads は使わない -- その型は
        // System.Collections.NonGeneric の参照を要求し、ノードの参照集合に無いことがある。
        const uint TH32CS_SNAPTHREAD = 0x00000004;
        const uint THREAD_QUERY_INFORMATION = 0x0040;
        const int ENTRY_SIZE = 28;

        long earliest = long.MaxValue;
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
        {
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
                    threadCount++;

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
                            mainThread = tid;
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
        }

        bool onMain = mainThread != 0 && mainThread == execThread;

        var report = new StringBuilder();
        report.Append("process      : ").Append(pid).Append('\n');
        report.Append("exec thread  : ").Append(execThread).Append('\n');
        report.Append("main thread  : ").Append(mainThread == 0 ? "(unknown)" : mainThread.ToString()).Append('\n');
        report.Append("threads      : ").Append(threadCount).Append('\n');
        report.Append(onMain
            ? "OK  this node IS on the host's main thread; host APIs are reachable from here\n"
            : "NO  this node is NOT on the host's main thread. Hand work over to the main thread before touching host APIs\n");

        ctx.SetPortValue("process_id", (double)pid);
        ctx.SetPortValue("exec_thread", (double)execThread);
        ctx.SetPortValue("main_thread", (double)mainThread);
        ctx.SetPortValue("thread_count", (double)threadCount);
        ctx.SetPortValue("on_main_thread", onMain);
        ctx.SetPortValue("result", report.ToString());
    }
}
