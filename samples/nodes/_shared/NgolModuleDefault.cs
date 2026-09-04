using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// `module` ポートが空のときに対象とするモジュールを決める。
///
/// 既定はプロセスの主モジュール（実行イメージ）。
/// 特定のエンジン・ローダーの DLL 名を既定値として持たない--
/// 既定値は「そのノードがどの環境向けか」を静かに宣言してしまうため。
///
/// 各ノードは従来どおり名前を受け取って `GetModuleHandleA` に渡せばよい。
/// 呼び出し形を変えないことで、置き換えの影響を既定値だけに閉じている。
///
/// 実装が Win32 のみなのは意図的:
/// 動的コンパイルされるノードが参照できるのは、固定の少数のアセンブリと
/// 「ホストが偶然ロード済みのもの」に限られる。`System.Diagnostics.Process` は後者に当たり、
/// ホスト次第で解決できたりできなかったりする。呼び出し側と同じ依存の範囲（kernel32）に留める。
/// </summary>
internal static class NgolModuleDefault
{
    // W 版を使う。A 版はシステムの ANSI コードページで返るため、
    // 非 ASCII を含むパスが化ける（呼び出し側が名前で照合するので化けると解決に失敗する）。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, [Out] char[] lpFilename, uint nSize);

    /// <summary>
    /// 指定があればそのまま返す。空ならプロセスの主モジュール名（例: "Host.exe"）を返す。
    /// 取得できない場合は空文字を返し、呼び出し側の解決失敗として通常のエラー経路に乗る。
    /// </summary>
    public static string Resolve(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;

        try
        {
            // hModule = IntPtr.Zero は「現在のプロセスの実行イメージ」を指す。
            // 長いパスに備えて一度伸ばす。戻り値がバッファ長と等しいときは切り詰められている。
            var buf = new char[260];
            var len = GetModuleFileNameW(IntPtr.Zero, buf, (uint)buf.Length);
            if (len == buf.Length)
            {
                buf = new char[4096];
                len = GetModuleFileNameW(IntPtr.Zero, buf, (uint)buf.Length);
            }
            if (len == 0) return "";

            var full = new string(buf, 0, (int)len);
            var sep = full.LastIndexOf('\\');
            return sep >= 0 ? full.Substring(sep + 1) : full;
        }
        catch
        {
            return "";
        }
    }

    // ---- 読み込まれているモジュールの一覧 ----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    // 引数は 4 つ。公開シグネチャは 4 引数で、内部の 5 引数版へ既定値を足して委譲している
    // （実装の先頭で第 5 引数のスタック位置へ定数を積んでから呼んでいることを逆アセンブルで確認済み）。
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded);

    // W 版を使う理由は Resolve と同じ（A 版は非 ASCII を含む名前が化ける）。
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleBaseNameW(IntPtr hProcess, IntPtr hModule, [Out] char[] lpBaseName, uint nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameExW(IntPtr hProcess, IntPtr hModule, [Out] char[] lpFilename, uint nSize);

    /// <summary>読み込まれているモジュール 1 件分。</summary>
    public struct ModuleEntry
    {
        public string Name;
        public string Path;
        public long Base;
        public long Size;
    }


    /// <summary>
    /// このプロセスに読み込まれているモジュールを、大きい順に返す。
    ///
    /// 大きさを添えるのが要点。実行ファイルが起動用の小さな殻でしかなく、
    ///   本体が別の DLL に入っている構成があるため、名前だけでは対象を取り違える。
    ///   大きさが並んでいれば、どれが本体かはひと目で分かる。
    ///
    /// <paramref name="truncated"/> が true のとき、一覧は全部ではない。
    ///   黙って縮めると「出てこない」を「読み込まれていない」と誤読させるため、
    ///   呼び出し側は必ず利用者へ伝えること。
    ///
    /// データとして読み込まれたモジュール（実行対象ではなく資源としてのマップ）は
    ///   この一覧に出てこない。列挙 API がそもそも返さない。
    /// </summary>
    public static List<ModuleEntry> List(int max, out bool truncated)
    {
        truncated = false;
        var result = new List<ModuleEntry>();
        try
        {
            var proc = GetCurrentProcess();

            // 必要な大きさを先に問い合わせてから、その分だけ確保して取り直す。
            var probe = new IntPtr[1];
            if (!EnumProcessModules(proc, probe, (uint)(IntPtr.Size), out var needed)) return result;

            var count = (int)(needed / IntPtr.Size);
            if (count <= 0) return result;
            if (count > max) { count = max; truncated = true; }

            var handles = new IntPtr[count];
            var cb = (uint)(count * IntPtr.Size);
            if (!EnumProcessModules(proc, handles, cb, out var needed2)) return result;

            // 取りこぼしの判定は「要求量が渡した容量を超えたか」で行う（API が定める方法）。
            //   問い合わせと取得の間に別のスレッドが読み込むと一覧は増えるため、
            //   1 回目の件数で確保しただけでは足りないことがある。
            if (needed2 > cb) truncated = true;

            var buf = new char[512];
            foreach (var h in handles)
            {
                if (h == IntPtr.Zero) continue;
                var len = GetModuleBaseNameW(proc, h, buf, (uint)buf.Length);
                if (len == 0) continue;

                long size = 0;
                if (GetModuleInformation(proc, h, out var info, (uint)Marshal.SizeOf<MODULEINFO>()))
                    size = info.SizeOfImage;

                var pathBuf = new char[4096];
                var pathLen = GetModuleFileNameExW(proc, h, pathBuf, (uint)pathBuf.Length);

                result.Add(new ModuleEntry
                {
                    Name = new string(buf, 0, (int)len),
                    Path = pathLen > 0 ? new string(pathBuf, 0, (int)pathLen) : "",
                    Base = h.ToInt64(),
                    Size = size,
                });
            }

            result.Sort((x, y) => y.Size.CompareTo(x.Size));
        }
        catch
        {
            // 取得できない環境では空を返す。呼び出し側は通常の解決失敗として扱えばよい。
        }
        return result;
    }

    /// <summary>一覧を人が読める表にする。</summary>
    public static string FormatList(IReadOnlyList<ModuleEntry> modules, int max = 40, bool truncated = false)
    {
        var sb = new StringBuilder();
        sb.Append("loaded modules (largest first): ").Append(modules.Count);
        // 数え切れていないことは必ず表に出す。伏せると「無い」と読まれる。
        if (truncated) sb.Append("  (INCOMPLETE: the module list could not be captured in full)");
        sb.Append('\n');
        var n = Math.Min(max, modules.Count);
        for (int i = 0; i < n; i++)
        {
            var m = modules[i];
            sb.Append("  ").Append(m.Name)
              .Append("  size=").Append(FormatSize(m.Size))
              .Append("  base=0x").Append(m.Base.ToString("x"));
            // 置き場所も事実として添える。どこから読み込まれたかは、
            // どれを対象にするか決めるときの手掛かりになる。
            if (!string.IsNullOrEmpty(m.Path)) sb.Append("  ").Append(m.Path);
            sb.Append('\n');
        }
        if (modules.Count > n) sb.Append("  ... and ").Append(modules.Count - n).Append(" more\n");
        return sb.ToString();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("F2") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        return bytes + " B";
    }
}
