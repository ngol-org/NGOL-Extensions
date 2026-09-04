using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 走査系ノードが共有する土台。範囲を刻んで走り、件数の上限で打ち切り、
/// 非同期ならホストの更新に乗って続きを走る。
///
/// 同期と非同期で照合の実装を分けない。同期は「チャンクが範囲全体と等しい非同期」として
/// 同じ経路を通す。分けると片方だけ直る事故が起きるため。
///
/// 結果を外へ返せるのは出力ポートだけで、それはノードが実行された時点にしか書けない。
/// そのため非同期は 2 相になる。1 回目で走査を始め、走り終えた後にもう一度実行して読む。
/// </summary>
internal static class NgolChunkedScan
{
    /// <summary>入力から作る鍵の前置き。同じ入力の再実行が同じ走査に辿り着くために使う。</summary>
    const string KeyPrefix = "ngol.scan.v1:";

    /// <summary>
    /// buffer の先頭 length バイトを照合する。
    /// sink へ積むのは開始位置が usable 未満のものだけ。重なり分を二重に数えないため。
    /// 戻り値はこの呼び出しで走査し終えたバイト数。0 以下なら usable とみなす。
    /// </summary>
    internal delegate long ChunkScanner(byte[] buffer, int length, long chunkStartRva, int usable, Sink sink);

    /// <summary>
    /// ヒットの受け皿。上限に達した時点で以降を受け取らず、落としたことを覚える。
    ///
    /// 走査に使っている自分のバッファの番地も覚えておき、そこに当たったヒットは捨てる。
    /// NGOL は対象と同じプロセスの中で動くので、走査そのものが「探している値を持つ番地」を増やす。
    /// 対象の外から読む道具にはこの問題が無いため、同じ発想で書くと静かに間違える。
    /// </summary>
    internal sealed class Sink
    {
        readonly List<string> _items = new List<string>();
        readonly int _max;

        /// <summary>自分の走査バッファが占めている絶対番地の範囲。</summary>
        long _bufLo, _bufHi, _base;

        /// <summary>自分の写しを拾って捨てた回数。0 でないなら走査範囲が自分の作業領域に重なっている。</summary>
        internal int SelfDropped { get; private set; }

        internal Sink(int max) { _max = max < 1 ? 1 : max; }

        internal void SetSelfRange(long moduleBase, long bufLo, long bufHi)
        {
            _base = moduleBase; _bufLo = bufLo; _bufHi = bufHi;
        }

        internal int Count { get { return _items.Count; } }

        /// <summary>この先にまだ一致があるのに見ていない、と言える状態か。</summary>
        internal bool Truncated { get; private set; }

        internal bool Full { get { return _items.Count >= _max; } }

        /// <summary>
        /// 1 件積む。上限に達していたら積まずに false を返す。
        /// rva が自分の走査バッファを指しているなら、それは対象ではなく自分の写しなので捨てる
        /// （捨てても走査は続けるので true を返す）。
        /// </summary>
        internal bool Add(long rva, string jsonItem)
        {
            if (_bufHi != 0)
            {
                var abs = _base + rva;
                if (abs >= _bufLo && abs < _bufHi) { SelfDropped++; return true; }
            }
            if (_items.Count >= _max) { Truncated = true; return false; }
            _items.Add(jsonItem);
            return true;
        }

        /// <summary>
        /// 上限で走査そのものを止めたときに呼ぶ。
        /// 上限に達した時点で走査を打ち切るため、「積もうとして断られた 1 件」は起きないことがある。
        /// それを truncated=false と読ませると、まさに防ぎたい誤読（これで全部だ）になる。
        /// </summary>
        internal void MarkTruncated() { Truncated = true; }

        internal string ToJson() { return "[" + string.Join(",", _items.ToArray()) + "]"; }
    }

