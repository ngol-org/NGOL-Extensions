using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodeGraphModLab;
using NodeGraphModLab.HostLogging;

namespace NgolActivator;

/// <summary>
/// C++ 側（hostfxr の load_assembly_and_get_function_pointer）から直接呼ばれる
/// マネージドエントリポイント。[UnmanagedCallersOnly] により UNMANAGEDCALLERSONLY_METHOD 経由で
/// デリゲート型名なしに関数ポインタとして取得できる。
///
/// NGOL Core / NodeAPI / HostLogging は ngol-resources/ から動的ロードする（コンパイル時参照は
/// Private=false で型解決のみ）。型に触れないこのメソッドが完了するまで他コードがその型を
/// 解決しないことを NoInlining で保証する。
/// </summary>
public static class EntryPoint
{
    private static NgolRuntime? s_runtime;

    [UnmanagedCallersOnly]
    public static int Init(IntPtr ngolRootUtf16)
    {
        try
        {
            string ngolRoot = Marshal.PtrToStringUni(ngolRootUtf16) ?? string.Empty;
            LoadNgolAssemblies(ngolRoot);
            return InitializeRuntime(ngolRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NgolActivator] Init failed: {ex}");
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Tick()
    {
        try { s_runtime?.Tick(); }
        catch (Exception ex) { Console.Error.WriteLine($"[NgolActivator] Tick error: {ex}"); }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try { s_runtime?.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"[NgolActivator] Shutdown error: {ex}"); }
        s_runtime = null;
    }

    // 型に触れないうちに実体を読み込む。NgolRuntime に触れるコードより先に必ず走らせる。
    // AssemblyLoadContext.Default.Resolving ハンドラは入れない。この経路は LoadFrom だけで
    //   解決でき、名前で別実体を返すハンドラを足すと型の同一性が壊れる。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadNgolAssemblies(string ngolRoot)
    {
        string[] names = { "NodeGraphModLab.NodeAPI.dll", "NodeGraphModLab.Core.dll", "NodeGraphModLab.HostLogging.dll" };
        foreach (var name in names)
        {
            var path = Path.Combine(ngolRoot, name);
            if (File.Exists(path)) Assembly.LoadFrom(path);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InitializeRuntime(string ngolRoot)
    {
        var logger = new ConsoleFileNgolLogger(Path.Combine(ngolRoot, "host.log"));
        var options = new NgolRuntimeOptions
        {
            // Tick() 駆動で回す。ホストのレンダーループが毎フレーム Tick を呼ぶ。
            EnableDirectMode = false,
            GameName = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
        };
        s_runtime = new NgolRuntime(logger, options);
        s_runtime.Initialize(ngolRoot);
        Console.WriteLine($"[NgolActivator] initialized (ngolRoot={ngolRoot})");
        return 0;
    }
}
