using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 式をホストのスクリプト実行環境で評価し、結果を受け取る。
///
/// 実行環境は自分のスレッドでしか触れないため、こちらからは直接呼べない。
/// 代わりに積んでおき、スクリプトが取りに来たときに、そのスレッドで実行される。
///
/// つまり評価が進むのは、Lua プロキシを適用したオブジェクトが
/// 描画されている間だけ。描画が止まっていれば、いつまでも結果は返らない。
/// </summary>
[NodeType("aviutl.lua.eval", "AviUtl2", "Lua Eval",
    Version = "1.0.0",
    Description = "Evaluates an expression inside the host's script runtime and returns the result. The runtime can only be touched from its own thread, so the request is queued and picked up by a Lua proxy script running on that thread. Nothing is evaluated while that script is not being drawn: timedOut then tells you the request was never picked up, which is a different situation from an expression that failed. Read done and timedOut before using result.")]
[NodePort("code", PortDirection.Input, "string", Description = "Expression or statements to evaluate. Return a value to get it back as text")]
[NodePort("poll_id", PortDirection.Input, "number", Description = "Read the answer to a request that was queued earlier instead of queueing a new one. Useful when the first attempt timed out because nothing was being drawn: the request stays queued, so it can be collected once drawing resumes")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to wait for the Lua proxy script to pick the request up and answer (default 3000)")]
[NodePort("done", PortDirection.Output, "boolean", Description = "true when an answer came back")]
[NodePort("timed_out", PortDirection.Output, "boolean", Description = "true when nothing picked the request up in time. Usually means no object carrying the Lua proxy script is being drawn")]
[NodePort("result", PortDirection.Output, "string", Description = "The value the script returned, as text. Starts with 'error:' when the expression itself failed")]
[NodePort("request_id", PortDirection.Output, "number", Description = "The number the request was queued under")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the wait took")]
public sealed class AviUtl2LuaEvalNode : INode
{
    // disasm-verified: Ngol_LuaEval RVA 0x5c30 / 引数1個（rcx=64bit ポインタ）/ 戻り値 rax の 64bit
    [DllImport("NgolForAviUtl2.aux2")]
    private static extern ulong Ngol_LuaEval(byte[] codeUtf8);

    // disasm-verified: Ngol_LuaPollResult RVA 0x5e20 / 引数3個（rcx=64bit / rdx=64bit ポインタ /
    // r8d=32bit、[rsp+X] からの引数読み取りは無い）/ 戻り値は al の 8bit
    [DllImport("NgolForAviUtl2.aux2")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Ngol_LuaPollResult(ulong id, byte[] outUtf8, int outLen);

    public void Execute(IExecutionContext ctx)
    {
        var code = (ctx.GetPortValue("code") as string ?? "").Trim();
        int timeout = ctx.GetPortValue("timeout_ms") is double td ? (int)td : 3000;
        if (timeout < 1) timeout = 1;

        ulong existing = ctx.GetPortValue("poll_id") is double pd && pd > 0 ? (ulong)pd : 0;

        if (existing == 0 && code.Length == 0)
        {
            Fail(ctx, 0, 0, "code is required unless poll_id is given");
            return;
        }

        ulong id = existing;
        if (existing == 0)
        {
            try
            {
                id = Ngol_LuaEval(Encoding.UTF8.GetBytes(code + "\0"));
            }
            catch (Exception ex)
            {
                Fail(ctx, 0, 0, ex.GetType().Name + ": " + ex.Message);
                return;
            }
            if (id == 0)
            {
                Fail(ctx, 0, 0, "the plugin refused the request");
                return;
            }
        }

        var buffer = new byte[64 * 1024];
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeout)
        {
            if (Ngol_LuaPollResult(id, buffer, buffer.Length))
            {
                int end = Array.IndexOf(buffer, (byte)0);
                if (end < 0) end = buffer.Length;
                ctx.SetPortValue("done", true);
                ctx.SetPortValue("timed_out", false);
                ctx.SetPortValue("result", Encoding.UTF8.GetString(buffer, 0, end));
                ctx.SetPortValue("request_id", (double)id);
                ctx.SetPortValue("elapsed_ms", (double)(Environment.TickCount64 - started));
                return;
            }
            Thread.Sleep(10);
        }

        ctx.SetPortValue("done", false);
        ctx.SetPortValue("timed_out", true);
        ctx.SetPortValue("result", "");
        ctx.SetPortValue("request_id", (double)id);
        ctx.SetPortValue("elapsed_ms", (double)(Environment.TickCount64 - started));
    }

    static void Fail(IExecutionContext ctx, ulong id, long elapsed, string message)
    {
        ctx.SetPortValue("done", false);
        ctx.SetPortValue("timed_out", false);
        ctx.SetPortValue("result", message);
        ctx.SetPortValue("request_id", (double)id);
        ctx.SetPortValue("elapsed_ms", (double)elapsed);
    }
}