    /// <summary>1 回の走査の注文。</summary>
    internal sealed class Request
    {
        /// <summary>ログと鍵に使う短い名前。</summary>
        internal string Name = "";
        /// <summary>入力の違いを鍵へ反映するための文字列（パターン・対象 RVA など）。</summary>
        internal string KeySuffix = "";
        internal IntPtr BaseAddress;
        internal long StartRva;
        internal long Size;
        internal int MaxHits = 200;
        internal bool Async;
        internal bool Restart;
        internal int ChunkBytes = 4 << 20;
        /// <summary>境界をまたぐ一致を落とさないために、チャンクの末尾へ余分に読む長さ。</summary>
        internal int Overlap;
        /// <summary>
        /// 読めない所に当たったときページ単位で飛ばして続けるか。
        /// モジュールを頭から読む走査は「読めなくなった＝実体の終わり」なので止めてよいが、
        /// 任意の番地を調べる走査では途中に穴があるのが普通で、そこで諦めると先を見られない。
        /// </summary>
        internal bool SkipUnreadable;
        internal ChunkScanner Scan = null!;
    }

    /// <summary>走査の結果。非同期の途中なら Done が false になる。</summary>
    internal sealed class Outcome
    {
        internal string HitsJson = "[]";
        internal int HitCount;
        internal bool Truncated;
        internal long ScannedBytes;
        internal bool Done;
        /// <summary>走査が到達した RVA。</summary>
        internal long ProgressRva;
        /// <summary>この実行で走査を始めたか（非同期の 1 回目）。</summary>
        internal bool JustStarted;
        /// <summary>読める範囲が要求より手前で終わったか。</summary>
        internal bool StoppedEarly;
        /// <summary>自分の走査バッファを指していたので捨てた件数。</summary>
        internal int SelfDropped;
    }

    sealed class State
    {
        internal IntPtr BaseAddress;
        internal long StartRva;
        internal long EndRva;
        internal long CurrentRva;
        internal long Scanned;
        internal bool Done;
        internal bool StoppedEarly;
        internal Sink Sink = null!;
        internal IPersistentRegistration? Reg;
        internal DateTime StartedUtc;
    }

    /// <summary>
    /// 走査する。async でなければその場で走り切って返す。
    /// async なら 1 回目で始めて即座に返し、2 回目以降は今の様子（走り終えていれば結果）を返す。
    /// </summary>
    internal static Outcome Run(IExecutionContext ctx, Request req)
    {
        if (!req.Async)
        {
            // チャンクを範囲全体にして、非同期と同じ照合を 1 回だけ通す。
            var sink = new Sink(req.MaxHits);
            long scanned;
            bool stopped;
            var reached = ScanRange(req, req.StartRva, req.StartRva + req.Size,
                (int)Math.Max(1, Math.Min(req.Size, int.MaxValue - req.Overlap)),
                sink, out scanned, out stopped);

            // 命令のように長さが可変なものは範囲の終わりを越えて読み終えることがある。
            // 次の位置を決めるには要るが、外へ報告する値が範囲を越えてはいけない。
            var hardEnd = req.StartRva + req.Size;
            if (reached > hardEnd) { scanned -= reached - hardEnd; reached = hardEnd; }

            return new Outcome
            {
                HitsJson = sink.ToJson(),
                HitCount = sink.Count,
                Truncated = sink.Truncated,
                ScannedBytes = scanned,
                Done = true,
                ProgressRva = reached,
                StoppedEarly = stopped,
                SelfDropped = sink.SelfDropped,
            };
        }

        // 答えを変える入力はすべて鍵に入れる。入れ忘れると、
        // 条件を変えたのに前の走査結果がそのまま返り、しかもそれと分からない。
        // チャンクの大きさは答えを変えないので入れない（変えて読み直せる方が都合がよい）。
        var key = KeyPrefix + req.Name + ":" + req.KeySuffix + ":"
                + req.BaseAddress.ToInt64().ToString("x") + ":"
                + req.StartRva.ToString("x") + ":" + req.Size.ToString("x") + ":"
                + req.MaxHits.ToString("x");

        var state = AppDomain.CurrentDomain.GetData(key) as State;

        if (state != null && req.Restart)
        {
            if (state.Reg != null && state.Reg.IsActive) state.Reg.Cancel();
            AppDomain.CurrentDomain.SetData(key, null);
            state = null;
        }

        if (state == null)
        {
            state = new State
            {
                BaseAddress = req.BaseAddress,
                StartRva = req.StartRva,
                EndRva = req.StartRva + req.Size,
                CurrentRva = req.StartRva,
                Sink = new Sink(req.MaxHits),
                StartedUtc = DateTime.UtcNow,
            };
            AppDomain.CurrentDomain.SetData(key, state);

            var chunk = Math.Max(4096, req.ChunkBytes);
            var captured = state;
            var reg = ctx.RegisterPersistent(new PersistentCallbacks
            {
                OnUpdate = () => Tick(captured, req, chunk),
            });
            state.Reg = reg;
            ctx.Logger.LogInfo($"[{req.Name}] background scan started: 0x{state.StartRva:x}..0x{state.EndRva:x} ({req.Size} bytes, chunk {chunk})");

            return new Outcome
            {
                Done = false,
                ProgressRva = state.CurrentRva,
                JustStarted = true,
            };
        }

        return new Outcome
        {
            HitsJson = state.Done ? state.Sink.ToJson() : "[]",
            HitCount = state.Done ? state.Sink.Count : 0,
            Truncated = state.Done && state.Sink.Truncated,
            ScannedBytes = state.Scanned,
            Done = state.Done,
            ProgressRva = state.CurrentRva,
            StoppedEarly = state.StoppedEarly,
            SelfDropped = state.Sink.SelfDropped,
        };
    }

