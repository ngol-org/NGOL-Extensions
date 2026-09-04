using NodeGraphModLab.NodeAPI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;

namespace NodeGraphModLab.CustomNodes;

// RVA 指定ネイティブフックノード - 外部のフックツール相当を NGOL 内で実現。
// 指定モジュールのベースアドレス + RVA で絶対アドレスを計算しフックを設置する。
// Stopwatch 使用で ~0.1us 精度のインターバル計測が可能。
//
// module は任意のモジュール名を指定できる。省略時はプロセスの主モジュールを対象にする。
//
// 出力: call_count(累計呼び出し回数) / interval_ms(直近間隔) / fps_estimate /
//       last_this・last_arg0(直近引数) / abs_ptr(計算済み絶対アドレス) / status
//
// 主な使い方:
//   呼び出し頻度・間隔・順序を高精度(Stopwatch)で計測したい場合に使う。
//   フック delegate は `orig.Invoke(a0,a1,a2)` の結果をそのまま返しているだけなので、
//   戻り値を条件次第で差し替えることも技術的には可能（この計測用の実装では使っていない）。
//
// 制約:
//   関数先頭以外の RVA を指定するとプロセスごと落ちる。必ず ngol.code.disasm や
//   ngol.hook.safety_check で先頭バイト・命令パターンを確認してから使うこと。
//
//   フックの本体はマネージドのコードなので、呼び出しのたびに実行環境の入口を通る。
//   ネイティブ側で数えるだけの ngol.hook.watch_function より 1 回あたりが重く、
//   実行環境そのものの状態にも左右される。回数と引数を見るだけなら向こうを使う。
//
//   実行環境を知らないスレッドから呼ばれても、その入口で実行環境が自分へ
//   結び付けるので、こちらでスレッドを登録する必要は無い。
//
//   標準的でないホストでは、フックの生成そのものが失敗することがある。
//   本番の対象に当てる前に、軽量な使い捨てフックで生成が完走するか確かめること。
[NodeType("ngol.hook.native_callback", "Hook", "Native Callback",
    Version = "1.1.3",
    Description =
        "Hook a native address so a managed callback runs on every call, recording how often it is reached and how "
      + "long between calls (Stopwatch precision). Observing is the default and leaves the target's behaviour alone. "
      + "Setting call_original=false changes what the target does: the original function no longer runs at all and "
      + "return_value is handed back in its place - everything downstream of that function sees a result it never "
      + "produced. The callback body is managed code, so every call goes through the runtime's entry "
      + "thunk: heavier per call than ngol.hook.watch_function, which counts on the native side. The runtime attaches "
      + "the calling thread there by itself, so a thread it has never seen needs nothing registered. Use "
      + "watch_function when the count and the arguments are all you need. Only the entry of a function can be hooked.")]
