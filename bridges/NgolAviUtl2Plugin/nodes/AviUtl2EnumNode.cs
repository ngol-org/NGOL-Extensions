using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストに登録されている効果・モジュール・設定項目の名前を、ホスト自身に列挙させる。
///
/// エイリアスへ書く効果名は、ここに出てくる名前でなければ受け付けられない。
/// 設定項目のキー名も同じで、違う名前を書いても黙って無視される。
/// 名前を推測して失敗するより、聞いた方が速く確実。
///
/// モジュールの一覧では、登録したスクリプトモジュールがホスト側に
/// 載っているかを、呼べたかどうかではなくホストの申告で確かめられる。
/// </summary>
[NodeType("aviutl.info.enumerate", "AviUtl2", "Enumerate Effects And Modules",
    Version = "1.1.0",
    Description = "Asks the host to list the effect names, loaded modules and per-effect setting items it knows about. The name written as effect.name in an alias has to be one of these, and so does every key under it, so listing them removes the guesswork that otherwise shows up as an object that silently fails to be created or a value that is quietly ignored. The module list also tells you whether a registered script module is really on the host side, rather than inferring it from a call that happened to work.")]
[NodePort("what", PortDirection.Input, "string", Description = "effects / modules / items / both (default both, which is effects and modules). items needs the effect port")]
[NodePort("effect", PortDirection.Input, "string", Description = "Effect name to list the setting items of, required when what=items. Use one of the names returned by what=effects")]
[NodePort("name_filter", PortDirection.Input, "string", Description = "Only include entries whose name contains this text (case-insensitive). Empty = all")]
[NodePort("effect_count", PortDirection.Output, "number", Description = "Number of effect names returned")]
[NodePort("module_count", PortDirection.Output, "number", Description = "Number of modules returned")]
[NodePort("item_count", PortDirection.Output, "number", Description = "Number of setting items returned. Zero with an empty result means the effect was not found; check result")]
[NodePort("effects", PortDirection.Output, "string", Description = "Effect names, one per line, as name / type / flag")]
[NodePort("modules", PortDirection.Output, "string", Description = "Modules, one per line, as kind / name / information")]
[NodePort("items", PortDirection.Output, "string", Description = "Setting items of the given effect, one per line, as key name / kind. The key name is what an alias line writes before the equals sign")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable summary")]
public sealed class AviUtl2EnumNode : INode
{
    // ホストが返すモジュール種別。SDK のヘッダーに定義された値。
    static readonly string[] ModuleKinds = {
        "", "script filter", "script object", "script camera", "script track",
        "script module", "input plugin", "output plugin", "filter plugin", "common plugin",
    };

    // 設定項目の種別。SDK のヘッダーに定義された値。
    // group と separator は値を持たないので、エイリアスには書けない。
    static readonly string[] ItemKinds = {
        "", "integer", "number", "check", "text", "string", "file", "color", "select",
        "scene", "layer range", "combo", "mask", "font", "figure", "data", "folder",
        "number group", "group (no value)", "separator (no value)",
    };

    // disasm-verified: RVA 0x5420 / 引数2個（rcx=64bit ポインタ / edx=32bit、
    // [rsp+X] からの引数読み取りは無い）/ 戻り値は eax の 32bit
    [DllImport("NgolForAviUtl2.aux2")]
    static extern int Ngol_EnumEffectNames(byte[] outUtf8, int outLen);

    // disasm-verified: RVA 0x5540 / 同じ形（引数2個・戻り値 32bit）
    [DllImport("NgolForAviUtl2.aux2")]
    static extern int Ngol_EnumModules(byte[] outUtf8, int outLen);

    // disasm-verified: RVA 0x7fa0 / 引数3個（rcx=64bit ポインタ / rdx=64bit ポインタ /
    // r8d=32bit、[rsp+X] からの引数読み取りは無い）/ 戻り値は eax の 32bit。
    // 効果が見つからない場合は -1 を返す。
    [DllImport("NgolForAviUtl2.aux2")]
    static extern int Ngol_EnumEffectItems(byte[] effectUtf8, byte[] outUtf8, int outLen);

    delegate int Collector(byte[] buffer, int length);

    public void Execute(IExecutionContext ctx)
    {
        var what = (ctx.GetPortValue("what") as string ?? "both").Trim().ToLowerInvariant();
        var filter = (ctx.GetPortValue("name_filter") as string ?? "").Trim();
        if (what.Length == 0) what = "both";

        string effects = "", modules = "", items = "";
        int effectCount = 0, moduleCount = 0, itemCount = 0;
        var notes = new System.Collections.Generic.List<string>();

        if (what is "both" or "effects")
        {
            effects = Collect(Ngol_EnumEffectNames, out var note);
            if (note.Length > 0) notes.Add("effects: " + note);
            effects = FilterLines(effects, filter, 0, out effectCount);
        }
        if (what is "both" or "modules")
        {
            modules = Collect(Ngol_EnumModules, out var note);
            if (note.Length > 0) notes.Add("modules: " + note);
            modules = DescribeModules(modules, filter, out moduleCount);
        }
        if (what is "items")
        {
            var effect = (ctx.GetPortValue("effect") as string ?? "").Trim();
            if (effect.Length == 0)
            {
                notes.Add("items: the effect port is required");
            }
            else
            {
                items = CollectItems(effect, out var note);
                if (note.Length > 0) notes.Add("items: " + note);
                items = DescribeItems(items, filter, out itemCount);
            }
        }

        ctx.SetPortValue("effect_count", (double)effectCount);
        ctx.SetPortValue("module_count", (double)moduleCount);
        ctx.SetPortValue("item_count", (double)itemCount);
        ctx.SetPortValue("effects", effects);
        ctx.SetPortValue("modules", modules);
        ctx.SetPortValue("items", items);
        ctx.SetPortValue("result", notes.Count > 0
            ? string.Join(" / ", notes)
            : what is "items"
                ? $"{itemCount} item(s)"
                : $"{effectCount} effect(s), {moduleCount} module(s)");
    }

    // 効果名を渡す分だけ別の入口になる。見つからなかった場合は -1 が返るので、
    // 項目が 0 個だった場合と区別して伝える。
    static string CollectItems(string effect, out string note)
    {
        note = "";
        var name = Encoding.UTF8.GetBytes(effect + "\0");

        var probe = new byte[1];
        int need = Ngol_EnumEffectItems(name, probe, probe.Length);
        if (need < 0) { note = $"the host does not know an effect called '{effect}'"; return ""; }
        if (need == 0) { note = "the host returned nothing (the edit handle may be missing)"; return ""; }

        var buffer = new byte[need + 16];
        int wrote = Ngol_EnumEffectItems(name, buffer, buffer.Length);
        if (wrote > buffer.Length) { note = $"needed {wrote} bytes but only {buffer.Length} were offered"; return ""; }

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    // 項目の行は名前と種別の番号。読める名前に置き換える。
    static string DescribeItems(string text, string filter, out int count)
    {
        count = 0;
        if (text.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length == 0) continue;
            var parts = t.Split('\t');
            var name = parts[0];
            if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var kind = parts.Length > 1 && int.TryParse(parts[1], out var k) && k >= 0 && k < ItemKinds.Length
                ? ItemKinds[k]
                : parts.Length > 1 ? parts[1] : "";
            sb.Append(name).Append("  ").Append(kind).Append('\n');
            count++;
        }
        return sb.ToString();
    }

    // 必要な長さを先に聞いてから確保する。足りないまま読むと途中で切れた一覧を
    // 全部だと思い込むため、短いときは黙って諦めず理由を返す。
    static string Collect(Collector call, out string note)
    {
        note = "";
        var probe = new byte[1];
        int need = call(probe, probe.Length);
        if (need <= 0) { note = "the host returned nothing (the edit handle may be missing)"; return ""; }

        var buffer = new byte[need + 16];
        int wrote = call(buffer, buffer.Length);
        if (wrote > buffer.Length) { note = $"needed {wrote} bytes but only {buffer.Length} were offered"; return ""; }

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    static string FilterLines(string text, string filter, int nameField, out int count)
    {
        count = 0;
        if (text.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length == 0) continue;
            var parts = t.Split('\t');
            var name = parts.Length > nameField ? parts[nameField] : t;
            if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            sb.Append(name);
            for (int i = 0; i < parts.Length; i++) if (i != nameField) sb.Append("  ").Append(parts[i]);
            sb.Append('\n');
            count++;
        }
        return sb.ToString();
    }

    // モジュールの行は先頭が種別の番号。読める名前に置き換える。
    static string DescribeModules(string text, string filter, out int count)
    {
        count = 0;
        if (text.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length == 0) continue;
            var parts = t.Split('\t');
            if (parts.Length < 2) continue;
            var kind = int.TryParse(parts[0], out var k) && k >= 0 && k < ModuleKinds.Length
                ? ModuleKinds[k]
                : parts[0];
            var name = parts[1];
            if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            sb.Append(kind).Append("  ").Append(name);
            if (parts.Length > 2 && parts[2].Length > 0) sb.Append("  ").Append(parts[2]);
            sb.Append('\n');
            count++;
        }
        return sb.ToString();
    }
}
