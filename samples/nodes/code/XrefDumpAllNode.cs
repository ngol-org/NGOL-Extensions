using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ngol.code.xref_index_build がKVStoreに構築した参照インデックス全件を
/// "instrRvaHex,targetRvaHex" のCSV(1行1参照)としてファイルへ書き出す。
///
/// 出力:
///   row_count -> 書き出した行数
///
/// 主な使い方:
///   構築した索引を、別の静的解析ツールが出した参照リストと突き合わせて確かめたい場合や、
///   中身を表計算ソフト等で直接見たい場合に使う。
///   索引が正しいかは、この索引だけを見ていても分からない。外で作った答えと
///   並べられる形にしておくと、件数の差から取りこぼしと過検出のどちらかを切り分けられる。
///
/// 制約:
///   対象moduleのインデックスが未構築の場合はrow_count=0の空ファイルを書き出す。
///
/// これは検証のために配列を1行ずつへ展開する出力であり、元へ戻すことはできない。
///   索引を保存先の外へ退避して後で戻したい場合は ngol.kvstore.manage を使う
///   （mode=export で書き出し、mode=import で読み戻す）。
/// </summary>
[NodeType("ngol.code.xref_dump", "Code", "Xref Dump (CSV)",
    Version = "1.0.2",
    Description = "Dump the entire xref index built by ngol.code.xref_index_build to a CSV file (instr_rva,target_rva per line) for external validation. This expands each stored array into one row per reference, so the output cannot be loaded back; to move the index out of the store and restore it later, use ngol.kvstore.manage (mode=export / mode=import) instead.")]
[NodePort("module",       PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("output_path",  PortDirection.Input,  "string", Description = "Output CSV file path")]
[NodePort("row_count",    PortDirection.Output, "number", Description = "Number of rows written")]
public sealed class XrefDumpAllNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
        var outputPath = ctx.GetPortValue("output_path") as string ?? "";

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            ctx.Logger.LogError("[XrefDumpAll] output_path is required");
            ctx.SetPortValue("row_count", 0.0);
            return;
        }

        var prefix = $"xrefidx:{moduleName}:0x";
        int rowCount = 0;
        var sb = new StringBuilder();

        foreach (var key in ctx.Store.Keys(prefix))
        {
            var targetRvaHex = key.Substring(prefix.Length);
            var json = ctx.Store.Get<string>(key);
            if (string.IsNullOrEmpty(json)) continue;

            foreach (var part in json.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim().Trim('"');
                if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(p.Substring(2)).Append(',').Append(targetRvaHex).Append('\n');
                    rowCount++;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllText(outputPath, sb.ToString());

        ctx.SetPortValue("row_count", (double)rowCount);
        ctx.Logger.LogInfo($"[XrefDumpAll] wrote {rowCount} rows to {outputPath}");
    }
}
