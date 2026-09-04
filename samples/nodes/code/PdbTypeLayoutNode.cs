using System;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ディスク上の PE の PDB から、C++ の型の配置をそのまま出す。
///
/// 生きたオブジェクトの中身を読むとき、フィールドの位置を「そのフィールドを触っている関数を
/// 逆アセンブルして割り出す」やり方は成立するが、出るのはその関数が触ったフィールドだけで、
/// 名前も出ず、ずれても静かに読めてしまう。PDB には型の全フィールドが入っている。
///
/// 実体の探し方までは扱わない。仮想関数がある型なら、vtable の
/// RVA を引き（ngol.code.pdb_lookup）、その絶対番地を ngol.mem.value_scan に
/// かけると 1 件に決まる。
/// </summary>
[NodeType("ngol.code.pdb_type_layout", "Code", "PDB Type Layout",
    Version = "1.0.0",
    Description =
        "Print the memory layout of a C++ type - every field with its offset, size and type - by reading the "
      + "PDB beside a PE file on disk. Deriving offsets by disassembling a function that touches them only "
      + "yields the fields that function used, gives no names, and fails silently when it is wrong; the PDB "
      + "has the whole layout. The target process is never opened. To find a live instance of the type, look "
      + "up its vftable with ngol.code.pdb_lookup and scan for that absolute address with ngol.mem.value_scan. "
      + "Whether type information was present is reported separately from the number of fields, because a PDB "
      + "that is missing, stale, in .NET's portable format, or built without type info yields nothing at all "
      + "rather than an empty type.")]
