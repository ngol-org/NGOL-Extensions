using NodeGraphModLab.NodeAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace NodeGraphModLab.CustomNodes;

// クラスのメソッド別所要時間プローブ。
//
// 指定したクラスの全メソッドの IL に入口・出口の計測を差し込み、
// メソッドごとの「呼び出し回数 / 合計時間 / 最大時間 / 初回呼び出し時刻」を集計する。
//
// メソッド単位の detour ではなく IL 書き換えを使う理由は、
// 対象を実行時に名前で見つけるためシグネチャが分からないこと。
// シグネチャ一致のデリゲートを要求する方式は、この用途には使えない。
//
// 用途:
//   「この処理のどこが重いのか」を、対象アプリのソースを読まずに絞り込む。
//   1メソッドずつ手で計測を仕込むより早く、当たりを付けてから深掘りできる。
//   初回呼び出し時刻が並ぶので、処理の順序（どのメソッドがいつ動くか）も同時に分かる。
//
// 観測専用:
//   元の命令列はそのまま残し、入口と各 ret の直前に計測呼び出しを足すだけ。処理内容は変えない。
//
// 例外で抜けたときは出口の計測が走らない（その呼び出しは回数・時間に計上されない）。
//    入口の記録は残るので、初回呼び出し時刻には現れる。
//
// -------------------------------------------------------------
//  全メソッド一括パッチで踏みやすい罠（このノードで対策済み）
// -------------------------------------------------------------
//
// (1) プロパティのアクセサ等（IsSpecialName）を必ず除外する
//     ランタイムによっては、フィールドのアクセサを本来の仕組みでパッチできず、
//     壊れたトランポリンを生成する経路へ落ちる。そこへ入るとプロセスごと落ちる。
//     ログに「パッチできなかった」と出るとは限らない--
//     「別の方式でパッチした」という体裁の警告になることがある。
//     そもそもアクセサの所要時間を測っても得るものが無い。
//
// (2) enabled=false の解除が効かない構造を避ける
//     起動直後は型解決が間に合わないことがあるため OnUpdate でリトライするが、
//     「張ったか」フラグだけで判定すると、解除の直後にリトライが張り直してしまう。
//     停止の意思を別フラグ（s_stopped）で持つこと。
//     解除できたかはログに設置メッセージが再度出ていないかで確認する。
//
// (3) 毎フレーム呼ばれるメソッドは既定で除外する
//     Update / LateUpdate 等を含めると集計の上位がそれで埋まり、
//     本当に見たい処理が埋もれる。exclude ポートで調整できる。
//
// -------------------------------------------------------------
// 読み方の注意
// -------------------------------------------------------------
//
//   ・所要時間は inclusive（呼び出し階層の内側も含む）。入れ子があると合計は重複する。
//     内訳を見るときは呼び出し回数と最大値を併せて見ること。
//   ・非同期メソッド（async / コルーチン）で計測できるのは
//     最初の中断までの同期部分だけ。完了までの時間ではない。
//   ・「合計が大きい」には2種類ある。1回が重いのか、回数が多いのか。
//     回数と最大値を見れば区別できる（毎フレーム呼ばれるものは回数が跳ね上がる）。
[NodeType("ngol.hook.managed_timing", "Hook", "Class Method Timing Probe",
    Version = "1.0.2",
    Description = "Insert entry/exit timing into the IL of every method of a class, and aggregate per-method call count, elapsed time and first-call time. Observation only - the original code still runs unchanged.")]
[NodePort("typeName", PortDirection.Input, "string",
    Description = "Class to measure, as a simple name without the namespace. Every loaded assembly is searched by name, because some types have no namespace at all")]
[NodePort("enabled", PortDirection.Input, "boolean",
    Description = "true = start measuring (default). false = remove the patches and stop. What was collected up to then is still reported in result")]
