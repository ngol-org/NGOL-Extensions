using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// このプロセスのスレッドが、実際にスケジューラに載っているかを出す。
///
/// CPU 時間はスケジューラの刻み（15.625ms）単位でしか積まれないため、1 周が
/// 数マイクロ秒のループは何百周しても「変化なし」に見える。コンテキストスイッチは
/// 1 回ずつ数えられるので、「一度も起こされていない」と「起こされてまた寝た」を
/// 分けられる。
///
/// 累積の数値なので、間を空けて 2 回読んで差を取る。
///
/// 配置は x64 のものを直書きしている。読み違えると意味のある数に見える値が出るので、
/// 自分のプロセスの入口でフィールドが噛み合うことを先に確かめ、合わなければ数を出さない。
/// </summary>
[NodeType("ngol.proc.thread_activity", "Process", "Thread Activity",
    Version = "1.0.0",
    Description =
        "Say which threads of this process are actually being run, by reading each thread's context switch "
      + "count and CPU time twice and reporting the difference. CPU time alone cannot answer this: it is "
      + "charged in scheduler ticks of about 15.6 ms, so a thread that wakes, does a few microseconds of work "
      + "and sleeps again shows no change at all. Context switches are counted one at a time, which separates "
      + "'never scheduled' from 'scheduled but charged no CPU'. Use it with ngol.proc.thread_stacks: that one "
      + "says where each thread sits, this one says whether it is moving, and together they cut a pool of "
      + "identical waiters down to the few threads worth reading. A rising switch count is not proof of "
      + "progress, only that the scheduler ran the thread - a thread woken and put straight back to sleep "
      + "counts the same as one doing work, which is why the CPU column is reported beside it.")]
[NodePort("window_ms", PortDirection.Input, "number", Description = "How long to wait between the two readings, in milliseconds. Default 1000. The node does not return until the window has passed, so keep it short inside a graph. Shorter windows make an occasional wake-up easy to miss")]
[NodePort("thread_id", PortDirection.Input, "number", Description = "Look at one thread only. 0 = every thread. Use it after a broad pass to watch a single suspect")]
[NodePort("top", PortDirection.Input, "number", Description = "How many threads to print, most switches first. Default 12. Threads that did not move are printed after the ones that did")]
[NodePort("supported", PortDirection.Output, "boolean", Description = "false when the counters could not be read here. The counts below are then meaningless rather than zero, and result says why")]
[NodePort("thread_count", PortDirection.Output, "number", Description = "How many threads were read, excluding the one running this node")]
[NodePort("moving_count", PortDirection.Output, "number", Description = "How many threads were scheduled at least once during the window")]
[NodePort("running_count", PortDirection.Output, "number", Description = "How many threads were also charged CPU time. Fewer than moving_count means the rest woke and went back to sleep")]
[NodePort("busiest_thread", PortDirection.Output, "number", Description = "Id of the thread with the most switches. 0 when nothing moved")]
[NodePort("threads", PortDirection.Output, "string", Description = "One line per thread: id, switches gained, CPU gained, and which of the three states it is in")]
[NodePort("result", PortDirection.Output, "string", Description = "Summary, or the reason nothing could be read")]
public sealed class ThreadActivityNode : INode
{
    // SystemProcessInformation。プロセスごとの可変長の並びが返る。
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004u;

    // x64 の配置。SYSTEM_PROCESS_INFORMATION の固定部は 0x100 で、その直後に
    // SYSTEM_THREAD_INFORMATION が NumberOfThreads 個並ぶ。
    private const int ProcNextEntryOffset = 0x00;
    private const int ProcNumberOfThreads = 0x04;
    private const int ProcUniqueProcessId = 0x50;
    private const int ProcThreadArray = 0x100;

    private const int ThreadEntrySize = 0x50;
    private const int ThreadKernelTime = 0x00;
    private const int ThreadUserTime = 0x08;
    private const int ThreadUniqueProcess = 0x28;
    private const int ThreadUniqueThread = 0x30;
    private const int ThreadContextSwitches = 0x40;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    private struct Sample
    {
        public uint Switches;
        public long HundredNs;
    }

