using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// このノードは操作対象が「解析したいアプリ」ではなく NGOL 本体（永続ストアの層）である。
///   本体を再ビルドせずに、バーストを 1 つのトランザクションへまとめるよう実行時に差し替える。
///
/// 使い方（対象のノードは何も知らなくてよい）:
///   (1) このノードを enabled=true で実行  ... 包む
///   (2) 対象のノードを実行                ... 書き込みがバッチにまとめられる
///   (3) このノードを enabled=false で実行  ... 外す
///   （実行中の件数を見たいときは reportOnly=true。設置を変えずに今の値だけ返す。
///    これが無いと、件数を読むには (2) の途中で解除するしかなく、そこでバッチ化が止まる）
///
/// 何が速くなるか:
///   保存層は書き込み 1 件ごとに確定するため、バーストの多い処理では確定処理が支配的になる。
///   バッチにまとめると、確定の回数がバーストの件数ではなくバッチの数まで減る。
///
/// 包む相手は保存層であって、対象のノードではない。
///   保存層は固定の DLL なので、ホットリロードで増える世代を追う必要がない。
///
/// -- 注意（NGOL 本体を触るノード共通） ---------------
/// このノードは NGOL の非公開メンバへリフレクションでアクセスする。
/// 本体の実装が変わると動かなくなる可能性があるため、更新時は動作を確認すること。
/// 参照している非公開メンバ:
///   IKVStore の実装の _backend フィールド / そのバックエンドの _db フィールド
/// いずれも見つからない場合は何もせずその旨を返す（誤動作より不動作を選ぶ）。
///
/// 動作を確認した NGOL の版: 0.7.34-beta
/// 実行時に検出した版は result に出るので、手元の版と突き合わせられる。
///
/// バッチを開くのはホスト更新スレッドの書き込みだけ。
///   バッチはスレッドごとに 1 つで、開いたスレッド以外からは閉じられない。
///   しかも他のスレッドから確定を頼んでも、例外ではなく
///   「そのスレッドにはバッチが無い」として何もせずに返る--失敗が見えない形で残る。
///   ウォッチドッグはホスト更新スレッドで動くので、そこで開いたバッチは必ず閉じられる。
///   他のスレッドでバッチを開くと、そのスレッドが二度と書かなかった場合に
///   開いたまま残り、保存先が固まる。
///   => 「閉じられる場所でしか開かない」を構造で守る。
///   帰結として、バックグラウンドスレッドのバーストは速くならない（素通しになる）。
///
/// 使う前に読むこと:
///   - 途中で失敗すると、そのバッチのぶんの書き込みは取り消される。
///     一括で書いて失敗したらやり直す処理には無害だが、
///     1件ずつ確実に残したい処理（進捗の保存など）には向かない
///   - バッチにまとめているあいだ、他のスレッドの書き込みは待たされる
///   - 一括確定を持たない保存先（追記型・メモリ）では何もしない
/// </summary>
[NodeType("ngol.dev.kvstore_transaction_patch", "Dev", "KVStore Transaction Patch",
    Version = "1.0.2",
    Description =
        "Patch NGOL's own persistent-store layer at runtime so that bursts of writes are batched into transactions "
      + "instead of committing every single write. Operates on NGOL itself, not on the application being analysed. "
      + "USAGE (three steps, the target node needs no changes and does not know about this): "
      + "1) run this node with enabled=true, "
      + "2) run the node that writes a lot (e.g. an index build), "
      + "3) run this node again with enabled=false to remove it. "
      + "Only writes on the host update thread are batched, and only while they arrive closer together than burstGapMs. "
      + "A transaction can only be committed by the thread that opened it, so batching is restricted to the one thread "
      + "the watchdog also runs on - that way an open batch can always be forced closed and the store can never be left locked. "
      + "WARNING: if the target fails midway, the writes of that batch are rolled back - suitable for work that is "
      + "rebuilt from scratch on failure, not for progress that must survive. Other threads' writes wait while a batch "
      + "is open. Does nothing on stores without transactions. "
      + "Set reportOnly=true to read the current counts without arming or releasing anything - otherwise the counts "
      + "can only be seen by releasing the patch, which stops the batching.")]
