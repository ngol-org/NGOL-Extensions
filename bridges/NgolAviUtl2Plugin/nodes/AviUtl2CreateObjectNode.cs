using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストの編集機能を借りてオブジェクトを 1 つ作る。
///
/// スクリプトの実行環境はスクリプトが使われるまで作られない。
/// オブジェクトを 1 つ置けばそこで作られるので、実行環境が要る確認を
/// 画面を操作せずに始められる。
///
/// 実際の作成はプラグイン側が編集セクション越しにホストへ依頼する。
/// 編集セクションのコールバックはホストのメインスレッドから呼ばれる。
/// </summary>
[NodeType("aviutl.edit.create_object", "AviUtl2", "Create Object",
    Version = "1.0.0",
    Description = "Creates one object from alias text through the host's edit section. The host builds its script runtime the first time a script runs, so this is also how to bring that runtime up without operating the UI. Returns created=false when the host refused to open an edit section (it is busy writing output) or when the alias overlapped an existing object.")]
[NodePort("alias", PortDirection.Input, "string", Description = "Object alias text (UTF-8), same format as an object alias file. Empty = a text object that calls this plugin's own script function, which checks the runtime and the registration together")]
[NodePort("layer", PortDirection.Input, "number", Description = "Layer number (default 0)")]
[NodePort("frame", PortDirection.Input, "number", Description = "Frame number (default 0)")]
[NodePort("created", PortDirection.Output, "boolean", Description = "true when the host created the object")]
[NodePort("alias_used", PortDirection.Output, "string", Description = "The alias text that was sent, so a failure can be read back")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2CreateObjectNode : INode
{
    // 既定のエイリアス。このプラグインが登録した関数を呼ぶテキストにしてあるので、
    // 実行環境が起きたことと、関数が届いていることを一度に確かめられる。
    const string DefaultAlias =
        "[Object]\r\n" +
        "[Object.0]\r\n" +
        "effect.name=テキスト\r\n" +
        "サイズ=60.00\r\n" +
        "テキスト=<?mes(ngol.version())?>\r\n" +
        "[Object.1]\r\n" +
        "effect.name=標準描画\r\n";

    // disasm-verified: RVA 0x39d0 / 引数3個（rcx=64bit ポインタ / edx=32bit / r8d=32bit、
    // [rsp+X] からの引数読み取りは無い）/ 戻り値は al の 8bit
    [DllImport("NgolForAviUtl2.aux2")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Ngol_CreateObjectFromAlias(byte[] aliasUtf8, int layer, int frame);

    public void Execute(IExecutionContext ctx)
    {
        var alias = (ctx.GetPortValue("alias") as string ?? "").Trim();
        if (alias.Length == 0) alias = DefaultAlias;

        int layer = ctx.GetPortValue("layer") is double ld ? (int)ld : 0;
        int frame = ctx.GetPortValue("frame") is double fd ? (int)fd : 0;

        var bytes = Encoding.UTF8.GetBytes(alias + "\0");

        bool created;
        try
        {
            created = Ngol_CreateObjectFromAlias(bytes, layer, frame);
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("created", false);
            ctx.SetPortValue("alias_used", alias);
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
            return;
        }

        ctx.SetPortValue("created", created);
        ctx.SetPortValue("alias_used", alias);
        ctx.SetPortValue("result", created
            ? $"created at layer {layer}, frame {frame}"
            : $"not created at layer {layer}, frame {frame} (edit section refused, or the position was taken)");
    }
}
