using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// blender.exe の **export されていない内部関数**を、同梱の PDB から名前で引く。
///
/// 「export が 10 個しか無いから内部 C API は呼べない」は **誤り**。
///     export は番地を得る手段の 1 つにすぎず、Blender は
///     `blender.pdb`（実測 88.9 MB）を実行ファイルの隣に同梱している。
///
/// `dbghelp.dll` は Windows の DLL で export を持つので、ノードから P/Invoke できる。
///    そこから PDB を読ませれば `BKE_*` / `RNA_*` / `ED_*` が名前で引ける。
///    番地さえ取れれば <c>Marshal.GetDelegateForFunctionPointer</c> で呼べる--
///    これは NGOL の既存ブリッジ（AviUtl2 の関数表、Lua の C API）と同じ手。
///
/// **このノードは引くだけで、呼ばない。** 呼ぶ側には別の条件があるため:
///     1) 引数と呼び出し規約を自分で決めることになる（PDB に型が在っても保証ではない）
///     2) Blender の内部 API は **メインスレッド**と正しい context を前提にしている
///     => 番地が取れることと、安全に呼べることは別。まず取れることだけを確かめる。
/// </summary>
[NodeType("blender.symbol.resolve", "Blender", "Resolve Symbol (PDB)",
    Version = "1.0.0",
    Description = "Look up functions inside blender.exe by name using the PDB that ships next to it, including the ones that are not exported. Proves that a missing export table does not mean a function cannot be reached: dbghelp reads the PDB and hands back an address, which is all a call needs. It only resolves - it does not call anything.")]
[NodePort("names", PortDirection.Input, "string", Description = "Symbol names to look up, comma separated, e.g. BKE_object_add,BKE_mesh_new_nomain,RNA_id_pointer_create")]
[NodePort("module", PortDirection.Input, "string", Description = "Module the symbols belong to. Default blender.exe")]
[NodePort("found", PortDirection.Output, "number", Description = "How many of the names resolved")]
[NodePort("asked", PortDirection.Output, "number", Description = "How many names were asked for")]
[NodePort("base_hex", PortDirection.Output, "string", Description = "Module base address")]
[NodePort("first_rva", PortDirection.Output, "string", Description = "RVA of the first name that resolved, ready to hand to ngol.code.disasm")]
[NodePort("result", PortDirection.Output, "string", Description = "One line per name: name / absolute address / RVA, or why it did not resolve")]
public sealed class BlenderResolveSymbolNode : INode
{
    // --- dbghelp: PDB を読ませる ------------------------------------------------------
    // dbghelp は「今のプロセス」を対象にできる。fInvadeProcess=true で
    //    読み込み済みモジュールを列挙し、各 PDB を隣から探して読む。
    private const uint SYMOPT_UNDNAME = 0x00000002;
    private const uint SYMOPT_FAIL_CRITICAL_ERRORS = 0x00000200;
    private const uint SYMOPT_NO_PROMPTS = 0x00080000;
    // SYMOPT_DEFERRED_LOADS は使わない。遅延にすると SymFromName が
    //    読み込みを起こさず ERROR_MOD_NOT_FOUND (126) で空振りする（実測）。

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SymInitializeW(IntPtr process, string searchPath, bool invadeProcess);