[NodePort("enabled", PortDirection.Input, "boolean", Description = "true = batch the writes (default). false = remove the patch")]
[NodePort("reportOnly", PortDirection.Input, "boolean", Description = "true = touch nothing and just return the current counts. Neither installs nor removes, so it can be read while a run is in progress")]
[NodePort("batchSize", PortDirection.Input, "number", Description = "Commit every this many writes (default 5000). Larger is faster, but more is rolled back if a commit fails")]
[NodePort("burstGapMs", PortDirection.Input, "number", Description = "A gap longer than this means the writes are not a burst, so they are not batched (default 50)")]
[NodePort("watchdogMs", PortDirection.Input, "number", Description = "Commit an open batch that has stayed open longer than this (default 3000)")]
[NodePort("armed", PortDirection.Output, "boolean", Description = "Whether the batching is currently in place. With reportOnly it reports the state without changing it")]
[NodePort("patchedCount", PortDirection.Output, "number", Description = "How many methods could be wrapped")]
[NodePort("writes", PortDirection.Output, "number", Description = "Writes that went into a batch")]
[NodePort("commits", PortDirection.Output, "number", Description = "How many times a batch was committed")]
[NodePort("forcedCommits", PortDirection.Output, "number", Description = "How many of those commits the watchdog forced")]
[NodePort("result", PortDirection.Output, "string", Description = "Batched writes, commits and watchdog commits, plus which store instance was patched, how many methods, whether it is active, and which thread the batching applies to")]
public sealed class KVStoreTransactionPatchNode : INode
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>包む相手。保存層のメソッドで、書き込みの実体はこの下にある。</summary>
    static readonly string[] TargetMethods = { "Upsert", "Delete" };

    public void Execute(IExecutionContext ctx)
    {
        bool enabled = ctx.GetPortValue("enabled") as bool? ?? true;
        bool reportOnly = ctx.GetPortValue("reportOnly") as bool? ?? false;

        // 読み取り専用の口。件数は解除しないと読めない、という穴を塞ぐためのもの。
        //   設置も解除もせず、いまの状態をそのまま返す。
        //   ここで ReleaseHooks / CancelWatchdog を呼ぶと、様子を見るたびにバッチ化が止まる。
        if (reportOnly)
        {
            var s = KVStoreTransactionPatchState.GetOrCreate();
            Report(ctx, s, s.IsActive, s.Hooks.Count, new StringBuilder("(read only - nothing was installed or removed)\n"));
            return;
        }

        int batchSize = ctx.GetPortValue("batchSize") is double bs ? (int)bs : 5000;
        if (batchSize < 1) batchSize = 1;
        int watchdogMs = ctx.GetPortValue("watchdogMs") is double wd ? (int)wd : 3000;
        if (watchdogMs < 100) watchdogMs = 100;
        int burstGapMs = ctx.GetPortValue("burstGapMs") is double bg ? (int)bg : 50;
        if (burstGapMs < 1) burstGapMs = 1;

        var sb = new StringBuilder();
        var st = KVStoreTransactionPatchState.GetOrCreate();

        // 何をするにせよ、前の設置と前のウォッチドッグが残っていれば先に外す。
        // ウォッチドッグを外さずに積み上げると、古い世代のウォッチドッグが毎フレーム自分用の
        //   入れ物を書き戻し、世代どうしで入れ物を奪い合う。
        int released = st.ReleaseHooks();
        if (released > 0) sb.Append("removed the previous install: ").Append(released).Append(" hook(s)\n");
        if (st.CancelWatchdog()) sb.Append("stopped the previous watchdog\n");

        if (!enabled)
        {
            st.Disable();
            Report(ctx, st, false, 0, sb);
            return;
        }

        // 保存先が一括確定を持っているか確かめる
        object store = ctx.Store;
        var backend = store?.GetType().GetField("_backend", All)?.GetValue(store);
        var db = backend?.GetType().GetField("_db", All)?.GetValue(backend);
        if (db == null)
        {
            ctx.SetPortValue("armed", false);
            ctx.SetPortValue("result",
                "this store has no batch commit, so nothing was done (append-only, in-memory, ...). backend="
                + (backend?.GetType().Name ?? "(unknown)"));
            return;
        }

        var dbType = db.GetType();
        st.Db = db;
        st.Begin = dbType.GetMethod("BeginTrans", Type.EmptyTypes);
        st.Commit = dbType.GetMethod("Commit", Type.EmptyTypes);
        st.Rollback = dbType.GetMethod("Rollback", Type.EmptyTypes);
        st.BatchSize = batchSize;
        st.WatchdogMs = watchdogMs;
        st.BurstGapMs = burstGapMs;
        if (st.Begin == null || st.Commit == null)
        {
            ctx.SetPortValue("armed", false);
            ctx.SetPortValue("result", "the store has no BeginTrans / Commit: " + dbType.FullName);
            return;
        }

        // 包む。保存層は固定の DLL なので、見つかるのは1つだけのはず。
        var types = FindTypesBySimpleName(backend.GetType().Name);
        int ok = 0;
        foreach (var t in types)
        {
            foreach (var name in TargetMethods)
            {
                var m = t.GetMethod(name, All);
                if (m == null || m.IsAbstract || m.ContainsGenericParameters) continue;
                try { st.Hooks.Add(new ILHook(m, EmitBatchGate)); ok++; }
                catch (Exception ex)
                {
                    sb.Append("could not wrap ").Append(t.Name).Append('.').Append(name)
                      .Append(": ").Append(ex.GetType().Name).Append('\n');
                }
            }
        }

        if (ok == 0)
        {
            sb.Append("found nothing to wrap: ").Append(backend.GetType().Name).Append('\n');
            Report(ctx, st, false, 0, sb);
            return;
        }

        st.Enable();
        sb.Append("methods wrapped: ").Append(ok)
          .Append(" (target=").Append(backend.GetType().FullName)
          .Append(", ").Append(types.Count).Append(" generation(s))\n");
        // 非公開メンバに依存するため、いま当てた相手の版を出す。
        //   動作を確認した版と違えば、まずここを疑える。
        sb.Append("target version: ").Append(VersionOf(backend)).Append(" (verified against: 0.7.34)\n");
        sb.Append("commit every: ").Append(batchSize).Append(" write(s) / watchdog ").Append(watchdogMs).Append("ms\n");

        // ウォッチドッグ。バッチを開いてよいスレッドを決める役でもある。
        //   ここが動いているスレッドの書き込みだけをバッチにまとめ、開いたままのバッチもここで閉じる。
        var myToken = Guid.NewGuid();
        st.WatchdogToken = myToken;
        IPersistentRegistration reg = null;
        reg = ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () =>
            {
                var s = KVStoreTransactionPatchState.GetOrCreate();
                // 自分が現役でなければ自分で止まる。
                //   ホットリロードで世代が変わっても、古いウォッチドッグがここで消える。
                if (s.WatchdogToken != myToken) { try { reg?.Cancel(); } catch { } return; }
                s.MarkWatchdogThread();
                s.WatchdogTick();
            },
            OnStop = () =>
            {
                var s = KVStoreTransactionPatchState.GetOrCreate();
                if (s.WatchdogToken != myToken) return;
                s.CloseOpenBatchHere();
                s.ReleaseHooks();
                s.Disable();
            }
        });
        st.Watchdog = reg;

        Report(ctx, st, true, ok, sb);
    }

    static void Report(IExecutionContext ctx, KVStoreTransactionPatchState st, bool armed, int patched, StringBuilder sb)
    {
        sb.Append("batched writes=").Append(st.Writes)
          .Append(" commits=").Append(st.Commits)
          .Append(" watchdog commits=").Append(st.ForcedCommits).Append('\n');
        // 同じ入れ物を見ているかを確かめる。世代がずれると別物になる。
        sb.Append("state id=").Append(st.BagId)
          .Append(" hooks=").Append(st.Hooks.Count)
          .Append(" active=").Append(st.IsActive)
          .Append(" batching thread=").Append(st.WatchdogThreadId).Append('\n');
        ctx.SetPortValue("armed", armed);
        ctx.SetPortValue("patchedCount", (double)patched);
        ctx.SetPortValue("writes", (double)st.Writes);
        ctx.SetPortValue("commits", (double)st.Commits);
        ctx.SetPortValue("forcedCommits", (double)st.ForcedCommits);
        ctx.SetPortValue("result", sb.ToString());
    }

    /// <summary>
    /// 書き込みメソッドの入口に、バッチの判定を1つ入れるだけ。元の処理はそのまま走る。
    /// MoveType.AfterLabel が要る。既定では分岐で入ってくる経路が差し込みを飛び越す。
    /// </summary>
    static void EmitBatchGate(ILContext il)
    {
        var c = new ILCursor(il);
        c.Goto(0, MoveType.AfterLabel);
        c.EmitDelegate<Action>(KVStoreTransactionPatchState.OnWrite);
    }

    /// <summary>パッチを当てた相手のアセンブリ版。取れなければその旨を返す。</summary>
    static string VersionOf(object instance)
    {
        try
        {
            var v = instance?.GetType().Assembly.GetName().Version;
            return v == null ? "(unknown)" : v.ToString();
        }
        catch { return "(unknown)"; }
    }

    static List<Type> FindTypesBySimpleName(string simpleName)
    {
        var found = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types) if (t.Name == simpleName) found.Add(t);
        }
        return found;
    }
}

