using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// このプロセスのスレッドを全部見て、どこで止まっているかを出す。
///
/// ウィンドウには一切触らないので、メッセージを捌く側が止まっていても動く。
/// 固まった相手では、ウィンドウを読む操作は返ってこないため、これが唯一の入口になる。
///
/// 守っている決まりは 3 つ。どれを外してもプロセスが落ちる。
///   1. 読む範囲を先に決める（Marshal の範囲外は例外にならず、アクセス違反で終わる）
///   2. 止めている間は生の数値を写すだけ。モジュールの解決は再開してから
///      （解決はローダーのロックを取るので、止めた相手が持っていると噛み合う）
///   3. スレッドの列挙は Toolhelp32（ホストによっては ProcessThread が参照集合に無い）
///
/// スタックから拾えるのは「モジュールの中を指す値」までで、古い呼び出しの残骸も混ざる。
/// 形で見て外れ値を探す使い方が向いている。
/// </summary>
[NodeType("ngol.proc.thread_stacks", "Process", "Thread Stacks",
    Version = "1.0.0",
    Description =
        "List every thread in this process with where it currently sits and which modules its stack mentions. "
      + "No window is touched, so this works while the message pump is stuck - which matters because on a "
      + "frozen target the window-reading nodes never return. Filter by module to find who is inside a given "
      + "library: when many threads share one identical frame list they are an idle worker pool, and the one "
      + "that differs is usually the thread that is stuck. Frames are gathered by scanning the stack for "
      + "values that land inside a module, so leftovers from older calls are included and the list is a rough "
      + "chain rather than an exact backtrace. Feed the module+RVA it prints to ngol.code.pdb_lookup to turn "
      + "the frames into names.")]