[NodePort("exclude", PortDirection.Input, "string",
    Description = "Method names to leave out, comma separated. Default is Update,LateUpdate,FixedUpdate,OnGUI - they run every frame and would bury everything else")]
[NodePort("armed", PortDirection.Output, "boolean",
    Description = "Whether the patches are currently in place")]
[NodePort("patchedCount", PortDirection.Output, "number",
    Description = "How many methods could actually be patched")]
[NodePort("result", PortDirection.Output, "string",
    Description = "Per-method totals, ordered by elapsed time")]
[NodePort("timeline", PortDirection.Output, "string",
    Description = "The same methods ordered by when they were first called, which shows what runs in what order")]
public class ClassMethodTimingNode : INode
{
    const string DefaultExclude = "Update,LateUpdate,FixedUpdate,OnGUI";

    // 設置した書き換えの解除用。
    // ホットリロードで static が消えると解除できなくなる。参照が失われた書き換えは
    //    GC で回収されるときに解除されるため、それまでは二重に計測されうる。
    static readonly List<ILHook> s_hooks = new List<ILHook>();

    static bool s_pumpRegistered;
    static bool s_installed;
    // enabled=false で明示的に停止された。永続 OnUpdate はこれを見て張り直しを止める。
    static bool s_stopped;
    static int s_retryFrames;
    static string s_typeName = "";
    static string[] s_exclude = new string[0];
    static readonly object s_lock = new object();

    public void Execute(IExecutionContext ctx)
    {
        bool enabled = ctx.GetPortValue("enabled") as bool? ?? true;

        if (!enabled)
        {
            s_stopped = true;
            ReleaseHooks();
            s_installed = false;
            ctx.SetPortValue("armed", false);
            ctx.SetPortValue("patchedCount", 0);
            ctx.SetPortValue("result", MethodTimingStore.BuildReport());
            ctx.SetPortValue("timeline", MethodTimingStore.BuildTimeline());
            return;
        }

        s_typeName = (ctx.GetPortValue("typeName") as string ?? "").Trim();
        if (s_typeName.Length == 0)
        {
            ctx.SetPortValue("armed", false);
            ctx.SetPortValue("patchedCount", 0);
            ctx.SetPortValue("result", "typeName is empty - give the class name to measure");
            ctx.SetPortValue("timeline", "");
            return;
        }

        var ex = (ctx.GetPortValue("exclude") as string ?? "").Trim();
        if (ex.Length == 0) ex = DefaultExclude;
        s_exclude = ex.Split(',');
        for (int i = 0; i < s_exclude.Length; i++) s_exclude[i] = s_exclude[i].Trim();

        s_stopped = false;
        s_installed = false;
        s_retryFrames = 0;

        // 起動直後は対象の型がまだ解決できないことがあるため、解決できるまでリトライする。
        if (!s_pumpRegistered)
        {
            ctx.RegisterPersistent(new PersistentCallbacks { OnUpdate = () => TryInstall(ctx) });
            s_pumpRegistered = true;
        }

        TryInstall(ctx);

        ctx.SetPortValue("armed", s_installed);
        ctx.SetPortValue("patchedCount", MethodTimingStore.PatchedCount);
        ctx.SetPortValue("result", MethodTimingStore.BuildReport());
        ctx.SetPortValue("timeline", MethodTimingStore.BuildTimeline());
    }

    // Execute()（ワーカースレッド）と OnUpdate（メインスレッド）の両方から呼ばれるため、
    // 排他しないと両方が s_installed のチェックを通り抜けて二重に設置しようとする。
    static void TryInstall(IExecutionContext ctx)
    {
        lock (s_lock) { TryInstallLocked(ctx); }
    }

