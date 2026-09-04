using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// もう 1 つの NGOL へ繋いで、要求を 1 件出して答えを受け取る。
///
/// 相手が何のアプリかは問わない。NGOL が載っていれば同じ口が開いている。
/// 繋いだ直後に相手が名乗るので、その名乗りを持ち帰る--ポート番号は
/// 使い回されるため、番号だけでは繋ぎ先を取り違えたことに気づけない。
///
/// 自分自身へは繋がない。同じ待ち行列を自分で待つ形になり、時間切れまで止まる。
/// </summary>
internal sealed class NgolLinkClient : IDisposable
{
    // 待ち受け側は WebSocket への切り替えであれば経路を見ていない。
    //   client=mcp は「画面を開いている側ではない」の申告で、これが無いと
    //   相手はキャンバスの押し付け先としてこちらを数える。
    private const string QueryMarker = "?client=mcp";

    private readonly ClientWebSocket _ws = new ClientWebSocket();

    internal string PeerName { get; private set; } = "";
    internal int PeerProcessId { get; private set; }
    internal int PeerPort { get; private set; }
    internal string PeerPluginDir { get; private set; } = "";

    /// <summary>繋いで名乗りを受け取る。繋がらなければ理由を文で返す。</summary>
    internal string Connect(string host, int port, string token, int timeoutMs)
    {
        if (!string.IsNullOrEmpty(token)) _ws.Options.AddSubProtocol(token);

        var deadline = new CancellationTokenSource(timeoutMs);
        try
        {
            var uri = new Uri("ws://" + host + ":" + port + "/" + QueryMarker);
            _ws.ConnectAsync(uri, deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return "could not reach a NGOL on " + host + ":" + port
                 + " (" + Innermost(ex).Message + ")";
        }

        // 名乗りは繋いだ直後に向こうから来る。来る前に要求を出しても順番は狂わないが、
        //   取り違えの検出は名乗りに頼っているので、先に受け取っておく。
        var welcome = Receive("welcome", timeoutMs, out string error);
        if (welcome == null) return error;

        using (var doc = JsonDocument.Parse(welcome))
        {
            var root = doc.RootElement;
            PeerName = Text(root, "gameName");
            PeerPluginDir = Text(root, "pluginDir");
            PeerProcessId = Number(root, "processId");
            PeerPort = Number(root, "port");
        }
        return null;
    }

    /// <summary>要求を出して、指定の型の答えが来るまで受け取る。</summary>
    internal string Request(string requestJson, string expectType, int timeoutMs, out string error)
    {
        error = null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(requestJson);
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                   .GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            error = "the request could not be sent (" + Innermost(ex).Message + ")";
            return null;
        }
        return Receive(expectType, timeoutMs, out error);
    }

    /// <summary>
    /// 求めている型が来るまで読み続ける。
    ///
    /// 待ち受け側は求めていないものも流してくる（記録・状態の変化など）ので、
    /// 最初の 1 通を答えだと決めつけない。
    /// </summary>
    private string Receive(string expectType, int timeoutMs, out string error)
    {
        error = null;
        var buffer = new byte[64 * 1024];
        var started = DateTime.UtcNow;

        while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
        {
            var text = new StringBuilder();
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    WebSocketReceiveResult got;
                    do
                    {
                        got = _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)
                                 .GetAwaiter().GetResult();
                        if (got.MessageType == WebSocketMessageType.Close)
                        {
                            error = "the other side closed the connection while waiting for "
                                  + expectType;
                            return null;
                        }
                        text.Append(Encoding.UTF8.GetString(buffer, 0, got.Count));
                    }
                    while (!got.EndOfMessage);
                }
            }
            catch (Exception ex)
            {
                error = "nothing came back within " + timeoutMs + "ms (" + Innermost(ex).Message + ")";
                return null;
            }

            var body = text.ToString();
            string type;
            try
            {
                using (var doc = JsonDocument.Parse(body)) type = Text(doc.RootElement, "type");
            }
            catch { continue; }

            if (type == expectType) return body;
            // error は型を問わず答えの代わりに来る。読み飛ばすと時間切れまで待つことになる。
            if (type == "error")
            {
                error = "the other side refused: " + Excerpt(body);
                return null;
            }
        }

        error = "no " + expectType + " arrived within " + timeoutMs + "ms";
        return null;
    }

    internal static string Text(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    internal static int Number(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>JSON の中の文字列として置ける形にする。</summary>
    internal static string Quote(string raw)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in raw ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string Excerpt(string body)
        => body.Length <= 300 ? body : body.Substring(0, 300) + " ...";

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    public void Dispose()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using (var cts = new CancellationTokenSource(1000))
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token)
                       .GetAwaiter().GetResult();
            }
        }
        catch { }
        try { _ws.Dispose(); } catch { }
    }
}
