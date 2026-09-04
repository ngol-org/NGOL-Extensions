using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NgolExt.NativeHook;

namespace NgolExt.NativeHook.Tests;

[TestFixture]
public class NativeHookBridgeTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DummyFunc(int a, int b);

    private static readonly DummyFunc DummyImpl = static (a, b) => a + b;
    private static readonly IntPtr DummyPtr = Marshal.GetFunctionPointerForDelegate(DummyImpl);

    [OneTimeSetUp]
    public void Setup()
    {
        var implDir = Path.GetDirectoryName(typeof(NativeHookBridge).Assembly.Location)!;
        var nativeDll = Path.Combine(implDir, "ngol_native.dll");
        Assert.That(File.Exists(nativeDll), Is.True, $"ngol_native.dll not found at {nativeDll}");
        NativeHookBridge.EnsureLoaded(implDir);
    }

    [TearDown]
    public void TearDown() => NativeHookBridge.NGOLHook_UninstallAll();

    [Test]
    public void GetLastError_InitialState_Empty()
    {
        Assert.That(NativeHookBridge.GetLastError(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void Install_ValidPtr_SucceedsWithNonZeroHandle()
    {
        Assert.That(NativeHookBridge.NGOLHook_Install(DummyPtr, out var hook), Is.True);
        Assert.That(hook, Is.Not.EqualTo(IntPtr.Zero));
    }

    [Test]
    public void Install_NullPtr_FailsWithError()
    {
        Assert.That(NativeHookBridge.NGOLHook_Install(IntPtr.Zero, out _), Is.False);
        Assert.That(NativeHookBridge.GetLastError(), Does.Contain("ERR:"));
    }

    [Test]
    public void IsActive_AfterInstall_True()
    {
        NativeHookBridge.NGOLHook_Install(DummyPtr, out var hook);
        Assert.That(NativeHookBridge.NGOLHook_IsActive(hook), Is.True);
    }

    [Test]
    public void IsActive_AfterUninstall_False()
    {
        NativeHookBridge.NGOLHook_Install(DummyPtr, out var hook);
        NativeHookBridge.NGOLHook_Uninstall(hook);
        Assert.That(NativeHookBridge.NGOLHook_IsActive(hook), Is.False);
    }

    [Test]
    public void Install_DoubleInstall_ReturnsAlreadyHooked()
    {
        NativeHookBridge.NGOLHook_Install(DummyPtr, out var hook1);
        Assert.That(NativeHookBridge.NGOLHook_Install(DummyPtr, out _), Is.False);
        Assert.That(NativeHookBridge.GetLastError(), Does.Contain("ALREADY_HOOKED"));
        NativeHookBridge.NGOLHook_Uninstall(hook1);
    }

    [Test]
    public void SetCallOriginal_Toggle_DoesNotThrow()
    {
        NativeHookBridge.NGOLHook_Install(DummyPtr, out var hook);
        Assert.That(NativeHookBridge.NGOLHook_SetCallOriginal(hook, true), Is.True);
        Assert.That(NativeHookBridge.NGOLHook_SetCallOriginal(hook, false), Is.True);
    }
}
