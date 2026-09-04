using NodeGraphModLab.NodeAPI;

namespace NgolExt.Code;

/// <summary>
/// コード読み取り用ライブラリを配る拡張。サービスは提供しない。
///
/// ライブラリ本体（逆アセンブラ）は lib/&lt;tfm&gt;/ へ置かれ、
/// 拡張ホストが読み込んだ時点で動的コンパイルノードの参照に加わる。
/// このクラスが行うのは「この接続で何ができるか」の宣言だけ。
///
/// サービスを持たないのは、逆アセンブルが純粋関数（バイト列 -> 命令列）であり、
/// ノードのホットリロードをまたいで保持すべき状態も資源も無いため。
/// </summary>
public sealed class CodeExtension : INgolExtension
{
    public void Load(IExtensionContext context)
    {
        context.RegisterCapability("code.disasm", "1.0.0");
        context.RegisterCapability("code.xref", "1.0.0");

        context.Logger.LogDebug("[code] capabilities registered: code.disasm / code.xref");
    }

    public void Unload(IExtensionContext context)
    {
        context.Logger.LogInfo("[code] extension unloaded");
    }
}
