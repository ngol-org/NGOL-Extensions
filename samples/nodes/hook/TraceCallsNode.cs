using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NgolExt.NativeHook;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 見張った関数の呼び出しを 1 件ずつ順番に記録する。
///
/// ngol.hook.watch_function との違いは「並びが残ること」。あちらは直近 1 件しか持たないので、
/// 見に行く間隔の中で複数回呼ばれると途中が消える。しかも消えたことが分からない。
/// このノードは置き場を預けて溜めさせ、消えた件数を lost_total として必ず出す。
///
/// 元関数は必ず呼ぶ。対象の挙動は変えない。止めたい場合は ngol.hook.skip_function を使う。
///
/// 主な使い道は「誰が確保しているのか」を突き止めること。
/// 確保系の関数に張って 1 往復ぶん動かし、記録を呼び出し元ごとに合計する。
/// 大きな 1 回を探しても出てこないことがあり、合計で見て初めて出てくる。
///
/// 記録に入るのは引数だけで、元関数が返した値は入らない。確保がどこを取ったかは
/// 戻り値なので、解放と番地で対にはできない。合計で分かるのは「確保した量」であって
/// 「返っていない量」ではない。返っていない量はプロセスの増分で測る。
/// 戻り番地からモジュールの載り位置を引けばモジュール名 + RVA になるので、
/// そのまま ngol.code.disasm や ngol.code.xref_find へ渡せる。
///
/// 呼び出し元の連なりを何段まで残すかは frames で決める。既定の 0 なら 1 段目だけ。
/// 段を頼むと 1 件あたりの費用が 2 桁上がるので、必要なときだけ増やす。
/// カーネル側まで遡るなら ETW（WPR）を使う。こちらはユーザー空間の範囲。
///
/// 対象の書き方（1 行 1 つ）:
///   module!Export            エクスポート名で引く
///   module!0x1234            RVA で指す
///   module!Export:2          レジスタ 4 個を超える引数の個数を添える（0-8）
///
/// 同じ入力でもう一度実行しても張り直さない。今の件数を返すだけ。
/// 張り直したいときは一度 enabled を false にする。
///
/// 書き出し先は追記のみで、消さない。実験ごとに別の名前にすること。
///
/// 置き場はこのノードが確保して拡張へ預ける。拡張も ngol_native.dll も確保しない。
/// 外すときは「預かりを外す -> 解除 -> 少し置いてから返す」の順を守る。
///
/// ngol.ext.native-hook 拡張（Api/Impl）が読み込まれている必要がある。
/// </summary>
[NodeType("ngol.hook.trace_calls", "Hook", "Trace Calls",
    Version = "1.3.1",
    Description =
        "Record every call to the watched native functions in order, one line per call, with the caller's return "
      + "address and the four register arguments. Unlike ngol.hook.watch_function, which keeps only the most recent "
      + "call, this keeps the sequence and reports how many entries were dropped, so a gap is never silent. The "
      + "original function always runs. Typical use: hook an allocation function, exercise the operation once, then "
      + "total the recorded bytes per caller - the culprit often shows up only in the total, not as one large call. "
      + "Set frames above 0 to also record that many callers above the immediate one, at roughly a hundred times "
      + "the cost per call. Subtract a module's load address from a return address to get module+RVA for "
      + "ngol.code.disasm or ngol.code.xref_find. Running it again with the same inputs does not re-arm - it "
      + "just reports the counts so far. Requires the native-hook extension.")]
