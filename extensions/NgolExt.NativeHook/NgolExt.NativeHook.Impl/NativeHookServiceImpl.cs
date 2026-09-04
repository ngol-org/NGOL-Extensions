using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NgolExt.NativeHook;

/// <summary>
/// INativeHookService の実装。NativeHookBridge（P/Invoke）を委譲する。
/// </summary>
internal sealed class NativeHookServiceImpl : INativeHookService
{
    // hook ハンドル(HookEntry*) -> 登録済みマネージドコールバックの対応表。
    // ネイティブ側は単一の静的サンク(OnHookFired)しか知らないため、
    // 実際の分岐（どのフックにどのコールバックを呼ぶか）はこの辞書でC#側が行う。
    private static readonly ConcurrentDictionary<IntPtr, Action<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>> s_callbacks = new();

    private static void OnHookFired(IntPtr hook, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
    {
        // ネイティブ境界を越えて例外を伝播させると即クラッシュするため、
        // ここで発生した例外は種類を問わず必ず握りつぶす。
        try
        {
            if (s_callbacks.TryGetValue(hook, out var cb)) cb(hook, a0, a1, a2, a3);
        }
        catch
        {
        }
    }

#if NET6_0_OR_GREATER
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnHookFiredThunk(IntPtr hook, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3)
        => OnHookFired(hook, a0, a1, a2, a3);

    private static unsafe IntPtr GetThunkFunctionPointer()
    {
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> fp = &OnHookFiredThunk;
        return (IntPtr)fp;
    }
#else
    private delegate void HookFiredDelegate(IntPtr hook, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3);

    // net462 には UnmanagedCallersOnly が無いため、従来型デリゲート + 関数ポインタ化を使う。
    // デリゲートインスタンスは静的フィールドで参照し続けることでGC回収を防ぐ
    // （ネイティブ側は関数ポインタしか保持しないため、デリゲートがGCされると
    //   次回発火時に無効なアドレスを呼び出しプロセスがクラッシュする）。
    private static readonly HookFiredDelegate s_thunkDelegate = OnHookFired;
    private static readonly IntPtr s_thunkFunctionPointer = Marshal.GetFunctionPointerForDelegate(s_thunkDelegate);

    private static IntPtr GetThunkFunctionPointer() => s_thunkFunctionPointer;
#endif

    public bool SetManagedCallback(IntPtr hook, Action<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>? callback)
    {
        if (callback == null)
        {
            s_callbacks.TryRemove(hook, out _);
            return NativeHookBridge.NGOLHook_SetManagedCallback(hook, IntPtr.Zero);
        }

        s_callbacks[hook] = callback;
        return NativeHookBridge.NGOLHook_SetManagedCallback(hook, GetThunkFunctionPointer());
    }

    public bool Install(IntPtr pTarget, out IntPtr hook)
        => NativeHookBridge.NGOLHook_Install(pTarget, out hook);

    public bool InstallTyped(IntPtr pTarget, int floatSlotMask, out IntPtr hook)
        => NativeHookBridge.NGOLHook_InstallTyped(pTarget, floatSlotMask, out hook);

    public bool Uninstall(IntPtr hook)
    {
        // HookEntryスロットは解除後に再利用されるため、辞書に残したままだと
        // 別のフックが同じハンドル値で再登録された際に古いコールバックが誤って呼ばれる。
        s_callbacks.TryRemove(hook, out _);
        return NativeHookBridge.NGOLHook_Uninstall(hook);
    }

    public void UninstallAll()
    {
        s_callbacks.Clear();
        NativeHookBridge.NGOLHook_UninstallAll();
    }

    public void Read(IntPtr hook, out long count, out long a0, out long a1, out long a2, out long a3)
        => NativeHookBridge.NGOLHook_Read(hook, out count, out a0, out a1, out a2, out a3);

    public void ResetCount(IntPtr hook)
        => NativeHookBridge.NGOLHook_ResetCount(hook);

    public bool IsActive(IntPtr hook)
        => NativeHookBridge.NGOLHook_IsActive(hook);

    public bool SetCallOriginal(IntPtr hook, bool callOriginal)
        => NativeHookBridge.NGOLHook_SetCallOriginal(hook, callOriginal);

    public bool SetReturnValue(IntPtr hook, long value)
        => NativeHookBridge.NGOLHook_SetReturnValue(hook, value);

    public IntPtr GetTrampoline(IntPtr hook)
        => NativeHookBridge.NGOLHook_GetTrampoline(hook);

    public bool SetExtraStackArgs(IntPtr hook, int count)
        => NativeHookBridge.NGOLHook_SetExtraStackArgs(hook, count);

    public long[] ReadExtra(IntPtr hook, int count)
    {
        var buf = new long[count];
        NativeHookBridge.NGOLHook_ReadExtra(hook, buf, count);
        return buf;
    }

    public long ReadReturnAddress(IntPtr hook)
    {
        NativeHookBridge.NGOLHook_ReadReturnAddress(hook, out var address);
        return address;
    }

    public int RecordSize(int frames) => NativeHookBridge.NGOLHook_RecordSize(frames);

    public bool SetRecordBuffer(IntPtr hook, IntPtr buffer, int capacity, int frames, out long firstSeq)
        => NativeHookBridge.NGOLHook_SetRecordBuffer(hook, buffer, capacity, frames, out firstSeq);

    public bool ReadQWORD(IntPtr pAddr, out long value)
        => NativeHookBridge.NGOLMem_ReadQWORD(pAddr, out value);

    public bool ReadDWORD(IntPtr pAddr, out uint value)
        => NativeHookBridge.NGOLMem_ReadDWORD(pAddr, out value);

    public bool ReadBytes(IntPtr pAddr, byte[] buf, UIntPtr len)
        => NativeHookBridge.NGOLMem_ReadBytes(pAddr, buf, len);

    public bool IsReadable(IntPtr pAddr, UIntPtr len)
        => NativeHookBridge.NGOLMem_IsReadable(pAddr, len);

    public bool WriteQWORD(IntPtr pAddr, long value)
        => NativeHookBridge.NGOLMem_WriteQWORD(pAddr, value);

    public bool WriteBytes(IntPtr pAddr, byte[] buf, UIntPtr len)
        => NativeHookBridge.NGOLMem_WriteBytes(pAddr, buf, len);

    public uint StackTrace(IntPtr[] frames, uint maxFrames)
        => NativeHookBridge.NGOLDbg_StackTrace(frames, maxFrames);

    public string GetLastError()
        => NativeHookBridge.GetLastError();

    public bool TryGetKlassName(IntPtr pObj, out string className, out string classNamespace)
    {
        var nameBuf = new byte[256];
        var nsBuf = new byte[256];
        var ok = NativeHookBridge.NGOLKlass_GetName(pObj, nameBuf, (UIntPtr)nameBuf.Length, nsBuf, (UIntPtr)nsBuf.Length);
        if (!ok)
        {
            className = "";
            classNamespace = "";
            return false;
        }
        className = ReadAnsiZ(nameBuf);
        classNamespace = ReadAnsiZ(nsBuf);
        return true;
    }

    static string ReadAnsiZ(byte[] buf)
    {
        var len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf, 0, len);
    }
}
