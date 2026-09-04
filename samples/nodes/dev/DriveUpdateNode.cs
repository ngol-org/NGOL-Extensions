using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// NGOL の更新を、このノードから直接回す。
///
/// 効くのは、更新を呼んでいるのがホスト側のスレッドである構成に限る。そのスレッドが
/// 止まると更新を回す主体が居なくなるが、止まるのは「捌く側」だけで、待ち受けと
/// run_node は動いたままなので、ここから回せばグラフ実行・永続コールバック・
/// ホットリロードが戻る。
///
/// NGOL が自前のスレッドで更新を回している構成では、ホストが止まってもそのスレッドは
/// 回り続けるため、戻すものが無い。更新の呼び出し自体が中で詰まっている場合も戻せず、
/// 時間切れとして報告するだけになる。
///
/// 何も奪わず何も残さない。駆動元そのものを取り上げる ngol.dev.tick_source と違い、
/// 後始末が要らない。
///
/// 返らない呼び出しでノードごと止まらないよう、別スレッドで呼んで時間を測る。
/// </summary>
[NodeType("ngol.dev.drive_update", "Dev", "Drive Update",
    Version = "1.0.0",
    Description =
        "Turn NGOL's update loop a given number of times from this node. Use it when the thread that normally "
      + "turns the update has stopped: the listener and run_node keep working, while execute_graph, persistent "
      + "per-frame callbacks, MainThreadDispatch and hot reload all stall. Turning the update from here brings "
      + "those back, so tools can be written and loaded while the host is stuck. "
      + "This helps only where a thread belonging to the host calls the update, such as a game loop or a "
      + "per-frame callback. Where NGOL turns the update on a thread of its own, that thread keeps running when "
      + "the host stops, so there is nothing to bring back and this node is not needed. If the update call "
      + "itself is what blocks, it cannot be brought back either: completed then comes out below times, which "
      + "is the answer rather than a failure. "
      + "Nothing is taken over and nothing is left behind, unlike ngol.dev.tick_source which moves the driver "
      + "itself and has to be given back. Each turn runs on a thread of its own and is timed, so an update "
      + "that never returns is reported instead of hanging this node.")]
[NodePort("times", PortDirection.Input, "number", Description = "How many times to turn the update. Default 1. Tens of turns are enough to let a pending hot reload be taken in")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to wait for one turn before giving up on it. Default 3000. A turn that does not come back means the update itself is blocked")]
[NodePort("returned", PortDirection.Output, "boolean", Description = "true when every turn came back within the timeout")]
[NodePort("completed", PortDirection.Output, "number", Description = "How many turns came back. Fewer than times means the update is blocked, not slow")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "Total time the turns took")]
[NodePort("result", PortDirection.Output, "string", Description = "Outcome, including why the runtime could not be reached if it could not")]
public sealed class DriveUpdateNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var times = (int)(ctx.GetPortValue("times") is double t ? t : 1.0);
        var timeout = (int)(ctx.GetPortValue("timeout_ms") is double to ? to : 3000.0);
        if (times < 1) times = 1;
        if (timeout < 1) timeout = 1;

        ctx.SetPortValue("returned", false);
        ctx.SetPortValue("completed", 0.0);
        ctx.SetPortValue("elapsed_ms", 0.0);

        var found = NgolRuntimeFind.Find(ctx);
        if (found.Runtime == null)
        {
            ctx.SetPortValue("result", "the running NGOL could not be reached: " + found.Explain());
            return;
        }

        var method = found.Runtime.GetType().GetMethod(
            "Tick", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (method == null)
        {
            ctx.SetPortValue("result", "the runtime was found but has no public parameterless update method");
            return;
        }
        var turn = (Action)Delegate.CreateDelegate(typeof(Action), found.Runtime, method);

        var watch = Stopwatch.StartNew();
        var completed = 0;
        for (var i = 0; i < times; i++)
        {
            // 呼び出しが返らないことがあるので、待つのは時間を区切ってから。
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() => { try { turn(); } catch { } finally { done.Set(); } })
            {
                IsBackground = true,
                Name = "ngol-drive-update",
            };
            thread.Start();
            if (!done.Wait(timeout)) break;
            completed++;
        }
        watch.Stop();

        ctx.SetPortValue("returned", completed == times);
        ctx.SetPortValue("completed", (double)completed);
        ctx.SetPortValue("elapsed_ms", Math.Round(watch.Elapsed.TotalMilliseconds, 2));
        ctx.SetPortValue("result", completed == times
            ? "found the runtime after looking at " + found.Visited + " object(s); "
              + completed + " of " + times + " turn(s) came back in "
              + Math.Round(watch.Elapsed.TotalMilliseconds, 2) + " ms"
            : "turn " + (completed + 1) + " of " + times + " did not come back within "
              + timeout + " ms, so the update itself is blocked");
    }
}
