using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

using GUID = NgolCom.GUID;
using GetDeviceFn = NgolCom.GetDeviceFn;
using GetBufferFn = NgolCom.GetBufferFn;

/// <summary>
/// D3D11 のスワップチェーンからバックバッファを読み出す。
/// GetBuffer -> GetDevice -> GetImmediateContext -> CreateTexture2D(staging) ->
/// CopyResource -> Map の順で、CPU から読める複製を作ってから写す。
///
/// com-abi: vtable スロット番号は公開ヘッダー通り
/// （IDXGISwapChain / ID3D11Device / ID3D11DeviceContext）。
/// 各関数ポインタは呼ぶ前に <see cref="NgolCom.LooksLikeCode"/> を通す。
/// </summary>
internal static class D3D11Capture
{
    static IntPtr GetVtableSlot(IntPtr o, int s) => NgolCom.GetVtableSlot(o, s);
    static bool LooksLikeCode(IntPtr a) => NgolCom.LooksLikeCode(a);
    static void Release(IntPtr o) => NgolCom.Release(o);

    // 共通部（NgolComInterop.cs）を短い名前で使う。
    static readonly GUID IID_ID3D11Texture2D = new GUID(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);
    static readonly GUID IID_ID3D11Resource   = new GUID(0xdc8e63f3, 0xd12b, 0x4952, 0xb4, 0x7b, 0x5e, 0x45, 0x02, 0x6a, 0x86, 0x2d);
    static readonly GUID IID_ID3D11Device    = new GUID(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height;
        public uint MipLevels, ArraySize;
        public uint Format;
        public uint SampleDescCount, SampleDescQuality;
        public uint Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch; public uint DepthPitch; }

    const uint D3D11_USAGE_STAGING = 3;
    const uint D3D11_CPU_ACCESS_READ = 0x20000;
    const uint D3D11_MAP_READ = 1;

