using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// pythonnet（Python.NET）で、**C# から直接 CPython／bpy を触れるか**を確かめる試作。
///
/// 狙い: いまの方式は「Python の文面をファイル経由で渡す」ので
///    1 往復あたり約 200ms かかり、書くものも C# の中の文字列になる。
///    pythonnet は CPython への**束縛**なので、うまくいけば
///    **C# の式のまま、プロセス内で直接** `bpy` を触れる（往復コストが消える）。
///
/// これは「Python を通らなくなる」話ではない。pythonnet は CPython そのもの。
///     変わるのは **書く言語と往復コスト**であって、実行経路ではない。
///
/// そして **メインスレッド制約は解決しない。**
///     `Py.GIL()` が与えるのは GIL であってメインスレッドではない。
///     => 段階を分けて、**危ないことに触れない順**で確かめる。
///
///     stage 1  参照が通り、アセンブリが読めるか（CPython に一切触れない）
///     stage 2  すでに動いている CPython に後乗りできるか（sys.version を読むだけ）
///     stage 3  bpy を import して**読み取りだけ**（bpy.app.version_string）
///
/// stage を上げるほど Blender を落とす危険が上がる。1 つずつ確かめること。
/// </summary>
[NodeType("blender.pynet.probe", "Blender", "PythonNet Probe",
    Version = "1.0.0",
    Description = "PROTOTYPE. Reach Blender's already-running CPython from C# through pythonnet, in stages, so a failure can be told apart from a crash. MEASURED: stage 2 and 3 both succeed and return real values - bpy.app.version_string comes back as '5.2.0 LTS' - but Blender's window stops responding immediately afterwards and never recovers. The initialize call happens on NGOL's own thread, and the main thread can no longer take the interpreter lock. Stage 1 is safe. Stage 2 and above cost the session, so only run them on a Blender you are willing to restart.")]
[NodePort("stage", PortDirection.Input, "number", Description = "1 = load the assembly only, safe. 2 = attach to CPython and read sys.version. 3 = import bpy and read one attribute. Default 1. WARNING: 2 and above freeze Blender's UI after returning when run on this node's own thread; the value is real but the session is spent")]
[NodePort("via_main_thread", PortDirection.Input, "boolean", Description = "Hand the work to the host's main thread instead of doing it here, using the queue that blender.tick.probe drains from its hook. The attach is what breaks the interpreter when it happens on the wrong thread, so this is the way to find out whether doing it on the right one avoids that. Needs blender.tick.probe installed and the host redrawing; the call returns straight away and the outcome shows up on a later run")]
[NodePort("queued", PortDirection.Output, "boolean", Description = "true when the work was handed to the main-thread queue rather than run here")]
[NodePort("main_thread_report", PortDirection.Output, "string", Description = "What the work reported once the main thread ran it. Empty until it has")]
[NodePort("python_dll", PortDirection.Input, "string", Description = "Which CPython to bind to. Leave it empty and the node asks the host which one it carries, which is what you want: Blender 5.0 and older run Python 3.11, 5.1 and newer run 3.13, so a fixed name is wrong on one side or the other. Naming one here skips the question")]
[NodePort("ok", PortDirection.Output, "boolean", Description = "true when the requested stage completed")]
[NodePort("reached_stage", PortDirection.Output, "number", Description = "The highest stage that completed. Compare it with what was asked for")]
[NodePort("assembly", PortDirection.Output, "string", Description = "Which Python.Runtime was loaded, and from where")]
[NodePort("python_version", PortDirection.Output, "string", Description = "What sys.version reported, from stage 2")]
[NodePort("bpy_version", PortDirection.Output, "string", Description = "What bpy.app.version_string reported, from stage 3")]
[NodePort("result", PortDirection.Output, "string", Description = "Step by step account, including the exact failure when one happens")]
public sealed class BlenderPyNetProbeNode : INode
{
    // NGOL が extra-libs を見に行くのは **起動時**（NgolRuntime の初期化）。
    //    あとからフォルダを作っても、そのセッションでは resolver が知らない。
    //    => ここで明示的に読み込んでおけば、既定の ALC に載るので解決できる。
    //      NgolActivator が NGOL 本体に対してやっているのと同じ手。
    private static Assembly _pythonRuntime;

    // 探す順は「同梱 -> NGOL の規約」。同梱を先に見るのは、配布物 1 つで完結させるため。
    // 規約側（ngolRoot の 2 階層上）はパッケージの外なので、導入場所を変えると解決先が変わる。
    private static string[] PythonRuntimeCandidates()
    {
        string ngolRoot = Path.GetDirectoryName(typeof(INode).Assembly.Location) ?? "";
        return new[]
        {
            Path.GetFullPath(Path.Combine(
                ngolRoot, "Nodes", "CustomNodes", "cs", "blender", "lib", "Python.Runtime.dll")),
            Path.GetFullPath(Path.Combine(
                ngolRoot, "..", "..", "extra-libs", "Python.Runtime.dll")),
        };
    }