    public void Execute(IExecutionContext ctx)
    {
        var windowMs = (int)(ctx.GetPortValue("window_ms") is double w ? w : 1000.0);
        var onlyThread = (int)(ctx.GetPortValue("thread_id") is double t ? t : 0.0);
        var top = (int)(ctx.GetPortValue("top") is double n ? n : 12.0);
        if (windowMs < 1) windowMs = 1;
        if (top < 1) top = 1;

        ctx.SetPortValue("supported", false);
        ctx.SetPortValue("thread_count", 0.0);
        ctx.SetPortValue("moving_count", 0.0);
        ctx.SetPortValue("running_count", 0.0);
        ctx.SetPortValue("busiest_thread", 0.0);
        ctx.SetPortValue("threads", "");

        if (IntPtr.Size != 8)
        {
            ctx.SetPortValue("result",
                "the field offsets used here are the 64-bit ones and this process is 32-bit, so nothing was "
                + "read. Reading with the wrong offsets would produce numbers that look ordinary and are wrong");
            return;
        }

        // System.Diagnostics.Process はホストによって参照集合に無いので OS から直接取る。
        var pid = GetCurrentProcessId();
        var self = GetCurrentThreadId();

        if (!TryRead(pid, out var first, out var why))
        {
            ctx.SetPortValue("result", why);
            return;
        }
        Thread.Sleep(windowMs);
        if (!TryRead(pid, out var second, out why))
        {
            ctx.SetPortValue("result", why);
            return;
        }

        var rows = new List<(int Tid, long Switches, double Ms)>();
        foreach (var kv in second)
        {
            if (kv.Key == self) continue;
            if (onlyThread != 0 && kv.Key != onlyThread) continue;
            if (!first.TryGetValue(kv.Key, out var before)) continue;   // 窓の途中で生まれた
            var sw = (long)kv.Value.Switches - before.Switches;
            var ms = (kv.Value.HundredNs - before.HundredNs) / 10000.0;
            rows.Add((kv.Key, sw < 0 ? 0 : sw, ms < 0 ? 0 : ms));
        }

        if (rows.Count == 0)
        {
            ctx.SetPortValue("supported", true);
            ctx.SetPortValue("result", onlyThread != 0
                ? "thread " + onlyThread + " was not in this process on both readings"
                : "no thread was present on both readings, which should not happen; the read may be wrong");
            return;
        }

        rows.Sort((a, b) => b.Switches != a.Switches
            ? b.Switches.CompareTo(a.Switches)
            : b.Ms.CompareTo(a.Ms));

        var moving = 0;
        var running = 0;
        foreach (var r in rows)
        {
            if (r.Switches > 0) moving++;
            if (r.Switches > 0 && r.Ms > 0) running++;
        }

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var r in rows)
        {
            if (shown++ >= top) break;
            sb.Append("tid=").Append(r.Tid)
              .Append("  switches +").Append(r.Switches)
              .Append("  cpu +").Append(Math.Round(r.Ms, 2)).Append(" ms  ")
              .Append(State(r.Switches, r.Ms)).Append('\n');
        }
        if (rows.Count > shown) sb.Append("... ").Append(rows.Count - shown).Append(" more\n");

        ctx.SetPortValue("supported", true);
        ctx.SetPortValue("thread_count", (double)rows.Count);
        ctx.SetPortValue("moving_count", (double)moving);
        ctx.SetPortValue("running_count", (double)running);
        ctx.SetPortValue("busiest_thread", rows[0].Switches > 0 ? (double)rows[0].Tid : 0.0);
        ctx.SetPortValue("threads", sb.ToString());
        ctx.SetPortValue("result",
            rows.Count + " thread(s) over " + windowMs + " ms: " + moving + " scheduled, "
            + running + " of those charged CPU. CPU is charged in ticks of about 15.6 ms, so a thread can be "
            + "scheduled and still show none");
    }

    private static string State(long switches, double ms)
    {
        if (switches <= 0) return "not scheduled";
        if (ms <= 0) return "scheduled, no CPU charged";
        return "running";
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentProcessId();

    /// <summary>
    /// 自分のプロセスの分だけ取り出す。フィールドが噛み合わない配置で読むと、意味のある数に
    /// 見える値がいくらでも出るので、スレッドの持ち主が全部自分であることを先に確かめる。
    /// </summary>
    private static bool TryRead(int pid, out Dictionary<int, Sample> map, out string why)
    {
        map = new Dictionary<int, Sample>();
        why = "";

        var size = 1 << 20;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = NtQuerySystemInformation(
                    SystemProcessInformation, buffer, size, out var needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != StatusInfoLengthMismatch)
                {
                    why = "the system information call refused with status 0x" + status.ToString("x8");
                    return false;
                }
                size = needed > size ? needed + (1 << 16) : size * 2;
            }
            if (buffer == IntPtr.Zero)
            {
                why = "the buffer never grew enough to hold the process list";
                return false;
            }

            var entry = buffer;
            while (true)
            {
                var owner = Marshal.ReadIntPtr(entry, ProcUniqueProcessId).ToInt64();
                var next = Marshal.ReadInt32(entry, ProcNextEntryOffset);
                if (owner == pid)
                {
                    var count = Marshal.ReadInt32(entry, ProcNumberOfThreads);
                    if (count <= 0 || count > 100000)
                    {
                        why = "this process reported " + count + " thread(s), which does not fit the layout "
                            + "being read; no numbers are produced rather than wrong ones";
                        return false;
                    }
                    for (var i = 0; i < count; i++)
                    {
                        var th = IntPtr.Add(entry, ProcThreadArray + i * ThreadEntrySize);
                        var ownerOfThread = Marshal.ReadIntPtr(th, ThreadUniqueProcess).ToInt64();
                        if (ownerOfThread != pid)
                        {
                            why = "thread entry " + i + " says it belongs to process " + ownerOfThread
                                + " rather than " + pid + ", so the layout being read does not match this "
                                + "system; no numbers are produced rather than wrong ones";
                            return false;
                        }
                        var tid = (int)Marshal.ReadIntPtr(th, ThreadUniqueThread).ToInt64();
                        map[tid] = new Sample
                        {
                            Switches = (uint)Marshal.ReadInt32(th, ThreadContextSwitches),
                            HundredNs = Marshal.ReadInt64(th, ThreadKernelTime)
                                      + Marshal.ReadInt64(th, ThreadUserTime),
                        };
                    }
                    return true;
                }
                if (next == 0) break;
                entry = IntPtr.Add(entry, next);
            }
            why = "this process was not found in the system's process list";
            return false;
        }
        catch (Exception ex)
        {
            why = "reading the process list failed: " + ex.Message;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }
}