    /// <summary>ホストの更新ごとに 1 チャンクだけ進める。</summary>
    static void Tick(State state, Request req, int chunkBytes)
    {
        if (state.Done) { if (state.Reg != null) state.Reg.Cancel(); return; }

        long scanned;
        bool stopped;
        var reached = ScanRange(req, state.CurrentRva,
            Math.Min(state.CurrentRva + chunkBytes, state.EndRva),
            chunkBytes, state.Sink, out scanned, out stopped);

        state.Scanned += scanned;
        state.CurrentRva = reached;

        // 範囲の終わりを越えて読み終えたぶんは、報告する値から戻す。
        if (state.CurrentRva > state.EndRva)
        {
            state.Scanned -= state.CurrentRva - state.EndRva;
            state.CurrentRva = state.EndRva;
        }

        if (stopped || state.Sink.Full || state.CurrentRva >= state.EndRva)
        {
            // チャンクの切れ目でちょうど上限に達した場合、ScanRange の中では
            // 「まだ先がある」と判定できない（あちらの範囲はチャンク単位のため）。ここで見る。
            if (state.Sink.Full && state.CurrentRva < state.EndRva) state.Sink.MarkTruncated();

            state.StoppedEarly = stopped;
            state.Done = true;
            if (state.Reg != null)
            {
                state.Reg.ReportProgress(
                    $"done: {state.Sink.Count} hit(s), {state.Scanned} byte(s) scanned, reached 0x{state.CurrentRva:x}"
                    + (state.Sink.Truncated ? " (stopped at max_hits)" : "")
                    + (stopped ? " (readable range ended early)" : ""));
                state.Reg.Cancel();
            }
            return;
        }

        if (state.Reg != null)
        {
            var total = state.EndRva - state.StartRva;
            var doneBytes = state.CurrentRva - state.StartRva;
            var pct = total > 0 ? (doneBytes * 100.0 / total) : 100.0;
            state.Reg.ReportProgress($"{pct:F1}% at 0x{state.CurrentRva:x}, {state.Sink.Count} hit(s)");
        }
    }

