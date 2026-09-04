using System;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ディスク上の PE の PDB から、名前と RVA を双方向に引く。
///
/// 対象プロセスを開かないので、相手が固まっていても、起動していなくても使える。
/// スタックから拾った「モジュール + RVA」を名前に変えるのが主な用途で、
/// 名前が付いた時点で公式ソースや Web を引けるようになる。
///
/// シンボルが載らなかった場合は、それを 0 件と区別して報告する。
/// 「一致しなかった」と「そもそも読めていない」を混ぜると、読めていないことを
/// 「無い」と読み違える。
/// </summary>
[NodeType("ngol.code.pdb_lookup", "Code", "PDB Lookup",
    Version = "1.0.0",
    Description =
        "Turn an RVA into a function name, or a name into an RVA, by reading the PDB that sits next to a PE "
      + "file on disk. The target process is never opened, so this works while it is frozen and even when it "
      + "is not running at all - useful for naming the frames that ngol.proc.thread_stacks reports. A wildcard "
      + "mask lists everything that matches, for when only part of the name is known. Whether symbols were "
      + "actually loaded is reported separately from the number of matches, because a PDB that is missing, "
      + "stale or in .NET's portable format yields no symbols at all rather than no matches. Reads only "
      + "classic PDBs; dbghelp does not read portable (.NET) PDBs.")]
