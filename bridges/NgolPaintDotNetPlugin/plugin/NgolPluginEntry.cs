using System;
using PaintDotNet;

[assembly: PluginSupportInfo(typeof(NgolForPaintDotNet.NgolPluginInfo))]

namespace NgolForPaintDotNet;

/// <summary>
/// ホストがこの型を構築したときに NGOL が起きる。それがこのプラグインの入口。
///
/// 置き場所はホストが走査する 3 つのフォルダのうち <c>FileTypes</c>。
/// ファイル形式を 1 つも返さないので、開く・保存の一覧には何も増えない。
///
/// 効果の型は置かない。置くと効果メニューへ並び、押した人の画像がその効果の
/// 出力で置き換わる。このプラグインは画像に触らないので出力が空になり、
/// レイヤーが消える。
///
/// メタデータを読ませるだけでは足りない。ホストは型を数え上げるのに反射を使うので、
/// モジュール初期化子は走らない。ホストが実際に構築する型が要る。
/// </summary>
public sealed class NgolFileTypes : IFileTypeFactory
{
    public NgolFileTypes() => NgolHost.EnsureStarted();

    public FileType[] GetFileTypeInstances() => Array.Empty<FileType>();
}

/// <summary>
/// ホストのプラグイン一覧に出す素性。上のアセンブリ属性で名指ししてある。
/// 構築されるのは一覧が開かれたときで、起動の合図には使えない。
/// </summary>
public sealed class NgolPluginInfo : IPluginSupportInfo
{
    public string DisplayName => "NGOL";
    public string Author => "Node Graph Mod Lab";
    public string Copyright => "MIT";
    public Version Version => typeof(NgolPluginInfo).Assembly.GetName().Version ?? new Version(1, 0);
    public Uri? WebsiteUri => null;
}
