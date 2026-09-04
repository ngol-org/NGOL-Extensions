using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// モジュールの.textセクションを1Tickにつき1チャンクずつバックグラウンドで
/// 逆アセンブルし、RIP相対参照(LEA/MOV等のメモリオペランド)とCALL/JMP直接分岐の
/// 「参照先RVA -> 参照元命令RVAのリスト」を ctx.Store (KVStore) に逐次登録する。
/// 単発スキャンの ngol.code.xref_find と対になる ngol.code.xref_lookup と組み合わせて使う。
///
/// 出力: なし（KVStoreへの永続化のみ。進捗と結果はログと KVStore の
///       progress_rva / done / scanned_bytes / truncated キーで確認）
///
/// 主な使い方:
///   restart=true で新規スキャン開始（既存インデックスは削除してから再構築）。
///   スキャンはバックグラウンドで進むため Execute() は即座に返る。他ノードの実行を妨げない。
///   スキャン中でも ngol.code.xref_lookup で処理済み範囲の逆引きが可能
///   （index_done/index_progress_rva で「未スキャンで不明」か「確定で不存在」かを判別できる）。
///
/// 制約:
///   RIP相対メモリオペランドと直接分岐(NearBranch64)のみ検出。レジスタ間接呼び出しや
///   デコンパイラのレジスタ値伝播由来の間接参照（値を追って初めて分かるもの）は対象外。
///   命令をデコードして作るため、データセクションからの参照は構造上索引に入らない
///   （例外テーブル(.pdata)・vtable・関数ポインタ表など）。
///   linear sweep方式のため、コード領域と誤認したデータバイトから誤検出が発生しうる。
///   読めない領域に当たった場合はそこで走査を終え、KVStoreの truncated キーに残す
///   （ngol.code.xref_lookup の index_truncated で確認できる）。
///   Windows x64専用（GetModuleHandleA使用）。
/// </summary>
[NodeType("ngol.code.xref_index_build", "Code", "Xref Index Build",
    Version = "1.2.2",
    Description = "Incrementally scan a module's code range in the background (one chunk per tick), building a target_rva -> [instr_rva...] reverse-reference index persisted to ctx.Store. Query with ngol.code.xref_lookup while it runs. Progress is reported to the job so a stalled run can be told apart from a slow one. Rebuilding is differential: an entry is written only when its contents actually change, and entries that no longer occur are removed once the scan completes (they are kept if the scan was truncated, since the unscanned range may still reference them). Instruction-level only: references from data sections are out of scope by construction.")]
