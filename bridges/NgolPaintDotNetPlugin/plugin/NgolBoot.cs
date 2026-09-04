using System.Diagnostics;
using System.Runtime.CompilerServices;
using NodeGraphModLab;
using NodeGraphModLab.HostLogging;

namespace NgolForPaintDotNet;

/// <summary>
/// NGOL の型に触れるのはここだけ。
///
/// 呼ばれるのは <see cref="NgolHost"/> がアセンブリを読み終えたあと。それより前に
/// このクラスへ触れると、まだ読めない型を解決しようとして落ちる。
///
/// public にしてあるのは、ノードから <see cref="ServerPort"/> を引くため。
/// ノードは実行時にコンパイルされ、このアセンブリへの参照を持てないので反射で辿る。
/// 型と名前が、このプラグインとノードの間の約束になる。
/// </summary>
public static class NgolBoot
{
    private static NgolRuntime? s_runtime;

    /// <summary>実際に待ち受けているポート。まだ起きていなければ 0。</summary>
    public static int ServerPort => s_runtime?.ServerPort ?? 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Start(string ngolRoot)
    {
        if (s_runtime != null) return;

        var logger = new ConsoleFileNgolLogger(Path.Combine(ngolRoot, "host.log"));

        string hostName;
        try { hostName = Process.GetCurrentProcess().ProcessName; }
        catch { hostName = "Unknown"; }

        s_runtime = new NgolRuntime(logger, new NgolRuntimeOptions
        {
            // NGOL 自身が専用スレッドで更新を回す。ホストの型に触るノードは、
            // そのノードの側でホストのスレッドへ渡す（nodes/ の実装を参照）。
            EnableDirectMode = true,
            PluginVersion = "1.0.0",
            GameName = hostName,
        });
        s_runtime.Initialize(ngolRoot);
    }
}
