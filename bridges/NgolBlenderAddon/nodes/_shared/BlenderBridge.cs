using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// Blender のメインスレッドで Python を走らせて、答えを受け取る。
///
/// ノードは NGOL のスレッドで走る。<c>bpy</c> はメインスレッド専用なので、
///     ここから直接 Blender を触ってはいけない。文面を置いて、
///     アドオン側の汎用ポンプ（<c>bpy.app.timers</c> ＝ メインスレッド）に走らせてもらう。
///
/// **アドオンの仕事は「NGOL を載せること」と「メインスレッドに乗せること」まで。**
///    何をするかはこちら（ノード）が決める。<c>bpy</c> に触る処理は
///    <c>&lt;ngolRoot&gt;/Nodes/CustomNodes/py/ngol_blender.py</c> に置いてあるので、
///    ノードは「その関数を呼ぶ 2 行」を送るだけでよい。
///
/// **どちらに書くかを「処理の重さ」で決めないこと。**
///     向こう（Python）は **Blender のメインスレッド**で走るので、
///     長い処理を置くと **Blender が固まる**。こちら（C#）は NGOL 自前のスレッドなので、
///     **重い計算はむしろこちらに置くほうが速く、Blender も止めない。**
///     判断の軸は 3 つ:
///       1) <c>bpy</c> に触る必要があるか（Yes なら Python 一択。性能の話ではない）
///       2) 境界を何度も跨ぐか（1 往復 約 200ms。ループは片側に閉じる）
///       3) メインスレッドを長く占有するか（Yes ならこちらへ逃がすか、向こうで分割する）
///
/// 口の形は NGOL の OBS ブリッジ（<c>Ngol_Obs_Call</c>）に揃えてある--
/// 要求も答えも JSON、失敗は <c>ok=false</c> と <c>error</c>。
/// </summary>
internal static class BlenderBridge
{
    /// <summary>
    /// 受け口の場所。ngolRoot の下に固定。
    ///
    /// ngolRoot は「NodeAPI アセンブリが置かれている場所」で引く。
    ///    環境変数やカレントディレクトリに頼らない--どちらもホスト次第で動く。
    /// </summary>
    internal static string NgolRoot()
        => Path.GetDirectoryName(typeof(INode).Assembly.Location) ?? "";

    private static string BridgeRoot() => Path.Combine(NgolRoot(), "blender_bridge");

    // ---------------------------------------------------------------------------------

    /// <summary>Blender へ渡す引数。Python 側では <c>args</c> という dict で受け取る。</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, object> _fields = new();

        internal Args Set(string name, string value)
        {
            if (value != null) _fields[name] = value;
            return this;
        }

        internal Args Set(string name, bool value) { _fields[name] = value; return this; }
        internal Args Set(string name, double value) { _fields[name] = value; return this; }