[NodePort("rva",      PortDirection.Input,  "string", Description = "RVA hex (e.g. '0x12340'). Only the entry of a function can be hooked - anything else takes the process down")]
[NodePort("module",  PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("enabled",      PortDirection.Input,  "boolean", Description = "true = install the hook, false = remove it")]
[NodePort("call_original", PortDirection.Input, "boolean", Description = "true (default) = observe only, the original always runs. false = stop the original and return return_value instead. Can be toggled while the hook is installed")]
[NodePort("return_value",  PortDirection.Input, "string",  Description = "Value handed back when call_original=false, as hex (default 0). Integer/pointer width only - floating-point returns use a different register and are not covered")]
[NodePort("call_original_active", PortDirection.Output, "boolean", Description = "Whether the original is currently being called")]
[NodePort("return_value_used",    PortDirection.Output, "string",  Description = "The value currently handed back when the original is skipped, as hex")]
[NodePort("call_count",   PortDirection.Output, "number", Description = "Calls seen since the hook was installed, cumulative")]
[NodePort("interval_ms",  PortDirection.Output, "number", Description = "Milliseconds between the last two calls, measured with Stopwatch. 0 until the second call arrives")]
[NodePort("fps_estimate", PortDirection.Output, "number", Description = "1000 / interval_ms. 0 until the second call arrives")]
[NodePort("last_this",    PortDirection.Output, "string", Description = "First argument (RCX) of the most recent call, as hex. Named after the usual case where it is the this pointer")]
[NodePort("last_arg0",    PortDirection.Output, "string", Description = "Second argument (RDX) of the most recent call, as hex")]
[NodePort("abs_ptr",      PortDirection.Output, "string", Description = "The address the hook was placed at, as hex: the module's load address plus rva")]
[NodePort("status",       PortDirection.Output, "string", Description = "hooked @ <address> / removed / idle, or the reason the hook could not be placed")]
public class NativeRvaHookNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string name);

    // 対象関数のシグネチャ。フックから元の実装を呼び戻すときにも使う。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr HookDelegate(IntPtr a0, IntPtr a1, IntPtr a2);

    // フック本体。第 1 引数で元の実装（正確には検出チェーンの次）を受け取る。
    // トランポリンを自分で生成する必要はない。
    delegate IntPtr HookWithOriginal(HookDelegate orig, IntPtr a0, IntPtr a1, IntPtr a2);

    // -- AppDomain 永続ストレージ（ホットリロード後も生存） ------------------

    static T GetOrCreate<T>(string key, Func<T> factory) where T : class
    {
        var v = AppDomain.CurrentDomain.GetData(key) as T;
        if (v == null) { v = factory(); AppDomain.CurrentDomain.SetData(key, v); }
        return v;
    }

    static System.Collections.Generic.HashSet<long> HookedSet =>
        GetOrCreate("NgolRvaHook_hooked", () => new System.Collections.Generic.HashSet<long>());

    static System.Collections.Generic.List<GCHandle> GcHandles =>
        GetOrCreate("NgolRvaHook_gchandles", () => new System.Collections.Generic.List<GCHandle>());

    static System.Collections.Concurrent.ConcurrentDictionary<long, long> Counters =>
        GetOrCreate("NgolRvaHook_counters", () => new System.Collections.Concurrent.ConcurrentDictionary<long, long>());

    // (lastA0, lastA1, lastTimestamp_ticks, intervalTicks)
    static System.Collections.Concurrent.ConcurrentDictionary<long, (long, long, long, long)> LastData =>
        GetOrCreate("NgolRvaHook_lastdata", () => new System.Collections.Concurrent.ConcurrentDictionary<long, (long, long, long, long)>());

    static System.Collections.Concurrent.ConcurrentDictionary<long, string> Statuses =>
        GetOrCreate("NgolRvaHook_statuses", () => new System.Collections.Concurrent.ConcurrentDictionary<long, string>());

    // (callOriginal, returnValue)。フック本体から毎回引くので、設置し直さずに切り替えられる。
    //   ConcurrentDictionary はどのスレッドから読んでも安全。
    static System.Collections.Concurrent.ConcurrentDictionary<long, (bool, long)> Behaviors =>
        GetOrCreate("NgolRvaHook_behaviors", () => new System.Collections.Concurrent.ConcurrentDictionary<long, (bool, long)>());

    static System.Collections.Concurrent.ConcurrentDictionary<long, NativeHook> Detours =>
        GetOrCreate("NgolRvaHook_detours", () => new System.Collections.Concurrent.ConcurrentDictionary<long, NativeHook>());

    // -- フック設置 ------------------------------------------------------------

    static void InstallHook(long absPtr)
    {
        if (HookedSet.Contains(absPtr)) return;

        HookWithOriginal hook = (orig, a0, a1, a2) =>
        {
            Counters.AddOrUpdate(absPtr, 1L, (_, v) => v + 1);
            long now = Stopwatch.GetTimestamp();
            LastData.AddOrUpdate(absPtr,
                ((long)a0, (long)a1, now, 0L),
                (_, prev) => ((long)a0, (long)a1, now, now - prev.Item3));

            // 元を呼ぶかどうかは毎回引く（設置し直さずに切り替えられるようにするため）。
            //   元を呼ばないなら、呼び出し元が受け取る値をこちらで決めるしかない。
            //     決めないと戻り値レジスタの中身が不定になる。
            if (Behaviors.TryGetValue(absPtr, out var behavior) && !behavior.Item1)
                return (IntPtr)behavior.Item2;

            // 元の実装はフックがスタックに載っている間だけ呼べる。
            //    後で呼ぶために orig を持ち出さないこと。
            return orig(a0, a1, a2);
        };

        // 委譲がガベージコレクションで回収されると、フック先が消えてプロセスごと落ちる。
        GcHandles.Add(GCHandle.Alloc(hook, GCHandleType.Normal));

        var detour = new NativeHook((IntPtr)absPtr, hook);

        Detours[absPtr]  = detour;
        HookedSet.Add(absPtr);
        Statuses[absPtr] = $"hooked @ 0x{absPtr:X}";
    }

    static long ParseHexOrZero(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? unchecked((long)v) : 0L;
    }

    // -- Execute --------------------------------------------------------------

    public void Execute(IExecutionContext ctx)
    {
        var rvaHex  = ((string?)ctx.GetPortValue("rva") ?? "").Trim();
        var modName = ((string?)ctx.GetPortValue("module") ?? "").Trim();
        var enabled = Convert.ToBoolean(ctx.GetPortValue("enabled") ?? true);

        modName = NgolModuleDefault.Resolve(modName);

        long rva = 0;
        try
        {
            var hex = rvaHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rvaHex[2..] : rvaHex;
            rva = Convert.ToInt64(hex, 16);
        }
        catch
        {
            ctx.SetPortValue("status", $"Invalid rva: '{rvaHex}'");
            return;
        }
        if (rva == 0) { ctx.SetPortValue("status", "rva is zero"); return; }

        var modHandle = GetModuleHandleA(modName);
        if (modHandle == IntPtr.Zero)
        {
            ctx.SetPortValue("status", $"Module not found: '{modName}'");
            return;
        }

        long absPtr = (long)modHandle + rva;
        ctx.SetPortValue("abs_ptr", $"0x{absPtr:X}");

        if (!enabled)
        {
            if (Detours.TryRemove(absPtr, out var d))
            {
                try { d.Dispose(); } catch { }
                HookedSet.Remove(absPtr);
                Behaviors.TryRemove(absPtr, out _);
                Statuses[absPtr] = "removed";
            }
            ctx.SetPortValue("call_original_active", true);
            ctx.SetPortValue("return_value_used", "0x0");
            ctx.SetPortValue("status", Statuses.TryGetValue(absPtr, out var s0) ? s0 : "idle");
            return;
        }

        // 設置より先に決めておく。フック本体はここを毎回引くので、
        //   設置と同時に呼ばれても中途半端な状態を見ない。
        var callOriginal = ctx.GetPortValue("call_original") is bool co ? co : true;
        var returnValue  = ParseHexOrZero((string?)ctx.GetPortValue("return_value"));
        Behaviors[absPtr] = (callOriginal, returnValue);
        ctx.SetPortValue("call_original_active", callOriginal);
        ctx.SetPortValue("return_value_used", $"0x{returnValue:X}");

        try { InstallHook(absPtr); }
        catch (Exception ex)
        {
            var msg = $"HOOK FAILED: {ex.GetType().Name}: {ex.Message}";
            Statuses[absPtr] = msg;
            ctx.SetPortValue("status", msg);
            return;
        }

        var count = Counters.TryGetValue(absPtr, out var c) ? c : 0L;
        var data  = LastData.TryGetValue(absPtr, out var d2) ? d2 : (0L, 0L, 0L, 0L);
        double intervalMs = data.Item4 > 0 ? data.Item4 * 1000.0 / Stopwatch.Frequency : 0.0;
        double fps        = intervalMs > 0.0001 ? 1000.0 / intervalMs : 0.0;

        ctx.SetPortValue("call_count",   (double)count);
        ctx.SetPortValue("interval_ms",  intervalMs);
        ctx.SetPortValue("fps_estimate", fps);
        ctx.SetPortValue("last_this",    $"0x{data.Item1:X}");
        ctx.SetPortValue("last_arg0",    $"0x{data.Item2:X}");
        ctx.SetPortValue("status",       Statuses.TryGetValue(absPtr, out var st) ? st : "idle");
    }
}
