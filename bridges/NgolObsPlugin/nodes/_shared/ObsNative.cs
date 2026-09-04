using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストへ 1 件頼んで、答えを受け取る。
///
/// 頼み事も答えも JSON。口はプラグインが持つ 3 つだけで、操作を増やしても
/// この宣言は動かない。ホストの型はプラグインの中に閉じている。
/// </summary>
internal static class ObsNative
{
    // disasm-verified 2026-08-20 (dumpbin -disasm, 本ブリッジのビルド成果物):
    //   Ngol_Obs_Call        RVA 0x2BE0 / ずれ 0x108 (push x4 + sub 0E8h)。
    //                        rcx=const char* / rdx=char* / r8d=32bit の 3 個。
    //                        [rsp+X] のうち 0x108 を超える読みが無く、スタック引数は無い。
    //                        戻りは mov eax,0FFFFFFFFh -> 32bit。
    //   Ngol_Obs_TakeResult  RVA 0x31A0 / ずれ 0x28 (push rbx + sub 20h)。
    //                        rcx=char* / edx=32bit の 2 個。戻りは mov eax,ebx -> 32bit。
    //   Ngol_Obs_TakeFrame   RVA 0x3190 は 0x9F60 への jmp。実体のずれ 0x28。
    //                        rcx / edx=32bit / r8 / r9 に加え [rsp+50h]-0x28=+0x28 -> 第 5 引数。
    //                        => 引数 5 個。戻りは eax -> 32bit。
    //   RVA は作り直すと動く。名前で解決すること（DllImport はそうしている）。
    private const string Module = "NgolForObs.dll";

    [DllImport(Module)]
    private static extern int Ngol_Obs_Call(byte[] request, byte[] result, int resultLen);

    [DllImport(Module)]
    private static extern int Ngol_Obs_TakeResult(byte[] result, int resultLen);

    [DllImport(Module)]
    internal static extern int Ngol_Obs_TakeFrame(byte[] pixels, int pixelsLen,
                                                  out int width, out int height, out int pitch);

    /// <summary>頼み事を組み立てる。渡さなかった項目は、ホスト側で「触らない」扱いになる。</summary>
    internal sealed class Request
    {
        private readonly Dictionary<string, object> _fields = new();

        internal Request(string op) { _fields["op"] = op; }

        internal Request With(string name, string value)
        {
            if (value != null) _fields[name] = value;
            return this;
        }

        internal Request With(string name, bool value) { _fields[name] = value; return this; }
        internal Request With(string name, double value) { _fields[name] = value; return this; }
        internal Request With(string name, long value) { _fields[name] = value; return this; }

        internal string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var pair in _fields)
                {
                    switch (pair.Value)
                    {
                        case string s: writer.WriteString(pair.Key, s); break;
                        case bool b: writer.WriteBoolean(pair.Key, b); break;
                        case double d: writer.WriteNumber(pair.Key, d); break;
                        case long l: writer.WriteNumber(pair.Key, l); break;
                    }
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    /// <summary>ホストの答え。読み終えたら捨てる。</summary>
    internal sealed class Reply : IDisposable
    {
        private readonly JsonDocument _document;

        internal Reply(string raw)
        {
            Raw = raw;
            try
            {
                _document = JsonDocument.Parse(raw);
                Ok = Bool("ok");
                Error = Text("error");
            }
            catch (Exception ex)
            {
                Ok = false;
                Error = "the answer was not JSON: " + ex.Message;
            }
        }

        internal string Raw { get; }
        internal bool Ok { get; }
        internal string Error { get; }

        internal string Text(string name)
            => _document != null && _document.RootElement.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.String
                ? v.GetString() : "";

        internal double Number(string name)
            => _document != null && _document.RootElement.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : 0d;

        internal bool Bool(string name)
            => _document != null && _document.RootElement.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.True;

        /// <summary>配列の中から 1 つの項目だけを取り出して並べる。</summary>
        internal string Column(string arrayName, string field)
        {
            if (_document == null) return "";
            if (!_document.RootElement.TryGetProperty(arrayName, out var array)) return "";
            if (array.ValueKind != JsonValueKind.Array) return "";

            var lines = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (!entry.TryGetProperty(field, out var v)) continue;
                // 真偽値は JSON と同じ綴りで並べる。C# の綴りに任せると、
                // 同じ答えの中で json ポートと綴りが食い違う。
                lines.Add(v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => v.ToString(),
                });
            }
            return string.Join("\n", lines);
        }

        internal int Count(string arrayName)
        {
            if (_document == null) return 0;
            if (!_document.RootElement.TryGetProperty(arrayName, out var array)) return 0;
            return array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;
        }

        public void Dispose() => _document?.Dispose();
    }

    /// <summary>
    /// 1 件頼む。答えが控えより大きかったときは、走らせ直さずに引き取る--
    /// 2 度目のシーン切り替えや録画開始は、頼まれていない操作になる。
    /// </summary>
    internal static Reply Call(Request request)
    {
        byte[] payload = Encoding.UTF8.GetBytes(request.ToJson() + "\0");

        var buffer = new byte[16 * 1024];
        int need = Ngol_Obs_Call(payload, buffer, buffer.Length);
        if (need <= 0) return new Reply("{\"ok\":false,\"error\":\"the host gave no answer\"}");

        if (need > buffer.Length)
        {
            buffer = new byte[need];
            need = Ngol_Obs_TakeResult(buffer, buffer.Length);
            if (need <= 0 || need > buffer.Length)
                return new Reply("{\"ok\":false,\"error\":\"the answer could not be collected\"}");
        }

        return new Reply(Encoding.UTF8.GetString(buffer, 0, need - 1));
    }

    internal static Reply Call(string op) => Call(new Request(op));
}