[NodePort("module",         PortDirection.Input, "string", Description = "Module name. Empty = the process's main module")]
[NodePort("scan_start_rva", PortDirection.Input, "string", Description = "Start RVA (default '0x1000')")]
[NodePort("scan_size",      PortDirection.Input, "number", Description = "Bytes to scan from scan_start_rva (default 0x9ce751)")]
[NodePort("chunk_bytes",    PortDirection.Input, "number", Description = "Bytes decoded per tick (default 262144 = 256KB)")]
[NodePort("restart",        PortDirection.Input, "boolean", Description = "If true, rebuild from scan_start_rva even if a previous run exists (default false). The rebuild is differential: entries whose contents are unchanged are left untouched")]
public sealed class XrefIndexBuildNode : INode
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandleA(string moduleName);

    // AppDomain 経由の状態(ホットリロードで消えない)。入れ物の形を変えるときは鍵の版を上げる。
    const string StateKeyPrefix = "NgolXrefIndexBuildState_v1_";

    sealed class IndexState
    {
        public long ModuleBase;
        public long ScanStartRva;
        public long ScanEndRva;
        public long CurrentRva;
        public long HitCount;
        public bool Done;
        public bool Truncated;
        public DateTime StartedAt;
        public int LastLoggedPercent = -1;
        public readonly Dictionary<long, List<long>> Pending = new();
        public IPersistentRegistration Reg;

        /// <summary>
        /// 作り直しのとき、開始時点で存在していたエントリ。
        /// 消すのは走査後に残ったもの（＝今回ヒットしなかったもの）だけ。
        /// 作り直しでないときは null。
        /// </summary>
        public HashSet<string> Stale;

        /// <summary>この実行で書いたキー。同じ参照先が複数のチャンクに現れるため要る。</summary>
        public readonly HashSet<string> Written = new();

        public int Writes;
        public int Unchanged;
        public int Deletes;
    }

    public void Execute(IExecutionContext ctx)
    {
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
        var scanStartStr = (ctx.GetPortValue("scan_start_rva") as string) ?? "0x1000";
        double scanSizeD = 0x9ce751;
        if (ctx.GetPortValue("scan_size") is double sd) scanSizeD = sd;
        double chunkBytesD = 262144;
        if (ctx.GetPortValue("chunk_bytes") is double cd) chunkBytesD = cd;
        bool restart = ctx.GetPortValue("restart") is bool rb && rb;

        var stateKey = StateKeyPrefix + moduleName;
        var state = AppDomain.CurrentDomain.GetData(stateKey) as IndexState;

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            ctx.Logger.LogWarning($"[XrefIndexBuild] module not found: {moduleName}");
            return;
        }

        if (state == null || restart)
        {
            if (state?.Reg != null && state.Reg.IsActive) state.Reg.Cancel();

            long scanStartRva = ParseHex(scanStartStr);
            state = new IndexState
            {
                ModuleBase = baseAddr.ToInt64(),
                ScanStartRva = scanStartRva,
                ScanEndRva = scanStartRva + (long)scanSizeD,
                CurrentRva = scanStartRva,
                HitCount = 0,
                Done = false,
                StartedAt = DateTime.UtcNow,
            };
            AppDomain.CurrentDomain.SetData(stateKey, state);
            ctx.Store.Set($"xrefidx:{moduleName}:done", false);

            if (restart)
            {
                // 作り直しでも既存エントリの大半はそのまま残るため、先に全部消すと
                //   同じ内容を消してから書き戻すことになる。ここでは消さずに控えておき、
                //   走査を終えてから「今回ヒットしなかったもの」だけを消す。
                state.Stale = new HashSet<string>(ctx.Store.Keys($"xrefidx:{moduleName}:0x"));
                ctx.Logger.LogInfo($"[XrefIndexBuild] restart: {state.Stale.Count} existing entries kept for comparison - only entries that no longer occur will be deleted");
                Report(state, $"restart: comparing against {state.Stale.Count} existing entries");
            }

            ctx.Logger.LogInfo($"[XrefIndexBuild] starting scan of {moduleName}: 0x{state.ScanStartRva:x}..0x{state.ScanEndRva:x} ({scanSizeD:F0} bytes)");
        }
        else if (state.Done)
        {
            ctx.Logger.LogInfo($"[XrefIndexBuild] already completed for {moduleName} ({state.HitCount} refs indexed). Pass restart=true to redo.");
            return;
        }
        else
        {
            ctx.Logger.LogInfo($"[XrefIndexBuild] resuming existing background scan for {moduleName} at 0x{state.CurrentRva:x}");
        }

        var chunkBytes = Math.Max(4096, (int)chunkBytesD);
        var capturedState = state;

        var reg = ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () => TickScan(ctx, moduleName, capturedState, chunkBytes),
        });
        state.Reg = reg;
    }

    static void TickScan(IExecutionContext ctx, string moduleName, IndexState state, int chunkBytes)
    {
        if (state.Done) { state.Reg?.Cancel(); return; }

        try
        {
            var remaining = state.ScanEndRva - state.CurrentRva;
            if (remaining <= 0)
            {
                FlushPending(ctx, moduleName, state);
                FinishScan(ctx, moduleName, state);
                return;
            }

            var thisChunk = (int)Math.Min(chunkBytes, remaining);
            // x64命令の最大長は15バイトなので、チャンク境界を跨ぐ命令が途中で
            // 切れて誤デコードされないよう、末尾に少し余分(overlap)に読み込む。
            // 読み取り自体はスキャン範囲を超えて次チャンク分の実メモリまで及ぶが、
            // ループの終了判定(endIP)は従来通りチャンクの名目境界のままなので、
            // 次チャンクは実際にデコーダが到達した位置(境界を跨いだ場合はその先)から始まる。
            const int overlap = 16;
            var readSize = thisChunk + overlap;
            var startAddr = new IntPtr(state.ModuleBase + state.CurrentRva);
            var bytes = new byte[readSize];
            // 読める範囲がチャンクの途中で終わることがある。そこで走査を終える。
            var readable = NgolSafeMemory.Read(startAddr, bytes, 0, readSize);
            if (readable <= 0)
            {
                // 要求した範囲の途中で読めなくなった。ここで終えるが、
                // 「最後まで見た」と「読める所まで見た」は別物なので区別して残す
                // --索引の未ヒットを「確定で不存在」と読まれてしまうため。
                state.Truncated = true;
                FlushPending(ctx, moduleName, state);
                FinishScan(ctx, moduleName, state);
                return;
            }

            var reader = new ByteArrayCodeReader(bytes);
            var decoder = Iced.Intel.Decoder.Create(64, reader);
            decoder.IP = (ulong)startAddr.ToInt64();
            var endIP = decoder.IP + (ulong)Math.Min(thisChunk, readable);

            while (decoder.IP < endIP)
            {
                var instr = decoder.Decode();
                if (instr.Code == Code.INVALID)
                {
                    // データ領域等でデコード不能。iced の Decoder は無効オペコード検出時も
                    // IP を1バイト進めるため continue でスキャンを継続できる
                    continue;
                }

                for (int i = 0; i < instr.OpCount; i++)
                {
                    if (instr.GetOpKind(i) == OpKind.Memory && instr.IsIPRelativeMemoryOperand)
                    {
                        var targetRva = (long)instr.IPRelativeMemoryAddress - state.ModuleBase;
                        var instrRva = (long)instr.IP - state.ModuleBase;
                        AddHit(state, targetRva, instrRva);
                    }
                }

                if (instr.Op0Kind == OpKind.NearBranch64)
                {
                    var fc = instr.FlowControl;
                    if (fc == FlowControl.Call || fc == FlowControl.UnconditionalBranch || fc == FlowControl.ConditionalBranch)
                    {
                        var targetRva = (long)instr.NearBranch64 - state.ModuleBase;
                        var instrRva = (long)instr.IP - state.ModuleBase;
                        AddHit(state, targetRva, instrRva);
                    }
                }
            }

            state.CurrentRva = (long)decoder.IP - state.ModuleBase;

            if (state.Pending.Count >= 500)
            {
                FlushPending(ctx, moduleName, state);
            }

            var totalSize = state.ScanEndRva - state.ScanStartRva;
            var doneSize = state.CurrentRva - state.ScanStartRva;
            var percent = totalSize > 0 ? (int)(doneSize * 100 / totalSize) : 100;
            if (percent >= state.LastLoggedPercent + 5)
            {
                state.LastLoggedPercent = percent;
                ctx.Store.Set($"xrefidx:{moduleName}:progress_rva", state.CurrentRva);
                ctx.Logger.LogInfo($"[XrefIndexBuild] {moduleName}: {percent}% (0x{state.CurrentRva:x}/0x{state.ScanEndRva:x}), {state.HitCount} refs so far");
                Report(state, $"{percent}% at 0x{state.CurrentRva:x}, {state.HitCount} refs");
            }

            if (state.CurrentRva >= state.ScanEndRva)
            {
                FlushPending(ctx, moduleName, state);
                FinishScan(ctx, moduleName, state);
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[XrefIndexBuild] tick error: {ex.Message}");
            Report(state, $"tick error: {ex.GetType().Name}: {ex.Message}");
            state.Reg?.Cancel();
        }
    }

    /// <summary>
    /// ジョブへ進捗を出す。
    /// 時刻を必ず添える--ジョブの状態は Running としか出ないため、
    ///   これが無いと「進んでいる」と「止まっている」を外から区別できない。
    /// </summary>
    static void Report(IndexState state, string text)
    {
        try { state.Reg?.ReportProgress($"[{DateTime.Now:HH:mm:ss}] {text}"); }
        catch { /* 進捗報告の失敗で本処理を止めない */ }
    }

    static void AddHit(IndexState state, long targetRva, long instrRva)
    {
        if (!state.Pending.TryGetValue(targetRva, out var list))
        {
            list = new List<long>();
            state.Pending[targetRva] = list;
        }
        list.Add(instrRva);
        state.HitCount++;
    }

    static void FlushPending(IExecutionContext ctx, string moduleName, IndexState state)
    {
        foreach (var kv in state.Pending)
        {
            var key = $"xrefidx:{moduleName}:0x{kv.Key:x}";
            var existing = ctx.Store.Get<string>(key);

            // 作り直しのときは、このキーを今回はじめて書くときだけ既存を捨てる。
            //   同じ参照先は複数のチャンクにまたがって現れるので、2回目以降に捨てると
            //   先に書いた今回のぶんまで失う。
            var firstTouchThisRun = state.Written.Add(key);
            var replaceExisting = state.Stale != null && firstTouchThisRun;

            var merged = new HashSet<long>();
            if (!replaceExisting && !string.IsNullOrEmpty(existing))
            {
                foreach (var part in existing.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = part.Trim().Trim('"');
                    if (p.StartsWith("0x") && long.TryParse(p.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var v))
                        merged.Add(v);
                }
            }
            foreach (var v in kv.Value) merged.Add(v);

            // 並びを決定的にする。順序が揺れると、中身が同じでも別物に見えて書き直しになる。
            var ordered = new List<long>(merged);
            ordered.Sort();
            var json = "[" + string.Join(",", System.Linq.Enumerable.Select(ordered, v => $"\"0x{v:x}\"")) + "]";

            // 中身が変わっていなければ書かない。
            //   読み出しはメモリ上の辞書で完結し、永続層へ届くのは書き込みと削除だけ。
            //   ここを飛ばせた分がそのまま所要時間の差になる。
            if (string.Equals(existing, json, StringComparison.Ordinal))
            {
                state.Unchanged++;
            }
            else
            {
                ctx.Store.Set(key, json);
                state.Writes++;
            }

            state.Stale?.Remove(key);
        }
        state.Pending.Clear();
    }

    static void FinishScan(IExecutionContext ctx, string moduleName, IndexState state)
    {
        state.Done = true;

        // 作り直しのとき、走査し終えても一度も現れなかったエントリを消す。
        // 打ち切られた場合は消さない。走査できなかった範囲にある参照は
        //   「今回ヒットしなかった」だけで、存在しないとは言えないため。
        if (state.Stale != null && state.Stale.Count > 0)
        {
            if (state.Truncated)
            {
                ctx.Logger.LogWarning($"[XrefIndexBuild] {moduleName}: {state.Stale.Count} entries kept because the scan was truncated - they may still be valid outside the scanned range");
            }
            else
            {
                foreach (var k in state.Stale) { ctx.Store.Delete(k); state.Deletes++; }
            }
            state.Stale.Clear();
        }

        var elapsed = DateTime.UtcNow - state.StartedAt;
        var scanned = state.CurrentRva - state.ScanStartRva;
        ctx.Store.Set($"xrefidx:{moduleName}:done", true);
        ctx.Store.Set($"xrefidx:{moduleName}:progress_rva", state.CurrentRva);
        ctx.Store.Set($"xrefidx:{moduleName}:total_hits", state.HitCount);
        ctx.Store.Set($"xrefidx:{moduleName}:scanned_bytes", scanned);
        ctx.Store.Set($"xrefidx:{moduleName}:truncated", state.Truncated);
        var how = state.Truncated
            ? $"STOPPED at 0x{state.CurrentRva:x} - the readable range ended before 0x{state.ScanEndRva:x}"
            : "DONE";
        // 書き込みと削除だけが永続層に届くので、その件数を出す。
        // 変化しなかった件数と並べると、作り直しで実際に何が起きたかが分かる。
        var churn = $"{state.Writes} written, {state.Unchanged} unchanged, {state.Deletes} deleted";
        ctx.Logger.LogInfo($"[XrefIndexBuild] {moduleName}: {how}. {state.HitCount} references indexed from {scanned} byte(s) in {elapsed.TotalSeconds:F1}s ({churn}).");
        Report(state, $"{how}. {state.HitCount} refs from {scanned} byte(s) in {elapsed.TotalSeconds:F1}s ({churn})");
        state.Reg?.Cancel();
    }

    static long ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }
}
