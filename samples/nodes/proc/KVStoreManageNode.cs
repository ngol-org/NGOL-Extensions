using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// KVStore（ctx.Store）の中身を調べ、書き出し・読み戻し・削除まで行う。
///
/// KVStore はセッションをまたいで値を持ち続けるため、長く使っていると
/// 「いつ誰が入れたのか分からないデータ」が積み上がりやすい。永続ファイルは
/// バイナリ形式のこともあり、中身を直接読むのが難しい。
/// このノードは KVStore の API だけを使って中身を俯瞰し、保存先の外へ退避し、戻せるようにする。
///
/// mode:
///   summary ... キーを接頭辞ごとにまとめ、件数と概算サイズを集計する（既定）
///   list    ... 個々のキーと値の先頭部分を一覧表示する（limit 件まで）
///   export  ... 全件（prefix 指定時はその範囲）を CSV または JSON で書き出す
///   import  ... export（format=json）が書いたファイルを読み戻す
///   delete  ... 対象キーを削除する。既定はドライラン（消さずに対象だけ表示）
///
/// export は値を保存されている形のまま 1 キー 1 行で書くので、import で元へ戻せる。
///   参照インデックスを外部ツールと突き合わせるための表（1 行 1 参照へ展開したもの）が
///   欲しい場合は ngol.code.xref_dump を使う。あちらは検証用で、元へは戻せない。
///
/// delete の安全策: 削除は取り返しがつかないため、
///   ・既定は dry_run=true。何件・どのキーが対象かだけを表示し、実際には消さない
///   ・prefix 無指定（＝全件対象）は allow_all_keys=true が無い限り拒否する
///   ・実削除時は必ず先に CSV バックアップを書き出し、書き出せなければ削除しない
///
/// import と delete はホストの更新に乗せて少しずつ進める（1 回あたり batch 件）。
///   件数が多いときに描画を止めず、呼び出し側もタイムアウトしないため。進捗はジョブへ報告する。
///
/// 件数が多いときに速くする方法:
///   保存層が書き込み 1 件ごとに確定する作りだと、確定処理が所要時間の大半を占める。
///   ngol.dev.kvstore_transaction_patch を先に有効にしておくと、
///   この 2 つの処理の書き込みがまとめて確定されるようになる（このノード側の指定は不要）。
///   効くのはホスト更新スレッドの書き込みだけなので、import と delete がその条件を満たしている。
///
/// サイズについて: KVStore は値を「復元済みのオブジェクト」として保持しているため、
///   永続ファイル上の実バイト数は API からは取得できない。ここで出しているのは
///   値を JSON へ書き戻したときの文字数で、あくまで目安であり実ファイルサイズとは一致しない
///   （JsonElement として保持されている値は元の JSON をそのまま使うため、より実態に近い）。
/// </summary>
[NodeType("ngol.kvstore.manage", "KVStore", "KVStore Manage",
    Version = "1.2.2",
    Description = "Inspect, export, restore and prune the KVStore. mode=summary groups keys by prefix with counts and approximate sizes, mode=list shows individual entries, mode=export writes CSV or JSON to a file, mode=import restores a JSON file written by export, mode=delete removes entries (dry run by default, always backs up first). Import and delete run as jobs on the host update loop, a few thousand entries per update, so large operations neither freeze rendering nor time out the caller; poll them with check_job_status. Sizes are re-serialized JSON lengths, not on-disk bytes.")]
