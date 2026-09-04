using System;
using System.Collections.Generic;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ngol.code.xref_index_build がバックグラウンドで構築中/構築済みの
/// KVStore逆引きインデックスを即座に読むだけの軽量ノード。
///
/// 出力:
///   hits               -> target_rva を参照している命令RVAのJSON配列
///   hit_count          -> hits の件数
///   index_done         -> バックグラウンドスキャンが終わっているか
///   index_truncated    -> 読めない領域に当たって指定範囲の手前で終わったか
///   index_progress_rva -> スキャンが到達した位置(16進RVA)
///
/// 主な使い方:
///   ngol.code.xref_index_build と対で使う。スキャン未完了でも、既に処理済みの
///   範囲であれば即座に結果を返せる。index_done/index_progress_rva を見れば
///   「未発見=本当に無い」なのか「未発見=まだその範囲を未スキャン」なのかを判断できる。
///
/// 制約:
///   xref_index_build が一度も実行されていないmoduleを指定した場合は空配列を返す
///   （エラーにはならない）。
///   index_done=true だけでは「確定で不存在」と言えない。読めない領域に当たって
///   打ち切られた場合も走査は終わるため、index_truncated=false まで見ること。
///   索引は命令をデコードして作るため、データセクション（.pdata・vtable・関数ポインタ表）
///   からの参照は構造上入らない。
/// </summary>
[NodeType("ngol.code.xref_lookup", "Code", "Xref Lookup",
    Version = "1.0.2",
    Description = "Look up target_rva in the xref index built by ngol.code.xref_index_build. Returns hits found so far plus current build progress so the caller can tell 'not found yet' apart from 'confirmed absent'.")]
[NodePort("target_rva",         PortDirection.Input,  "string",  Description = "Target RVA hex to look up (e.g. '0x9df7a0')")]
[NodePort("module",             PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module")]
[NodePort("max_hits",           PortDirection.Input,  "number",  Description = "Max references to list (default: 200). The index keeps them all; this only caps what is returned, so the output does not get cut off in transit")]
[NodePort("hits",               PortDirection.Output, "string",  Description = "JSON array of referencing instruction RVAs (hex strings), at most max_hits of them")]
[NodePort("hit_count",          PortDirection.Output, "number",  Description = "Number of hits listed in 'hits'")]
[NodePort("total_hits",         PortDirection.Output, "number",  Description = "Number of hits the index holds, before max_hits was applied")]
[NodePort("hits_truncated",     PortDirection.Output, "boolean", Description = "true = the index holds more than max_hits references; 'hits' lists only the first ones. Named apart from index_truncated, which is about the scan being incomplete")]
[NodePort("index_done",         PortDirection.Output, "boolean", Description = "true if the background scan has finished. Check index_truncated too before reading 'no hits' as 'confirmed absent'")]
[NodePort("index_truncated",    PortDirection.Output, "boolean", Description = "true if the scan stopped early because the readable range ended before the requested scan_size")]
[NodePort("index_progress_rva", PortDirection.Output, "string",  Description = "RVA (hex) the scan has reached")]
public sealed class XrefLookupNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var targetRvaStr = ctx.GetPortValue("target_rva") as string ?? "";
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));

        long targetRva;
        try
        {
            var s = targetRvaStr.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            targetRva = long.Parse(s, System.Globalization.NumberStyles.HexNumber);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[XrefLookup] parse error: {ex.Message}");
            ctx.SetPortValue("hits", "[]");
            ctx.SetPortValue("hit_count", 0.0);
            ctx.SetPortValue("total_hits", 0.0);
            ctx.SetPortValue("hits_truncated", false);
            ctx.SetPortValue("index_done", false);
            ctx.SetPortValue("index_truncated", false);
            ctx.SetPortValue("index_progress_rva", "");
            return;
        }

        double maxRaw = 200;
        if (ctx.GetPortValue("max_hits") is double mv) maxRaw = mv;
        var maxHits = Math.Max(1, (int)maxRaw);

        var key = $"xrefidx:{moduleName}:0x{targetRva:x}";
        var json = ctx.Store.Get<string>(key) ?? "[]";

        var items = new List<string>();
        // 区切り文字を配列で渡す形にする。1 文字を直接取る多重定義は
        //    netstandard2.0 の土台には無く、ホストによってはコンパイルが通らない。
        foreach (var part in json.Trim('[', ']').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(part)) items.Add(part.Trim());

        var totalHits = items.Count;
        // 全件そのまま返すと、参照の多い番地では出力が経路の上限を越えて切り詰められる。
        // 黙って切れると「これで全部」と読まれるので、こちらで切って切ったことを出す。
        var hitsTruncated = totalHits > maxHits;
        if (hitsTruncated) items.RemoveRange(maxHits, totalHits - maxHits);

        bool done = ctx.Store.TryGet<bool>($"xrefidx:{moduleName}:done", out var doneVal) && doneVal;
        bool truncated = ctx.Store.TryGet<bool>($"xrefidx:{moduleName}:truncated", out var tv) && tv;
        long progressRva = ctx.Store.TryGet<long>($"xrefidx:{moduleName}:progress_rva", out var pv) ? pv : 0;

        ctx.SetPortValue("hits", "[" + string.Join(",", items) + "]");
        ctx.SetPortValue("hit_count", (double)items.Count);
        ctx.SetPortValue("total_hits", (double)totalHits);
        ctx.SetPortValue("hits_truncated", hitsTruncated);
        ctx.SetPortValue("index_done", done);
        ctx.SetPortValue("index_truncated", truncated);
        ctx.SetPortValue("index_progress_rva", $"0x{progressRva:x}");

        ctx.Logger.LogInfo($"[XrefLookup] target=0x{targetRva:x} module={moduleName} -> {items.Count} of {totalHits} hits, index_done={done}, index_truncated={truncated}, progress=0x{progressRva:x}"
            + (hitsTruncated ? $" -- listed the first {maxHits}; raise max_hits to see the rest" : ""));
    }
}
