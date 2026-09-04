using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 走っているスレッドを何度も止めては rip を見て、目当ての範囲に居るときだけ値を読む。
///
/// 呼び出し側のスタック上に置かれた一時的な値（戻り値・構造体・出力引数）は、
/// 呼び出しの合間に別の用途で使い回される。そのまま覗くと関係ない値が大量に混ざり、
/// 正しい値が埋もれる。「その関数の中に居る」という条件を付けると混ざりが落ちる。
///
/// フックと違って対象の命令を書き換えないので、フックを張れない相手にも使える。
/// 代わりに当たりは確率で、滞在の短い経路ほど当たりにくい。
/// </summary>
[NodeType("ngol.mem.sample_in_function", "Memory", "Sample In Function",
    Version = "1.0.0",
    Description =
        "Read a value over and over, but keep only the reads taken while a given thread is executing inside a "
      + "given address range. Values that live on the caller's stack - a return code, an output argument, a "
      + "temporary struct - are reused between calls, so reading the address directly returns mostly unrelated "
      + "values; requiring the thread to be inside the function removes them. Nothing in the target is "
      + "modified, so this works where a hook cannot be installed. Catching is a matter of chance: a path that "
      + "returns immediately is occupied for so short a time that many attempts find nothing. Before raising "
      + "the attempts, widen the range to a whole module as a positive control - if that catches nothing "
      + "either, the thread or the address is wrong rather than the value being rare.")]