    static void TryInstallLocked(IExecutionContext ctx)
    {
        if (s_stopped || s_installed) return;

        s_retryFrames++;
        var types = FindTypesBySimpleName(s_typeName);
        if (types.Count == 0)
        {
            // 60フレームに1回だけ残す（ログを埋めないため）
            if (s_retryFrames % 60 == 1)
                ctx.Logger.LogInfo($"[MethodTiming] waiting for type '{s_typeName}' ... (frames={s_retryFrames})");
            return;
        }

        MethodTimingStore.Reset();
        // 同じセッション内で張り直す場合に二重計測にならないよう、先に解除する。
        ReleaseHooks();

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        int ok = 0;
        var skipped = new StringBuilder();
        foreach (var type in types)
        foreach (var m in type.GetMethods(flags))
        {
            // プロパティのアクセサ等は必ず除外する（冒頭コメント (1) 参照）。
            //    ランタイムによってはアクセサを本来の仕組みでパッチできず、
            //    危険な経路へ落ちてプロセスごと落ちることがある。
            if (m.IsSpecialName) continue;
            if (m.IsAbstract || m.ContainsGenericParameters) continue;
            if (IsExcluded(m.Name)) continue;

            var key = m.Name;
            try
            {
                s_hooks.Add(new ILHook(m, il => MethodTimingStore.EmitTiming(il, key)));
                ok++;
            }
            catch (Exception ex) { skipped.Append($"{m.Name}({ex.GetType().Name}) "); }
        }

        MethodTimingStore.PatchedCount = ok;
        MethodTimingStore.StartClock();
        s_installed = ok > 0;

        var msg = $"ARMED - timing patched into {ok} method(s) of {types[0].FullName}"
                + $" ({types.Count} generation(s) of the type, all patched, frames={s_retryFrames})";
        if (skipped.Length > 0) msg += $" / could not patch: {skipped}";
        ctx.Logger.LogInfo("[MethodTiming] " + msg);
    }

    static void ReleaseHooks()
    {
        foreach (var h in s_hooks)
        {
            try { h.Dispose(); } catch { }
        }
        s_hooks.Clear();
    }

