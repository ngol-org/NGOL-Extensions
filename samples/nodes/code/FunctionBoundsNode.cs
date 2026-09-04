using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定 RVA を含む関数の先頭・末尾を、PE の例外ディレクトリ（`.pdata`）から引く。
///
/// x64 Windows の PE は `RUNTIME_FUNCTION`（BeginAddress / EndAddress / UnwindInfo、各12バイト）の
/// 配列を BeginAddress 昇順で持っており、これがコンパイラの出した**関数境界の一次情報**にあたる。
/// 二分探索で引くだけなので、モジュールが大きくても即座に返る。
///
/// 主な用途は**フックを設置する前に「そのアドレスは本当に関数先頭か」を確定させる**こと。
/// 関数の途中にフック（JMP の書き込み）を張ると、正規の呼び出し元が関数先頭から実行して
/// そこへ到達した瞬間に、潰された命令の分だけ状態が壊れる。特にエピローグを潰した場合は
/// スタック復元（`add rsp` / `pop` 列）がスキップされ、**フック設置時ではなく後続の
/// 無関係なコードで落ちる**ため、事後の原因究明が非常に高くつく。
///
/// 「先頭バイトが 0x00 ならアライメント用パディング」という従来の確認方法は、
/// 関数の1バイト手前しか検出できない。xref スキャン等で得たアドレスが関数の深い位置を
/// 指している場合はそちらでは素通りするので、本ノードで先頭からのオフセットを見る。
///
/// フック以外にも、xref やクラッシュログの fault offset が「どの関数の何オフセットか」を
/// 特定する用途に使える（逆アセンブルを後方へ遡ると命令境界がずれるため、この判定は
/// 目視では確実に行えない）。
///
/// 注意: スタックフレームを持たないリーフ関数は `.pdata` に載らないことがあり、その場合は
/// `found=false` を返す。Windows x64 専用。
///
/// `function_end` は `.pdata` の `EndAddress` をそのまま返す。これは終端を含まない値で、
/// **末尾のアライメント用パディングを含みうる**。命令単位で境界を求める解析ツールは
/// 「最後の命令の次のバイト」を末尾とするため、両者は数バイト食い違うことがある。
/// どちらかが誤りなのではなく数え方が違うだけなので、突き合わせる際は `bounds_source` を見ること。
/// </summary>
[NodeType("ngol.code.function_bounds", "Code", "Function Bounds",
    Version = "1.0.1",
    Description = "Resolve the enclosing function's start/end for an RVA using the PE exception directory (.pdata).")]