    // 対象モジュールの PDB を明示的に読ませる。自動任せにしない。
    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ulong SymLoadModuleExW(IntPtr process, IntPtr file, string imageName,
        string moduleName, ulong baseOfDll, uint dllSize, IntPtr data, uint flags);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SymFromNameW(IntPtr process, string name, IntPtr symbolInfo);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymCleanup(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(IntPtr module, StringBuilder fileName, int size);

    // SYMBOL_INFOW の中で必要なのは Address（x64 で offset 56）だけ。
    // 構造体を C# で宣言するとパディングを取り違えやすいので、
    //    生のバッファに書いて読む（offset を根拠付きで固定する）。
    private const int OFF_SIZE_OF_STRUCT = 0;    // ULONG
    private const int OFF_MOD_BASE = 32;         // ULONG64
    private const int OFF_ADDRESS = 56;          // ULONG64
    private const int OFF_MAX_NAME_LEN = 80;     // ULONG
    private const int SIZEOF_SYMBOL_INFOW = 88;  // Name[1] を含む公称サイズ
    private const int NAME_CAPACITY = 1024;

    // SymInitialize はプロセスに 1 回。2 度目は失敗するので覚えておく。
    private static bool s_initialised;
    private static bool s_moduleLoaded;
    private static readonly object s_gate = new();

    public void Execute(IExecutionContext ctx)
    {
        string names = (ctx.GetPortValue("names") as string) ?? "";
        string module = (ctx.GetPortValue("module") as string) ?? "";
        if (module.Length == 0) module = "blender.exe";

        ctx.SetPortValue("found", 0d);
        ctx.SetPortValue("asked", 0d);

        IntPtr moduleHandle = GetModuleHandleW(module);
        if (moduleHandle == IntPtr.Zero)
        {
            ctx.SetPortValue("result", module + " is not loaded in this process");
            return;
        }
        long moduleBase = moduleHandle.ToInt64();
        ctx.SetPortValue("base_hex", "0x" + moduleBase.ToString("x"));

        var report = new StringBuilder();
        int asked = 0, found = 0;
        string firstRva = "";

        lock (s_gate)
        {
            // 対象モジュールの実体パスを取る。PDB は実行ファイルの隣にある。
            //
            // `System.Diagnostics.Process.Modules` は使えない。
            //    `ProcessModuleCollection` の基底が `ReadOnlyCollectionBase` で、
            //    `System.Collections.NonGeneric` の参照が要る（CS0012 / CS1579）。
            //    ノードの参照集合はホストが決めるので、足りない前提で書く。
            //    => Win32 だけで済ませる。
            var pathBuffer = new StringBuilder(1024);
            int pathLength = GetModuleFileNameW(moduleHandle, pathBuffer, pathBuffer.Capacity);
            if (pathLength == 0)
            {
                ctx.SetPortValue("result",
                    module + " -- could not get its file path (GetLastError="
                    + Marshal.GetLastWin32Error() + ")");
                return;
            }
            string imagePath = pathBuffer.ToString(0, pathLength);
            // SymLoadModuleExW は DllSize=0 なら画像から自分で求める。
            //    => SizeOfImage を自前で読む必要が無い。
            const uint imageSize = 0;

            if (!s_initialised)
            {
                // dbghelp の状態は **プロセス全体で 1 つ**。
                //    前に別の設定（SYMOPT_DEFERRED_LOADS 付き）で初期化されていると、
                //    そのまま引き継がれて **SymFromName が ERROR_MOD_NOT_FOUND (126) で空振りする**
                //    （実測でこれを踏んだ。ノードをホットリロードしても static が作り直されるだけで、
                //     dbghelp 側の状態は残る）。
                //    => 片付けてから入り直す。
                SymCleanup(GetCurrentProcess());

                SymSetOptions(SYMOPT_UNDNAME | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
                // 探索パスは実行ファイルの置き場所。ネットワークのシンボルサーバーは使わない
                // （外へ出さない）。
                string searchPath = System.IO.Path.GetDirectoryName(imagePath) ?? "";
                bool initOk = SymInitializeW(GetCurrentProcess(), searchPath, false);
                s_initialised = true;
                s_moduleLoaded = false;
                report.Append("dbghelp: SymInitializeW=").Append(initOk)
                      .Append("  search path = ").Append(searchPath).Append('\n');
            }

            if (!s_moduleLoaded)
            {
                ulong loaded = SymLoadModuleExW(GetCurrentProcess(), IntPtr.Zero, imagePath,
                                                null, (ulong)moduleBase, imageSize, IntPtr.Zero, 0);
                int loadError = Marshal.GetLastWin32Error();
                // 0 でも「既に読んである」(ERROR_SUCCESS) なら成功扱い。
                if (loaded == 0 && loadError != 0)
                {
                    ctx.SetPortValue("result",
                        "SymLoadModuleExW failed (GetLastError=" + loadError + ")\n  " + imagePath);
                    return;
                }
                s_moduleLoaded = true;
                report.Append("dbghelp: loaded ").Append(imagePath).Append('\n').Append('\n');
            }

            IntPtr buffer = Marshal.AllocHGlobal(SIZEOF_SYMBOL_INFOW + NAME_CAPACITY * 2);
            try
            {
                foreach (string raw in names.Split(','))
                {
                    string name = raw.Trim();
                    if (name.Length == 0) continue;
                    asked++;

                    // 毎回きれいにしてから引く。前回の残りを答えだと読まないため。
                    for (int i = 0; i < SIZEOF_SYMBOL_INFOW; i++) Marshal.WriteByte(buffer, i, 0);
                    Marshal.WriteInt32(buffer, OFF_SIZE_OF_STRUCT, SIZEOF_SYMBOL_INFOW);
                    Marshal.WriteInt32(buffer, OFF_MAX_NAME_LEN, NAME_CAPACITY);

                    if (!SymFromNameW(GetCurrentProcess(), name, buffer))
                    {
                        report.Append(name).Append("  not found (GetLastError=")
                              .Append(Marshal.GetLastWin32Error()).Append(")\n");
                        continue;
                    }

                    long address = Marshal.ReadInt64(buffer, OFF_ADDRESS);
                    long symbolModuleBase = Marshal.ReadInt64(buffer, OFF_MOD_BASE);
                    long rva = address - symbolModuleBase;

                    found++;
                    if (firstRva.Length == 0) firstRva = "0x" + rva.ToString("x");

                    report.Append(name)
                          .Append("  0x").Append(address.ToString("x"))
                          .Append("  rva 0x").Append(rva.ToString("x"));
                    if (symbolModuleBase != moduleBase)
                        report.Append("  different module, base=0x").Append(symbolModuleBase.ToString("x"));
                    report.Append('\n');
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        ctx.SetPortValue("asked", (double)asked);
        ctx.SetPortValue("found", (double)found);
        ctx.SetPortValue("first_rva", firstRva);
        ctx.SetPortValue("result", report.ToString());
    }
}
