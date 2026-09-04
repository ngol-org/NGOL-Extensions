using NodeGraphModLab.NodeAPI;

namespace NgolExt.Il;

/// <summary>
/// マネージドアセンブリの読み取り用ライブラリを配る拡張。サービスは提供しない。
///
/// ネイティブ側に命令列を読む手段があるのと同じ位置づけのものを、マネージド側に用意する。
/// アセンブリをロードせずにメタデータと IL を読めるため、
/// 他のターゲットフレームワーク向けの DLL や、ロードしたくない DLL も対象にできる。
///
/// 読み取り（il.inspect）と書き換え（managed.detour）を同じ拡張で配る。
/// 同じメタデータ基盤の上に乗るもので、読めない対象は書き換えられないため分ける意味が薄い。
///
/// 同名アセンブリをホストが別版で持ち込む場合がある。解決は名前一致で版を見ないため、
///    食い違いは例外にならず「機能が無いこと」としてしか現れない。
///    どの版が実際に使われているかはログに出す--無音にしないことが唯一の防御になる。
/// </summary>
public sealed class IlExtension : INgolExtension
{
    public void Load(IExtensionContext context)
    {
        context.RegisterCapability("il.inspect", "1.0.0");
        context.RegisterCapability("managed.detour", "1.0.0");

        context.Logger.LogDebug("[il] capabilities registered: il.inspect / managed.detour");
        LogLoadedLibraryVersions(context);
    }

    /// <summary>
    /// 同梱ライブラリが実際にどこから・どの版で解決されたかを出す。
    /// ホストが同名の別版を持っていた場合、ここだけが食い違いの手がかりになる。
    ///
    /// 平常時は 1 行だけにする。10 行以上を毎起動出すと、読む人はまとめて読み飛ばすので
    ///    「食い違いの手がかり」という目的に届かない。
    /// 数えた結果（どれだけホスト側から来たか）を 1 行で言い、明細は Debug に置く。
    ///    ホスト側から来た数が想定と違えば、そこで初めて明細を見に行けばよい。
    /// </summary>
    static void LogLoadedLibraryVersions(IExtensionContext context)
    {
        var extensionDir = context.ExtensionDirectory;
        var fromHost = 0;
        var fromExtension = 0;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name is null) continue;
            if (!name.StartsWith("MonoMod", StringComparison.Ordinal) &&
                !name.StartsWith("Mono.Cecil", StringComparison.Ordinal)) continue;

            var location = "";
            try { location = asm.Location; } catch { }

            if (extensionDir.Length > 0 && location.StartsWith(extensionDir, StringComparison.OrdinalIgnoreCase))
                fromExtension++;
            else
                fromHost++;

            context.Logger.LogDebug($"[il]   {name} {asm.GetName().Version} <- {(location.Length > 0 ? location : "(no location)")}");
        }

        // ここを Info にしない。拡張ホスト側が拡張 1 本につき 1 行を出しており、
        //    そこに同じ趣旨の件数が載る。拡張ごとに独自の要約行を足すと、
        //    拡張が増えた分だけ起動ログが伸びる。
        if (fromHost + fromExtension > 0)
            context.Logger.LogDebug($"[il] analysis libraries: {fromExtension} from this extension, {fromHost} from the host");
    }

    public void Unload(IExtensionContext context)
    {
        context.Logger.LogInfo("[il] extension unloaded");
    }
}
