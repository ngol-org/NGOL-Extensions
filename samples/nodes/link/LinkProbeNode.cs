using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// この機械で待ち受けている NGOL を探して、どこに誰が居るかを返す。
///
/// ポート番号をグラフに書き固めないために要る。待ち受けようとした番号が
/// 埋まっていた場合は次の空きへ移るので、設定に書いた番号と実際は食い違いうる。
///
/// 名前で引けるようにしてあるのは、番号ではなく相手で指定するため。
/// 同じアプリを 2 つ起動していれば 2 件出るので、その場合は識別子まで見る。
/// </summary>
[NodeType("ngol.link.probe", "Link", "Find Other NGOLs",
    Version = "1.0.0",
    Description = "Finds the NGOL instances listening on this machine and reports which one is where. It exists so port numbers do not have to be written into graphs: a server whose configured port is taken moves to the next free one, so the configured value and the real one can differ. Looking one up by name is the point - name the target instead of the number. Two copies of the same application show up as two entries, and then the process id is what tells them apart.")]
[NodePort("start_port", PortDirection.Input, "number", Description = "First port to look at. Default 11156")]
[NodePort("count", PortDirection.Input, "number", Description = "How many consecutive ports to look at. Default 15")]
[NodePort("name_contains", PortDirection.Input, "string", Description = "Take out the one whose name contains this, case-insensitive. Leave empty to just list")]
[NodePort("timeout_ms", PortDirection.Input, "number", Description = "How long to give each port. Default 800")]
[NodePort("found", PortDirection.Output, "number", Description = "How many were listening")]
[NodePort("peers", PortDirection.Output, "string", Description = "One per line: port, name, process id, plugin folder")]
[NodePort("port", PortDirection.Output, "number", Description = "Port of the one matched by name_contains. 0 when nothing matched")]
[NodePort("matched", PortDirection.Output, "string", Description = "Name and process id of the matched one")]
[NodePort("ambiguous", PortDirection.Output, "boolean", Description = "true when name_contains matched more than one, in which case port is the first. Narrow the name rather than trusting it")]
[NodePort("elapsed_ms", PortDirection.Output, "number", Description = "How long the whole sweep took")]
public sealed class LinkProbeNode : INode
{
    /// <summary>1 つの待ち受けから受け取った名乗り。</summary>
    private sealed class Peer
    {
        public int Port;
        public string Name;
        public int ProcessId;
        public string PluginDir;
    }

    public void Execute(IExecutionContext ctx)
    {
        int start = ctx.GetPortValue("start_port") is double s ? (int)s : 11156;
        int count = ctx.GetPortValue("count") is double c ? (int)c : 15;
        string want = (ctx.GetPortValue("name_contains") as string ?? "").Trim();
        int timeout = ctx.GetPortValue("timeout_ms") is double t ? (int)t : 800;
        if (count < 1) count = 1;
        if (count > 100) count = 100;

        var watch = Stopwatch.StartNew();

        // 待っていない番号でも時間切れまで待たされる。順に当てると、居ない分だけ
        //   全体が伸びる（15 番地で実測 9.0 秒）。同時に当てれば 1 回分で済む。
        var probes = new Task<Peer>[count];
        for (int i = 0; i < count; i++)
        {
            int port = start + i;
            probes[i] = Task.Run(() =>
            {
                using (var link = new NgolLinkClient())
                {
                    if (link.Connect("127.0.0.1", port, "", timeout) != null) return null;
                    return new Peer
                    {
                        Port = link.PeerPort != 0 ? link.PeerPort : port,
                        Name = link.PeerName,
                        ProcessId = link.PeerProcessId,
                        PluginDir = link.PeerPluginDir,
                    };
                }
            });
        }
        Task.WaitAll(probes);

        var lines = new StringBuilder();
        var hits = new List<int>();
        var names = new List<string>();
        int found = 0;

        // 並べ直す。同時に当てた結果は終わった順なので、そのままだと番号が飛び飛びになる。
        foreach (var probe in probes)
        {
            var peer = probe.Result;
            if (peer == null) continue;

            found++;
            lines.Append(peer.Port).Append('\t').Append(peer.Name)
                 .Append("\tpid ").Append(peer.ProcessId)
                 .Append('\t').Append(peer.PluginDir).Append('\n');

            if (want.Length > 0 &&
                peer.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hits.Add(peer.Port);
                names.Add(peer.Name + " (pid " + peer.ProcessId + ")");
            }
        }

        ctx.SetPortValue("found", (double)found);
        ctx.SetPortValue("peers", lines.ToString().TrimEnd('\n'));
        ctx.SetPortValue("port", hits.Count > 0 ? (double)hits[0] : 0d);
        ctx.SetPortValue("matched", hits.Count > 0 ? names[0] : "");
        ctx.SetPortValue("ambiguous", hits.Count > 1);
        ctx.SetPortValue("elapsed_ms", (double)watch.ElapsedMilliseconds);
    }
}