[NodePort("module_contains", PortDirection.Input, "string", Description = "Case-insensitive substring of a module name to look for. Empty lists every frame that lands in any module")]
[NodePort("thread_id", PortDirection.Input, "number", Description = "Look at one thread only. 0 = every thread. Use this after a broad pass to see one thread's frames in full")]
[NodePort("depth", PortDirection.Input, "number", Description = "How many stack slots to look at per thread. Default 200. Reads never go past the end of the stack whatever this says")]
[NodePort("max_frames", PortDirection.Input, "number", Description = "How many frames to print per thread. Default 40")]
[NodePort("thread_count", PortDirection.Output, "number", Description = "How many threads were looked at, excluding the one running this node")]
[NodePort("hit_count", PortDirection.Output, "number", Description = "How many threads matched the filter")]
[NodePort("unreadable", PortDirection.Output, "number", Description = "How many threads could not be read. A thread that ends between the listing and the read lands here")]
[NodePort("threads", PortDirection.Output, "string", Description = "Matching threads: id, where it sits as module+RVA, and the frames that matched")]
[NodePort("result", PortDirection.Output, "string", Description = "Summary of what was looked at and what matched")]
public sealed class ThreadStacksNode : INode
{
    private const uint THREAD_ACCESS = 0x0008 | 0x0002 | 0x0040;   // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
    private const uint CONTEXT_AMD64 = 0x00100000, CONTEXT_CONTROL = CONTEXT_AMD64 | 0x1;
    private const int CONTEXT_SIZE = 1232, RIP_OFFSET = 0xF8, RSP_OFFSET = 0x98, CONTEXT_FLAGS_OFFSET = 0x30;
    private const uint FLAG_FROM_ADDRESS = 0x4, FLAG_UNCHANGED_REFCOUNT = 0x2;
    private const uint TH32CS_SNAPTHREAD = 0x4;
    private const int THREADENTRY32_SIZE = 28, TE_TID = 8, TE_OWNER_PID = 12;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResumeThread(IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr t, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(IntPtr module, StringBuilder name, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snap, IntPtr entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snap, IntPtr entry);

    private sealed class Capture
    {
        public uint Tid;
        public long Rip;
        public long[] Slots;
        public int Count;
        public string Problem;
    }

    public void Execute(IExecutionContext ctx)
    {
        var want = (ctx.GetPortValue("module_contains") as string ?? "").Trim();
        var only = (uint)(ctx.GetPortValue("thread_id") is double td ? td : 0.0);
        var depth = (int)(ctx.GetPortValue("depth") is double d ? d : 200.0);
        var maxFrames = (int)(ctx.GetPortValue("max_frames") is double mf ? mf : 40.0);
        if (depth < 1) depth = 1;
        if (depth > 2000) depth = 2000;
        if (maxFrames < 1) maxFrames = 1;

        var self = GetCurrentThreadId();
        var taken = new List<Capture>();

        // 第 1 段: 写すだけ。解釈も文字列化もここではしない。
        foreach (var tid in ThreadIds())
        {
            if (tid == self) continue;
            if (only != 0 && tid != only) continue;
            taken.Add(Read(tid, depth));
        }

        // 第 2 段: 全員が再開したあとで解釈する。
        int hits = 0, refused = 0;
        var sb = new StringBuilder();
        foreach (var one in taken)
        {
            if (one.Problem != null) { refused++; continue; }

            var frames = new List<string>();
            for (var i = 0; i < one.Count; i++)
            {
                var described = Describe(one.Slots[i]);
                if (described == null) continue;
                if (want.Length == 0 || described.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                    frames.Add(described);
            }
            if (frames.Count == 0) continue;

            hits++;
            sb.Append("thread ").Append(one.Tid).Append("  at ").Append(Describe(one.Rip) ?? "(no module)")
              .Append("  ").Append(frames.Count).Append(" frame(s)\n");
            for (var i = 0; i < frames.Count && i < maxFrames; i++) sb.Append("    ").Append(frames[i]).Append('\n');
            if (frames.Count > maxFrames) sb.Append("    ... ").Append(frames.Count - maxFrames).Append(" more\n");
        }

        ctx.SetPortValue("thread_count", (double)taken.Count);
        ctx.SetPortValue("hit_count", (double)hits);
        ctx.SetPortValue("unreadable", (double)refused);
        ctx.SetPortValue("threads", sb.ToString());
        ctx.SetPortValue("result", "looked at " + taken.Count + " thread(s), " + refused + " could not be read; "
                                 + hits + (want.Length == 0 ? " have module frames" : " mention '" + want + "'"));
    }

    /// <summary>このプロセスのスレッド id。Toolhelp32 なので追加の参照集合が要らない。</summary>
    private static List<uint> ThreadIds()
    {
        var ids = new List<uint>();
        var pid = GetCurrentProcessId();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return ids;
        var entry = Marshal.AllocHGlobal(THREADENTRY32_SIZE);
        try
        {
            Marshal.WriteInt32(entry, 0, THREADENTRY32_SIZE);
            var ok = Thread32First(snap, entry);
            while (ok)
            {
                if ((uint)Marshal.ReadInt32(entry, TE_OWNER_PID) == pid)
                    ids.Add((uint)Marshal.ReadInt32(entry, TE_TID));
                Marshal.WriteInt32(entry, 0, THREADENTRY32_SIZE);
                ok = Thread32Next(snap, entry);
            }
        }
        finally { Marshal.FreeHGlobal(entry); CloseHandle(snap); }
        return ids;
    }

    /// <summary>止めて、読める範囲だけを写して、必ず再開する。</summary>
    private static Capture Read(uint tid, int depth)
    {
        var taken = new Capture { Tid = tid, Slots = new long[depth] };
        var h = OpenThread(THREAD_ACCESS, false, tid);
        if (h == IntPtr.Zero) { taken.Problem = "OpenThread err=" + Marshal.GetLastWin32Error(); return taken; }

        var raw = Marshal.AllocHGlobal(CONTEXT_SIZE + 16);
        var aligned = new IntPtr((raw.ToInt64() + 15) & ~15L);
        try
        {
            for (var i = 0; i < CONTEXT_SIZE; i++) Marshal.WriteByte(aligned, i, 0);
            Marshal.WriteInt32(aligned, CONTEXT_FLAGS_OFFSET, unchecked((int)CONTEXT_CONTROL));

            if (SuspendThread(h) == uint.MaxValue)
            {
                taken.Problem = "SuspendThread err=" + Marshal.GetLastWin32Error();
                return taken;
            }
            try
            {
                if (!GetThreadContext(h, aligned))
                {
                    taken.Problem = "GetThreadContext err=" + Marshal.GetLastWin32Error();
                    return taken;
                }
                taken.Rip = Marshal.ReadInt64(aligned, RIP_OFFSET);
                var rsp = Marshal.ReadInt64(aligned, RSP_OFFSET);

                // 読んでよい長さを先に決める。ここを省くと端を越えてプロセスが落ちる。
                var readable = NgolSafeMemory.ReadableLength(new IntPtr(rsp), (long)depth * 8);
                if (readable < 8) { taken.Problem = "the stack pointer is not in readable memory"; return taken; }
                var n = (int)(readable / 8);
                if (n > depth) n = depth;
                for (var i = 0; i < n; i++) taken.Slots[i] = Marshal.ReadInt64(new IntPtr(rsp + i * 8));
                taken.Count = n;
            }
            finally { ResumeThread(h); }
        }
        finally { Marshal.FreeHGlobal(raw); CloseHandle(h); }
        return taken;
    }

    private static string Describe(long address)
    {
        if (address < 0x10000) return null;
        if (!GetModuleHandleExW(FLAG_FROM_ADDRESS | FLAG_UNCHANGED_REFCOUNT, new IntPtr(address), out var module)
            || module == IntPtr.Zero) return null;
        var name = new StringBuilder(600);
        if (GetModuleFileNameW(module, name, name.Capacity) <= 0) return null;
        var full = name.ToString();
        var cut = full.LastIndexOf('\\');
        return (cut >= 0 ? full.Substring(cut + 1) : full) + " + 0x" + (address - module.ToInt64()).ToString("x");
    }
}