    // blender.tick.probe が **ホストのメインスレッドで** 吸い出すキューを共有する。
    //    AppDomain の入れ物なので、ノードが別ファイルでもホットリロードされても同じものを指す。
    private static T Keep<T>(string key, Func<T> factory) where T : class
    {
        var v = AppDomain.CurrentDomain.GetData(key) as T;
        if (v == null) { v = factory(); AppDomain.CurrentDomain.SetData(key, v); }
        return v;
    }

    private static System.Collections.Concurrent.ConcurrentQueue<Action> MainThreadQueue
        => Keep("NgolTickProbe_queue",
                () => new System.Collections.Concurrent.ConcurrentQueue<Action>());

    /// <summary>メインスレッド側が書き、こちらが読む 1 枠。</summary>
    private static string[] ReportSlot => Keep("NgolPyNet_report", () => new string[1]);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    /// <summary>
    /// 結び付ける CPython を、このプロセスに既に載っているものから決める。
    ///
    /// ホストの版で名前が変わる（Blender 5.0 以下は python311、5.1 以降は python313）ため、
    /// 名前を固定すると必ずどちらかの側で外れる。載っているものを見れば版を知らずに済む。
    ///
    /// GetModuleHandleW は読み込みを起こさない--既に載っているものの位置を返すだけなので、
    /// 当たらなかった候補が副作用を持つことはない。
    ///
    /// 見つからないときに既定の名前を返さない。載っていないということは結び付ける先が
    ///   無いということで、どの名前を返しても当たらない。名前を返すと呼ぶ側が
    ///   「決まった」と読んで先へ進み、失敗する場所が後ろへずれて原因が見えなくなる。
    /// </summary>
    /// <returns>載っている CPython の名前。載っていなければ null。</returns>
    private static string ResolveLoadedPythonDll()
    {
        for (int minor = 20; minor >= 7; minor--)
        {
            string name = "python3" + minor.ToString() + ".dll";
            if (GetModuleHandleW(name) != IntPtr.Zero) { return name; }
        }
        return null;
    }

    public void Execute(IExecutionContext ctx)
    {
        int stage = (int)ToDouble(ctx.GetPortValue("stage"), 1);
        string pythonDll = ToText(ctx.GetPortValue("python_dll"), "");
        bool viaMainThread = ctx.GetPortValue("via_main_thread") is bool v && v;

        var report = new StringBuilder();
        bool namedByCaller = !string.IsNullOrEmpty(pythonDll);
        if (!namedByCaller)
        {
            pythonDll = ResolveLoadedPythonDll();
            report.Append("PythonDLL  : ")
                  .Append(pythonDll ?? "not found")
                  .Append(pythonDll != null ? " (chosen from what's loaded in the process)\n"
                                            : " -- no CPython loaded in this process\n");
        }

        int reached = 0;
        ctx.SetPortValue("ok", false);
        ctx.SetPortValue("queued", false);

        // 前回メインスレッドが書き残した結果があれば、まず出す。
        string previous = ReportSlot[0];
        ctx.SetPortValue("main_thread_report", previous ?? "");

        try
        {
            // ---- stage 1: アセンブリを載せる（CPython には触れない） -------------------
            string dllPath = null;
            foreach (var candidate in PythonRuntimeCandidates())
            {
                report.Append("looked at  : ").Append(candidate).Append('\n');
                if (File.Exists(candidate)) { dllPath = candidate; break; }
            }
            if (dllPath == null)
            {
                report.Append("Python.Runtime.dll not found. Place it at one of the paths above\n");
                ctx.SetPortValue("result", report.ToString());
                return;
            }

            _pythonRuntime ??= Assembly.LoadFrom(dllPath);
            ctx.SetPortValue("assembly", _pythonRuntime.FullName + "  <- " + _pythonRuntime.Location);
            report.Append("stage 1 OK : ").Append(_pythonRuntime.FullName).Append('\n');
            reached = 1;

            // stage 1 はアセンブリを載せるだけなので CPython が無くても通る。
            // stage 2 以上は結び付ける先が要る。無いまま進むと、pythonnet の中で
            //   「読み込めない」形で落ちるので、どの名前が無かったのかが出てこない。
            if (stage >= 2 && string.IsNullOrEmpty(pythonDll))
            {
                report.Append("stage ").Append(stage)
                      .Append(" unreachable: no CPython loaded in this process to attach to.\n")
                      .Append("   This node assumes it runs inside Blender (which loads CPython at startup).\n")
                      .Append("   On another host, pass a name via python_dll.\n");
                ctx.SetPortValue("reached_stage", (double)reached);
                ctx.SetPortValue("result", report.ToString());
                return;   // ok は false のまま
            }

            if (viaMainThread && stage >= 2)
            {
                // ここでは何もせず、**ホストのメインスレッドに任せる**。
                //    実際に走るのは blender.tick.probe のフックが次に発火したとき。
                //      ホストが再描画しなければ走らない--動かないときはまずそれを疑う。
                int wanted = stage;
                string dll = pythonDll;
                ReportSlot[0] = "(waiting on main thread)";
                MainThreadQueue.Enqueue(() =>
                {
                    var log = new StringBuilder();
                    log.Append("ran on thread ").Append(Environment.CurrentManagedThreadId)
                       .Append(" (managed id)\n");
                    log.Append(Attach(dll, out string pyVersion));
                    if (pyVersion != null) log.Append("sys.version : ").Append(pyVersion).Append('\n');
                    if (wanted >= 3 && pyVersion != null)
                    {
                        log.Append(ReadBpy(out string bpyVersion));
                        if (bpyVersion != null)
                            log.Append("bpy.app.version_string : ").Append(bpyVersion).Append('\n');
                    }
                    ReportSlot[0] = log.ToString();
                });
                report.Append("stage ").Append(stage)
                      .Append(" handed to the main-thread queue.\n")
                      .Append("   Runs the next time blender.tick.probe's hook fires.\n")
                      .Append("   Does not run unless the host redraws. Run this node again to read the result.\n");
                ctx.SetPortValue("queued", true);
                ctx.SetPortValue("reached_stage", (double)reached);
                ctx.SetPortValue("ok", true);
                ctx.SetPortValue("result", report.ToString());
                return;
            }

            if (stage >= 2)
            {
                report.Append(Attach(pythonDll, out string pyVersion));
                if (pyVersion != null)
                {
                    ctx.SetPortValue("python_version", pyVersion);
                    reached = 2;
                }
            }

            if (stage >= 3 && reached >= 2)
            {
                report.Append(ReadBpy(out string bpyVersion));
                if (bpyVersion != null)
                {
                    ctx.SetPortValue("bpy_version", bpyVersion);
                    reached = 3;
                }
            }
        }
        catch (Exception ex)
        {
            // 例外を握って返す。ノードが落ちると理由が残らない。
            report.Append("EXCEPTION: ").Append(ex.GetType().Name).Append(": ")
                  .Append(ex.Message).Append('\n');
            if (ex.InnerException != null)
                report.Append("  inner: ").Append(ex.InnerException.Message).Append('\n');
        }

        ctx.SetPortValue("reached_stage", (double)reached);
        ctx.SetPortValue("ok", reached >= stage);
        ctx.SetPortValue("result", report.ToString());
    }