    // com-abi: 公開ヘッダー通りの vtable スロット番号。
    const int DEV_CreateTexture2D = 5, DEV_GetImmediateContext = 40;
    const int TEX_GetDesc = 10;
    const int CTX_Map = 14, CTX_Unmap = 15, CTX_CopyResource = 47;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, out D3D11_TEXTURE2D_DESC desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr pInitialData, out IntPtr ppTexture2D);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetImmediateContextFn(IntPtr self, out IntPtr ppContext);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResourceFn(IntPtr self, IntPtr pDst, IntPtr pSrc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr pResource, uint Subresource, uint MapType, uint MapFlags, out D3D11_MAPPED_SUBRESOURCE pMapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr pResource, uint Subresource);

    /// <summary>
    /// 取り込みは対象自身の即時コンテキストを借りる。即時コンテキストはスレッド安全ではなく、
    /// このノードは対象の描画スレッドとは別のスレッドで走るため、横から触ると落ちる。
    /// 対象が多重スレッド保護を有効にしていれば <c>Enter</c>/<c>Leave</c> で守れるが、
    /// 既定では切れており、そのとき Enter/Leave は何も守らない。
    /// => 既定では取り込みの間だけ保護を入れ、終わったら元へ戻す。
    /// <paramref name="allowEnableProtection"/> を false にすると、対象の設定に触らず、
    /// 保護が無ければ断る。保護を入れている間は対象の描画呼び出しにロックの負荷が乗る。
    /// </summary>
    internal static NgolCaptureResult Capture(IntPtr swapChain, IExecutionContext ctx, bool allowEnableProtection, bool wantPng)
    {
        IntPtr backBuffer = IntPtr.Zero, device = IntPtr.Zero, context = IntPtr.Zero, staging = IntPtr.Zero;
        IntPtr multithread = IntPtr.Zero;
        bool entered = false, protectionTurnedOnHere = false;
        try
        {
            // 1. GetBuffer(0) -> backbuffer。Texture2D で取れなければ、より広い Resource で引き直す。
            var getBufferPtr = GetVtableSlot(swapChain, NgolCom.SC_GetBuffer);
            if (!LooksLikeCode(getBufferPtr)) return NgolCaptureResult.Failed("GetBuffer vtable slot doesn't look like code");
            var riidTex = IID_ID3D11Texture2D;
            var getBufferFn = Marshal.GetDelegateForFunctionPointer<GetBufferFn>(getBufferPtr);
            var hr = getBufferFn(swapChain, 0, ref riidTex, out backBuffer);
            if (hr != 0 || backBuffer == IntPtr.Zero)
            {
                var riidRes = IID_ID3D11Resource;
                var hr2 = getBufferFn(swapChain, 0, ref riidRes, out backBuffer);
                if (hr2 != 0 || backBuffer == IntPtr.Zero)
                    return NgolCaptureResult.Failed($"GetBuffer failed: Texture2D hr=0x{hr:X}, Resource hr=0x{hr2:X}");
            }

            // 2. GetDesc -> width/height/format
            var getDescPtr = GetVtableSlot(backBuffer, TEX_GetDesc);
            if (!LooksLikeCode(getDescPtr)) return NgolCaptureResult.Failed("GetDesc vtable slot doesn't look like code");
            Marshal.GetDelegateForFunctionPointer<GetDescFn>(getDescPtr)(backBuffer, out var desc);
            ctx.Logger.LogInfo($"[D3D11Capture] backbuffer {desc.Width}x{desc.Height} fmt={desc.Format}");
            if (!NgolCom.TryGetBmpChannelOrder(desc.Format, out var swapRedBlue))
                return NgolCaptureResult.Failed(
                    $"backbuffer format {desc.Format} cannot be written as a 32bpp BMP: {NgolCom.DescribeFormat(desc.Format)}");

            // 3. GetDevice
            var getDevicePtr = GetVtableSlot(swapChain, NgolCom.SC_GetDevice);
            if (!LooksLikeCode(getDevicePtr)) return NgolCaptureResult.Failed("GetDevice vtable slot doesn't look like code");
            var riidDev = NgolCom.IID_ID3D11Device;
            hr = Marshal.GetDelegateForFunctionPointer<GetDeviceFn>(getDevicePtr)(swapChain, ref riidDev, out device);
            if (hr != 0 || device == IntPtr.Zero) return NgolCaptureResult.Failed($"GetDevice failed hr=0x{hr:X}");

            // 4. CPU から読める複製先を作る
            var stagingDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                Format = desc.Format, SampleDescCount = 1, SampleDescQuality = 0,
                Usage = D3D11_USAGE_STAGING, BindFlags = 0, CPUAccessFlags = D3D11_CPU_ACCESS_READ, MiscFlags = 0,
            };
            var createTex2DPtr = GetVtableSlot(device, DEV_CreateTexture2D);
            if (!LooksLikeCode(createTex2DPtr)) return NgolCaptureResult.Failed("CreateTexture2D vtable slot doesn't look like code");
            hr = Marshal.GetDelegateForFunctionPointer<CreateTexture2DFn>(createTex2DPtr)(device, ref stagingDesc, IntPtr.Zero, out staging);
            if (hr != 0 || staging == IntPtr.Zero) return NgolCaptureResult.Failed($"CreateTexture2D(staging) failed hr=0x{hr:X}");

            // 5. GetImmediateContext
            var getCtxPtr = GetVtableSlot(device, DEV_GetImmediateContext);
            if (!LooksLikeCode(getCtxPtr)) return NgolCaptureResult.Failed("GetImmediateContext vtable slot doesn't look like code");
            Marshal.GetDelegateForFunctionPointer<GetImmediateContextFn>(getCtxPtr)(device, out context);
            if (context == IntPtr.Zero) return NgolCaptureResult.Failed("GetImmediateContext returned null");

            // 5-b. 即時コンテキストを借りてよいかを確かめる。ここを飛ばすと落ちる。
            var riidMt = NgolCom.IID_ID3D10Multithread;
            var qiPtr = GetVtableSlot(context, NgolCom.SlotQueryInterface);
            if (!LooksLikeCode(qiPtr)) return NgolCaptureResult.Failed("QueryInterface vtable slot doesn't look like code");
            var mtHr = Marshal.GetDelegateForFunctionPointer<NgolCom.QueryInterfaceFn>(qiPtr)(context, ref riidMt, out multithread);
            if (mtHr != 0 || multithread == IntPtr.Zero)
                return NgolCaptureResult.Failed(
                    $"the device context does not expose ID3D10Multithread (hr=0x{mtHr:X}), so the capture cannot be serialised against the render thread");

            bool isProtected = Marshal.GetDelegateForFunctionPointer<NgolCom.MtGetProtectedFn>(
                GetVtableSlot(multithread, NgolCom.MT_GetProtected))(multithread);
            if (!isProtected)
            {
                if (!allowEnableProtection)
                    return NgolCaptureResult.Failed(
                        "the target has D3D11 multithread protection off, so borrowing its immediate context from here " +
                        "would race with its render thread and can kill the process. " +
                        "allow_enable_multithread_protection=false was given, so the capture was refused instead of " +
                        "changing the target's protection setting");

                Marshal.GetDelegateForFunctionPointer<NgolCom.MtSetProtectedFn>(
                    GetVtableSlot(multithread, NgolCom.MT_SetProtected))(multithread, true);
                protectionTurnedOnHere = true;
                ctx.Logger.LogInfo("[D3D11Capture] multithread protection was off; turned it on for this capture");
            }

            Marshal.GetDelegateForFunctionPointer<NgolCom.MtEnterFn>(
                GetVtableSlot(multithread, NgolCom.MT_Enter))(multithread);
            entered = true;

            // 6. 複製
            var copyResPtr = GetVtableSlot(context, CTX_CopyResource);
            if (!LooksLikeCode(copyResPtr)) return NgolCaptureResult.Failed("CopyResource vtable slot doesn't look like code");
            Marshal.GetDelegateForFunctionPointer<CopyResourceFn>(copyResPtr)(context, staging, backBuffer);

            // 7. Map -> 画素を読む -> Unmap
            var mapPtr = GetVtableSlot(context, CTX_Map);
            if (!LooksLikeCode(mapPtr)) return NgolCaptureResult.Failed("Map vtable slot doesn't look like code");
            hr = Marshal.GetDelegateForFunctionPointer<MapFn>(mapPtr)(context, staging, 0, D3D11_MAP_READ, 0, out var mapped);
            if (hr != 0 || mapped.pData == IntPtr.Zero) return NgolCaptureResult.Failed($"Map failed hr=0x{hr:X}");

            var image = NgolCom.BuildImage(mapped.pData, (int)desc.Width, (int)desc.Height, mapped.RowPitch, swapRedBlue, wantPng);

            Marshal.GetDelegateForFunctionPointer<UnmapFn>(GetVtableSlot(context, CTX_Unmap))(context, staging, 0);

            return new NgolCaptureResult
            {
                Ok = true, Message = "ok", Bmp = image, ImageFormat = wantPng ? "png" : "bmp",
                Width = (int)desc.Width, Height = (int)desc.Height, Format = desc.Format,
            };
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[D3D11Capture] {ex}");
            return NgolCaptureResult.Failed($"exception: {ex.Message}");
        }
        finally
        {
            if (multithread != IntPtr.Zero)
            {
                // 入ったら必ず出る。保護を入れたのがこちらなら元へ戻す（対象の状態を残さない）。
                if (entered)
                    Marshal.GetDelegateForFunctionPointer<NgolCom.MtLeaveFn>(
                        GetVtableSlot(multithread, NgolCom.MT_Leave))(multithread);
                if (protectionTurnedOnHere)
                    Marshal.GetDelegateForFunctionPointer<NgolCom.MtSetProtectedFn>(
                        GetVtableSlot(multithread, NgolCom.MT_SetProtected))(multithread, false);
                Release(multithread);
            }
            Release(staging);
            Release(context);
            Release(device);
            Release(backBuffer);
        }
    }
}