[NodePort("thread_id", PortDirection.Input, "number", Description = "The thread to sample. Get it from ngol.proc.thread_stacks. Required")]
[NodePort("range_start_hex", PortDirection.Input, "string", Description = "Absolute start of the range the thread must be inside, hex. Use a function's entry, from ngol.code.pdb_lookup plus ngol.code.module_base. Required")]
[NodePort("range_end_hex", PortDirection.Input, "string", Description = "Absolute end of the range, exclusive, hex. A function's end comes from ngol.code.function_bounds. Required")]
[NodePort("address_hex", PortDirection.Input, "string", Description = "Absolute address of the value to read while inside. Required")]
[NodePort("value_bits", PortDirection.Input, "number", Description = "Width of the value: 32 or 64. Default 32")]
[NodePort("attempts", PortDirection.Input, "number", Description = "How many times to stop and look. Default 20000, maximum 200000. About 12 microseconds each")]
[NodePort("inside", PortDirection.Output, "number", Description = "How many attempts found the thread inside the range. 0 means nothing was caught, not that the value is absent")]
[NodePort("attempted", PortDirection.Output, "number", Description = "How many attempts actually ran")]
[NodePort("distinct", PortDirection.Output, "number", Description = "How many different values were read while inside. 1 is the answer you want; several means the range is too wide")]
[NodePort("top_value", PortDirection.Output, "number", Description = "The value read most often while inside, as a signed number")]
[NodePort("top_count", PortDirection.Output, "number", Description = "How many times the most frequent value was read")]
[NodePort("result", PortDirection.Output, "string", Description = "Every value seen while inside with its count, most frequent first, or why nothing was caught")]
public sealed class SampleInFunctionNode : INode
{
    private const uint THREAD_ACCESS = 0x0008 | 0x0002 | 0x0040;
    private const uint CONTEXT_AMD64 = 0x00100000, CONTEXT_CONTROL = CONTEXT_AMD64 | 0x1;
    private const int CONTEXT_SIZE = 1232, RIP_OFFSET = 0xF8, CONTEXT_FLAGS_OFFSET = 0x30;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResumeThread(IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr t, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public void Execute(IExecutionContext ctx)
    {
        var tid = (uint)(ctx.GetPortValue("thread_id") is double t ? t : 0.0);
        var bits = (int)(ctx.GetPortValue("value_bits") is double vb ? vb : 32.0);
        var attempts = (int)(ctx.GetPortValue("attempts") is double a ? a : 20000.0);
        if (attempts < 1) attempts = 1;
        if (attempts > 200000) attempts = 200000;
        if (bits != 64) bits = 32;

        ctx.SetPortValue("inside", 0.0);
        ctx.SetPortValue("attempted", 0.0);
        ctx.SetPortValue("distinct", 0.0);
        ctx.SetPortValue("top_value", 0.0);
        ctx.SetPortValue("top_count", 0.0);

        if (tid == 0
            || !TryHex(ctx.GetPortValue("range_start_hex") as string, out var low)
            || !TryHex(ctx.GetPortValue("range_end_hex") as string, out var high)
            || !TryHex(ctx.GetPortValue("address_hex") as string, out var readAt))
        {
            ctx.SetPortValue("result", "thread_id, range_start_hex, range_end_hex and address_hex are all required");
            return;
        }
        if (high <= low)
        {
            ctx.SetPortValue("result", "range_end_hex must be above range_start_hex");
            return;
        }
        if (tid == GetCurrentThreadId())
        {
            ctx.SetPortValue("result", "that is the thread running this node; stopping it would stop the sampling");
            return;
        }
        if (NgolSafeMemory.ReadableLength(new IntPtr(readAt), bits / 8) < bits / 8)
        {
            ctx.SetPortValue("result", "address_hex is not readable");
            return;
        }

        var h = OpenThread(THREAD_ACCESS, false, tid);
        if (h == IntPtr.Zero)
        {
            ctx.SetPortValue("result", "OpenThread err=" + Marshal.GetLastWin32Error());
            return;
        }

        var raw = Marshal.AllocHGlobal(CONTEXT_SIZE + 16);
        var aligned = new IntPtr((raw.ToInt64() + 15) & ~15L);
        var target = new IntPtr(readAt);
        var tally = new Dictionary<long, int>();
        int inside = 0, attempted = 0;
        try
        {
            for (var i = 0; i < attempts; i++)
            {
                for (var b = 0; b < CONTEXT_SIZE; b++) Marshal.WriteByte(aligned, b, 0);
                Marshal.WriteInt32(aligned, CONTEXT_FLAGS_OFFSET, unchecked((int)CONTEXT_CONTROL));
                if (SuspendThread(h) == uint.MaxValue) break;

                long value = 0;
                var hit = false;
                try
                {
                    if (GetThreadContext(h, aligned))
                    {
                        var rip = Marshal.ReadInt64(aligned, RIP_OFFSET);
                        if (rip >= low && rip < high)
                        {
                            // 止めている間に読むのは生の数値だけ。解釈は再開してから。
                            value = bits == 64 ? Marshal.ReadInt64(target) : Marshal.ReadInt32(target);
                            hit = true;
                        }
                    }
                }
                finally { ResumeThread(h); }

                attempted++;
                if (!hit) continue;
                inside++;
                tally.TryGetValue(value, out var n);
                tally[value] = n + 1;
            }
        }
        finally { Marshal.FreeHGlobal(raw); CloseHandle(h); }

        var ordered = new List<KeyValuePair<long, int>>(tally);
        ordered.Sort((x, y) => y.Value.CompareTo(x.Value));

        var sb = new StringBuilder();
        sb.Append(attempted).Append(" attempt(s), ").Append(inside)
          .Append(" inside 0x").Append(low.ToString("x")).Append("-0x").Append(high.ToString("x")).Append('\n');
        if (inside == 0)
            sb.Append("nothing was caught inside. Widen the range to the whole module as a positive control "
                    + "before raising attempts - if that catches nothing either, the thread or the range is wrong\n");
        var shown = 0;
        foreach (var kv in ordered)
        {
            if (shown++ >= 20) { sb.Append("    ... and ").Append(ordered.Count - 20).Append(" more\n"); break; }
            sb.Append("    ").Append(kv.Value.ToString().PadLeft(7)).Append("  ").Append(kv.Key)
              .Append("  (0x").Append((bits == 64 ? (ulong)kv.Key : (uint)kv.Key).ToString("x")).Append(")\n");
        }

        ctx.SetPortValue("inside", (double)inside);
        ctx.SetPortValue("attempted", (double)attempted);
        ctx.SetPortValue("distinct", (double)ordered.Count);
        if (ordered.Count > 0)
        {
            ctx.SetPortValue("top_value", (double)ordered[0].Key);
            ctx.SetPortValue("top_count", (double)ordered[0].Value);
        }
        ctx.SetPortValue("result", sb.ToString());
    }

    private static bool TryHex(string s, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
