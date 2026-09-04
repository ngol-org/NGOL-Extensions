using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// PE ファイルが import している DLL を、ディスク上のファイルとして読む。
///
/// このノードは、他のノードと同じく NGOL が動いているホストの中で実行される。
///   動いていなくてよいのは「調べる相手」の方で、NGOL 自身ではない。
///
/// 他の code.* は読み込み済みのモジュールを対象にするため、まだ起動していない実行ファイルの
/// import 表は読めない。プロキシ DLL をどの名前で置けるかは import 表で決まるので、
/// 相手を起動せずに答えを出せる必要がある。
///
/// 通常の import と遅延読み込みを区別して返す。前者はエントリポイント前に必ず
/// 読み込まれ、後者は最初の呼び出しまで読み込まれない。
/// </summary>
[NodeType("ngol.code.pe_imports", "Code", "PE Imports",
    Version = "1.0.2",
    Description = "Read the import table of a PE file on disk. The target does not have to be running. Normal and "
      + "delay-loaded imports are reported separately, as are imports by name and by ordinal.")]
[NodePort("path", PortDirection.Input, "string", IsRequired = true, Description = "PE file to read (.exe / .dll)")]
[NodePort("include_names", PortDirection.Input, "boolean", Description = "Also list the imported function names (default false)")]
[NodePort("text", PortDirection.Output, "string", Description = "One section per DLL the file imports from")]
[NodePort("dll_count", PortDirection.Output, "number", Description = "How many DLLs the file imports from. One per [import] line in text")]
[NodePort("machine", PortDirection.Output, "string", Description = "x64 / x86 / other")]
public sealed class PeImportsNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var path = ctx.GetPortValue("path") as string;
        var withNames = ctx.GetPortValue("include_names") as bool? ?? false;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ctx.SetPortValue("text", "file not found: " + path);
            ctx.SetPortValue("dll_count", 0);
            return;
        }

        var pe = new Pe(File.ReadAllBytes(path));
        var entries = pe.ReadImports();

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e.Kind == "delay" ? "  [delay] " : "  [import] ").Append(e.Dll);
            sb.Append("  named=").Append(e.Named.Count).Append(" ordinal=").Append(e.Ordinals);
            sb.AppendLine();
            if (withNames && e.Named.Count > 0)
            {
                foreach (var n in e.Named) sb.Append("        ").AppendLine(n);
            }
        }

        ctx.SetPortValue("text", sb.ToString());
        ctx.SetPortValue("dll_count", entries.Count);
        ctx.SetPortValue("machine", pe.MachineName);
    }

    private sealed class ImportEntry
    {
        public string Dll;
        public string Kind;
        public readonly List<string> Named = new List<string>();
        public int Ordinals;
    }

    /// <summary>PE を読むのに要る最小限。構造体の並びは PE/COFF の仕様どおり。</summary>
    private sealed class Pe
    {
        private readonly byte[] _b;
        private readonly int _optOff;
        private readonly bool _plus;
        private readonly int _dirOff;
        private readonly List<(uint Va, uint VSize, uint RSize, uint Raw)> _sections = new List<(uint, uint, uint, uint)>();

        internal string MachineName { get; }

        internal Pe(byte[] bytes)
        {
            _b = bytes;
            var peOff = BitConverter.ToInt32(_b, 0x3C);
            if (BitConverter.ToUInt32(_b, peOff) != 0x00004550) throw new InvalidDataException("not a PE");

            var machine = BitConverter.ToUInt16(_b, peOff + 4);
            MachineName = machine == 0x8664 ? "x64" : machine == 0x14c ? "x86" : "0x" + machine.ToString("x");

            var numSections = BitConverter.ToUInt16(_b, peOff + 6);
            var optSize = BitConverter.ToUInt16(_b, peOff + 20);
            _optOff = peOff + 24;
            _plus = BitConverter.ToUInt16(_b, _optOff) == 0x20B;
            _dirOff = _optOff + (_plus ? 112 : 96);

            var sectOff = _optOff + optSize;
            for (int i = 0; i < numSections; i++)
            {
                var s = sectOff + i * 40;
                _sections.Add((
                    BitConverter.ToUInt32(_b, s + 12),   // VirtualAddress
                    BitConverter.ToUInt32(_b, s + 8),    // VirtualSize
                    BitConverter.ToUInt32(_b, s + 16),   // SizeOfRawData
                    BitConverter.ToUInt32(_b, s + 20))); // PointerToRawData
            }
        }

        /// <summary>
        /// RVA をファイル内オフセットへ。収まる範囲は VirtualSize ではなく
        /// SizeOfRawData で決まる（VirtualSize の方が大きい区間はファイルに実体が無い）。
        /// </summary>
        private int Offset(uint rva)
        {
            foreach (var s in _sections)
            {
                if (rva < s.Va) continue;
                var delta = rva - s.Va;
                if (delta >= Math.Max(s.VSize, s.RSize)) continue;
                if (delta >= s.RSize) return -1;          // ファイルに実体が無い
                var o = (long)s.Raw + delta;
                return o >= 0 && o < _b.Length ? (int)o : -1;
            }
            return -1;
        }

        private string CString(int off)
        {
            if (off < 0 || off >= _b.Length) return null;
            var end = off;
            while (end < _b.Length && _b[end] != 0) end++;
            return Encoding.ASCII.GetString(_b, off, end - off);
        }

        private uint Dir(int index) => BitConverter.ToUInt32(_b, _dirOff + index * 8);

        internal List<ImportEntry> ReadImports()
        {
            var result = new List<ImportEntry>();
            // データディレクトリ 1 = import、13 = 遅延読み込み。
            Walk(result, Dir(1), descriptorSize: 20, nameField: 12, thunkField: 0, altThunkField: 16, kind: "import");
            Walk(result, Dir(13), descriptorSize: 32, nameField: 4, thunkField: 16, altThunkField: 12, kind: "delay");
            return result;
        }

        private void Walk(List<ImportEntry> into, uint dirRva, int descriptorSize, int nameField,
                          int thunkField, int altThunkField, string kind)
        {
            if (dirRva == 0) return;
            var p = Offset(dirRva);
            if (p < 0) return;

            for (; p + descriptorSize <= _b.Length; p += descriptorSize)
            {
                var nameRva = BitConverter.ToUInt32(_b, p + nameField);
                var thunkRva = BitConverter.ToUInt32(_b, p + thunkField);
                if (thunkRva == 0) thunkRva = BitConverter.ToUInt32(_b, p + altThunkField);
                if (nameRva == 0 && thunkRva == 0) break;      // 終端は全ゼロの記述子

                var dll = CString(Offset(nameRva));
                if (string.IsNullOrEmpty(dll)) continue;

                var e = new ImportEntry
                {
                    Dll = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant(),
                    Kind = kind,
                };

                var t = Offset(thunkRva);
                if (t >= 0)
                {
                    var step = _plus ? 8 : 4;
                    for (var q = t; q + step <= _b.Length; q += step)
                    {
                        var v = _plus ? BitConverter.ToUInt64(_b, q) : BitConverter.ToUInt32(_b, q);
                        if (v == 0) break;
                        var ordinalFlag = _plus ? 0x8000000000000000UL : 0x80000000UL;
                        if ((v & ordinalFlag) != 0) { e.Ordinals++; continue; }
                        // Hint/Name テーブル: 2 バイトの hint のあとに名前。
                        var no = Offset((uint)v);
                        if (no < 0) continue;
                        var n = CString(no + 2);
                        if (!string.IsNullOrEmpty(n)) e.Named.Add(n);
                    }
                }
                into.Add(e);
            }
        }
    }
}
