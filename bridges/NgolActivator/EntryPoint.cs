using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodeGraphModLab;
using NodeGraphModLab.HostLogging;

namespace NgolActivator;

/// <summary>
/// ネイティブ側から hostfxr の load_assembly_and_get_function_pointer 経由で直接呼ばれる
/// マネージドエントリポイント。[UnmanagedCallersOnly] により、デリゲート型名なしに
/// 関数ポインタとして取得できる。
///
/// 対象アプリごとに変わる値は焼き込まず、実行時に決めること。
/// </summary>
public static class EntryPoint
{
    private static NgolRuntime? s_runtime;

    [UnmanagedCallersOnly]
    public static int Init(IntPtr pluginDirUtf16)
    {
        string pluginDir = string.Empty;
        try
        {
            pluginDir = Marshal.PtrToStringUni(pluginDirUtf16) ?? string.Empty;
            LoadNgolAssemblies(pluginDir);
            return InitializeRuntime(pluginDir);
        }
        catch (Exception ex)
        {
            // ここへ来た時点で NGOL のログはまだ無く、コンソールは閉じれば消える。
            // 起動しなかった理由が何も残らないのを避けるため、隣にも書く。
            Console.Error.WriteLine($"[NgolActivator] Init failed: {ex}");
            TryWriteFailure(pluginDir, ex);
            return -1;
        }
    }

    private static void TryWriteFailure(string pluginDir, Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Directory.Exists(pluginDir) ? pluginDir : Path.GetTempPath(),
                "activator-error.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] Init failed: {ex}{Environment.NewLine}");
        }
        catch
        {
            // 書けないなら諦める。ここでの失敗で対象アプリを巻き込まない。
        }
    }

    /// <summary>
    /// 実際に待ち受けているポート。まだ起きていない場合も、設定したポートを取れなかった場合も 0。
    /// 設定値は読まない。設定と実際の待ち受け先は食い違うことがあり、
    /// 呼ぶ側が要るのは繋ぎに行ける先のほうであるため。
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetServerPort()
    {
        try { return s_runtime?.ServerPort ?? 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try { s_runtime?.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"[NgolActivator] Shutdown error: {ex}"); }
        s_runtime = null;
    }

    // NGOL 本体はコンパイル時参照を持たず、配置先から読み込む。
    // 置き場所はこのアセンブリと同じフォルダ（README の配置図）。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadNgolAssemblies(string pluginDir)
    {
        if (!File.Exists(Path.Combine(pluginDir, "NodeGraphModLab.Core.dll")))
        {
            throw new FileNotFoundException(
                "NodeGraphModLab.Core.dll not found in " + pluginDir);
        }

        string[] names =
        [
            "NodeGraphModLab.NodeAPI.dll",
            "NodeGraphModLab.Core.dll",
            "NodeGraphModLab.HostLogging.dll",
        ];
        foreach (var name in names)
        {
            var path = Path.Combine(pluginDir, name);
            if (File.Exists(path))
            {
                System.Reflection.Assembly.LoadFrom(path);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InitializeRuntime(string pluginDir)
    {
        var logger = new ConsoleFileNgolLogger(Path.Combine(pluginDir, "host.log"));

        string gameName;
        try
        {
            gameName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            gameName = "Unknown";
        }

        var options = new NgolRuntimeOptions
        {
            // NGOL 自身が専用スレッドで更新を回す（間隔は ngol-config.json）。
            // false にすると更新を回す主体がいなくなり、実行も永続ノードも止まる。
            EnableDirectMode = true,
            PluginVersion = "1.0.0",
            GameName = gameName,
        };
        s_runtime = new NgolRuntime(logger, options);
        s_runtime.Initialize(pluginDir);
        return 0;
    }
}
