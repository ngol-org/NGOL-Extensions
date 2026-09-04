using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// オブジェクトの設定項目の値を読み書きする。
///
/// スクリプト側からは自分の設定値を読むことしか出来ないので、外から振りたい場合はここを通す。
/// 見本を撮る・値を機械的に走査する・元へ戻す、といった往復から手作業が消える。
///
/// 対象は「そのレイヤーの、そのフレーム以降で最初に見つかるオブジェクト」。
/// 項目名は推測せず、aviutl.info.enumerate の what=items で列挙して確かめること。
/// </summary>
[NodeType("aviutl.edit.object_item", "AviUtl2", "Object Item Value",
    Version = "1.1.0",
    Description = "Reads or writes one setting value of an object. Scripts can only read their own settings, so anything that drives values from outside goes through here. Leave value empty to read. The target is the first object found on that layer at or after that frame. Item names are not guessable - list them with aviutl.info.enumerate (what=items) first, because a wrong name fails silently on some hosts.")]
[NodePort("layer", PortDirection.Input, "number", Description = "Layer number counted from 0. The host's UI counts from 1, so Layer5 on screen is 4 here")]
[NodePort("frame", PortDirection.Input, "number", Description = "Frame to start searching from, counted from 0 (default 0)")]
[NodePort("effect", PortDirection.Input, "string", Description = "Effect name, as written after effect.name in an alias. Add ':n' to pick the n-th one when the same effect appears more than once")]
[NodePort("item", PortDirection.Input, "string", Description = "Setting item name, as written before the equals sign in an alias")]
[NodePort("value", PortDirection.Input, "string", Description = "Leave empty to read. Anything else is written, in the same format an alias file uses")]
[NodePort("allow_comma", PortDirection.Input, "boolean", Description = "Permit a value holding a comma. Off by default: the host ends the whole process on one here, and a lost project is worse than a refused write. Turn it on only once the format for that item is known")]
[NodePort("read_value", PortDirection.Output, "string", Description = "The value that was there. Read before writing, so it also gives the previous value on a write")]
[NodePort("ok", PortDirection.Output, "boolean", Description = "true when the read or the write went through")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2ObjectItemNode : INode
{
    // disasm-verified: Ngol_SetObjectItemValue RVA 0x93d0 / 引数5個
    // （ecx=32bit layer / edx=32bit frame / r8,r9=64bit ポインタ / [rsp+0x28]=64bit ポインタ）
    // 戻り値は al の 8bit
    [DllImport("NgolForAviUtl2.aux2", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Ngol_SetObjectItemValue(
        int layer, int frame,
        [MarshalAs(UnmanagedType.LPWStr)] string effect,
        [MarshalAs(UnmanagedType.LPWStr)] string item,
        byte[] valueUtf8);

    // disasm-verified: Ngol_GetObjectItemValue RVA 0x8e60 / 引数6個
    // （ecx=32bit layer / edx=32bit frame / r8,r9=64bit ポインタ /
    //   [rsp+0x28]=64bit 出力先 / [rsp+0x30]=32bit 長さ）戻り値は eax の 32bit
    [DllImport("NgolForAviUtl2.aux2", CharSet = CharSet.Unicode)]
    private static extern int Ngol_GetObjectItemValue(
        int layer, int frame,
        [MarshalAs(UnmanagedType.LPWStr)] string effect,
        [MarshalAs(UnmanagedType.LPWStr)] string item,
        byte[] outUtf8, int outLen);

    public void Execute(IExecutionContext ctx)
    {
        int layer = ctx.GetPortValue("layer") is double l ? (int)l : 0;
        int frame = ctx.GetPortValue("frame") is double f ? (int)f : 0;
        string effect = (ctx.GetPortValue("effect") as string ?? "").Trim();
        string item = (ctx.GetPortValue("item") as string ?? "").Trim();
        string value = ctx.GetPortValue("value") as string ?? "";
        bool allowComma = ctx.GetPortValue("allow_comma") is bool c && c;

        ctx.SetPortValue("read_value", "");
        ctx.SetPortValue("ok", false);

        if (effect.Length == 0 || item.Length == 0)
        {
            ctx.SetPortValue("result", "effect and item are both required");
            return;
        }

        try
        {
            // 書く前にも読む。書き換えた側が元の値を持っておけるようにする。
            string before = Read(layer, frame, effect, item);
            ctx.SetPortValue("read_value", before);

            if (value.Length == 0)
            {
                bool found = before.Length > 0;
                ctx.SetPortValue("ok", found);
                ctx.SetPortValue("result", found
                    ? $"read '{item}' of '{effect}' on layer {layer}"
                    : $"nothing to read: no object with '{effect}' at layer {layer} frame {frame} or later");
                return;
            }

            // カンマを含む値は既定で止める。
            //   同じ書式でも経路で挙動が分かれ、こちらは落ちる側だった（実測）。
            //   作成時のエイリアスへ書けば黙って捨てられるだけで済むが、この経路は
            //   プロセスごと終わるので、編集中のものが失われる。
            if (!allowComma && value.Contains(','))
            {
                ctx.SetPortValue("result",
                    "refused: this value holds a comma, and the host ends the process on one here. "
                  + "Write it in the alias when creating the object instead, or set allow_comma "
                  + "once the format for this item is known");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            bool written = Ngol_SetObjectItemValue(layer, frame, effect, item, bytes);

            ctx.SetPortValue("ok", written);
            ctx.SetPortValue("result", written
                ? $"set '{item}' of '{effect}' on layer {layer} to '{value}' (was '{before}')"
                : $"the host refused. Check that '{effect}' exists at layer {layer} frame {frame} or later and that '{item}' is one of its items");
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
        }
    }

    static string Read(int layer, int frame, string effect, string item)
    {
        var probe = new byte[1];
        int need = Ngol_GetObjectItemValue(layer, frame, effect, item, probe, probe.Length);
        if (need <= 0) return "";

        var buffer = new byte[need + 16];
        if (Ngol_GetObjectItemValue(layer, frame, effect, item, buffer, buffer.Length) <= 0) return "";

        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }
}