[NodePort("image_path", PortDirection.Input, "string", Description = "Full path to the .exe or .dll on disk. Its .pdb is looked for beside it. Required")]
[NodePort("rva", PortDirection.Input, "string", Description = "RVAs to name, hex, comma separated (e.g. '0x64f470,0x6a2210'). Each is reported as name + offset")]
[NodePort("name", PortDirection.Input, "string", Description = "Symbol names to resolve to an RVA, comma separated. Use the exact name as it appears in the PDB")]
[NodePort("match", PortDirection.Input, "string", Description = "Wildcard mask listing every matching symbol with its RVA (e.g. '*QueuePresent*'). Use when only part of the name is known")]
[NodePort("match_limit", PortDirection.Input, "number", Description = "How many matches to list. Default 60")]
[NodePort("exact_symbols", PortDirection.Input, "boolean", Description = "Refuse a PDB whose signature does not match the image. Default true. Turning this off makes dbghelp answer with names from a different build, which look ordinary and are wrong")]
[NodePort("symbols_loaded", PortDirection.Output, "boolean", Description = "true when real symbols were loaded. When false, every count below is meaningless rather than zero")]
[NodePort("symbol_kind", PortDirection.Output, "string", Description = "What dbghelp loaded: pdb / export / none / deferred. 'export' means only the export table was available")]
[NodePort("pdb_format", PortDirection.Output, "string", Description = "Format of the .pdb beside the image: classic / portable / missing. A portable PDB is .NET's format; dbghelp claims to load it and then returns nothing, so it is reported as no symbols here")]
[NodePort("pdb_path", PortDirection.Output, "string", Description = "The PDB that was actually loaded, as dbghelp reports it. Empty when none was")]
[NodePort("first_rva", PortDirection.Output, "string", Description = "RVA of the first resolved name, hex. Empty when nothing resolved")]
[NodePort("first_name", PortDirection.Output, "string", Description = "Name of the first resolved RVA. Empty when nothing resolved")]
[NodePort("match_count", PortDirection.Output, "number", Description = "How many symbols the mask matched. -1 when the enumeration itself failed")]
[NodePort("result", PortDirection.Output, "string", Description = "Every answer in order, or the reason nothing could be read")]
public sealed class PdbLookupNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var imagePath = ctx.GetPortValue("image_path") as string ?? "";
        var rvaText = ctx.GetPortValue("rva") as string ?? "";
        var nameText = ctx.GetPortValue("name") as string ?? "";
        var mask = ctx.GetPortValue("match") as string ?? "";
        var limit = (int)(ctx.GetPortValue("match_limit") is double d ? d : 60.0);
        var exact = !(ctx.GetPortValue("exact_symbols") is bool b) || b;
        if (limit < 1) limit = 1;

        ctx.SetPortValue("symbols_loaded", false);
        ctx.SetPortValue("symbol_kind", "");
        ctx.SetPortValue("pdb_path", "");
        ctx.SetPortValue("first_rva", "");
        ctx.SetPortValue("first_name", "");
        ctx.SetPortValue("match_count", 0.0);

        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
        {
            ctx.SetPortValue("result", "image_path must be an existing file");
            return;
        }
        if (rvaText.Length == 0 && nameText.Length == 0 && mask.Length == 0)
        {
            ctx.SetPortValue("result", "give at least one of rva, name or match");
            return;
        }

        var sb = new StringBuilder();
        using var pdb = NgolPdb.Open(imagePath, exact);

        ctx.SetPortValue("symbol_kind", pdb.SymbolKind);
        ctx.SetPortValue("pdb_format", pdb.PdbFormat);
        ctx.SetPortValue("pdb_path", pdb.LoadedPdb);
        ctx.SetPortValue("symbols_loaded", pdb.HasSymbols);

        sb.Append("image  ").Append(imagePath).Append('\n');
        sb.Append("pdb    ").Append(string.IsNullOrEmpty(pdb.LoadedPdb) ? "(none)" : pdb.LoadedPdb)
          .Append("   format ").Append(pdb.PdbFormat).Append('\n');
        sb.Append("symbols ").Append(pdb.SymbolKind).Append('\n');

        if (!pdb.HasSymbols)
        {
            sb.Append('\n').Append(pdb.Problem ?? "no symbols").Append('\n');
            ctx.SetPortValue("result", sb.ToString());
            return;
        }
        sb.Append('\n');

        if (mask.Length > 0)
        {
            var found = pdb.EnumSymbols(mask, limit, out var failed);
            if (failed)
            {
                ctx.SetPortValue("match_count", -1.0);
                sb.Append("'").Append(mask).Append("': the enumeration itself failed\n");
            }
            else
            {
                ctx.SetPortValue("match_count", (double)found.Count);
                sb.Append("'").Append(mask).Append("': ").Append(found.Count).Append(" match(es)")
                  .Append(found.Count >= limit ? "  (cut off at match_limit)" : "").Append('\n');
                foreach (var kv in found)
                    sb.Append("    0x").Append(kv.Key.ToString("x")).Append('\t').Append(kv.Value).Append('\n');
            }
            sb.Append('\n');
        }

        // 出力ポートは書いた値を読み返せないので、最初の 1 件は手元で覚える。
        string firstRva = null, firstName = null;

        foreach (var one in Split(nameText))
        {
            if (pdb.TryNameToRva(one, out var rva))
            {
                sb.Append("  ").Append(one).Append("  -> RVA 0x").Append(rva.ToString("x")).Append('\n');
                firstRva ??= "0x" + rva.ToString("x");
            }
            else sb.Append("  ").Append(one).Append("  -> not found\n");
        }

        foreach (var one in Split(rvaText))
        {
            if (!TryHex(one, out var value)) { sb.Append("  ").Append(one).Append("  -> not a hex value\n"); continue; }
            if (pdb.TryRvaToName(value, out var name, out var disp))
            {
                sb.Append("  0x").Append(value.ToString("x")).Append("  -> ").Append(name)
                  .Append(" + 0x").Append(disp.ToString("x")).Append('\n');
                firstName ??= name;
            }
            else sb.Append("  0x").Append(value.ToString("x")).Append("  -> no symbol here\n");
        }

        if (firstRva != null) ctx.SetPortValue("first_rva", firstRva);
        if (firstName != null) ctx.SetPortValue("first_name", firstName);
        ctx.SetPortValue("result", sb.ToString());
    }

    private static string[] Split(string s)
        => string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryHex(string s, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