[NodePort("image_path", PortDirection.Input, "string", Description = "Full path to the .exe or .dll on disk. Its .pdb is looked for beside it. Required")]
[NodePort("type_name", PortDirection.Input, "string", Description = "Exact type names to lay out, comma separated (e.g. 'vvl::Swapchain'). Required unless type_match is given")]
[NodePort("type_match", PortDirection.Input, "string", Description = "Wildcard mask listing type names only, without layouts (e.g. 'vvl::Swap*'). Use when the exact name is unknown")]
[NodePort("match_limit", PortDirection.Input, "number", Description = "How many type names to list for type_match. Default 60")]
[NodePort("recurse", PortDirection.Input, "boolean", Description = "Also expand base classes inline at their real offsets. Default false, which lists a base class as one entry")]
[NodePort("exact_symbols", PortDirection.Input, "boolean", Description = "Refuse a PDB whose signature does not match the image. Default true. Turning this off yields a layout from a different build, which looks ordinary and is wrong")]
[NodePort("symbols_loaded", PortDirection.Output, "boolean", Description = "true when real symbols were loaded. When false, the counts below are meaningless rather than zero")]
[NodePort("type_info_available", PortDirection.Output, "boolean", Description = "true when the PDB carries type information. A PDB can hold names without types, and then no layout can be produced")]
[NodePort("pdb_format", PortDirection.Output, "string", Description = "Format of the .pdb beside the image: classic / portable / missing. A portable PDB is .NET's format; dbghelp claims to load it and then returns nothing, so it is reported as no symbols here")]
[NodePort("size", PortDirection.Output, "number", Description = "Size of the first type in bytes. 0 when nothing was laid out")]
[NodePort("field_count", PortDirection.Output, "number", Description = "How many entries the first type produced, base classes included")]
[NodePort("layout", PortDirection.Output, "string", Description = "Offset, name, size and type for each entry, one per line")]
[NodePort("result", PortDirection.Output, "string", Description = "What was read, or the reason nothing could be")]
public sealed class PdbTypeLayoutNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var imagePath = ctx.GetPortValue("image_path") as string ?? "";
        var typeNames = ctx.GetPortValue("type_name") as string ?? "";
        var mask = ctx.GetPortValue("type_match") as string ?? "";
        var limit = (int)(ctx.GetPortValue("match_limit") is double d ? d : 60.0);
        var recurse = ctx.GetPortValue("recurse") is bool r && r;
        var exact = !(ctx.GetPortValue("exact_symbols") is bool b) || b;
        if (limit < 1) limit = 1;

        ctx.SetPortValue("symbols_loaded", false);
        ctx.SetPortValue("type_info_available", false);
        ctx.SetPortValue("size", 0.0);
        ctx.SetPortValue("field_count", 0.0);
        ctx.SetPortValue("layout", "");

        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
        {
            ctx.SetPortValue("result", "image_path must be an existing file");
            return;
        }
        if (typeNames.Length == 0 && mask.Length == 0)
        {
            ctx.SetPortValue("result", "give type_name or type_match");
            return;
        }

        var sb = new StringBuilder();
        var layout = new StringBuilder();
        using var pdb = NgolPdb.Open(imagePath, exact);

        ctx.SetPortValue("symbols_loaded", pdb.HasSymbols);
        ctx.SetPortValue("type_info_available", pdb.HasTypeInfo);
        ctx.SetPortValue("pdb_format", pdb.PdbFormat);

        sb.Append("image  ").Append(imagePath).Append('\n');
        sb.Append("pdb    ").Append(string.IsNullOrEmpty(pdb.LoadedPdb) ? "(none)" : pdb.LoadedPdb)
          .Append("   format ").Append(pdb.PdbFormat).Append('\n');
        sb.Append("symbols ").Append(pdb.SymbolKind)
          .Append("   type info ").Append(pdb.HasTypeInfo ? "yes" : "no").Append('\n');

        if (!pdb.HasSymbols)
        {
            sb.Append('\n').Append(pdb.Problem ?? "no symbols").Append('\n');
            ctx.SetPortValue("result", sb.ToString());
            return;
        }
        if (!pdb.HasTypeInfo)
        {
            sb.Append("\nthe PDB carries no type information, so no layout can be produced\n");
            ctx.SetPortValue("result", sb.ToString());
            return;
        }
        sb.Append('\n');

        if (mask.Length > 0)
        {
            var names = pdb.EnumTypeNames(mask, limit, out var failed);
            if (failed) sb.Append("'").Append(mask).Append("': the enumeration itself failed\n");
            else
            {
                names.Sort(StringComparer.Ordinal);
                sb.Append("'").Append(mask).Append("': ").Append(names.Count).Append(" type(s)")
                  .Append(names.Count >= limit ? "  (cut off at match_limit)" : "").Append('\n');
                foreach (var n in names) sb.Append("    ").Append(n).Append('\n');
            }
            sb.Append('\n');
        }

        var first = true;
        foreach (var one in Split(typeNames))
        {
            if (!pdb.TryTypeLayout(one, recurse, out var total, out var fields))
            {
                sb.Append(one).Append("  -> not found\n");
                continue;
            }
            sb.Append(one).Append("   size 0x").Append(total.ToString("x"))
              .Append(" (").Append(total).Append(" bytes)   ").Append(fields.Count).Append(" entries\n");
            foreach (var f in fields)
            {
                var indent = new string(' ', 2 + f.Depth * 2);
                layout.Append("0x").Append(f.Offset.ToString("x4")).Append(indent);
                if (f.IsBaseClass) layout.Append("[base] ").Append(f.Name).Append("  (size ").Append(f.Size).Append(')');
                else
                {
                    layout.Append(f.Name.PadRight(Math.Max(1, 34 - f.Depth * 2)))
                          .Append(f.Size.ToString().PadLeft(6));
                    if (!string.IsNullOrEmpty(f.TypeName)) layout.Append("  ").Append(f.TypeName);
                }
                layout.Append('\n');
            }
            if (first)
            {
                ctx.SetPortValue("size", (double)total);
                ctx.SetPortValue("field_count", (double)fields.Count);
                first = false;
            }
        }

        ctx.SetPortValue("layout", layout.ToString());
        ctx.SetPortValue("result", sb.ToString());
    }

    private static string[] Split(string s)
        => string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
}