/// <summary>
/// 設置とバッチの状態。
/// AppDomain 側に置く。static のままだとホットリロードで世代ごと切り離され、
///   設置したフックを二度と外せなくなり、保存先が止まったままになる。
///
/// 入れ物は BCL の型だけで作る。自前の型で持つと、世代が変わった瞬間に as が失敗する。
/// 入れ物の形を変えるときは必ず Key の版も上げること。
///   同じ鍵に別の形を置くと、両方の世代が「読めない -> 作り直して上書き」を
///   毎フレーム繰り返し、どちらも自分の状態を保てなくなる。
/// </summary>
internal sealed class KVStoreTransactionPatchState
{
    const string Key = "NgolKVStoreTransactionPatchState_v1";

    static Dictionary<string, object> Bag()
    {
        var d = AppDomain.CurrentDomain.GetData(Key) as Dictionary<string, object>;
        if (d == null)
        {
            d = new Dictionary<string, object>
            {
                ["hooks"] = new List<IDisposable>(),
                ["counters"] = new int[3],   // 0=Writes 1=Commits 2=ForcedCommits
                ["active"] = new bool[1],
                ["wdthread"] = new int[1],   // バッチを開いてよいスレッド（0=まだ決まっていない）
            };
            AppDomain.CurrentDomain.SetData(Key, d);
        }
        return d;
    }