[NodePort("targets",    PortDirection.Input,  "string",  Description = "One target per line: module!Export, module!0xRVA, or either with :N appended to declare N (0-8) stack-passed args beyond the four register args")]
[NodePort("path",       PortDirection.Input,  "string",  Description = "File to append the records to. Each batch is appended and the file is closed again, so the records survive a crash")]
[NodePort("capacity",   PortDirection.Input,  "number",  Description = "Entries held per target before the oldest are overwritten. Must be a power of two. Default 4096 (about 192 KB per target)")]
[NodePort("poll_ms",    PortDirection.Input,  "number",  Description = "How often to drain the entries, in milliseconds. Because the sequence is kept, this can be coarse. Default 50")]
[NodePort("frames",     PortDirection.Input,  "number",  Description = "How many stack frames above the immediate caller to record, 0-64. Default 0. Above 0 costs roughly a hundred times more per call, so raise it only when one caller is not enough")]
[NodePort("enabled",    PortDirection.Input,  "boolean", Description = "true = install the hooks and start recording, false = stop and release everything")]
[NodePort("installed",  PortDirection.Output, "number",  Description = "How many targets were hooked")]
[NodePort("failed",     PortDirection.Output, "string",  Description = "Targets that could not be hooked, with the reason for each")]
[NodePort("lost_total", PortDirection.Output, "number",  Description = "Entries dropped because the drain came too late. Non-zero means the record is partial - raise capacity or lower poll_ms")]
[NodePort("recorded_total", PortDirection.Output, "number", Description = "How many calls have been written so far, across all targets. Zero with a non-zero installed means the targets were hooked but never reached")]
[NodePort("recorded_by_target", PortDirection.Output, "string", Description = "Per-target count, one line each. A target sitting at 0 was never called during the window - check the target itself before suspecting the recording")]
[NodePort("active",     PortDirection.Output, "boolean", Description = "Whether recording is currently running")]
[NodePort("result",     PortDirection.Output, "string",  Description = "Status or error message")]
public sealed class TraceCallsNode : INode
{
    // ホットリロードをまたいで残すのは、このファイルで定義していない型の値だけにする。
    // ここで定義した型は入れ替わるので、預けても取り出せない。
    private const string GenKey  = "NgolTraceCalls_Generation";
    private const string RegKey  = "NgolTraceCalls_Registration";
    private const string LostKey = "NgolTraceCalls_LostTotal";
    private const string HandlesKey = "NgolTraceCalls_Handles";
    // 的ごとの件数。ホットリロードをまたぐので、ここで定義していない型で持つ。
    // 中身を書き換えるだけなので、ポーリングの周期で確保が起きない。
    private const string CountsKey = "NgolTraceCalls_Counts";
    private const string NamesKey  = "NgolTraceCalls_Names";
    // 同じ頼みで再実行されたかを見分ける。違えば張り直す。
    private const string SigKey    = "NgolTraceCalls_Signature";
    // 溜まっている分をその場で書き切るための呼び口と、書き出しの取り合いを防ぐロック。
    // どちらもここで定義していない型なので、ホットリロードをまたいで取り出せる。
    private const string FlushKey  = "NgolTraceCalls_Flush";
    private const string GateKey   = "NgolTraceCalls_Gate";