    /// <summary>
    /// startRva から endRva まで chunkBytes ずつ読み、各チャンクを照合器へ渡す。
    /// 戻り値は走査が到達した RVA。
    /// </summary>
    static long ScanRange(Request req, long startRva, long endRva, int chunkBytes,
        Sink sink, out long scannedBytes, out bool stoppedEarly)
    {
        scannedBytes = 0;
        stoppedEarly = false;
        var cur = startRva;

        // バッファは 1 本を使い回して固定する。チャンクごとに作って捨てると、
        // 読んだ内容が載ったゴミがチャンクの数だけ散らばり、
        // 走査範囲がそこへ重なったときに自分の写しを候補として拾う。
        var bufCapacity = (int)Math.Min((long)chunkBytes + req.Overlap, endRva - startRva + req.Overlap);
        if (bufCapacity < 1) bufCapacity = 1;
        var buf = new byte[bufCapacity];
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
        var bufLo = pin.AddrOfPinnedObject().ToInt64();
        sink.SetSelfRange(req.BaseAddress.ToInt64(), bufLo, bufLo + buf.Length);

        while (cur < endRva && !sink.Full)
        {
            var remaining = endRva - cur;
            var nominal = (int)Math.Min(chunkBytes, remaining);
            // 重なりの分だけ余分に読む。範囲の外まで読むことになるが、報告するのは
            // 開始位置が nominal 未満のものだけなので、範囲を越えたヒットは出ない。
            var readSize = (int)Math.Min((long)nominal + req.Overlap, buf.Length);

            var got = NgolSafeMemory.Read(new IntPtr(req.BaseAddress.ToInt64() + cur), buf, 0, readSize);
            if (got <= 0)
            {
                if (!req.SkipUnreadable) { stoppedEarly = true; break; }
                var skipped = NextPage(req.BaseAddress.ToInt64() + cur) - req.BaseAddress.ToInt64();
                if (skipped <= cur) break;   // 進まないなら止める（無限に回らないため）
                cur = skipped;
                continue;
            }

            var usable = (int)Math.Min(nominal, got);
            var consumed = req.Scan(buf, got, cur, usable, sink);
            if (consumed <= 0) consumed = usable;
            // 命令のように長さが可変なものは、名目の切れ目を越えて読み終えることがある。
            // そこまで進めておかないと、次のチャンクが命令の途中から始まって誤って復号される。
            // 上限は「実際に読めた長さ」で、それを越えて進むことはない。
            if (consumed > got) consumed = got;

            scannedBytes += consumed;
            cur += consumed;

            // 名目の長さに届かなかった＝この先は読めない。
            // 重なりの分は範囲の外まで要求しているので、そこが読めなくても異常ではない。
            // readSize と比べると、モジュールの末尾まで走査しただけで「読める範囲が尽きた」と誤って言う。
            if (got < nominal && cur < endRva)
            {
                if (!req.SkipUnreadable) { stoppedEarly = true; break; }
                var skipped = NextPage(req.BaseAddress.ToInt64() + cur) - req.BaseAddress.ToInt64();
                if (skipped <= cur) break;
                cur = skipped;
            }
        }

        // 上限で止めたまま範囲を見終えていないなら、この先にまだ一致がありうる。
        if (sink.Full && cur < endRva) sink.MarkTruncated();

        return cur;
        }
        finally
        {
            // 手放す前に消す。残しておくと、次の走査がこの写しを拾う。
            Array.Clear(buf, 0, buf.Length);
            pin.Free();
        }
    }

    const long PageSize = 0x1000;

    /// <summary>次のページ境界。読めない所を飛ばすときの進み先。</summary>
    static long NextPage(long address)
    {
        return (address + PageSize) & ~(PageSize - 1);
    }

    /// <summary>出力ポートへ結果を書き出す。4 本で同じ名前・同じ意味にするためにここへ置く。</summary>
    internal static void Emit(IExecutionContext ctx, Outcome outcome, string hitsPort, string countPort)
    {
        ctx.SetPortValue(hitsPort, outcome.HitsJson);
        ctx.SetPortValue(countPort, (double)outcome.HitCount);
        ctx.SetPortValue("scanned_bytes", (double)outcome.ScannedBytes);
        ctx.SetPortValue("truncated", outcome.Truncated);
        ctx.SetPortValue("done", outcome.Done);
        ctx.SetPortValue("progress_rva", $"0x{outcome.ProgressRva:x}");
        ctx.SetPortValue("self_dropped", (double)outcome.SelfDropped);
    }

    /// <summary>打ち切りと途中終わりを人が読む形にする。空文字なら何も起きていない。</summary>
    internal static string DescribeLimits(Outcome outcome, int maxHits)
    {
        var notes = new List<string>();
        if (outcome.Truncated)
            notes.Add($"stopped at max_hits={maxHits}; there are more matches beyond this point");
        if (outcome.StoppedEarly)
            notes.Add("the readable range ended before the requested size; the rest was not examined");
        if (outcome.SelfDropped > 0)
            notes.Add($"dropped {outcome.SelfDropped} hit(s) that pointed into this scan's own buffer "
                    + "- the range overlaps the scanner's working memory, so run it twice and trust only what appears both times");
        return notes.Count == 0 ? "" : " -- " + string.Join("; ", notes.ToArray());
    }
}
