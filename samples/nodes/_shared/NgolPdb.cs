using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ディスク上の PE とその PDB を、対象プロセスに触らずに読むための共有部品。
///
/// PDB は作り物のベースアドレスへ載せる。対象プロセスは開かないので、
/// 相手が固まっていても、そもそも起動していなくても使える。
///
/// 見つからなかったのか、そもそもシンボルが載っていないのかを必ず区別する。
/// dbghelp は SymGetModuleInfo64 の SymType でそれを教えるので、呼び出し側へ渡す。
/// 区別せずに「0 件」とだけ返すと、読めていないことを「無い」と読み違える。
/// </summary>
internal static class NgolPdb
{
    private const uint SYMOPT_UNDNAME = 0x00000002;
    private const uint SYMOPT_LOAD_LINES = 0x00000010;
    private const uint SYMOPT_FAIL_CRITICAL_ERRORS = 0x00000200;
    // これが無いと、署名の合わない PDB を黙って受け入れ、別のビルドの名前を返す。
    private const uint SYMOPT_EXACT_SYMBOLS = 0x00000400;

    // SYMBOL_INFO（x64）: Address は +56、MaxNameLen は +80、Name は +84。
    private const int SYM_SIZEOF = 88;
    private const int SYM_TYPEINDEX = 4;
    private const int SYM_INDEX = 24;
    private const int SYM_ADDRESS = 56;
    private const int SYM_MAXNAMELEN = 80;
    private const int SYM_NAME = 84;

    // IMAGEHLP_MODULE64: SizeOfStruct 0 / SymType 32 / LoadedPdbName 580 / TypeInfo 1660。
    private const int MOD_SIZEOF = 1680;
    private const int MOD_SYMTYPE = 32;
    private const int MOD_LOADEDPDBNAME = 580;
    private const int MOD_TYPEINFO = 1660;

    // IMAGEHLP_SYMBOL_TYPE_INFO
    private const uint TI_GET_SYMTAG = 0;
    private const uint TI_GET_SYMNAME = 1;
    private const uint TI_GET_LENGTH = 2;
    private const uint TI_GET_TYPEID = 4;
    private const uint TI_FINDCHILDREN = 7;
    private const uint TI_GET_DATAKIND = 8;
    private const uint TI_GET_OFFSET = 10;
    private const uint TI_GET_CHILDRENCOUNT = 13;