    // 1 件の決まったフィールドは long 6 個: seq, returnAddress, a0, a1, a2, a3。
    // frames を頼むとその後ろに段が続く。ずれていないかは RecordSize と突き合わせて確かめる。
    private const int FixedFields = 6;
    private const int MaxFrames = 64;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    // 置き場はこのノードが確保して拡張へ預ける。拡張も ngol_native.dll も確保しない。
    private const uint MEM_COMMIT     = 0x1000;
    private const uint MEM_RESERVE    = 0x2000;
    private const uint MEM_RELEASE    = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    // 大きさは 1 件のバイト数 x 件数。足りなければ SetRecordBuffer が断る。
    private static IntPtr AllocBuffer(int capacity, int recordSize)
    {
        var bytes = (UIntPtr)((ulong)capacity * (ulong)recordSize);
        return VirtualAlloc(IntPtr.Zero, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    }

    private sealed class Target
    {
        public int Index;       // 件数の控えの何番目か
        public string Name;
        public IntPtr Hook;
        public IntPtr Buffer;
        public long FirstSeq;   // 貸した時点で書かれ始める番号
        public long NextSeq;    // 次に欲しい番号
    }

    public void Execute(IExecutionContext ctx)
    {
        var svc = ctx.GetExtensionService<INativeHookService>();
        if (svc == null)
        {
            Finish(ctx, 0, string.Empty, 0, false, "ngol.ext.native-hook extension not loaded");
            return;
        }

        var enabled  = ReadBool(ctx, "enabled", false);
        var capacity = ReadInt(ctx, "capacity", 4096);
        var pollMs   = ReadInt(ctx, "poll_ms", 50);
        var frames   = ReadInt(ctx, "frames", 0);
        var path     = ReadString(ctx, "path", string.Empty);
        var targets  = ReadString(ctx, "targets", string.Empty);

        // 同じ頼みで再実行されたときは、畳まずに今の件数を返す。
        // 状態を見るつもりの再実行で自分の記録を畳んでしまうのを防ぐ。
        var signature = string.Join("|", new[]
        {
            targets, path,
            capacity.ToString(CultureInfo.InvariantCulture),
            pollMs.ToString(CultureInfo.InvariantCulture),
            frames.ToString(CultureInfo.InvariantCulture),
        });
        if (enabled && IsStillRunning() && (AppDomain.CurrentDomain.GetData(SigKey) as string) == signature)
        {
            Finish(ctx, ReadNames().Length, string.Empty, ReadLostTotal(), true,
                "already recording to " + path + " (same inputs, so nothing was re-armed)");
            return;
        }

        // 前の世代が動いていれば、溜まっている分を書き切ってから畳む。
        StopPrevious(svc, out var lastTotal, out var lastCounts);

        if (!enabled)
        {
            Finish(ctx, 0, string.Empty, ReadLostTotal(), false, "disabled", lastTotal, lastCounts);
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            Finish(ctx, 0, string.Empty, 0, false, "path is empty");
            return;
        }
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            Finish(ctx, 0, string.Empty, 0, false, "capacity must be a power of two");
            return;
        }
        if (frames < 0 || frames > MaxFrames)
        {
            Finish(ctx, 0, string.Empty, 0, false, $"frames must be 0-{MaxFrames}");
            return;
        }
        if (pollMs < 1) pollMs = 1;

        var stride = FixedFields + frames;
        var recordSize = svc.RecordSize(frames);
        if (recordSize != stride * sizeof(long))
        {
            Finish(ctx, 0, string.Empty, 0, false,
                $"the extension records {recordSize} bytes per call, this node expects {stride * sizeof(long)}");
            return;
        }
        var list = new List<Target>();
        var failed = new StringBuilder();
        // 途中で投げても、そこまでに掴んだものは返す。
        // 返さずに抜けると、持ち主の居ないフックが張られたまま残る。
        try
        {
            foreach (var line in SplitLines(targets))
            {
                if (!TryResolve(line, out var addr, out var extraArgs, out var why))
                {
                    AppendFailure(failed, line, why);
                    continue;
                }
                if (!svc.Install(addr, out var hook))
                {
                    AppendFailure(failed, line, Describe(svc));
                    continue;
                }
                svc.SetCallOriginal(hook, true);
                if (extraArgs > 0) svc.SetExtraStackArgs(hook, extraArgs);

                var buffer = AllocBuffer(capacity, recordSize);
                if (buffer == IntPtr.Zero)
                {
                    svc.Uninstall(hook);
                    AppendFailure(failed, line, "could not allocate the ring buffer");
                    continue;
                }
                if (!svc.SetRecordBuffer(hook, buffer, capacity, frames, out var firstSeq))
                {
                    var why2 = Describe(svc);
                    svc.Uninstall(hook);
                    VirtualFree(buffer, UIntPtr.Zero, MEM_RELEASE);
                    AppendFailure(failed, line, why2);
                    continue;
                }
                list.Add(new Target
                {
                    Index = list.Count,
                    Name = line, Hook = hook, Buffer = buffer, FirstSeq = firstSeq, NextSeq = firstSeq,
                });
                StoreHandles(list);
            }
        }
        catch
        {
            ReleaseStored(svc);
            throw;
        }

        if (list.Count == 0)
        {
            Finish(ctx, 0, failed.ToString(), 0, false, "no target could be hooked");
            return;
        }

        var generation = ReadGeneration() + 1;
        AppDomain.CurrentDomain.SetData(GenKey, generation);
        AppDomain.CurrentDomain.SetData(LostKey, 0L);

        // 件数の控えは先に用意する。以後は中身を書き換えるだけ。
        var counts = new long[list.Count];
        var names = new string[list.Count];
        for (var i = 0; i < list.Count; i++) names[i] = list[i].Name;
        AppDomain.CurrentDomain.SetData(CountsKey, counts);
        AppDomain.CurrentDomain.SetData(NamesKey, names);
        AppDomain.CurrentDomain.SetData(SigKey, signature);

        // 読み出しに使う入れ物は先に確保して使い回す。
        // 覗きに行くたびに確保すると、確保系の関数に張っているときに自分の記録が混ざる。
        var scratch = new long[stride];
        var text = new StringBuilder(4096);
        var watch = Stopwatch.StartNew();
        long lastTick = 0;

        File.AppendAllText(path, Header(list, capacity, pollMs, frames));

        // 書き出しは 2 か所（ポーリングと、止めるとき）から呼ばれる。ロックで重ならないようにする。
        // ここは 50ms 間隔の処理で、フックの本体ではない。
        var gate = new object();
        Action flushNow = () =>
        {
            lock (gate) { Drain(svc, list, scratch, capacity, stride, text, path, counts); }
        };
        AppDomain.CurrentDomain.SetData(GateKey, gate);
        AppDomain.CurrentDomain.SetData(FlushKey, flushNow);

        var reg = ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () =>
            {
                if (ReadGeneration() != generation) return;   // 新しい世代に置き換わっている
                var now = watch.ElapsedMilliseconds;
                if (now - lastTick < pollMs) return;
                lastTick = now;
                flushNow();
            },
            // 世代で弾かない。自分が確保したものは、誰が次に来ていようと自分で返す。
            //
            // 書き出しは、まだ置き場が生きているときだけ行う。
            // 止める側が先に書き切って返していれば FlushKey は空になっており、ここでは触らない。
            OnStop = () =>
            {
                if (ReadGeneration() == generation &&
                    AppDomain.CurrentDomain.GetData(FlushKey) is Action pending)
                {
                    try { pending(); } catch { }
                }
                ReleaseStored(svc);
            },
        });
        AppDomain.CurrentDomain.SetData(RegKey, reg);

        Finish(ctx, list.Count, failed.ToString(), 0, true,
            $"recording {list.Count} target(s) to {path}", 0, FormatCounts());
    }

    private static string Header(List<Target> list, int capacity, int pollMs, int frames)
    {
        var sb = new StringBuilder();
        sb.Append("# trace_calls start capacity=").Append(capacity)
          .Append(" poll_ms=").Append(pollMs).Append(" frames=").Append(frames).AppendLine();
        foreach (var t in list) sb.Append("# target ").AppendLine(t.Name);
        sb.Append("# seq target ret a0 a1 a2 a3");
        for (var i = 0; i < frames; i++) sb.Append(" f").Append(i);
        sb.AppendLine();
        return sb.ToString();
    }

    // 溜まっている分を書き出す。見つけたぶんだけ追記して毎回閉じる。
    // 溜めてから書くと、落ちた瞬間の分がまるごと消える。
    //
    // 置き場はこのノードのものなので、拡張を通さず自分で読む。
    // 読み方（後入れ先出しにする・絞る・並べ替える等）はここで決められる。
    private static void Drain(INativeHookService svc, List<Target> list, long[] scratch,
                              int capacity, int stride, StringBuilder text, string path, long[] counts)
    {
        text.Length = 0;
        long lostHere = 0;
        foreach (var t in list)
        {
            svc.Read(t.Hook, out var count, out _, out _, out _, out _);

            // まだ置き場に残っている最も古い番号。貸す前の分はそもそも書かれていない。
            var oldest = count - capacity + 1;
            if (oldest < t.FirstSeq) oldest = t.FirstSeq;
            if (t.NextSeq < oldest)
            {
                lostHere += oldest - t.NextSeq;
                t.NextSeq = oldest;
            }

            while (t.NextSeq <= count)
            {
                if (!TryReadRecord(t, capacity, stride, scratch)) break;
                text.Append(scratch[0].ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(t.Name).Append(' ')
                    .Append(Hex(scratch[1])).Append(' ')
                    .Append(Hex(scratch[2])).Append(' ')
                    .Append(Hex(scratch[3])).Append(' ')
                    .Append(Hex(scratch[4])).Append(' ')
                    .Append(Hex(scratch[5]));
                // 段は 0 で埋めてある。埋まっている所までを書く。
                for (var i = FixedFields; i < stride && scratch[i] != 0; i++)
                    text.Append((char)32).Append(Hex(scratch[i]));
                text.AppendLine();
                if (t.Index < counts.Length) counts[t.Index]++;
                t.NextSeq++;
            }
        }
        if (lostHere > 0)
        {
            AppDomain.CurrentDomain.SetData(LostKey, ReadLostTotal() + lostHere);
            text.Append("# lost ").Append(lostHere.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        if (text.Length > 0) File.AppendAllText(path, text.ToString());
    }

    // 目当ての 1 件を採る。書きかけ、または読んでいる最中に上書きされたら false。
    //
    // 通し番号を前後で 2 回読み、両方が目当ての番号と一致したときだけ採る。
    // 書き手は「書きかけの印(0) -> 中身 -> 通し番号」の順に書くので、
    // これで一周して上書き中の中身を掴まずに済む。
    private static bool TryReadRecord(Target t, int capacity, int stride, long[] scratch)
    {
        var slot = (t.NextSeq - 1) & (capacity - 1);
        var at = new IntPtr(t.Buffer.ToInt64() + slot * stride * sizeof(long));

        if (Marshal.ReadInt64(at) != t.NextSeq) return false;
        Marshal.Copy(at, scratch, 0, stride);
        if (Marshal.ReadInt64(at) != t.NextSeq) return false;
        return scratch[0] == t.NextSeq;
    }

    // 外す順序: 貸すのをやめる -> 解除 -> 少し置く -> 返す。
    // 外した直後に返すと、書きかけの 1 件が返却済みの番地へ落ちる。
    //
    // 何を持っているかは AppDomain に文字列で控える。ここで定義した型では控えられない
    // （ホットリロードで入れ替わるので取り出せなくなる）。
    // 控えから解放するので、次の世代も、リロード後の版も、同じ手順で片付けられる。
    private static void ReleaseStored(INativeHookService svc)
    {
        var stored = AppDomain.CurrentDomain.GetData(HandlesKey) as string;
        AppDomain.CurrentDomain.SetData(HandlesKey, null);
        if (string.IsNullOrEmpty(stored)) return;

        var hooks = new List<IntPtr>();
        var buffers = new List<IntPtr>();
        foreach (var pair in stored.Split(','))
        {
            var kv = pair.Split(':');
            if (kv.Length != 2) continue;
            if (!long.TryParse(kv[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) continue;
            if (!long.TryParse(kv[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) continue;
            hooks.Add(new IntPtr(h));
            buffers.Add(new IntPtr(b));
        }
        foreach (var h in hooks) svc.SetRecordBuffer(h, IntPtr.Zero, 0, 0, out _);
        foreach (var h in hooks) svc.Uninstall(h);
        System.Threading.Thread.Sleep(50);
        foreach (var b in buffers) VirtualFree(b, UIntPtr.Zero, MEM_RELEASE);
    }

    private static void StoreHandles(List<Target> list)
    {
        var sb = new StringBuilder();
        foreach (var t in list)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(t.Hook.ToInt64().ToString("x", CultureInfo.InvariantCulture)).Append(':')
              .Append(t.Buffer.ToInt64().ToString("x", CultureInfo.InvariantCulture));
        }
        AppDomain.CurrentDomain.SetData(HandlesKey, sb.ToString());
    }

    // 前の世代を止めて、その場で片付ける。
    //
    // Cancel() は OnStop を呼ばない（Core はホストの次の周回でメインスレッドから呼ぶ）。
    // なので Cancel だけで次を設置しにいくと、前のフックが残っていて ALREADY_HOOKED になる。
    //
    // 溜まっている分は、返す前にここで書き切る。OnStop に任せてはいけない。
    // 任せると、呼ばれるのがホストの次の周回になり、その前に置き場を返すことになる。
    // 実測: 仕掛ける・動かす・止める がポーリングの 1 周期に収まると、1 件も書かれなかった。
    private static void StopPrevious(INativeHookService svc, out long lastTotal, out string lastByTarget)
    {
        if (AppDomain.CurrentDomain.GetData(RegKey) is IPersistentRegistration reg) reg.Cancel();
        AppDomain.CurrentDomain.SetData(RegKey, null);

        if (AppDomain.CurrentDomain.GetData(FlushKey) is Action flush)
        {
            try { flush(); } catch { }
        }
        AppDomain.CurrentDomain.SetData(FlushKey, null);
        AppDomain.CurrentDomain.SetData(GateKey, null);

        // 控えを取るのは書き切った後。先に取ると、最後の分が数に入らない。
        lastTotal = TotalCount();
        lastByTarget = FormatCounts();

        // 件数の控えも捨てる。残すと、次の頼みの結果として前の数を返してしまう。
        AppDomain.CurrentDomain.SetData(CountsKey, null);
        AppDomain.CurrentDomain.SetData(NamesKey, null);
        AppDomain.CurrentDomain.SetData(SigKey, null);
        ReleaseStored(svc);
    }

    private static long ReadGeneration()
        => AppDomain.CurrentDomain.GetData(GenKey) is long g ? g : 0L;

    private static bool IsStillRunning()
        => AppDomain.CurrentDomain.GetData(RegKey) is IPersistentRegistration reg && reg.IsActive;

    private static long[] ReadCounts()
        => AppDomain.CurrentDomain.GetData(CountsKey) as long[] ?? new long[0];

    private static string[] ReadNames()
        => AppDomain.CurrentDomain.GetData(NamesKey) as string[] ?? new string[0];

    private static long TotalCount()
    {
        var total = 0L;
        foreach (var c in ReadCounts()) total += c;
        return total;
    }

    // 的ごとに 1 行。0 の行が見えたら、疑うのは記録ではなくその的が呼ばれたかどうか。
    private static string FormatCounts()
    {
        var counts = ReadCounts();
        var names = ReadNames();
        var sb = new StringBuilder();
        for (var i = 0; i < names.Length; i++)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(names[i]).Append(' ').Append((i < counts.Length ? counts[i] : 0L)
                .ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static long ReadLostTotal()
        => AppDomain.CurrentDomain.GetData(LostKey) is long v ? v : 0L;

    private static bool TryResolve(string spec, out IntPtr address, out int extraArgs, out string why)
    {
        address = IntPtr.Zero;
        extraArgs = 0;
        why = string.Empty;

        var body = spec;
        var colon = body.LastIndexOf(':');
        if (colon > 0)
        {
            var tail = body.Substring(colon + 1);
            if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n < 0 || n > 8) { why = "the arg count after ':' must be 0-8"; return false; }
                extraArgs = n;
                body = body.Substring(0, colon);
            }
        }

        var bang = body.IndexOf('!');
        if (bang <= 0 || bang == body.Length - 1)
        {
            why = "expected module!Export or module!0xRVA";
            return false;
        }
        var moduleName = body.Substring(0, bang).Trim();
        var symbol = body.Substring(bang + 1).Trim();

        var module = GetModuleHandleA(moduleName);
        if (module == IntPtr.Zero) { why = "module is not loaded"; return false; }

        if (symbol.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(symbol.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rva))
            {
                why = "the RVA is not a hex number";
                return false;
            }
            address = new IntPtr(module.ToInt64() + rva);
            return true;
        }

        address = GetProcAddress(module, symbol);
        if (address == IntPtr.Zero) { why = "the module does not export that name"; return false; }
        return true;
    }

    private static string Describe(INativeHookService svc)
    {
        var err = svc.GetLastError();
        return string.IsNullOrEmpty(err) ? "install failed" : err;
    }

    private static void AppendFailure(StringBuilder sb, string line, string why)
    {
        if (sb.Length > 0) sb.Append("; ");
        sb.Append(line).Append(" -> ").Append(why);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            yield return line;
        }
    }

    private static string Hex(long v) => "0x" + v.ToString("x", CultureInfo.InvariantCulture);

    private static void Finish(IExecutionContext ctx, int installed, string failed,
                               long lostTotal, bool active, string result,
                               long recordedTotal = -1, string recordedByTarget = null)
    {
        ctx.SetPortValue("installed", installed);
        ctx.SetPortValue("failed", failed);
        ctx.SetPortValue("lost_total", lostTotal);
        ctx.SetPortValue("active", active);
        ctx.SetPortValue("result", result);
        ctx.SetPortValue("recorded_total", (double)(recordedTotal >= 0 ? recordedTotal : TotalCount()));
        ctx.SetPortValue("recorded_by_target", recordedByTarget ?? FormatCounts());
    }

    private static string ReadString(IExecutionContext ctx, string port, string fallback)
    {
        var v = ctx.GetPortValue(port);
        return v == null ? fallback : Convert.ToString(v, CultureInfo.InvariantCulture) ?? fallback;
    }

    private static bool ReadBool(IExecutionContext ctx, string port, bool fallback)
    {
        var v = ctx.GetPortValue(port);
        if (v == null) return fallback;
        if (v is bool b) return b;
        return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(IExecutionContext ctx, string port, int fallback)
    {
        var v = ctx.GetPortValue(port);
        if (v == null) return fallback;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }
}
