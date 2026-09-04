using NodeGraphModLab.NodeAPI;

namespace NgolExt.NativeHook;

/// <summary>
/// ngol.ext.native-hook Extension エントリポイント。
/// ngol_native.dll (MinHook ベース) をロードし、INativeHookService をサービスとして公開する。
/// Extension がロードされていない場合、ngol.hook.* ノード（watch_function など）は登録されない。
/// </summary>
public sealed class NativeHookExtension : INgolExtension
{
    public void Load(IExtensionContext context)
    {
        NativeHookBridge.EnsureLoaded(context.ExtensionDirectory);
        context.RegisterService(typeof(INativeHookService), new NativeHookServiceImpl(), ExtensionServiceScope.Singleton);
        context.RegisterCapability("native.hook", "1.0.0");
        context.Logger.LogDebug("[native-hook] extension loaded (ngol_native.dll ready)");
    }

    public void Unload(IExtensionContext context)
    {
        if (NativeHookBridge.IsLoaded)
            NativeHookBridge.NGOLHook_UninstallAll();
        context.Logger.LogInfo("[native-hook] extension unloaded, all hooks removed");
    }
}
