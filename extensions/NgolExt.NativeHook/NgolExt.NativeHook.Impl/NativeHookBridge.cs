using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NgolExt.NativeHook;

/// <summary>
/// P/Invoke bridge to ngol_native.dll (CLR-independent native hooking helpers).
/// DLL is loaded from the Extension directory via EnsureLoaded(extensionDir).
/// </summary>
public static class NativeHookBridge
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("ngol_native", EntryPoint = "NGOL_GetLastError")]
    private static extern IntPtr NGOL_GetLastError_Raw();

    public static string GetLastError()
        => Marshal.PtrToStringAnsi(NGOL_GetLastError_Raw()) ?? string.Empty;

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_Install(IntPtr pTarget, out IntPtr hook);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_InstallTyped(IntPtr pTarget, int floatSlotMask, out IntPtr hook);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_Uninstall(IntPtr hook);

    [DllImport("ngol_native")]
    public static extern void NGOLHook_UninstallAll();

    [DllImport("ngol_native")]
    public static extern void NGOLHook_Read(
        IntPtr hook, out long count, out long a0, out long a1, out long a2, out long a3);

    [DllImport("ngol_native")]
    public static extern void NGOLHook_ReadReturnAddress(IntPtr hook, out long pReturnAddress);

    // 発火のたびに 1 件ずつ書く貸し先。置き場は呼ぶ側が確保し、読むのも呼ぶ側。
    [DllImport("ngol_native")]
    public static extern int NGOLHook_RecordSize(int frames);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_SetRecordBuffer(
        IntPtr hook, IntPtr buffer, int capacity, int frames, out long pFirstSeq);

    [DllImport("ngol_native")]
    public static extern void NGOLHook_ResetCount(IntPtr hook);

    [DllImport("ngol_native")]
    public static extern IntPtr NGOLHook_GetTrampoline(IntPtr hook);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_IsActive(IntPtr hook);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_SetCallOriginal(IntPtr hook, bool callOriginal);

    // disasm-verified: RVA 0x10d30 / 引数2個（[rsp+0x28] 以降の読み取り無し）/
    //   第1引数 rcx=64bit ハンドル（test rcx,rcx でテーブル範囲と比較）/ 第2引数 rdx=64bit（mov r9,rdx で全幅を使用）
    [DllImport("ngol_native")]
    public static extern bool NGOLHook_SetReturnValue(IntPtr hook, long value);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_SetManagedCallback(IntPtr hook, IntPtr callbackFnPtr);

    [DllImport("ngol_native")]
    public static extern bool NGOLHook_SetExtraStackArgs(IntPtr hook, int count);

    [DllImport("ngol_native")]
    public static extern void NGOLHook_ReadExtra(IntPtr hook, [Out] long[] pBuf, int bufCount);

    [DllImport("ngol_native")]
    public static extern bool NGOLMem_ReadQWORD(IntPtr pAddr, out long pValue);

    [DllImport("ngol_native")]
    public static extern bool NGOLMem_ReadDWORD(IntPtr pAddr, out uint pValue);

    [DllImport("ngol_native")]
    public static extern bool NGOLMem_ReadBytes(IntPtr pAddr, byte[] pBuf, UIntPtr len);

    [DllImport("ngol_native")]
    public static extern bool NGOLMem_IsReadable(IntPtr pAddr, UIntPtr len);

    [DllImport("ngol_native")]
    public static extern bool NGOLMem_WriteQWORD(IntPtr pAddr, long value);

    [DllImport("ngol_native")]
    public static extern bool NGOLMem_WriteBytes(IntPtr pAddr, byte[] pBuf, UIntPtr len);

    [DllImport("ngol_native")]
    public static extern uint NGOLDbg_StackTrace(IntPtr[] pFrames, uint maxFrames);

    [DllImport("ngol_native")]
    public static extern bool NGOLKlass_GetName(
        IntPtr pObj, byte[] nameBuf, UIntPtr nameBufLen, byte[] nsBuf, UIntPtr nsBufLen);

    static bool _loaded;

    /// <summary>
    /// ngol_native.dll を extensionDir から探してロードする。
    /// Extension の Load() から呼ぶこと。
    /// </summary>
    public static void EnsureLoaded(string extensionDir)
    {
        if (_loaded) return;

        var dllPath = Path.Combine(extensionDir, "ngol_native.dll");
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("ngol_native.dll not found", dllPath);

#if NET6_0_OR_GREATER
        NativeLibrary.SetDllImportResolver(
            typeof(NativeHookBridge).Assembly,
            (libraryName, _, _) => libraryName == "ngol_native"
                ? NativeLibrary.Load(dllPath)
                : IntPtr.Zero);
#else
        if (LoadLibrary(dllPath) == IntPtr.Zero)
            throw new DllNotFoundException($"Failed to load ngol_native.dll: {dllPath}");
#endif
        _loaded = true;
    }

    public static bool IsLoaded => _loaded;
}