    static T Get<T>(string key) where T : class
    {
        Bag().TryGetValue(key, out var v);
        return v as T;
    }
    static void Put(string key, object v) { Bag()[key] = v; }
    static int GetInt(string key, int fallback)
        => Bag().TryGetValue(key, out var v) && v is int i ? i : fallback;

    public List<IDisposable> Hooks => (List<IDisposable>)Bag()["hooks"];
    static int[] Counters => (int[])Bag()["counters"];
    static bool[] ActiveFlag => (bool[])Bag()["active"];
    static int[] WatchdogThreadSlot => (int[])Bag()["wdthread"];

    public object Db { get => Get<object>("db"); set => Put("db", value); }
    public MethodInfo Begin { get => Get<MethodInfo>("begin"); set => Put("begin", value); }
    public MethodInfo Commit { get => Get<MethodInfo>("commit"); set => Put("commit", value); }
    public MethodInfo Rollback { get => Get<MethodInfo>("rollback"); set => Put("rollback", value); }
    public int BatchSize { get => GetInt("batch", 5000); set => Put("batch", value); }
    public int WatchdogMs { get => GetInt("watchdog", 3000); set => Put("watchdog", value); }

    /// <summary>いま現役のウォッチドッグの印。これが違うウォッチドッグは自分で止まる。</summary>
    public Guid WatchdogToken
    {
        get => Bag().TryGetValue("wdtoken", out var v) && v is Guid g ? g : Guid.Empty;
        set => Put("wdtoken", value);
    }
    public IPersistentRegistration Watchdog
    {
        get => Get<IPersistentRegistration>("wdreg");
        set => Put("wdreg", value);
    }

    public int Writes => Counters[0];
    public int Commits => Counters[1];
    public int ForcedCommits => Counters[2];