        internal void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject("args");
            foreach (var pair in _fields)
            {
                switch (pair.Value)
                {
                    case string s: writer.WriteString(pair.Key, s); break;
                    case bool b: writer.WriteBoolean(pair.Key, b); break;
                    case double d: writer.WriteNumber(pair.Key, d); break;
                }
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// 答え。
    ///
    /// 失敗には 2 段あるので、どちらも <see cref="Ok"/> = false にまとめる。
    ///   1) ブリッジが通らない・Python が例外を投げた  -> 外側の ok=false
    ///   2) Python は通ったが処理が断った        -> result.ok=false
    /// 2)を成功として扱うと、「動いたのに何も起きない」を見逃す。
    /// </summary>
    internal sealed class Reply : IDisposable
    {
        private readonly JsonDocument _document;
        private readonly JsonElement _result;
        private readonly bool _hasResult;

        internal Reply(string raw)
        {
            Raw = raw ?? "";
            try
            {
                _document = JsonDocument.Parse(Raw);
                var root = _document.RootElement;

                bool outerOk = root.TryGetProperty("ok", out var ok)
                               && ok.ValueKind == JsonValueKind.True;
                Stdout = root.TryGetProperty("stdout", out var so) && so.ValueKind == JsonValueKind.String
                    ? so.GetString() : "";
                Milliseconds = root.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number
                    ? ms.GetDouble() : 0d;

                if (!outerOk)
                {
                    Ok = false;
                    Error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString() : "Blender declined without a reason";
                    return;
                }

                _hasResult = root.TryGetProperty("result", out _result)
                             && _result.ValueKind == JsonValueKind.Object;
                if (!_hasResult)
                {
                    // result を返さない Python もありうる。値が無いだけで失敗ではない。
                    Ok = true;
                    return;
                }

                bool innerOk = !_result.TryGetProperty("ok", out var iok)
                               || iok.ValueKind != JsonValueKind.False;
                Ok = innerOk;
                if (!innerOk)
                {
                    Error = _result.TryGetProperty("error", out var ie)
                            && ie.ValueKind == JsonValueKind.String
                        ? ie.GetString() : "The operation declined without a reason";
                }
            }
            catch (Exception ex)
            {
                Ok = false;
                Error = "The reply was not JSON: " + ex.Message;
            }
        }

        internal string Raw { get; }
        internal bool Ok { get; }
        internal string Error { get; } = "";
        internal string Stdout { get; } = "";
        internal double Milliseconds { get; }

        private bool Get(string name, out JsonElement value)
        {
            value = default;
            return _hasResult && _result.TryGetProperty(name, out value);
        }

        internal string Text(string name)
            => Get(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        internal double Number(string name)
            => Get(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0d;

        internal bool Bool(string name)
            => Get(name, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>そのまま読める形で返す。文字列でも数でも配列でも潰さない。</summary>
        internal string Any(string name)
        {
            if (!Get(name, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null => "",
                _ => v.ToString(),
            };
        }

        /// <summary>配列の要素を 1 行ずつ並べる。</summary>
        internal string Lines(string arrayName)
        {
            if (!Get(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) return "";
            var lines = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                lines.Add(entry.ValueKind == JsonValueKind.String
                    ? entry.GetString() : entry.ToString());
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 配列の中から 1 つの項目だけを取り出して並べる。
        /// 前後 2 回の実行で差分を取るための形（OBS ブリッジの <c>Column</c> と同じ）。
        /// </summary>
        internal string Column(string arrayName, string field)
        {
            if (!Get(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) return "";
            var lines = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty(field, out var v)) continue;
                // 真偽値は JSON と同じ綴りで並べる。C# の綴りに任せると、
                // 同じ答えの中で json ポートと綴りが食い違う。
                lines.Add(v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => v.ToString(),
                });
            }
            return string.Join("\n", lines);
        }

        /// <summary>result 全体。ポートに載せて中身を目で見るため。</summary>
        internal string ResultJson()
            => _hasResult
                ? JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true })
                : "";

        public void Dispose() => _document?.Dispose();
    }

    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Python を 1 件走らせる。
    /// 例外を投げない--ノードが落ちると情報が残らないので、失敗も答えとして返す。
    /// </summary>
    internal static Reply Run(string code, Args args = null,
                              double timeoutSeconds = 20.0, string label = null)
    {
        string root = BridgeRoot();
        string reqDir = Path.Combine(root, "req");
        string resDir = Path.Combine(root, "res");

        if (!Directory.Exists(reqDir) || !Directory.Exists(resDir))
        {
            return new Reply(Fail(
                "No inbox on the Blender side: " + root +
                "  -- run 'NGOL を起動' from the addon"));
        }

        string id = Guid.NewGuid().ToString("N");
        string reqPath = Path.Combine(reqDir, id + ".json");
        string resPath = Path.Combine(resDir, id + ".json");

        try
        {
            string payload = BuildPayload(code, args, label);
            // 相手が書き途中を読まないよう、別名で書いてから置き換える。
            string staging = reqPath + ".staging";
            File.WriteAllText(staging, payload, new UTF8Encoding(false));
            File.Move(staging, reqPath);
        }
        catch (Exception ex)
        {
            return new Reply(Fail("Could not write the request: " + ex.Message));
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resPath))
            {
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        string raw = File.ReadAllText(resPath, Encoding.UTF8);
                        TryDelete(resPath);
                        return new Reply(raw);
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(5);   // 置き換えの瞬間に当たった。読み直す
                    }
                }
                TryDelete(resPath);
                return new Reply(Fail("Could not read the reply: " + resPath));
            }
            Thread.Sleep(5);
        }

        // 置きっぱなしにしない。次の実行で古い要求が処理されると筋が通らなくなる。
        TryDelete(reqPath);
        return new Reply(Fail(
            "Blender did not answer within " + timeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
            " seconds. Blender's timer does not run while it is in a modal operation" +
            " (a menu is open, a drag is in progress, or it is rendering). " +
            "Check the screen."));
    }

    /// <summary>
    /// 土台 <c>ngol_blender.py</c> の関数を 1 つ呼ぶ。
    /// ノード側は「どの関数を、どの引数で」だけを決めればよい。
    /// </summary>
    internal static Reply CallPy(string functionName, Args args = null,
                                 double timeoutSeconds = 20.0)
    {
        string code =
            "import ngol_blender as nb\n" +
            "result = nb." + functionName + "(**args)\n";
        return Run(code, args, timeoutSeconds, functionName);
    }

    private static string BuildPayload(string code, Args args, string label)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            if (label != null) writer.WriteString("label", label);
            if (args != null) args.WriteTo(writer);
            else { writer.WriteStartObject("args"); writer.WriteEndObject(); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Fail(string message)
        => "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(message) + "}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 消せないなら諦める */ }
    }

    // ---- ポート値の読み取り。数は double で来ることが多い ----------------------------

    internal static double ToDouble(object value, double fallback = 0d)
    {
        switch (value)
        {
            case null: return fallback;
            case double d: return d;
            case float f: return f;
            case int i: return i;
            case long l: return l;
            case bool b: return b ? 1d : 0d;
        }
        return double.TryParse(value.ToString(), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    internal static string ToText(object value, string fallback = "")
    {
        if (value == null) return fallback;
        var text = value as string ?? value.ToString();
        return text.Length == 0 ? fallback : text;
    }

    /// <summary>
    /// <see cref="ToText"/> と違い、空文字を既定値へ倒さない。
    /// 「線が来ていない（null）＝未指定」と「明示的に空文字」を区別する必要があるポート用。
    /// 取り消せない操作の入力には必ずこちらを使う。
    /// </summary>
    internal static string ToTextKeepEmpty(object value, string whenMissing)
    {
        if (value == null) return whenMissing;
        return value as string ?? value.ToString();
    }

    internal static bool ToBool(object value, bool fallback = false)
    {
        if (value == null) return fallback;
        if (value is bool b) return b;
        if (value is double d) return d != 0d;
        var text = value.ToString().Trim().ToLowerInvariant();
        if (text.Length == 0) return fallback;
        return text is "true" or "1" or "yes" or "on";
    }
}
