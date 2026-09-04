using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ある番地へ辿り着く「ポインタの道順」を探す。
///
/// ngol.mem.value_scan で見つけた番地は、その起動でしか通用しない--
///   確保のたびに位置が変わるため、次回起動時には別のものが置かれている。
///   モジュールの中の固定位置から始まる道順を見つければ、再起動しても同じ値へ辿り着ける。
///
/// 仕組みは後ろ向きの探索:
///   目標 T に対し「読むと T の少し手前を指している番地 A」を全走査で集める（T = *A + offset）。
///   A がモジュールの内側なら、そこが再起動に耐える起点なので道順が 1 本完成する。
///   そうでなければ A を次の目標にして、同じことを繰り返す。
///
/// 段ごとに全走査が要る。max_level を 1 増やすと走査が 1 回増える。
///   実アプリでは 1 回の全走査が数秒〜数十秒かかるので、非同期で実行すること。
/// ngol.mem.value_scan と同じ打ち切りの問題を持つ。scanned_mb と total_writable_mb を
///   見比べ、届いていないなら max_scan_mb を上げる--0 件は「無い」ではなく「まだ見ていない」。
/// 見つかった道順は、そのとき成り立っていたというだけで、意味のある構造とは限らない
///   （偶然その値が置かれていた番地でも一致する）。再起動して同じ道順が通るかを必ず確かめる。
///
/// 走査するのは書き込み可能な領域だけ。実行時に書き込まれた値しか道順にならないので
///   通常はこれで足りるが、読み取り専用に落とされた領域に起点があると見つからない。
/// ポインタは 8 バイト境界にあるものとして探す。
///
/// 番地を持つ配列はすべて確保時に固定し、走査結果から自分の番地を除いている--
///   除かないと、探索が「自分が今まさに探しているものを並べた配列」を辿り始める。
/// </summary>
[NodeType("ngol.mem.pointer_path", "Memory", "Pointer Path",
    Version = "1.1.1",
    Description = "Find pointer paths that reach a target address from a fixed location inside a module, so the "
      + "address can be found again after a restart - addresses from ngol.mem.value_scan are only valid for the run "
      + "that produced them. Works backwards: it scans for addresses whose stored pointer lands just before the "
      + "target, and repeats with those as the new target until it reaches something inside a module image. Each "
      + "level costs a full memory scan, so raising max_level makes it much slower - run this asynchronously. Every "
      + "path found is walked once and reported as verified or not. Only writable memory is searched and pointers are "
      + "assumed to be 8-byte aligned, so an anchor in read-only data is not found. Compare scanned_mb with "
      + "total_writable_mb, and check candidates_truncated, before believing a result of zero. Re-check surviving "
      + "paths after an actual restart: a path can match by coincidence rather than because it reflects real "
      + "structure.")]