    /// <summary>入れ物の同一性を外から確かめるための識別子。</summary>
    public int BagId => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Bag());
    public bool IsActive => ActiveFlag[0];
    public int WatchdogThreadId => WatchdogThreadSlot[0];

    /// <summary>これ以上間が空いたら「バーストではない」とみなす。</summary>
    public int BurstGapMs { get => GetInt("burst", 50); set => Put("burst", value); }

    // 確定はスレッド単位なので、バッチの状態もスレッドごとに持つ。
    [ThreadStatic] static bool t_open;
    [ThreadStatic] static int t_count;
    [ThreadStatic] static long t_openedAtTicks;
    [ThreadStatic] static long t_lastWriteTicks;

    // 状態そのものは AppDomain の入れ物にあるので、この型は素通しの窓でよい。
    public static KVStoreTransactionPatchState GetOrCreate() => new KVStoreTransactionPatchState();

    public void Enable()
    {
        var c = Counters; c[0] = 0; c[1] = 0; c[2] = 0;
        ActiveFlag[0] = true;
    }

    public void Disable()
    {
        ActiveFlag[0] = false;
        Db = null;
        WatchdogThreadSlot[0] = 0;
    }

    /// <summary>現役のウォッチドッグを止める。止めたら true。</summary>
    public bool CancelWatchdog()
    {
        WatchdogToken = Guid.Empty;   // 取り残されたウォッチドッグは、これを見て自分で止まる
        var reg = Watchdog;
        Watchdog = null;
        if (reg == null) return false;
        try { if (reg.IsActive) { reg.Cancel(); return true; } } catch { }
        return false;
    }

    /// <summary>バッチを開いてよいスレッドをここに決める。ウォッチドッグが動く場所＝閉じられる場所。</summary>
    public void MarkWatchdogThread()
    {
        int id = Thread.CurrentThread.ManagedThreadId;
        if (WatchdogThreadSlot[0] != id) WatchdogThreadSlot[0] = id;
    }

    public int ReleaseHooks()
    {
        int n = 0;
        foreach (var h in Hooks) { try { h.Dispose(); n++; } catch { } }
        Hooks.Clear();
        return n;
    }

    /// <summary>
    /// 書き込みの入口。
    ///
    /// バッチを開くのはウォッチドッグと同じスレッドだけ。
    ///   確定はスレッド単位なので、他のスレッドで開いたバッチは誰にも閉じられない。
    ///   そのスレッドが二度と書かなければ開いたまま残り、保存先が固まる。
    /// さらに、バーストの最中（直前の書き込みから間が空いていない）だけバッチにまとめる。
    ///   単発の書き込みで開くと、確定まで最大 watchdogMs のあいだ他を待たせる。
    /// </summary>
    public static void OnWrite()
    {
        var s = GetOrCreate();
        if (!s.IsActive || s.Db == null) return;

        var now = DateTime.UtcNow.Ticks;
        var sinceLastMs = t_lastWriteTicks == 0
            ? long.MaxValue
            : (now - t_lastWriteTicks) / TimeSpan.TicksPerMillisecond;
        t_lastWriteTicks = now;

        if (!t_open)
        {
            if (s.WatchdogThreadId != Thread.CurrentThread.ManagedThreadId) return;
            if (sinceLastMs > s.BurstGapMs) return;

            try { s.Begin.Invoke(s.Db, null); }
            catch { return; }
            t_open = true;
            t_count = 0;
            t_openedAtTicks = now;
        }
        else if (sinceLastMs > s.BurstGapMs)
        {
            // バーストが途切れた。端数をここで確定して次に備える。
            s.CommitHere();
            Interlocked.Increment(ref Counters[0]);
            return;
        }

        Interlocked.Increment(ref Counters[0]);
        if (++t_count >= s.BatchSize) s.CommitHere();
    }

    void CommitHere()
    {
        try { Commit.Invoke(Db, null); Interlocked.Increment(ref Counters[1]); }
        catch { try { Rollback?.Invoke(Db, null); } catch { } }
        t_open = false;
        t_count = 0;
    }

    /// <summary>いま動いているスレッドに開いたままのバッチがあれば確定する。</summary>
    public string CloseOpenBatchHere()
    {
        if (!t_open) return "no batch is open on this thread";
        CommitHere();
        return "committed the batch that was left open on this thread";
    }

    /// <summary>
    /// ウォッチドッグ。時間を超えて開いたままのバッチを確定する。
    /// バッチを開くのはこのスレッドだけと決めてあるので、開いたバッチは必ずここで閉じられる。
    /// </summary>
    public void WatchdogTick()
    {
        if (!t_open || Db == null) return;
        var elapsedMs = (DateTime.UtcNow.Ticks - t_openedAtTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs < WatchdogMs) return;
        CommitHere();
        Interlocked.Increment(ref Counters[2]);
    }
}
