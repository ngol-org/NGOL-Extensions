using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

using GUID = NgolCom.GUID;
using GetDeviceFn = NgolCom.GetDeviceFn;
using GetBufferFn = NgolCom.GetBufferFn;

/// <summary>
/// D3D12 のスワップチェーンからバックバッファを読み出す。
/// D3D11 と違い ID3D12Resource は直接 Map できない（DEFAULT ヒープにあるため）ので、
/// コマンドリスト経由で READBACK ヒープのバッファへ写し、フェンスで完了を待ってから読む。
///
/// com-abi: vtable スロット番号は公開ヘッダー通り
/// （IDXGISwapChain / ID3D12Device / ID3D12GraphicsCommandList / ID3D12CommandQueue / ID3D12Fence）。
/// </summary>
internal static class D3D12Capture
{
    static IntPtr GetVtableSlot(IntPtr o, int s) => NgolCom.GetVtableSlot(o, s);
    static void Release(IntPtr o) => NgolCom.Release(o);


    static readonly GUID IID_ID3D12Resource        = new GUID(0x696442be, 0xa72e, 0x4059, 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad);
    static readonly GUID IID_ID3D12CommandAllocator = new GUID(0x6102dee4, 0xaf59, 0x4b09, 0xb9, 0x99, 0xb4, 0x4d, 0x73, 0xf0, 0x9b, 0x24);
    static readonly GUID IID_ID3D12CommandQueue     = new GUID(0x0ec870a6, 0x5d7e, 0x4c22, 0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed);
    static readonly GUID IID_ID3D12GraphicsCommandList = new GUID(0x5b160d0f, 0xac1b, 0x4185, 0x8b, 0xa8, 0xb3, 0xae, 0x42, 0xa5, 0xa4, 0x55);
    static readonly GUID IID_ID3D12Fence            = new GUID(0x0a753dcf, 0xc4d8, 0x4b91, 0xad, 0xf6, 0xbe, 0x5a, 0x60, 0xd9, 0x5a, 0x76);

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_RESOURCE_DESC
    {
        public uint Dimension;
        public ulong Alignment;
        public ulong Width;
        public uint Height;
        public ushort DepthOrArraySize;
        public ushort MipLevels;
        public uint Format;
        public uint SampleDescCount, SampleDescQuality;
        public uint Layout;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_HEAP_PROPERTIES
    {
        public uint Type;
        public uint CPUPageProperty;
        public uint MemoryPoolPreference;
        public uint CreationNodeMask;
        public uint VisibleNodeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_RANGE { public ulong Begin; public ulong End; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_COMMAND_QUEUE_DESC { public uint Type; public int Priority; public uint Flags; public uint NodeMask; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_TEXTURE_COPY_LOCATION_SUBRESOURCE
    {
        public IntPtr pResource;
        public uint Type; // 0 = SUBRESOURCE_INDEX for source(texture), 1 = PLACED_FOOTPRINT for dest(buffer)
        public uint SubresourceIndex; // used when Type==0
        // PLACED_FOOTPRINT layout follows when Type==1 (we build a second struct for that case)
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_SUBRESOURCE_FOOTPRINT { public uint Format; public uint Width, Height, Depth; public uint RowPitch; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_PLACED_SUBRESOURCE_FOOTPRINT { public ulong Offset; public D3D12_SUBRESOURCE_FOOTPRINT Footprint; }

    [StructLayout(LayoutKind.Sequential)]
    struct COPY_LOCATION_DEST
    {
        public IntPtr pResource;
        public uint Type; // 1 = PLACED_FOOTPRINT
        public D3D12_PLACED_SUBRESOURCE_FOOTPRINT PlacedFootprint;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COPY_LOCATION_SRC
    {
        public IntPtr pResource;
        public uint Type; // 0 = SUBRESOURCE_INDEX
        // D3D12_TEXTURE_COPY_LOCATIONのunionはPlacedFootprint(ulongを含む)の8バイトアライン
        // 要求により offset 16 から開始する(SubresourceIndexアームを使う場合も同じ)。
        // Type(offset 8-11)の直後に4バイトの明示パディングが必要。
        uint _pad;
        public uint SubresourceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_RESOURCE_TRANSITION_BARRIER
    {
        public IntPtr pResource;
        public uint Subresource;
        public uint StateBefore;
        public uint StateAfter;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D12_RESOURCE_BARRIER
    {
        public uint Type; // 0 = TRANSITION
        public uint Flags;
        public D3D12_RESOURCE_TRANSITION_BARRIER Transition;
        // union tail padding not needed since Transition is the largest/only variant we use
    }

    const uint D3D12_HEAP_TYPE_READBACK = 3;
    const uint D3D12_RESOURCE_DIMENSION_BUFFER = 1;
    const uint D3D12_TEXTURE_LAYOUT_ROW_MAJOR = 1;
    const uint D3D12_RESOURCE_STATE_COPY_DEST = 0x400;
    const uint D3D12_RESOURCE_STATE_PRESENT = 0;
    const uint D3D12_RESOURCE_STATE_COPY_SOURCE = 0x800;
    const uint D3D12_COMMAND_LIST_TYPE_DIRECT = 0;
    const uint D3D12_RESOURCE_BARRIER_TYPE_TRANSITION = 0;
    const uint D3D12_RESOURCE_BARRIER_FLAG_NONE = 0;
    const uint D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX = 0;
    const uint D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT = 1;

    // --- vtable slot indices (stable D3D12 COM ABI) ---
    const int RES_GetDesc = 10;
    const int DEV_CreateCommandQueue = 8, DEV_CreateCommandAllocator = 9, DEV_CreateCommandList = 12,
              DEV_CreateCommittedResource = 27, DEV_CreateFence = 36, DEV_GetCopyableFootprints = 38;
    const int ALLOC_Reset = 8;
    const int CL_Close = 9, CL_ResourceBarrier = 26, CL_CopyTextureRegion = 16;
    const int Q_ExecuteCommandLists = 10, Q_Signal = 14;
    const int FENCE_GetCompletedValue = 8, FENCE_SetEventOnCompletion = 9;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescResFn(IntPtr self, out D3D12_RESOURCE_DESC desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateCommandQueueFn(IntPtr self, ref D3D12_COMMAND_QUEUE_DESC desc, ref GUID riid, out IntPtr ppQueue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateCommandAllocatorFn(IntPtr self, uint type, ref GUID riid, out IntPtr ppAllocator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateCommandListFn(IntPtr self, uint nodeMask, uint type, IntPtr pAllocator, IntPtr pInitialState, ref GUID riid, out IntPtr ppList);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateCommittedResourceFn(IntPtr self, ref D3D12_HEAP_PROPERTIES heapProps, uint heapFlags, ref D3D12_RESOURCE_DESC desc, uint initialState, IntPtr pOptimizedClearValue, ref GUID riid, out IntPtr ppResource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFenceFn(IntPtr self, ulong initialValue, uint flags, ref GUID riid, out IntPtr ppFence);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetCopyableFootprintsFn(
        IntPtr self, ref D3D12_RESOURCE_DESC pResourceDesc, uint FirstSubresource, uint NumSubresources, ulong BaseOffset,
        out D3D12_PLACED_SUBRESOURCE_FOOTPRINT pLayouts, out uint pNumRows, out ulong pRowSizeInBytes, out ulong pTotalBytes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResetAllocatorFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CloseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ResourceBarrierFn(IntPtr self, uint numBarriers, ref D3D12_RESOURCE_BARRIER barriers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyTextureRegionFn(IntPtr self, ref COPY_LOCATION_DEST dst, uint dstX, uint dstY, uint dstZ, ref COPY_LOCATION_SRC src, IntPtr pSrcBox);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ExecuteCommandListsFn(IntPtr self, uint numLists, ref IntPtr ppLists);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SignalFn(IntPtr self, IntPtr fence, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate ulong GetCompletedValueFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetEventOnCompletionFn(IntPtr self, ulong value, IntPtr hEvent);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapResFn(IntPtr self, uint subresource, IntPtr pReadRange, out IntPtr ppData);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapResFn(IntPtr self, uint subresource, IntPtr pWrittenRange);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string lpName);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    /// <param name="stateBefore">
    /// 写す直前のバックバッファの状態。既定は PRESENT/COMMON。
    /// ここが実際とずれていると Close() が失敗する。
    /// </param>
    internal static NgolCaptureResult Capture(IntPtr swapChain, uint stateBefore, IExecutionContext ctx, bool wantPng)
    {
        var step = "start";
        IntPtr backBuffer = IntPtr.Zero, device = IntPtr.Zero, readback = IntPtr.Zero;
        IntPtr allocator = IntPtr.Zero, cmdList = IntPtr.Zero, queue = IntPtr.Zero, fence = IntPtr.Zero, evt = IntPtr.Zero;

        try
        {
            step = "GetBuffer";
            var getBufferPtr = GetVtableSlot(swapChain, NgolCom.SC_GetBuffer);
            var riidRes = IID_ID3D12Resource;
            var hr = Marshal.GetDelegateForFunctionPointer<GetBufferFn>(getBufferPtr)(swapChain, 0, ref riidRes, out backBuffer);
            if (hr != 0 || backBuffer == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"GetBuffer hr=0x{hr:X}");

            step = "GetDesc";
            var desc = default(D3D12_RESOURCE_DESC);
            Marshal.GetDelegateForFunctionPointer<GetDescResFn>(GetVtableSlot(backBuffer, RES_GetDesc))(backBuffer, out desc);
            ctx.Logger.LogInfo($"[D3D12Capture] backbuffer {desc.Width}x{desc.Height} fmt={desc.Format}");
            if (!NgolCom.TryGetBmpChannelOrder(desc.Format, out var swapRedBlue))
                return NgolCaptureResult.Failed($"failed at '{step}': " +
                    $"backbuffer format {desc.Format} cannot be written as a 32bpp BMP: {NgolCom.DescribeFormat(desc.Format)}");

            step = "GetDevice";
            var riidDev = NgolCom.IID_ID3D12Device;
            hr = Marshal.GetDelegateForFunctionPointer<GetDeviceFn>(GetVtableSlot(swapChain, NgolCom.SC_GetDevice))(swapChain, ref riidDev, out device);
            if (hr != 0 || device == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"GetDevice hr=0x{hr:X}");

            step = "GetCopyableFootprints";
            Marshal.GetDelegateForFunctionPointer<GetCopyableFootprintsFn>(GetVtableSlot(device, DEV_GetCopyableFootprints))(
                device, ref desc, 0, 1, 0, out var footprint, out var numRows, out var rowSizeInBytes, out var totalBytes);
            ctx.Logger.LogInfo($"[D3D12Capture] footprint offset={footprint.Offset} fmt={footprint.Footprint.Format} w={footprint.Footprint.Width} h={footprint.Footprint.Height} rowPitch={footprint.Footprint.RowPitch} numRows={numRows} rowSizeInBytes={rowSizeInBytes} totalBytes={totalBytes}");

            step = "CreateCommittedResource(readback)";
            var heapProps = new D3D12_HEAP_PROPERTIES { Type = D3D12_HEAP_TYPE_READBACK };
            var bufDesc = new D3D12_RESOURCE_DESC
            {
                Dimension = D3D12_RESOURCE_DIMENSION_BUFFER, Alignment = 0, Width = totalBytes, Height = 1,
                DepthOrArraySize = 1, MipLevels = 1, Format = 0, SampleDescCount = 1, SampleDescQuality = 0,
                Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR, Flags = 0,
            };
            var riidResObj = IID_ID3D12Resource;
            hr = Marshal.GetDelegateForFunctionPointer<CreateCommittedResourceFn>(GetVtableSlot(device, DEV_CreateCommittedResource))(
                device, ref heapProps, 0, ref bufDesc, D3D12_RESOURCE_STATE_COPY_DEST, IntPtr.Zero, ref riidResObj, out readback);
            if (hr != 0 || readback == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"CreateCommittedResource hr=0x{hr:X}");

            step = "CreateCommandAllocator";
            var riidAlloc = IID_ID3D12CommandAllocator;
            hr = Marshal.GetDelegateForFunctionPointer<CreateCommandAllocatorFn>(GetVtableSlot(device, DEV_CreateCommandAllocator))(
                device, D3D12_COMMAND_LIST_TYPE_DIRECT, ref riidAlloc, out allocator);
            if (hr != 0 || allocator == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"CreateCommandAllocator hr=0x{hr:X}");

            step = "CreateCommandList";
            var riidList = IID_ID3D12GraphicsCommandList;
            hr = Marshal.GetDelegateForFunctionPointer<CreateCommandListFn>(GetVtableSlot(device, DEV_CreateCommandList))(
                device, 0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, IntPtr.Zero, ref riidList, out cmdList);
            if (hr != 0 || cmdList == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"CreateCommandList hr=0x{hr:X}");

            step = "ResourceBarrier(PRESENT->COPY_SOURCE)";
            var barrierToSrc = new D3D12_RESOURCE_BARRIER
            {
                Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION, Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE,
                Transition = new D3D12_RESOURCE_TRANSITION_BARRIER { pResource = backBuffer, Subresource = 0xFFFFFFFF, StateBefore = stateBefore, StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE },
            };
            Marshal.GetDelegateForFunctionPointer<ResourceBarrierFn>(GetVtableSlot(cmdList, CL_ResourceBarrier))(cmdList, 1, ref barrierToSrc);

            step = "CopyTextureRegion";
            var dst = new COPY_LOCATION_DEST { pResource = readback, Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT, PlacedFootprint = footprint };
            var src = new COPY_LOCATION_SRC { pResource = backBuffer, Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, SubresourceIndex = 0 };
            Marshal.GetDelegateForFunctionPointer<CopyTextureRegionFn>(GetVtableSlot(cmdList, CL_CopyTextureRegion))(cmdList, ref dst, 0, 0, 0, ref src, IntPtr.Zero);

            step = "ResourceBarrier(COPY_SOURCE->PRESENT)";
            var barrierBack = new D3D12_RESOURCE_BARRIER
            {
                Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION, Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE,
                Transition = new D3D12_RESOURCE_TRANSITION_BARRIER { pResource = backBuffer, Subresource = 0xFFFFFFFF, StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE, StateAfter = stateBefore },
            };
            Marshal.GetDelegateForFunctionPointer<ResourceBarrierFn>(GetVtableSlot(cmdList, CL_ResourceBarrier))(cmdList, 1, ref barrierBack);

            step = "Close command list";
            hr = Marshal.GetDelegateForFunctionPointer<CloseFn>(GetVtableSlot(cmdList, CL_Close))(cmdList);
            if (hr != 0) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"CommandList.Close hr=0x{hr:X}");

            step = "CreateCommandQueue";
            var qDesc = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE_DIRECT, Priority = 0, Flags = 0, NodeMask = 0 };
            var riidQ = IID_ID3D12CommandQueue;
            hr = Marshal.GetDelegateForFunctionPointer<CreateCommandQueueFn>(GetVtableSlot(device, DEV_CreateCommandQueue))(device, ref qDesc, ref riidQ, out queue);
            if (hr != 0 || queue == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"CreateCommandQueue hr=0x{hr:X}");

            step = "ExecuteCommandLists";
            Marshal.GetDelegateForFunctionPointer<ExecuteCommandListsFn>(GetVtableSlot(queue, Q_ExecuteCommandLists))(queue, 1, ref cmdList);

            step = "CreateFence";
            var riidFence = IID_ID3D12Fence;
            hr = Marshal.GetDelegateForFunctionPointer<CreateFenceFn>(GetVtableSlot(device, DEV_CreateFence))(device, 0, 0, ref riidFence, out fence);
            if (hr != 0 || fence == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"CreateFence hr=0x{hr:X}");

            step = "Queue.Signal";
            hr = Marshal.GetDelegateForFunctionPointer<SignalFn>(GetVtableSlot(queue, Q_Signal))(queue, fence, 1);
            if (hr != 0) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"Queue.Signal hr=0x{hr:X}");

            step = "wait fence";
            evt = CreateEventW(IntPtr.Zero, false, false, null);
            var completed = Marshal.GetDelegateForFunctionPointer<GetCompletedValueFn>(GetVtableSlot(fence, FENCE_GetCompletedValue))(fence);
            if (completed < 1)
            {
                Marshal.GetDelegateForFunctionPointer<SetEventOnCompletionFn>(GetVtableSlot(fence, FENCE_SetEventOnCompletion))(fence, 1, evt);
                WaitForSingleObject(evt, 5000);
            }

            step = "Map(readback)";
            hr = Marshal.GetDelegateForFunctionPointer<MapResFn>(GetVtableSlot(readback, 8))(readback, 0, IntPtr.Zero, out var pData);
            if (hr != 0 || pData == IntPtr.Zero) return NgolCaptureResult.Failed($"failed at \'{step}\': " + $"Map(readback) hr=0x{hr:X}");

            var image = NgolCom.BuildImage(pData, (int)desc.Width, (int)desc.Height, footprint.Footprint.RowPitch, swapRedBlue, wantPng);

            Marshal.GetDelegateForFunctionPointer<UnmapResFn>(GetVtableSlot(readback, 9))(readback, 0, IntPtr.Zero);

            return new NgolCaptureResult
            {
                Ok = true, Message = "ok", Bmp = image, ImageFormat = wantPng ? "png" : "bmp",
                Width = (int)desc.Width, Height = (int)desc.Height, Format = desc.Format,
            };
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"[D3D12Capture] step={step} {ex}");
            return NgolCaptureResult.Failed($"exception at step '{step}': {ex.Message}");
        }
        finally
        {
            if (evt != IntPtr.Zero) CloseHandle(evt);
            Release(fence); Release(queue); Release(cmdList); Release(allocator);
            Release(readback); Release(device); Release(backBuffer);
        }
    }

}