[NodePort("rva",                PortDirection.Input,  "string", IsRequired = true, Description = "RVA to inspect, hex string (e.g. '0x1a2b3c')")]
[NodePort("module",             PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module")]
[NodePort("found",              PortDirection.Output, "boolean", Description = "False when the RVA is not covered by any .pdata entry")]
[NodePort("function_start",     PortDirection.Output, "string", Description = "Enclosing function start RVA (hex), empty when not found")]
[NodePort("function_end",       PortDirection.Output, "string", Description = "Enclosing function end RVA, exclusive (hex). Taken verbatim from .pdata, so it can sit past the last instruction when the compiler padded the function for alignment")]
[NodePort("function_size",      PortDirection.Output, "number", Description = "Function size in bytes (function_end - function_start; includes trailing alignment padding)")]
[NodePort("bounds_source",      PortDirection.Output, "string", Description = "Where the bounds came from, so the numbers can be compared with other tools without guessing")]
[NodePort("offset_in_function", PortDirection.Output, "number", Description = "Offset from the function start; 0 means the RVA is the entry point. -1 when not found")]
[NodePort("is_function_start",  PortDirection.Output, "boolean", Description = "True only when the RVA is exactly the function entry point")]
[NodePort("detail",             PortDirection.Output, "string", Description = "Human-readable summary of the result")]
public sealed class FunctionBoundsNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string moduleName);

    const int RuntimeFunctionSize = 12;   // BeginAddress / EndAddress / UnwindInfoAddress
    const int ExceptionDirectoryIndex = 3;

    public void Execute(IExecutionContext ctx)
    {
        var moduleName = NgolModuleDefault.Resolve(ctx.GetPortValue("module") as string ?? ctx.GetParam<string>("module"));
        var rvaStr = ctx.GetPortValue("rva") as string ?? ctx.GetParam<string>("rva") ?? "";

        try
        {
            var rva = ParseRva(rvaStr);

            var moduleBase = GetModuleHandleA(moduleName);
            if (moduleBase == IntPtr.Zero)
                throw new Exception("module not loaded: " + moduleName);

            if (!TryGetExceptionDirectory(moduleBase, out var tableRva, out var tableSize))
                throw new Exception("module has no exception directory (.pdata)");

            var entryCount = (int)(tableSize / RuntimeFunctionSize);
            var table = moduleBase + (int)tableRva;

            // テーブル全体を読める分だけ取り込む。ヘッダが示すサイズと実際にマップされている
            // 範囲が食い違うことがあるため、読めた分で件数を数え直す。
            var tableBytes = new byte[tableSize];
            var tableGot = NgolSafeMemory.Read(table, tableBytes, 0, (int)tableSize);
            if (tableGot < RuntimeFunctionSize)
                throw new Exception("exception directory (.pdata) is not readable");
            entryCount = tableGot / RuntimeFunctionSize;

            // RUNTIME_FUNCTION は BeginAddress 昇順に並ぶので二分探索できる
            var lo = 0;
            var hi = entryCount - 1;
            var hit = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var begin = (uint)BitConverter.ToInt32(tableBytes, mid * RuntimeFunctionSize);
                var end = (uint)BitConverter.ToInt32(tableBytes, mid * RuntimeFunctionSize + 4);
                if (rva < begin) hi = mid - 1;
                else if (rva >= end) lo = mid + 1;
                else { hit = mid; break; }
            }

            if (hit < 0)
            {
                SetNotFound(ctx,
                    "RVA 0x" + rva.ToString("x") + " is not covered by any of the " + entryCount
                    + " .pdata entries. Functions without a stack frame are sometimes omitted from .pdata.");
                return;
            }

            var fnStart = (uint)BitConverter.ToInt32(tableBytes, hit * RuntimeFunctionSize);
            var fnEnd = (uint)BitConverter.ToInt32(tableBytes, hit * RuntimeFunctionSize + 4);
            var offset = (int)(rva - fnStart);

            var detail = "function 0x" + fnStart.ToString("x") + " - 0x" + fnEnd.ToString("x")
                       + " (" + (fnEnd - fnStart) + " bytes); RVA 0x" + rva.ToString("x")
                       + " is at +0x" + offset.ToString("x") + ". "
                       + (offset == 0
                            ? "This is the function entry point."
                            : "WARNING: this is inside the function body, not its entry point. "
                              + "Hooking here overwrites live instructions and corrupts execution state.");

            ctx.SetPortValue("found", true);
            ctx.SetPortValue("function_start", "0x" + fnStart.ToString("x"));
            ctx.SetPortValue("function_end", "0x" + fnEnd.ToString("x"));
            ctx.SetPortValue("function_size", (double)(fnEnd - fnStart));
            ctx.SetPortValue("offset_in_function", (double)offset);
            ctx.SetPortValue("is_function_start", offset == 0);
            ctx.SetPortValue("bounds_source",
                "PE exception directory (.pdata) RUNTIME_FUNCTION[" + hit + "] of " + entryCount
                + "; end is exclusive and may include trailing alignment padding");
            ctx.SetPortValue("detail", detail);

            if (offset != 0)
                ctx.Logger.LogWarning("[FunctionBounds] 0x" + rva.ToString("x")
                    + " is +0x" + offset.ToString("x") + " inside 0x" + fnStart.ToString("x")
                    + " - not a function entry point");
        }
        catch (Exception ex)
        {
            SetNotFound(ctx, "ERROR: " + ex.GetType().Name + ": " + ex.Message);
            ctx.Logger.LogWarning("[FunctionBounds] " + ex.Message);
        }
    }

    static uint ParseRva(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) throw new Exception("rva is empty");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return Convert.ToUInt32(s, 16);
    }

    /// <summary>PE ヘッダを辿って DataDirectory[3]（例外ディレクトリ）の RVA とサイズを得る。</summary>
    static bool TryGetExceptionDirectory(IntPtr moduleBase, out uint tableRva, out uint tableSize)
    {
        tableRva = 0;
        tableSize = 0;

        // ヘッダは先頭 1 ページに収まる。読めることを確かめてから辿る
        // （渡されたハンドルがモジュールでない場合、そのまま読むとプロセスごと落ちる）。
        var header = new byte[0x400];
        if (NgolSafeMemory.Read(moduleBase, header, 0, header.Length) < header.Length) return false;

        var peOffset = BitConverter.ToInt32(header, 0x3C);
        var optionalHeader = peOffset + 24;
        if (peOffset <= 0 || optionalHeader + 120 + ExceptionDirectoryIndex * 8 + 8 > header.Length) return false;

        var magic = BitConverter.ToUInt16(header, optionalHeader);
        if (magic != 0x20B) return false;              // PE32+ 以外は対象外

        var dataDirectory = optionalHeader + 112;      // PE32+ の DataDirectory 先頭
        var entry = dataDirectory + ExceptionDirectoryIndex * 8;
        tableRva = (uint)BitConverter.ToInt32(header, entry);
        tableSize = (uint)BitConverter.ToInt32(header, entry + 4);
        return tableRva != 0 && tableSize != 0;
    }

    static void SetNotFound(IExecutionContext ctx, string detail)
    {
        ctx.SetPortValue("found", false);
        ctx.SetPortValue("function_start", "");
        ctx.SetPortValue("function_end", "");
        ctx.SetPortValue("function_size", 0d);
        ctx.SetPortValue("offset_in_function", -1d);
        ctx.SetPortValue("is_function_start", false);
        ctx.SetPortValue("bounds_source", "");
        ctx.SetPortValue("detail", detail);
    }
}