    static bool IsExcluded(string name)
    {
        foreach (var e in s_exclude)
            if (e.Length > 0 && string.Equals(e, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // 名前空間を持たない型が存在するため、Assembly.GetType(fullName) では取りこぼす。
    // 全アセンブリの型を単純名で走査する。
    //
    // ホットリロードすると同名の型が世代の数だけ存在する。最初に見つかったものだけを
    //   掴むと、一度も実行されていない世代にパッチを当てて「設置できたのに 1 件も
    //   計上されない」状態になる。armed=true・patchedCount>0 が返るため、
    //   設置に失敗したようには見えない。見つかった世代すべてに設置する。
    static List<Type> FindTypesBySimpleName(string simpleName)
    {
        var found = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
                if (t.Name == simpleName) found.Add(t);
        }
        return found;
    }
}

// 計測値の入れ物。差し込む IL と、そこから呼ばれる本体もここに置く。
internal static class MethodTimingStore
{
    /// <summary>
    /// 入口に「開始時刻をローカルへ」、各 ret の直前に「経過を計上」を差し込む。
    ///
    /// `MoveType.AfterLabel` が要点。既定の `Before` は入ってくる分岐のラベルを
    ///    元の命令に残すため、`ret` へ分岐してくる経路が差し込んだ計測を飛び越してしまう。
    /// </summary>
    public static void EmitTiming(ILContext il, string key)
    {
        var tsLocal = new VariableDefinition(il.Method.Module.ImportReference(typeof(long)));
        il.Body.Variables.Add(tsLocal);
        il.Body.InitLocals = true;

        var c = new ILCursor(il);

        c.Goto(0, MoveType.AfterLabel);
        c.Emit(OpCodes.Ldstr, key);
        c.EmitDelegate<Func<string, long>>(Enter);
        c.Emit(OpCodes.Stloc, tsLocal);

        while (c.TryGotoNext(MoveType.AfterLabel, i => i.MatchRet()))
        {
            c.Emit(OpCodes.Ldstr, key);
            c.Emit(OpCodes.Ldloc, tsLocal);
            c.EmitDelegate<Action<string, long>>(Exit);
            c.Index++;   // 今扱った ret を飛び越す。しないと同じ ret を拾い続ける
        }
    }

    internal sealed class Stat
    {
        public int Calls;
        public double TotalMs;
        public double MaxMs;
        public double FirstEnterMs = -1;
        public double LastExitMs;
    }

    public static int PatchedCount;

    static readonly Stopwatch s_clock = new Stopwatch();
    static readonly object s_lock = new object();
    static readonly Dictionary<string, Stat> s_stats = new Dictionary<string, Stat>();

    public static void StartClock()
    {
        if (!s_clock.IsRunning) s_clock.Restart();
    }

    public static void Reset()
    {
        lock (s_lock)
        {
            s_stats.Clear();
            s_clock.Reset();
        }
    }

    /// <summary>入口。開始時刻を返し、呼び出し側（差し込んだ IL）がローカルに持つ。</summary>
    public static long Enter(string name)
    {
        try
        {
            lock (s_lock)
            {
                if (!s_clock.IsRunning) s_clock.Restart();
                var st = Get(name);
                if (st.FirstEnterMs < 0) st.FirstEnterMs = s_clock.Elapsed.TotalMilliseconds;
            }
        }
        catch { }
        // 計測開始は最後に取る。上の記録にかかった時間を対象の時間に混ぜないため。
        return Stopwatch.GetTimestamp();
    }

    /// <summary>出口。入口で得た開始時刻との差を計上する。</summary>
    public static void Exit(string name, long state)
    {
        try
        {
            double ms = (Stopwatch.GetTimestamp() - state) * 1000.0 / Stopwatch.Frequency;
            lock (s_lock)
            {
                var st = Get(name);
                st.Calls++;
                st.TotalMs += ms;
                if (ms > st.MaxMs) st.MaxMs = ms;
                st.LastExitMs = s_clock.Elapsed.TotalMilliseconds;
            }
        }
        catch { }
    }

    static Stat Get(string name)
    {
        Stat st;
        if (!s_stats.TryGetValue(name, out st)) { st = new Stat(); s_stats[name] = st; }
        return st;
    }

    public static string BuildReport()
    {
        lock (s_lock)
        {
            if (s_stats.Count == 0)
                return "[MethodTiming] nothing has been called yet - run the target's work once";

            var list = new List<KeyValuePair<string, Stat>>(s_stats);
            list.Sort((a, b) => b.Value.TotalMs.CompareTo(a.Value.TotalMs));

            double sum = 0, lastExit = 0;
            foreach (var kv in list)
            {
                sum += kv.Value.TotalMs;
                if (kv.Value.LastExitMs > lastExit) lastExit = kv.Value.LastExitMs;
            }

            var sb = new StringBuilder();
            sb.Append("[MethodTiming] by elapsed time (inclusive - nested calls are counted in both)\n");
            sb.Append($"  plain sum={sum:F1}ms / last exit=+{lastExit:F0}ms\n");
            foreach (var kv in list)
            {
                var s = kv.Value;
                sb.Append($"  {kv.Key,-28} total={s.TotalMs,9:F1}ms  calls={s.Calls,5}  "
                        + $"max={s.MaxMs,8:F1}ms  first=+{s.FirstEnterMs:F0}ms\n");
            }
            return sb.ToString();
        }
    }

    public static string BuildTimeline()
    {
        lock (s_lock)
        {
            if (s_stats.Count == 0) return "";
            var list = new List<KeyValuePair<string, Stat>>(s_stats);
            list.Sort((a, b) => a.Value.FirstEnterMs.CompareTo(b.Value.FirstEnterMs));

            var sb = new StringBuilder("[MethodTiming] in order of first call\n");
            foreach (var kv in list)
                sb.Append($"  +{kv.Value.FirstEnterMs,8:F0}ms  {kv.Key}\n");
            return sb.ToString();
        }
    }
}