    // Python.Runtime の型に触れるのは **別メソッド**にして NoInlining を付ける。
    //    同じメソッドの中に書くと、そのメソッドへ入る時点で型解決が走り、
    //    Assembly.LoadFrom より先に FileNotFoundException になる。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Attach(string pythonDll, out string pyVersion)
    {
        pyVersion = null;
        var log = new StringBuilder();
        try
        {
            // Blender の CPython は **既に初期化済み**。pythonnet は本来
            //    「自分で Py_Initialize する」前提なので、後乗りが成立するかがこの段の要点。
            //
            // `Runtime.PythonDLL` は **初期化前にしか設定できない**
            //     （2 回目の実行で `InvalidOperationException:
            //      This property must be set before runtime is initialized` を踏んだ）。
            //     => 初期化済みなら触らない。ノードは何度も走るので、
            //       「1 回目だけ通る書き方」にしておかないと 2 回目から壊れる。
            if (!Python.Runtime.PythonEngine.IsInitialized)
            {
                Python.Runtime.Runtime.PythonDLL = pythonDll;
                log.Append("PythonDLL  : ").Append(pythonDll).Append('\n');
                Python.Runtime.PythonEngine.Initialize();
                log.Append("Initialize : called\n");
            }
            else
            {
                log.Append("Initialize : already initialized (left alone)\n");
            }

            using (Python.Runtime.Py.GIL())
            {
                var sys = Python.Runtime.Py.Import("sys");
                pyVersion = sys.GetAttr("version").ToString();
            }
            log.Append("stage 2 OK : read sys.version\n");
        }
        catch (Exception ex)
        {
            log.Append("stage 2 FAILED: ").Append(ex.GetType().Name).Append(": ")
               .Append(ex.Message).Append('\n');
        }
        return log.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadBpy(out string bpyVersion)
    {
        bpyVersion = null;
        var log = new StringBuilder();
        try
        {
            using (Python.Runtime.Py.GIL())
            {
                // bpy は Blender が既に import 済みなので、これは sys.modules から取るだけ。
                //    読むのも定数の文字列 1 つに留める--書き込みはメインスレッドが要る。
                var bpy = Python.Runtime.Py.Import("bpy");
                bpyVersion = bpy.GetAttr("app").GetAttr("version_string").ToString();
            }
            log.Append("stage 3 OK : read bpy.app.version_string from C#\n");
        }
        catch (Exception ex)
        {
            log.Append("stage 3 FAILED: ").Append(ex.GetType().Name).Append(": ")
               .Append(ex.Message).Append('\n');
        }
        return log.ToString();
    }

    private static double ToDouble(object v, double fallback)
    {
        if (v == null) return fallback;
        if (v is double d) return d;
        if (v is int i) return i;
        return double.TryParse(v.ToString(), out var p) ? p : fallback;
    }

    private static string ToText(object v, string fallback)
    {
        if (v == null) return fallback;
        var s = v as string ?? v.ToString();
        return s.Length == 0 ? fallback : s;
    }
}
