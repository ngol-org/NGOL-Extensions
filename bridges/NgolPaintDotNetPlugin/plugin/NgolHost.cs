using System.Reflection;
using System.Runtime.CompilerServices;

namespace NgolForPaintDotNet;

/// <summary>
/// NGOL を起こす。ホストが既に .NET なので、境界を越える仕掛けは何も要らない。
///
/// ホストのローダーは NGOL を知らないので、こちらで読んでから使う。
/// このクラスは NGOL の型に一切触れないこと。触れると、この中の 1 行目を実行する前に
/// メソッドの JIT が型解決を試みて失敗し、下の catch にも届かないまま
/// 型の初期化ごと落ちる。NGOL の型は <see cref="NgolBoot"/> の中だけ。
/// </summary>
internal static class NgolHost
{
    private static bool s_started;

    public static void EnsureStarted()
    {
        if (s_started) return;
        try
        {
            LoadAndStart();
            s_started = true;
        }
        catch (Exception ex)
        {
            // 起こせなくてもホストを巻き込まない。ここで投げるとプラグインの走査ごと壊れる。
            TryWriteFailure(ex);
        }
    }

    /// <summary>
    /// NGOL 一式は、このプラグインの隣ではなく 1 つ上の <c>ngol</c> に置く。
    /// ホストはプラグインのフォルダを走査するので、そこへ置くと NGOL 本体まで
    /// プラグインとして読み込もうとする。
    /// </summary>
    private static string ResolveNgolRoot()
    {
        var pluginDir = Path.GetDirectoryName(typeof(NgolHost).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(pluginDir, "..", "ngol"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAndStart()
    {
        var ngolRoot = ResolveNgolRoot();
        if (!Directory.Exists(ngolRoot))
        {
            throw new DirectoryNotFoundException("NGOL runtime not found: " + ngolRoot);
        }

        string[] names =
        [
            "NodeGraphModLab.NodeAPI.dll",
            "NodeGraphModLab.Core.dll",
            "NodeGraphModLab.HostLogging.dll",
        ];
        foreach (var name in names)
        {
            var path = Path.Combine(ngolRoot, name);
            if (File.Exists(path)) Assembly.LoadFrom(path);
        }

        NgolBoot.Start(ngolRoot);
    }

    // NGOL が起きる前に失敗すると、ログの出し先がまだ無い。理由が何も残らないのを避ける。
    private static void TryWriteFailure(Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ngol-for-paintdotnet-error.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // 書けないなら諦める。ここでの失敗でホストを巻き込まない。
        }
    }
}