[NodePort("target_address_hex", PortDirection.Input,  "string", Description = "Address to reach, as hex. Typically an address found by ngol.mem.value_scan")]
[NodePort("max_level",          PortDirection.Input,  "number", Description = "Maximum pointer dereferences in a path (default 3). Each level costs one more full scan")]
[NodePort("max_offset",         PortDirection.Input,  "number", Description = "How far past a stored pointer the next address may sit, in bytes (default 2048). Larger finds more paths and more coincidences")]
[NodePort("max_scan_mb",        PortDirection.Input,  "number", Description = "Writable memory to scan per level, in MB (default 512). Compare the scanned_mb output with total_writable_mb")]
[NodePort("max_candidates",     PortDirection.Input,  "number", Description = "Intermediate addresses kept per level (default 2000). One path is kept per address, and candidates_truncated reports when the cap was hit")]
[NodePort("max_results",        PortDirection.Input,  "number", Description = "Stop once this many paths have been found (default 10)")]
[NodePort("static_module",      PortDirection.Input,  "string", Description = "Only accept paths anchored in this module. Empty = any loaded module")]
[NodePort("path_count",         PortDirection.Output, "number", Description = "How many paths were found. Compare with verified_count: a gap means the layout moved while scanning")]
[NodePort("verified_count",     PortDirection.Output, "number", Description = "Paths that still resolved to the target when walked. Anything lower than path_count means the layout moved while scanning")]
[NodePort("best_path",          PortDirection.Output, "string", Description = "Shortest verified path, e.g. 'App.exe+0x1234 -> +0x18 -> +0x40'")]
[NodePort("paths",              PortDirection.Output, "string", Description = "All paths found, one per line, shortest first")]
[NodePort("levels_used",        PortDirection.Output, "number", Description = "Levels actually scanned")]
[NodePort("scanned_mb",         PortDirection.Output, "number", Description = "Total across all levels")]
[NodePort("total_writable_mb",  PortDirection.Output, "number", Description = "All writable memory in the process, for one level. scanned_mb below levels_used times this means the scan never reached the end")]
[NodePort("truncated",          PortDirection.Output, "boolean", Description = "true = max_scan_mb was reached, so paths may exist in memory that was never looked at")]
[NodePort("candidates_truncated", PortDirection.Output, "boolean", Description = "true = max_candidates was reached on some level, so intermediate addresses were discarded and a path through them cannot be found")]
[NodePort("result",             PortDirection.Output, "string", Description = "Paths found, how many verified, levels used and megabytes scanned. Says explicitly when the scan was cut short and how much was never looked at")]
public sealed class PointerPathNode : INode
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int PointerSize = 8;

    public void Execute(IExecutionContext ctx)
    {
        var targetHex = (ctx.GetPortValue("target_address_hex") as string ?? "").Trim();
        if (!NgolAddressResolve.TryParseHex(targetHex, out var targetU) || targetU == 0)
        {
            SetOutputs(ctx, 0, 0, "", "", 0, 0, 0, false, false, $"target_address_hex could not be read: '{targetHex}'");
            return;
        }
        var target = unchecked((long)targetU);

        var maxLevel      = ctx.GetPortValue("max_level") is double lv && lv >= 1 ? (int)lv : 3;
        var maxOffset     = ctx.GetPortValue("max_offset") is double mo && mo >= 0 ? (long)mo : 2048L;
        var maxScanMb     = ctx.GetPortValue("max_scan_mb") is double ms && ms > 0 ? ms : 512.0;
        var maxCandidates = ctx.GetPortValue("max_candidates") is double mc && mc >= 1 ? (int)mc : 2000;
        var maxResults    = ctx.GetPortValue("max_results") is double mr && mr >= 1 ? (int)mr : 10;
        var moduleFilter  = (ctx.GetPortValue("static_module") as string ?? "").Trim();
        var maxScanBytes  = (long)(maxScanMb * 1024 * 1024);

        var modules = NgolModuleDefault.List(4096, out _);
        if (modules.Count == 0)
        {
            SetOutputs(ctx, 0, 0, "", "", 0, 0, 0, false, false, "could not enumerate modules, so a path could not be anchored anywhere");
            return;
        }
        // 番地からモジュールを引くため、ベース順に並べておく。
        modules.Sort((x, y) => x.Base.CompareTo(y.Base));
        var moduleBases = new long[modules.Count];
        for (int i = 0; i < modules.Count; i++) moduleBases[i] = modules[i].Base;

        var totalWritableMb = NgolMemoryRegions.MeasureWritableTotal() / (1024.0 * 1024.0);

        // 番地を持つ配列は、確保しなおさず最初に取って固定する。
        //   List で伸ばすと、伸びる前の配列が「目標の番地が並んだまま」ゴミとして残り、
        //   次の段の走査がそれを拾う（自分の作業領域を辿り始める）。
        var slots = Math.Max(1, maxCandidates);
        var state = new ScanState
        {
            CurAddrs    = new long[slots],
            CurOffsets  = new long[slots][],
            NextAddrs   = new long[slots],
            NextOffsets = new long[slots][],
            ResultAddrs   = new long[maxResults],
            ResultOffsets = new long[maxResults][],
            MaxOffset     = maxOffset,
            MaxCandidates = maxCandidates,
            MaxResults    = maxResults,
            Modules       = modules,
            ModuleBases   = moduleBases,
            ModuleFilter  = moduleFilter,
        };

        var buffer = new byte[ChunkSize];
        var pins = new List<GCHandle>
        {
            GCHandle.Alloc(buffer, GCHandleType.Pinned),
            GCHandle.Alloc(state.CurAddrs, GCHandleType.Pinned),
            GCHandle.Alloc(state.NextAddrs, GCHandleType.Pinned),
            GCHandle.Alloc(state.ResultAddrs, GCHandleType.Pinned),
        };
        var sizes = new[] { (long)buffer.Length, slots * 8L, slots * 8L, maxResults * 8L };

        int levelsUsed = 0;
        try
        {
            state.Excluded = new long[pins.Count][];
            for (int i = 0; i < pins.Count; i++)
            {
                var start = pins[i].AddrOfPinnedObject().ToInt64();
                state.Excluded[i] = new[] { start, start + sizes[i] };
            }

            state.CurAddrs[0] = target;
            state.CurOffsets[0] = Array.Empty<long>();
            state.CurCount = 1;

            for (int level = 1; level <= maxLevel && state.ResultCount < maxResults && state.CurCount > 0; level++)
            {
                levelsUsed = level;

                // 走査中は「読んだ値の少し先にある目標」を二分探索で引くので、番地順に並べておく。
                Array.Sort(state.CurAddrs, state.CurOffsets, 0, state.CurCount);
                state.NextCount = 0;

                ScanLevel(state, buffer, maxScanBytes);

                // 使い終わった段の番地を消してから入れ替える。残しておくと次の段の目標と
                // 一致する値が居座り、自分の配列が候補として出てくる。
                Array.Clear(state.CurAddrs, 0, state.CurCount);
                Swap(state);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            Array.Clear(state.CurAddrs, 0, state.CurAddrs.Length);
            Array.Clear(state.NextAddrs, 0, state.NextAddrs.Length);
            foreach (var pin in pins) pin.Free();
        }

        // 見つけた道順を実際に 1 度だけ辿る。探索の誤りと、走査中に配置が動いた場合の
        //   両方がここで表に出る--「見つかった」と「今も通る」は別のことなので分けて返す。
        var order = new int[state.ResultCount];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => state.ResultOffsets[a].Length.CompareTo(state.ResultOffsets[b].Length));

        var sb = new StringBuilder();
        string best = "";
        int verifiedCount = 0;
        foreach (var i in order)
        {
            var addr = state.ResultAddrs[i];
            var offsets = state.ResultOffsets[i];
            var ok = Walk(addr, offsets, target);
            if (ok) verifiedCount++;

            var text = Describe(addr, offsets, modules, moduleBases);
            sb.Append(text).Append(ok ? "  [verified]" : "  [did not resolve when walked]").Append('\n');
            if (best.Length == 0 && ok) best = text;
        }
        Array.Clear(state.ResultAddrs, 0, state.ResultAddrs.Length);

        var scannedMb = state.ScannedBytes / (1024.0 * 1024.0);
        var perLevelMb = levelsUsed > 0 ? scannedMb / levelsUsed : 0;
        var note = new StringBuilder();
        if (state.Truncated)
            note.Append($" (stopped at max_scan_mb - {totalWritableMb - perLevelMb:F0} MB per level was never looked at; raise max_scan_mb)");
        if (state.CandidatesTruncated)
            note.Append(" (hit max_candidates - intermediate addresses were discarded; raise max_candidates or lower max_offset)");

        SetOutputs(ctx, state.ResultCount, verifiedCount, best, sb.ToString(), levelsUsed, scannedMb, totalWritableMb,
            state.Truncated, state.CandidatesTruncated,
            $"{state.ResultCount} path(s), {verifiedCount} verified, {levelsUsed} level(s), {scannedMb:F1} MB scanned{note}");
    }

    /// <summary>探索の途中の状態。番地を持つ配列は使い回すので、ここでまとめて持つ。</summary>
    private sealed class ScanState
    {
        public long[] CurAddrs;    public long[][] CurOffsets;    public int CurCount;
        public long[] NextAddrs;   public long[][] NextOffsets;   public int NextCount;
        public long[] ResultAddrs; public long[][] ResultOffsets; public int ResultCount;
        public long[][] Excluded;
        public long MaxOffset;
        public int MaxCandidates, MaxResults;
        public List<NgolModuleDefault.ModuleEntry> Modules;
        public long[] ModuleBases;
        public string ModuleFilter;
        public long ScannedBytes;
        public bool Truncated, CandidatesTruncated;
    }

    private static void Swap(ScanState s)
    {
        var a = s.CurAddrs;   s.CurAddrs   = s.NextAddrs;   s.NextAddrs   = a;
        var o = s.CurOffsets; s.CurOffsets = s.NextOffsets; s.NextOffsets = o;
        s.CurCount = s.NextCount;
    }

    /// <summary>1 段ぶんの全走査。</summary>
    private static void ScanLevel(ScanState s, byte[] buffer, long maxScanBytes)
    {
        // 読んだ値がこの範囲の外なら、どの目標にも届かない。ほとんどの番地はここで落ちる。
        var lowValue  = s.CurAddrs[0] - s.MaxOffset;
        var highValue = s.CurAddrs[s.CurCount - 1];

        bool levelTruncated = false;
        foreach (var region in NgolMemoryRegions.EnumerateWritableRegions(maxScanBytes, x => levelTruncated = x))
        {
            var regionBase = region.Base.ToInt64();
            long pos = 0;
            while (pos < region.Size)
            {
                var want = (int)Math.Min(ChunkSize, region.Size - pos);
                var got = NgolSafeMemory.Read(new IntPtr(regionBase + pos), buffer, 0, want);
                if (got <= 0) break;
                s.ScannedBytes += got;

                // ポインタは 8 バイト境界に置かれる。領域の途中から読んでも境界に乗るよう揃える。
                var chunkBase = regionBase + pos;
                var first = (int)((PointerSize - (chunkBase % PointerSize)) % PointerSize);

                for (int off = first; off + PointerSize <= got; off += PointerSize)
                {
                    var value = BitConverter.ToInt64(buffer, off);
                    if (value < lowValue || value > highValue) continue;

                    var addr = chunkBase + off;
                    if (IsExcluded(s.Excluded, addr)) continue;

                    // value 以上で最も小さい目標から見る。value + max_offset を超えたら届かない。
                    var idx = LowerBound(s.CurAddrs, s.CurCount, value);
                    if (idx >= s.CurCount) continue;
                    var t = s.CurAddrs[idx];
                    if (t - value > s.MaxOffset) continue;

                    var offsets = Prepend(t - value, s.CurOffsets[idx]);
                    var moduleIndex = FindModule(s.Modules, s.ModuleBases, addr);
                    if (moduleIndex >= 0)
                    {
                        if (s.ModuleFilter.Length > 0 &&
                            s.Modules[moduleIndex].Name.IndexOf(s.ModuleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        s.ResultAddrs[s.ResultCount] = addr;
                        s.ResultOffsets[s.ResultCount] = offsets;
                        s.ResultCount++;
                        if (s.ResultCount >= s.MaxResults) return;
                    }
                    else if (s.NextCount < s.MaxCandidates)
                    {
                        if (!Contains(s.NextAddrs, s.NextCount, addr))
                        {
                            s.NextAddrs[s.NextCount] = addr;
                            s.NextOffsets[s.NextCount] = offsets;
                            s.NextCount++;
                        }
                    }
                    else
                    {
                        // 黙って捨てると「道順が無い」と読まれる。捨てたことは必ず返す。
                        s.CandidatesTruncated = true;
                    }
                }

                pos += got;
                if (got < want) break;
            }
        }
        if (levelTruncated) s.Truncated = true;
    }

    /// <summary>道順を実際に辿り、目標へ着くかを確かめる。</summary>
    private static bool Walk(long start, long[] offsets, long target)
    {
        var buf = new byte[PointerSize];
        try
        {
            var cur = start;
            foreach (var off in offsets)
            {
                if (NgolSafeMemory.Read(new IntPtr(cur), buf, 0, PointerSize) < PointerSize) return false;
                cur = BitConverter.ToInt64(buf, 0) + off;
            }
            return cur == target;
        }
        finally
        {
            // 辿った先の番地が載ったまま残ると、次の走査がこのバッファを候補として拾う。
            Array.Clear(buf, 0, buf.Length);
        }
    }

    private static string Describe(long address, long[] offsets,
                                    List<NgolModuleDefault.ModuleEntry> modules, long[] moduleBases)
    {
        var sb = new StringBuilder();
        var index = FindModule(modules, moduleBases, address);
        if (index >= 0) sb.Append(modules[index].Name).Append("+0x").Append((address - modules[index].Base).ToString("x"));
        else sb.Append("0x").Append(address.ToString("x"));
        foreach (var off in offsets) sb.Append(" -> +0x").Append(off.ToString("x"));
        return sb.ToString();
    }

    private static long[] Prepend(long head, long[] rest)
    {
        var combined = new long[rest.Length + 1];
        combined[0] = head;
        Array.Copy(rest, 0, combined, 1, rest.Length);
        return combined;
    }

    private static bool Contains(long[] values, int count, long value)
    {
        for (int i = 0; i < count; i++) if (values[i] == value) return true;
        return false;
    }

    private static bool IsExcluded(long[][] ranges, long address)
    {
        for (int i = 0; i < ranges.Length; i++)
            if (address >= ranges[i][0] && address < ranges[i][1]) return true;
        return false;
    }

    /// <summary>value 以上である最初の要素の位置。無ければ count を返す。</summary>
    private static int LowerBound(long[] sorted, int count, long value)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>番地を含むモジュールの位置。どれにも属さないなら -1。</summary>
    private static int FindModule(List<NgolModuleDefault.ModuleEntry> modules, long[] bases, long address)
    {
        var idx = LowerBound(bases, bases.Length, address);
        // ベース以上で最初のものを引いているので、含む可能性があるのは 1 つ前。
        if (idx < bases.Length && bases[idx] == address) return idx;
        idx--;
        if (idx < 0) return -1;
        return address < modules[idx].Base + modules[idx].Size ? idx : -1;
    }

    private static void SetOutputs(IExecutionContext ctx, int pathCount, int verifiedCount, string best, string paths,
                                    int levelsUsed, double scannedMb, double totalWritableMb,
                                    bool truncated, bool candidatesTruncated, string result)
    {
        ctx.SetPortValue("path_count", (double)pathCount);
        ctx.SetPortValue("verified_count", (double)verifiedCount);
        ctx.SetPortValue("best_path", best);
        ctx.SetPortValue("paths", paths);
        ctx.SetPortValue("levels_used", (double)levelsUsed);
        ctx.SetPortValue("scanned_mb", scannedMb);
        ctx.SetPortValue("total_writable_mb", totalWritableMb);
        ctx.SetPortValue("truncated", truncated);
        ctx.SetPortValue("candidates_truncated", candidatesTruncated);
        ctx.SetPortValue("result", result);
    }
}