[NodePort("mode", PortDirection.Input, "string", Description = "summary (default) = group keys by prefix with counts/sizes, list = show individual entries, export = write all matching entries to a file, import = restore a JSON file written by export, delete = remove matching entries (see dry_run). import and delete run as jobs; poll with check_job_status")]
[NodePort("prefix", PortDirection.Input, "string", Description = "Only include keys starting with this prefix. Empty = all keys")]
[NodePort("limit", PortDirection.Input, "number", Description = "mode=list only: maximum number of entries to show. Default 50")]
[NodePort("value_preview_chars", PortDirection.Input, "number", Description = "mode=list only: how many characters of each value to show. Default 120")]
[NodePort("separator", PortDirection.Input, "string", Description = "mode=summary only: characters treated as prefix delimiters. Default \":.\" (colon and dot). The text before the first delimiter becomes the group name")]
[NodePort("output_path", PortDirection.Input, "string", Description = "mode=export only: destination file path. Empty = kvstore_export.csv (or .json) in the system temp folder. Entries are written one key per line with the value kept as stored, so the file can be loaded back with mode=import")]
[NodePort("format", PortDirection.Input, "string", Description = "mode=export only: csv (default) or json")]
[NodePort("dry_run", PortDirection.Input, "boolean", Description = "mode=delete only: true (default) = only report what would be deleted without touching anything. Set false to actually delete")]
[NodePort("allow_all_keys", PortDirection.Input, "boolean", Description = "mode=delete only: required to be true when prefix is empty, because that would delete every entry. Default false")]
[NodePort("backup_path", PortDirection.Input, "string", Description = "mode=delete only: CSV written before deleting. Empty = kvstore_deleted_<timestamp>.csv in the system temp folder. Deletion is aborted if this cannot be written")]
[NodePort("input_path", PortDirection.Input, "string", Description = "mode=import only: JSON file to restore (written by mode=export with format=json)")]
[NodePort("overwrite", PortDirection.Input, "boolean", Description = "mode=import only: overwrite keys that already exist. Default true")]
[NodePort("batch", PortDirection.Input, "number", Description = "mode=import and mode=delete: entries processed per host update. Default 5000")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable report")]
[NodePort("entry_count", PortDirection.Output, "number", Description = "Number of entries covered by this run")]
[NodePort("total_size", PortDirection.Output, "number", Description = "Approximate total value size in characters (re-serialized JSON)")]
[NodePort("output_path_used", PortDirection.Output, "string", Description = "mode=export only: the file that was actually written")]
public sealed class KVStoreManageNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var store = ctx.Store;
        if (store == null)
        {
            ctx.SetPortValue("result", "ERROR: KVStore is not available on this host");
            return;
        }

        var mode = (ctx.GetPortValue("mode") as string ?? "summary").Trim().ToLowerInvariant();
        var prefix = ctx.GetPortValue("prefix") as string;
        if (string.IsNullOrEmpty(prefix)) prefix = null;

        // 取り込みは既存のキーを見ないので、先に分岐して列挙を省く。
        if (mode == "import") { RunImport(ctx); return; }

        // Keys(null) は全件を返す。ここで一度リスト化しておかないと、
        // 走査中に他ノードが Set した場合に列挙が壊れうる。
        List<string> keys;
        try
        {
            keys = store.Keys(prefix).ToList();
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", "ERROR: failed to enumerate keys: " + ex.Message);
            return;
        }
        keys.Sort(StringComparer.Ordinal);

        switch (mode)
        {
            case "list":   RunList(ctx, store, keys, prefix); break;
            case "export": RunExport(ctx, store, keys, prefix); break;
            case "delete": RunDelete(ctx, store, keys, prefix); break;
            default:       RunSummary(ctx, store, keys, prefix); break;
        }
    }

    // ---- summary ----

    private static void RunSummary(IExecutionContext ctx, IKVStore store, List<string> keys, string prefix)
    {
        var sepText = ctx.GetPortValue("separator") as string;
        if (string.IsNullOrEmpty(sepText)) sepText = ":.";
        var separators = sepText.ToCharArray();

        var groups = new Dictionary<string, (int Count, long Size, long Max, string MaxKey)>(StringComparer.Ordinal);
        long grandTotal = 0;

        foreach (var key in keys)
        {
            var group = GroupOf(key, separators);
            long size = ValueLength(store, key);
            grandTotal += size;

            groups.TryGetValue(group, out var g);
            long max = g.Max; string maxKey = g.MaxKey;
            if (size > max) { max = size; maxKey = key; }
            groups[group] = (g.Count + 1, g.Size + size, max, maxKey);
        }

        var sb = new StringBuilder();
        sb.Append("KVStore summary");
        if (prefix != null) sb.Append(" (prefix: ").Append(prefix).Append(')');
        sb.Append('\n');
        sb.Append("entries=").Append(keys.Count)
          .Append(", approx total value size=").Append(FormatSize(grandTotal))
          .Append("  *approximate: the length of the values written back as JSON, not the size of the store on disk\n\n");

        if (keys.Count == 0)
        {
            sb.Append("(no entries)\n");
        }
        else
        {
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-28}{1,8}{2,14}{3,14}  {4}\n",
                "prefix group", "count", "total", "largest", "largest key"));
            sb.Append(new string('-', 110)).Append('\n');

            foreach (var kv in groups.OrderByDescending(g => g.Value.Size))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0,-28}{1,8}{2,14}{3,14}  {4}\n",
                    Truncate(kv.Key, 27), kv.Value.Count, FormatSize(kv.Value.Size),
                    FormatSize(kv.Value.Max), Truncate(kv.Value.MaxKey, 50)));
            }
        }

        ctx.SetPortValue("result", sb.ToString());
        ctx.SetPortValue("entry_count", (double)keys.Count);
        ctx.SetPortValue("total_size", (double)grandTotal);
    }

    /// <summary>キーを接頭辞グループへ振り分ける。区切り文字が無いキーは "(no prefix)" にまとめる。</summary>
    private static string GroupOf(string key, char[] separators)
    {
        int idx = key.IndexOfAny(separators);
        return idx <= 0 ? "(no prefix)" : key.Substring(0, idx);
    }

    // ---- list ----

    private static void RunList(IExecutionContext ctx, IKVStore store, List<string> keys, string prefix)
    {
        int limit = ToInt(ctx.GetPortValue("limit"), 50);
        if (limit < 1) limit = 1;
        int previewChars = ToInt(ctx.GetPortValue("value_preview_chars"), 120);
        if (previewChars < 1) previewChars = 1;

        var sb = new StringBuilder();
        sb.Append("KVStore entries");
        if (prefix != null) sb.Append(" (prefix: ").Append(prefix).Append(')');
        sb.Append(": ").Append(keys.Count).Append(" total");
        if (keys.Count > limit) sb.Append(", showing first ").Append(limit);
        sb.Append('\n').Append('\n');

        long total = 0;
        int shown = 0;
        foreach (var key in keys)
        {
            var json = ValueJson(store, key);
            total += json.Length;
            if (shown < limit)
            {
                sb.Append(key).Append("  [").Append(FormatSize(json.Length)).Append("]\n")
                  .Append("    ").Append(OneLine(Truncate(json, previewChars))).Append('\n');
                shown++;
            }
        }

        ctx.SetPortValue("result", sb.ToString());
        ctx.SetPortValue("entry_count", (double)keys.Count);
        ctx.SetPortValue("total_size", (double)total);
    }

    // ---- export ----

    private static void RunExport(IExecutionContext ctx, IKVStore store, List<string> keys, string prefix)
    {
        var format = (ctx.GetPortValue("format") as string ?? "csv").Trim().ToLowerInvariant();
        if (format != "json") format = "csv";

        var path = ctx.GetPortValue("output_path") as string;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Path.GetTempPath(), "kvstore_export." + format);

        long total;
        try
        {
            total = WriteExport(store, keys, path, format);
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", "ERROR: export failed: " + ex.Message);
            ctx.SetPortValue("output_path_used", path);
            return;
        }

        var sb = new StringBuilder();
        sb.Append("Exported ").Append(keys.Count).Append(" entr").Append(keys.Count == 1 ? "y" : "ies");
        if (prefix != null) sb.Append(" (prefix: ").Append(prefix).Append(')');
        sb.Append(" as ").Append(format).Append('\n');
        sb.Append("file: ").Append(path).Append('\n');
        sb.Append("approx total value size: ").Append(FormatSize(total)).Append('\n');

        ctx.SetPortValue("result", sb.ToString());
        ctx.SetPortValue("entry_count", (double)keys.Count);
        ctx.SetPortValue("total_size", (double)total);
        ctx.SetPortValue("output_path_used", path);
    }

    /// <summary>
    /// 対象キーを指定形式でファイルへ書き出し、値の総文字数を返す。
    /// 全件を1つの文字列に組み立てると巨大になりうるため、逐次書き出す。
    /// </summary>
    private static long WriteExport(IKVStore store, List<string> keys, string path, string format)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        long total = 0;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));

        if (format == "csv")
        {
            writer.WriteLine("key,size,value");
            foreach (var key in keys)
            {
                var json = ValueJson(store, key);
                total += json.Length;
                writer.Write(CsvField(key));
                writer.Write(',');
                writer.Write(json.Length.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(CsvField(json));
                writer.Write('\n');
            }
        }
        else
        {
            writer.Write('{');
            bool first = true;
            foreach (var key in keys)
            {
                var json = ValueJson(store, key);
                total += json.Length;
                if (!first) writer.Write(',');
                first = false;
                writer.Write('\n');
                writer.Write(JsonSerializer.Serialize(key));
                writer.Write(':');
                // 値は既に JSON なのでそのまま埋め込む。壊れた文字列だった場合に
                // 出力全体を壊さないよう、パースできないものは文字列として入れる。
                writer.Write(IsProbablyJson(json) ? json : JsonSerializer.Serialize(json));
            }
            writer.Write("\n}\n");
        }
        return total;
    }

    // ---- delete ----

    /// <summary>
    /// 対象キーを削除する。取り返しがつかない操作なので、既定はドライラン（実際には消さない）。
    /// 実削除時は必ず先に CSV バックアップを書き出し、書き出しに失敗したら削除しない。
    /// </summary>
    private static void RunDelete(IExecutionContext ctx, IKVStore store, List<string> keys, string prefix)
    {
        bool dryRun = ctx.GetPortValue("dry_run") as bool? ?? true;
        bool allowAllKeys = ctx.GetPortValue("allow_all_keys") as bool? ?? false;

        // prefix 無指定は「全件削除」を意味してしまうため、明示的な許可が無い限り拒否する。
        if (prefix == null && !allowAllKeys)
        {
            ctx.SetPortValue("result",
                "REFUSED: mode=delete with no prefix would delete every entry.\n"
                + "Specify a prefix, or set allow_all_keys=true if you really mean all entries.");
            ctx.SetPortValue("entry_count", 0.0);
            return;
        }

        if (keys.Count == 0)
        {
            ctx.SetPortValue("result", "No entries matched" + (prefix != null ? " (prefix: " + prefix + ")" : "") + ". Nothing to delete.");
            ctx.SetPortValue("entry_count", 0.0);
            ctx.SetPortValue("total_size", 0.0);
            return;
        }

        long total = 0;
        foreach (var key in keys) total += ValueLength(store, key);

        var sb = new StringBuilder();

        if (dryRun)
        {
            sb.Append("DRY RUN - nothing was deleted.\n");
            sb.Append("Would delete ").Append(keys.Count).Append(" entr").Append(keys.Count == 1 ? "y" : "ies");
            if (prefix != null) sb.Append(" (prefix: ").Append(prefix).Append(')');
            sb.Append(", approx ").Append(FormatSize(total)).Append(" of values\n\n");
            sb.Append("Sample of affected keys:\n");
            foreach (var key in keys.Take(20)) sb.Append("  ").Append(key).Append('\n');
            if (keys.Count > 20) sb.Append("  ... and ").Append(keys.Count - 20).Append(" more\n");
            sb.Append("\nSet dry_run=false to actually delete (a CSV backup is written first).\n");

            ctx.SetPortValue("result", sb.ToString());
            ctx.SetPortValue("entry_count", (double)keys.Count);
            ctx.SetPortValue("total_size", (double)total);
            return;
        }

        // 実削除。先にバックアップを取り、失敗したら削除しない。
        var backupPath = ctx.GetPortValue("backup_path") as string;
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            backupPath = Path.Combine(Path.GetTempPath(),
                "kvstore_deleted_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
        }

        try
        {
            WriteExport(store, keys, backupPath, "csv");
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result",
                "ABORTED: backup could not be written, so nothing was deleted.\n"
                + "backup path: " + backupPath + "\nreason: " + ex.Message);
            ctx.SetPortValue("entry_count", 0.0);
            ctx.SetPortValue("output_path_used", backupPath);
            return;
        }

        // 削除はホストの更新に乗せて少しずつ進める。
        //   1 回で片付けると、件数が多いときに描画が止まり、呼び出し側も応答を待ちきれない。
        //   この経路がホスト更新スレッドで動くことには、もう一つ意味がある--
        //   バーストをバッチにまとめる仕組み（ngol.dev.kvstore_transaction_patch）が効くのは、
        //   バッチを確実に閉じられるこのスレッドの書き込みだけであるため。
        int batch = ctx.GetPortValue("batch") is double bd ? (int)bd : 5000;
        if (batch < 1) batch = 1;

        var state = new DeleteState
        {
            Keys = keys,
            Prefix = prefix,
            BackupPath = backupPath,
            TotalSize = total,
            StartedAt = DateTime.UtcNow,
        };

        IPersistentRegistration reg = null;
        reg = ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () => DeleteTick(ctx, store, state, batch),
        });
        state.Reg = reg;

        sb.Append("Deleting ").Append(keys.Count).Append(" entr").Append(keys.Count == 1 ? "y" : "ies");
        if (prefix != null) sb.Append(" (prefix: ").Append(prefix).Append(')');
        sb.Append(" in the background, ").Append(batch).Append(" per update.\n");
        sb.Append("backup: ").Append(backupPath).Append('\n');
        sb.Append("Poll progress with check_job_status.\n");

        ctx.SetPortValue("result", sb.ToString());
        ctx.SetPortValue("entry_count", (double)keys.Count);
        ctx.SetPortValue("total_size", (double)total);
        ctx.SetPortValue("output_path_used", backupPath);
    }

    private sealed class DeleteState
    {
        public List<string> Keys;
        public string Prefix;
        public string BackupPath;
        public long TotalSize;
        public DateTime StartedAt;
        public int Index;
        public int Deleted;
        public string FirstError;
        public int LastReportedPercent = -1;
        public IPersistentRegistration Reg;
    }

    private static void DeleteTick(IExecutionContext ctx, IKVStore store, DeleteState st, int batch)
    {
        var end = Math.Min(st.Index + batch, st.Keys.Count);
        for (; st.Index < end; st.Index++)
        {
            try { store.Delete(st.Keys[st.Index]); st.Deleted++; }
            catch (Exception ex) { if (st.FirstError == null) st.FirstError = st.Keys[st.Index] + ": " + ex.Message; }
        }

        if (st.Index < st.Keys.Count)
        {
            var percent = st.Keys.Count > 0 ? (int)((long)st.Index * 100 / st.Keys.Count) : 100;
            if (percent >= st.LastReportedPercent + 5)
            {
                st.LastReportedPercent = percent;
                JobReport(st.Reg, $"{percent}%, {st.Deleted} deleted");
            }
            return;
        }

        var elapsed = DateTime.UtcNow - st.StartedAt;
        var note = st.FirstError != null ? $", first error: {st.FirstError}" : "";
        ctx.Logger.LogInfo($"[KVStoreInspect] DONE. deleted {st.Deleted} of {st.Keys.Count} in {elapsed.TotalSeconds:F1}s{note}");
        JobReport(st.Reg, $"DONE. {st.Deleted} of {st.Keys.Count} deleted in {elapsed.TotalSeconds:F1}s{note}");
        st.Reg?.Cancel();
    }


    // ---- import ----

    private sealed class ImportState
    {
        public StreamReader Reader;
        public long TotalBytes;
        public int Imported;
        public int Skipped;
        public int Malformed;
        public int LastReportedPercent = -1;
        public DateTime StartedAt;
        public IPersistentRegistration Reg;
    }

    private const string ImportStateKey = "NgolKVStoreManageImportState_v1";

    /// <summary>
    /// mode=export（format=json）が書いたファイルを読み戻す。
    /// ファイルは 1 行 1 エントリで書かれている（値の中の改行は JSON の規則で
    ///   エスケープされるため、行が途中で切れることはない）。そのため全体をメモリへ
    ///   載せずに 1 行ずつ読み進められる。数十万件・数十 MB でも扱える。
    /// </summary>
    private static void RunImport(IExecutionContext ctx)
    {
        var path = (ctx.GetPortValue("input_path") as string ?? "").Trim();
        int batch = ctx.GetPortValue("batch") is double bd ? (int)bd : 5000;
        if (batch < 1) batch = 1;
        bool overwrite = ctx.GetPortValue("overwrite") as bool? ?? true;

        if (path.Length == 0 || !File.Exists(path))
        {
            ctx.SetPortValue("result", "ERROR: file not found: " + path);
            return;
        }

        // 前回の取り込みが残っていれば片付けてから始める（二重に走らせない）
        if (AppDomain.CurrentDomain.GetData(ImportStateKey) is ImportState old)
        {
            try { if (old.Reg != null && old.Reg.IsActive) old.Reg.Cancel(); } catch { }
            try { old.Reader?.Dispose(); } catch { }
            AppDomain.CurrentDomain.SetData(ImportStateKey, null);
        }

        var state = new ImportState
        {
            Reader = new StreamReader(path),
            TotalBytes = new FileInfo(path).Length,
            StartedAt = DateTime.UtcNow,
        };
        AppDomain.CurrentDomain.SetData(ImportStateKey, state);

        state.Reg = ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () => ImportTick(ctx, state, batch, overwrite),
            OnStop = () => { try { state.Reader?.Dispose(); } catch { } },
        });

        ctx.Logger.LogInfo($"[KVStoreManage] starting import from {path} ({state.TotalBytes} bytes)");
        ctx.SetPortValue("entry_count", 0.0);
        ctx.SetPortValue("result",
            "Importing from " + path + " in the background, " + batch + " per update.\n"
            + "Poll progress with check_job_status.\n");
    }

    private static void ImportTick(IExecutionContext ctx, ImportState state, int batch, bool overwrite)
    {
        try
        {
            for (int i = 0; i < batch; i++)
            {
                var line = state.Reader.ReadLine();
                if (line == null) { ImportFinish(ctx, state); return; }

                // 外側の括弧と空行はエントリではないので、読めなかった行として数えない。
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed == "{" || trimmed == "}") continue;

                if (!TryParseEntryLine(line, out var key, out var value)) { state.Malformed++; continue; }
                if (!overwrite && ctx.Store.ContainsKey(key)) { state.Skipped++; continue; }

                ctx.Store.Set(key, value);
                state.Imported++;
            }

            long pos;
            try { pos = state.Reader.BaseStream.Position; } catch { return; }
            var percent = state.TotalBytes > 0 ? (int)(pos * 100 / state.TotalBytes) : 0;
            if (percent < state.LastReportedPercent + 5) return;
            state.LastReportedPercent = percent;
            JobReport(state.Reg, $"{percent}%, {state.Imported} imported");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[KVStoreManage] import failed after {state.Imported} entries - {ex.GetType().Name}: {ex.Message}");
            JobReport(state.Reg, $"FAILED after {state.Imported}: {ex.GetType().Name}: {ex.Message}");
            try { state.Reader?.Dispose(); } catch { }
            state.Reg?.Cancel();
        }
    }

    private static void ImportFinish(IExecutionContext ctx, ImportState state)
    {
        var elapsed = DateTime.UtcNow - state.StartedAt;
        var note = state.Malformed > 0 ? $", {state.Malformed} unparsable line(s)" : "";
        ctx.Logger.LogInfo($"[KVStoreManage] import DONE. {state.Imported} imported, {state.Skipped} skipped{note} in {elapsed.TotalSeconds:F1}s");
        JobReport(state.Reg, $"DONE. {state.Imported} imported, {state.Skipped} skipped{note} in {elapsed.TotalSeconds:F1}s");
        try { state.Reader?.Dispose(); } catch { }
        state.Reg?.Cancel();
    }

    /// <summary>
    /// 1 行を "キー":値 として読む。キーの終わりはエスケープを見ながら探す
    /// （キーの中に引用符が含まれていても取り違えないため）。
    /// </summary>
    private static bool TryParseEntryLine(string line, out string key, out object value)
    {
        key = null;
        value = null;

        var s = line.Trim();
        if (s.EndsWith(",", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        if (!s.StartsWith("\"", StringComparison.Ordinal)) return false;

        int i = 1;
        bool escaped = false;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '"') break;
        }
        if (i >= s.Length) return false;

        var keyJson = s.Substring(0, i + 1);
        var rest = s.Substring(i + 1).TrimStart();
        if (!rest.StartsWith(":", StringComparison.Ordinal)) return false;
        var valueJson = rest.Substring(1).Trim();
        if (valueJson.Length == 0) return false;

        try
        {
            key = JsonSerializer.Deserialize<string>(keyJson);
            using var doc = JsonDocument.Parse(valueJson);
            value = FromJson(doc.RootElement);
        }
        catch { return false; }

        return key != null;
    }

    /// <summary>
    /// 書き出した側と同じ形へ戻す。配列やオブジェクトは元の要素を複製して保持する
    /// （複製しないと、読み取りに使った文書を閉じた時点で無効になる）。
    /// </summary>
    private static object FromJson(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number: return e.TryGetInt64(out var l) ? (object)l : e.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: return e.Clone();
        }
    }

    /// <summary>
    /// ジョブへ進捗を出す。時刻を必ず添える--ジョブの状態は Running としか出ないため、
    /// これが無いと「進んでいる」と「止まっている」を外から区別できない。
    /// </summary>
    private static void JobReport(IPersistentRegistration reg, string text)
    {
        try { reg?.ReportProgress($"[{DateTime.Now:HH:mm:ss}] {text}"); }
        catch { /* 進捗報告の失敗で本処理を止めない */ }
    }

    // ---- 値の取り出し ----

    /// <summary>
    /// 値を JSON 文字列として取り出す。KVStore は復元済みオブジェクトを保持しているため、
    /// JsonElement ならそのままの生テキストを、それ以外は JSON へ書き戻したものを返す。
    /// </summary>
    private static string ValueJson(IKVStore store, string key)
    {
        try
        {
            var v = store.Get(key);
            if (v == null) return "null";
            if (v is JsonElement je) return je.GetRawText();
            if (v is string s) return JsonSerializer.Serialize(s);
            return JsonSerializer.Serialize(v);
        }
        catch (Exception ex)
        {
            return "\"<unreadable: " + ex.GetType().Name + ">\"";
        }
    }

    private static long ValueLength(IKVStore store, string key) => ValueJson(store, key).Length;

    // ---- 整形ヘルパー ----

    private static bool IsProbablyJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        char c = s[0];
        return c == '{' || c == '[' || c == '"' || c == '-' || (c >= '0' && c <= '9')
            || s == "true" || s == "false" || s == "null";
    }

    /// <summary>RFC 4180 準拠のCSVフィールド。ダブルクォート・カンマ・改行を含む値を安全に囲む。</summary>
    private static string CsvField(string s)
    {
        if (s == null) return "\"\"";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string OneLine(string s)
        => s == null ? "" : s.Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, max) + "...";
    }

    private static string FormatSize(long chars)
    {
        if (chars < 1024) return chars + " ch";
        if (chars < 1024 * 1024) return (chars / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " Kch";
        return (chars / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " Mch";
    }

    private static int ToInt(object v, int fallback)
    {
        if (v == null) return fallback;
        try { return (int)Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }
}
