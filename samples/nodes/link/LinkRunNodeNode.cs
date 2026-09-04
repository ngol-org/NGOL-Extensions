using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// もう 1 つの NGOL でノードを 1 つ実行して、その出力を持ち帰る。
///
/// 2 つのアプリを跨いで仕事を組み立てるための最小の 1 手。これが無いと、
/// 繋いでいるのは人か AI であって、グラフだけでは片方からもう片方へ何も頼めない。
///
/// 相手が何のアプリかは問わない。渡すのは相手のノードの名前と入力だけで、
/// こちらは相手の作りを何も知らなくてよい。
///
/// 相手の名乗りを必ず返す。ポート番号は使い回されるので、番号が合っていることは
/// 繋ぎ先が合っていることの証明にならない。
/// </summary>
[NodeType("ngol.link.run_node", "Link", "Run Node On Another NGOL",
    Version = "1.1.0",
    Description = "Runs one node on another NGOL and brings its outputs back. This is the smallest step for building work that spans two applications: without it a graph cannot ask anything of the other side, and the only thing joining them is a person or an agent. It does not care what the other application is - all that is handed over is the name of a node and its inputs. The other side's own name always comes back, because port numbers get reused and a matching number is no proof of a matching target.")]
[NodePort("port", PortDirection.Input, "number", Description = "The port the other NGOL listens on. Default 11156. Read it from ngol.link.probe rather than writing it down, because a server whose port was taken moves to the next free one")]
[NodePort("host", PortDirection.Input, "string", Description = "Default 127.0.0.1. Leave it unless there is a reason to reach off the machine")]
[NodePort("node_type_id", PortDirection.Input, "string", IsRequired = true, Description = "Which node to run over there, by its exact id")]
[NodePort("inputs_json", PortDirection.Input, "string", Description = "What to hand that node, as a JSON object. Empty means no inputs. Write {1} {2} {3} where a value from an earlier node should go, and feed those on arg1..arg3")]
[NodePort("arg1", PortDirection.Input, "string", Description = "Goes where inputs_json says {1}. This is how a path or a name produced upstream reaches the other side. Put it in as written - use forward slashes in paths, because a backslash means something else inside JSON")]
[NodePort("arg2", PortDirection.Input, "string", Description = "Goes where inputs_json says {2}")]
[NodePort("arg3", PortDirection.Input, "string", Description = "Goes where inputs_json says {3}")]
[NodePort("pick", PortDirection.Input, "string", Description = "Name of one output port to take out on its own. Leave empty to get them all as JSON")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to wait for the answer. Default 30000")]
[NodePort("token", PortDirection.Input, "string", Description = "Only when the other side requires one")]
[NodePort("ok", PortDirection.Output, "boolean", Description = "true when the node over there ran and succeeded")]
[NodePort("peer", PortDirection.Output, "string", Description = "What the other side calls itself, with its process id. Check this before trusting the result")]
[NodePort("value", PortDirection.Output, "any", Description = "The single output named by pick")]
[NodePort("outputs_json", PortDirection.Output, "string", Description = "Every output port of the node that ran, as JSON")]
[NodePort("remote_ms", PortDirection.Output, "number", Description = "How long the node took on the other side")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the whole round trip took, connection included")]
[NodePort("error", PortDirection.Output, "string", Description = "Why nothing came back. Empty when something did")]
public sealed class LinkRunNodeNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        int port = ctx.GetPortValue("port") is double p ? (int)p : 11156;
        string host = (ctx.GetPortValue("host") as string ?? "").Trim();
        string nodeTypeId = (ctx.GetPortValue("node_type_id") as string ?? "").Trim();
        string inputsJson = (ctx.GetPortValue("inputs_json") as string ?? "").Trim();
        string pick = (ctx.GetPortValue("pick") as string ?? "").Trim();
        int timeout = ctx.GetPortValue("timeout_ms") is double t ? (int)t : 30000;
        string token = (ctx.GetPortValue("token") as string ?? "").Trim();
        if (host.Length == 0) host = "127.0.0.1";
        if (inputsJson.Length == 0) inputsJson = "{}";

        // 上流のノードが作った値を差し込む。入れ子になった JSON の中へ入ることもあるので、
        //   ここでは何も足さずそのまま置く。代わりに、置いた結果が JSON として
        //   成り立っているかを下で確かめる--壊れたまま送ると、相手は既定値で動いて成功を返す。
        for (int i = 1; i <= 3; i++)
        {
            string arg = ctx.GetPortValue("arg" + i) as string;
            if (arg != null) inputsJson = inputsJson.Replace("{" + i + "}", arg);
        }

        var watch = Stopwatch.StartNew();
        ctx.SetPortValue("ok", false);
        ctx.SetPortValue("peer", "");
        ctx.SetPortValue("outputs_json", "");
        ctx.SetPortValue("remote_ms", 0d);

        if (nodeTypeId.Length == 0) { Fail(ctx, watch, "give the id of a node to run over there"); return; }

        try { using (JsonDocument.Parse(inputsJson)) { } }
        catch (Exception ex)
        {
            // 送ってしまうと、相手は読めなかった入力を既定値で埋めて成功を返す。
            //   その成功はこちらの取り違えを隠すので、送る前に止める。
            Fail(ctx, watch, "inputs_json is not valid JSON after the arguments were put in ("
                           + ex.Message + "). It reads: " + inputsJson);
            return;
        }

        using (var link = new NgolLinkClient())
        {
            string why = link.Connect(host, port, token, timeout);
            if (why != null) { Fail(ctx, watch, why); return; }

            ctx.SetPortValue("peer", link.PeerName + " (pid " + link.PeerProcessId
                                   + ", port " + link.PeerPort + ")");

            // 自分自身へ頼むと、いま動いているこの実行の後ろへ並ぶことになり、
            //   自分が終わるのを自分で待つ形になる。時間切れまで動かない。
            if (link.PeerProcessId == Process.GetCurrentProcess().Id)
            {
                Fail(ctx, watch, "that port is this very process; asking it to run a node would "
                               + "queue the request behind this one and wait for itself");
                return;
            }

            string request = "{\"type\":\"execute_node\",\"nodeTypeId\":"
                           + NgolLinkClient.Quote(nodeTypeId)
                           + ",\"inputs\":" + inputsJson + "}";

            string body = link.Request(request, "execute_node_response", timeout, out string error);
            if (body == null) { Fail(ctx, watch, error); return; }

            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                bool success = root.TryGetProperty("success", out var s)
                            && s.ValueKind == JsonValueKind.True;

                if (root.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number)
                    ctx.SetPortValue("remote_ms", d.GetDouble());

                string missedPick = null;
                if (root.TryGetProperty("outputs", out var outs) && outs.ValueKind == JsonValueKind.Object)
                {
                    ctx.SetPortValue("outputs_json", outs.GetRawText());
                    if (pick.Length > 0 && outs.TryGetProperty(pick, out var one))
                    {
                        ctx.SetPortValue("value", ToValue(one));
                    }
                    else if (pick.Length > 0)
                    {
                        // 名前が違っていても null が返るだけだと、値が無かったのか
                        //   名前を間違えたのか区別できない。実際にあった名前を並べて言う。
                        ctx.SetPortValue("value", null);
                        missedPick = "there is no output named '" + pick + "' over there. It has: "
                                   + string.Join(", ", NamesOf(outs));
                    }
                }

                ctx.SetPortValue("ok", success && missedPick == null);
                ctx.SetPortValue("error", !success ? NgolLinkClient.Text(root, "errorMessage")
                                                   : (missedPick ?? ""));
            }
        }
        ctx.SetPortValue("elapsed_ms", (double)watch.ElapsedMilliseconds);
    }

    /// <summary>相手が返した出力ポートの名前を並べる。取り違えたときに何があったかを示すため。</summary>
    private static List<string> NamesOf(JsonElement outputs)
    {
        var names = new List<string>();
        foreach (var p in outputs.EnumerateObject()) names.Add(p.Name);
        return names;
    }

    /// <summary>JSON の値を、こちらのポートへ載る形へ移す。</summary>
    private static object ToValue(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.Number: return v.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            // 並びや入れ子は、こちらの側に対応する物が無い。文字のまま渡して
            //   受け取る側に決めさせる。
            default: return v.GetRawText();
        }
    }

    private static void Fail(IExecutionContext ctx, Stopwatch watch, string why)
    {
        ctx.SetPortValue("ok", false);
        ctx.SetPortValue("error", why);
        ctx.SetPortValue("elapsed_ms", (double)watch.ElapsedMilliseconds);
    }
}