    private const int SymTagData = 7;
    private const int SymTagBaseClass = 18;
    private const int DataIsMember = 7;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymInitialize(IntPtr h, string searchPath, bool invade);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymCleanup(IntPtr h);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern ulong SymLoadModuleEx(IntPtr h, IntPtr file, string image, string module,
                                                ulong baseAddr, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymGetModuleInfo64(IntPtr h, ulong addr, IntPtr info);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymFromName(IntPtr h, string name, IntPtr symbol);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymFromAddr(IntPtr h, ulong addr, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymEnumSymbols(IntPtr h, ulong baseAddr, string mask, EnumProc cb, IntPtr ctx);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymEnumTypes(IntPtr h, ulong baseAddr, EnumProc cb, IntPtr ctx);
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymGetTypeFromName(IntPtr h, ulong baseAddr, string name, IntPtr symbol);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymGetTypeInfo(IntPtr h, ulong modBase, uint typeId, uint what, IntPtr info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private delegate bool EnumProc(IntPtr sym, uint size, IntPtr ctx);

    /// <summary>1 つの PE と PDB を読んでいる間の状態。使い終わったら Dispose する。</summary>
    internal sealed class Session : IDisposable
    {
        internal ulong Base;
        /// <summary>true のときだけ結果を信用してよい。false なら Problem に理由が入る。</summary>
        internal bool HasSymbols;
        /// <summary>型情報まで持っているか。無い PDB では配置を引けない。</summary>
        internal bool HasTypeInfo;
        /// <summary>dbghelp が言うシンボルの種類。pdb / export / none など</summary>
        internal string SymbolKind = "none";
        /// <summary>PDB ファイル自体の形式。classic / portable / missing / unknown</summary>
        internal string PdbFormat = "missing";
        internal string LoadedPdb = "";
        internal string Problem;

        private IntPtr _handle;
        private bool _initialized;

        internal Session(string imagePath, bool exactSymbols)
        {
            _handle = GetCurrentProcess();
            var dir = System.IO.Path.GetDirectoryName(imagePath) ?? ".";

            uint opts = SYMOPT_UNDNAME | SYMOPT_LOAD_LINES | SYMOPT_FAIL_CRITICAL_ERRORS;
            if (exactSymbols) opts |= SYMOPT_EXACT_SYMBOLS;
            SymSetOptions(opts);

            if (!SymInitialize(_handle, dir, false))
            {
                Problem = "SymInitialize failed (err " + Marshal.GetLastWin32Error() + ")";
                return;
            }
            _initialized = true;

            var size = (uint)new System.IO.FileInfo(imagePath).Length;
            Base = 0x40000000UL;
            var loaded = SymLoadModuleEx(_handle, IntPtr.Zero, imagePath, null, Base, size, IntPtr.Zero, 0);
            if (loaded == 0)
            {
                Problem = "SymLoadModuleEx failed (err " + Marshal.GetLastWin32Error() + ")";
                return;
            }
            Base = loaded;
            ReadModuleInfo();
            InspectPdbFile(imagePath);
        }

        /// <summary>
        /// PDB ファイルの形式を、ファイルの先頭から直に見る。
        /// dbghelp は .NET の portable PDB を「読んだ」と報告しながら 1 件も返さないので、
        /// 種類を聞くだけでは「一致しなかった」と区別できない。
        /// </summary>
        private void InspectPdbFile(string imagePath)
        {
            var path = !string.IsNullOrEmpty(LoadedPdb) && System.IO.File.Exists(LoadedPdb)
                ? LoadedPdb
                : System.IO.Path.ChangeExtension(imagePath, ".pdb");
            if (!System.IO.File.Exists(path))
            {
                if (!HasSymbols && Problem != null) Problem += ". No .pdb file sits beside the image";
                return;
            }

            byte[] head;
            try
            {
                head = new byte[4];
                using var f = System.IO.File.OpenRead(path);
                if (f.Read(head, 0, 4) != 4) { PdbFormat = "unknown"; return; }
            }
            catch { PdbFormat = "unknown"; return; }

            if (head[0] == 'B' && head[1] == 'S' && head[2] == 'J' && head[3] == 'B')
            {
                PdbFormat = "portable";
                // dbghelp は portable PDB を扱えない。読めたように見えても中身は出ない。
                HasSymbols = false;
                HasTypeInfo = false;
                Problem = "the .pdb beside this image is a portable (.NET) PDB. dbghelp reports it as loaded "
                        + "but yields no symbols at all. Inspect managed assemblies through reflection or the "
                        + "IL nodes instead";
                return;
            }
            PdbFormat = head[0] == 'M' ? "classic" : "unknown";
            if (!HasSymbols)
            {
                Problem = (Problem ?? "no symbols were loaded")
                        + ". A classic .pdb is present beside the image but was not accepted, which normally "
                        + "means it does not match this build. Compare their timestamps, or set "
                        + "exact_symbols false to read it anyway and accept that the names may be wrong";
            }
        }

        private void ReadModuleInfo()
        {
            var buf = Marshal.AllocHGlobal(MOD_SIZEOF);
            try
            {
                for (int i = 0; i < MOD_SIZEOF; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, MOD_SIZEOF);
                if (!SymGetModuleInfo64(_handle, Base, buf))
                {
                    Problem = "SymGetModuleInfo64 failed (err " + Marshal.GetLastWin32Error() + ")";
                    return;
                }
                var symType = Marshal.ReadInt32(buf, MOD_SYMTYPE);
                HasTypeInfo = Marshal.ReadInt32(buf, MOD_TYPEINFO) != 0;
                LoadedPdb = Marshal.PtrToStringAnsi(IntPtr.Add(buf, MOD_LOADEDPDBNAME)) ?? "";
                SymbolKind = symType switch
                {
                    0 => "none", 1 => "coff", 2 => "codeview", 3 => "pdb",
                    4 => "export", 5 => "deferred", 6 => "sym", 7 => "dia", 8 => "virtual",
                    _ => "unknown(" + symType + ")",
                };
                HasSymbols = symType == 3 || symType == 2 || symType == 7;
                if (!HasSymbols)
                {
                    Problem = SymbolKind == "export"
                        ? "no PDB was loaded; only the export table is available. "
                        + "The PDB is missing, does not match the image, or is a .NET portable PDB, which dbghelp does not read"
                        : "no symbols were loaded (" + SymbolKind + ")";
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal bool TryNameToRva(string name, out ulong rva)
        {
            rva = 0;
            if (!HasSymbols) return false;
            var buf = AllocSym();
            try
            {
                if (!SymFromName(_handle, name, buf)) return false;
                rva = (ulong)Marshal.ReadInt64(buf, SYM_ADDRESS) - Base;
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal bool TryRvaToName(ulong rva, out string name, out ulong displacement)
        {
            name = null; displacement = 0;
            if (!HasSymbols) return false;
            var buf = AllocSym();
            try
            {
                if (!SymFromAddr(_handle, Base + rva, out displacement, buf)) return false;
                name = Marshal.PtrToStringAnsi(IntPtr.Add(buf, SYM_NAME));
                return !string.IsNullOrEmpty(name);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>ワイルドカードで関数などを列挙する。enumerationFailed で「呼び出しが失敗した」を区別する。</summary>
        internal List<KeyValuePair<ulong, string>> EnumSymbols(string mask, int limit, out bool enumerationFailed)
        {
            var found = new List<KeyValuePair<ulong, string>>();
            enumerationFailed = false;
            if (!HasSymbols) return found;
            EnumProc cb = (sym, size, ctx) =>
            {
                if (found.Count < limit)
                {
                    var addr = (ulong)Marshal.ReadInt64(sym, SYM_ADDRESS);
                    var nm = Marshal.PtrToStringAnsi(IntPtr.Add(sym, SYM_NAME));
                    found.Add(new KeyValuePair<ulong, string>(addr - Base, nm));
                }
                return true;
            };
            if (!SymEnumSymbols(_handle, Base, mask, cb, IntPtr.Zero)) enumerationFailed = true;
            GC.KeepAlive(cb);
            return found;
        }

        internal List<string> EnumTypeNames(string mask, int limit, out bool enumerationFailed)
        {
            var found = new List<string>();
            enumerationFailed = false;
            if (!HasSymbols) return found;
            // 同じ型が複数の翻訳単位から出るので、名前で 1 つにまとめる。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            EnumProc cb = (sym, size, ctx) =>
            {
                if (found.Count < limit)
                {
                    var nm = Marshal.PtrToStringAnsi(IntPtr.Add(sym, SYM_NAME));
                    if (!string.IsNullOrEmpty(nm) && Matches(nm, mask) && seen.Add(nm)) found.Add(nm);
                }
                return true;
            };
            if (!SymEnumTypes(_handle, Base, cb, IntPtr.Zero)) enumerationFailed = true;
            GC.KeepAlive(cb);
            return found;
        }

        /// <summary>型の中の 1 つのフィールド。</summary>
        internal sealed class Field
        {
            internal long Offset;
            internal string Name;
            internal long Size;
            internal string TypeName;
            internal bool IsBaseClass;
            internal int Depth;
        }

        internal bool TryTypeLayout(string typeName, bool recurse, out long totalSize, out List<Field> fields)
        {
            totalSize = 0;
            fields = new List<Field>();
            if (!HasSymbols) return false;
            var buf = AllocSym();
            try
            {
                if (!SymGetTypeFromName(_handle, Base, typeName, buf)) return false;
                var typeId = (uint)Marshal.ReadInt32(buf, SYM_INDEX);
                totalSize = GetQword(typeId, TI_GET_LENGTH) ?? 0;
                Collect(typeId, 0, 0, recurse, fields);
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void Collect(uint typeId, long addOffset, int depth, bool recurse, List<Field> into)
        {
            foreach (var childId in Children(typeId))
            {
                var tag = GetDword(childId, TI_GET_SYMTAG);
                if (tag == null) continue;
                var name = GetName(childId);
                var off = GetDword(childId, TI_GET_OFFSET) ?? 0;
                var memberType = GetDword(childId, TI_GET_TYPEID);
                long len = 0;
                string typeName = null;
                if (memberType != null)
                {
                    len = GetQword((uint)memberType.Value, TI_GET_LENGTH) ?? 0;
                    typeName = GetName((uint)memberType.Value);
                }

                if (tag.Value == SymTagBaseClass)
                {
                    into.Add(new Field { Offset = addOffset + off, Name = name, Size = len, IsBaseClass = true, Depth = depth });
                    if (recurse && memberType != null) Collect((uint)memberType.Value, addOffset + off, depth + 1, true, into);
                    continue;
                }
                if (tag.Value != SymTagData) continue;
                // オフセットを持たない静的メンバは配置の話ではないので落とす。
                if (GetDword(childId, TI_GET_DATAKIND) != DataIsMember) continue;
                into.Add(new Field { Offset = addOffset + off, Name = name, Size = len, TypeName = typeName, Depth = depth });
            }
        }

        private uint[] Children(uint typeId)
        {
            var count = GetDword(typeId, TI_GET_CHILDRENCOUNT);
            if (count == null || count.Value <= 0) return Array.Empty<uint>();
            var size = 8 + 4 * count.Value;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, 0, count.Value);
                Marshal.WriteInt32(buf, 4, 0);
                if (!SymGetTypeInfo(_handle, Base, typeId, TI_FINDCHILDREN, buf)) return Array.Empty<uint>();
                var ids = new uint[count.Value];
                for (int i = 0; i < ids.Length; i++) ids[i] = (uint)Marshal.ReadInt32(buf, 8 + 4 * i);
                return ids;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private int? GetDword(uint id, uint what)
        {
            var buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(buf, 0, 0);
                if (!SymGetTypeInfo(_handle, Base, id, what, buf)) return null;
                return Marshal.ReadInt32(buf, 0);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private long? GetQword(uint id, uint what)
        {
            var buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(buf, 0, 0);
                if (!SymGetTypeInfo(_handle, Base, id, what, buf)) return null;
                return Marshal.ReadInt64(buf, 0);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private string GetName(uint id)
        {
            var buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(buf, 0, 0);
                if (!SymGetTypeInfo(_handle, Base, id, TI_GET_SYMNAME, buf)) return null;
                var p = Marshal.ReadIntPtr(buf, 0);
                if (p == IntPtr.Zero) return null;
                var s = Marshal.PtrToStringUni(p);
                LocalFree(p);
                return s;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static IntPtr AllocSym()
        {
            const int nameLen = 1024;
            var buf = Marshal.AllocHGlobal(SYM_SIZEOF + nameLen);
            for (int i = 0; i < SYM_SIZEOF + nameLen; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, SYM_SIZEOF);
            Marshal.WriteInt32(buf, SYM_MAXNAMELEN, nameLen);
            return buf;
        }

        public void Dispose()
        {
            if (_initialized) { SymCleanup(_handle); _initialized = false; }
        }
    }

    internal static Session Open(string imagePath, bool exactSymbols) => new Session(imagePath, exactSymbols);

    /// <summary>`*` と `?` だけの簡単な一致。dbghelp の型列挙はマスクを受け取らないので自前で絞る。</summary>
    internal static bool Matches(string text, string mask)
    {
        if (string.IsNullOrEmpty(mask) || mask == "*") return true;
        return IsMatch(text, 0, mask, 0);
    }

    private static bool IsMatch(string t, int ti, string m, int mi)
    {
        while (mi < m.Length)
        {
            if (m[mi] == '*')
            {
                for (int k = ti; k <= t.Length; k++) if (IsMatch(t, k, m, mi + 1)) return true;
                return false;
            }
            if (ti >= t.Length) return false;
            if (m[mi] != '?' && char.ToLowerInvariant(m[mi]) != char.ToLowerInvariant(t[ti])) return false;
            ti++; mi++;
        }
        return ti == t.Length;
    }
}
