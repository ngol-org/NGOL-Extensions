using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 対象アプリのウィンドウの中に D3D12 で描く。対象アプリは D3D を一切持たなくてよい。
///
/// 描画先は対象のクライアント領域を覆う子ウィンドウ。対象が自分でウィンドウを塗り直す場合、
/// 同じ HWND にスワップチェーンを載せると取り合いになる。
///
/// 駆動元について:
///   NGOL が自前のスレッドで一定間隔に回っている環境（direct mode）では、更新の速さを
///   決めるのは設定値であり、描画の周期とは無関係になる。take_tick_source を true にすると
///   駆動元をこのノード側へ移し、この描画が待つ周期でそのまま Tick を回す。
///   ホストが自分の更新に合わせて Tick を呼んでいる環境では不要（既定は false）。
///
/// com-abi: vtable のスロット番号と IID は Windows SDK のヘッダーから取得したもの。
/// </summary>
[NodeType("ngol.gfx.draw_cube", "Graphics", "Draw Cube In Window",
    Version = "0.5.0",
    Description =
        "Draw a rotating cube with D3D12 inside the target application's window, without the target having any "
      + "graphics code of its own. A child window is created over the target's client area, and the device, the "
      + "swapchain and the shaders all belong to this node - nothing of the target's is borrowed or hooked, so "
      + "whatever graphics API the target uses, or whether it draws at all, does not matter. The cost of that "
      + "independence is that the picture sits over the target's frame rather than being part of it: a target "
      + "that presents fullscreen exclusive will cover it. ngol.gfx.overlay_dice is the opposite trade - it "
      + "draws into the target's own backbuffer, so it composites properly but requires the target to present "
      + "through Direct3D 11. Set take_tick_source when NGOL is running on its own timer and the drawing should "
      + "set the pace instead. Shaders are built at run time through d3dcompiler_47.dll.")]
[NodePort("enabled",          PortDirection.Input,  "boolean", Description = "true starts drawing, false stops it")]
[NodePort("resolution",       PortDirection.Input,  "number",  Description = "Side of the square render surface in pixels. Default 0 = the largest square that fits the target's client area. Larger values are clamped to that")]
[NodePort("take_tick_source", PortDirection.Input,  "boolean", Description = "Move NGOL's update driver onto this node so the drawing sets the pace. Only needed when NGOL runs on its own timer; leave false when the host already drives Tick. Asking for it on a host that drives Tick itself is refused and reported, because two drivers on the same update path is what breaks. Default false")]
[NodePort("enable_debug_layer", PortDirection.Input, "boolean", Description = "Turn on the D3D12 debug layer before the device is created, which turns invalid calls into reported errors instead of an instant exit. Default false: the setting is process-wide, cannot be turned off again, and in a process that also uses another graphics API it can remove that API's device. Only set it while debugging this node in a process that has nothing else to lose")]
[NodePort("status",           PortDirection.Output, "string",  Description = "What happened, or which step the setup failed at. Taking the tick source is asked for on another thread, so this cannot say whether it succeeded: run the node again while it draws and the answer comes back as driver=node or driver=core")]
public sealed class D3D12CubeNode : INode
{
    // 属性の Version と同じ値にしておく。こちらは記録と status に出るので、
    // ずれると「どの版が動いているか」を記録から読めなくなる。
    private const string Version = "0.5.0";

    public void Execute(IExecutionContext ctx)
    {
        var enabled = ctx.GetPortValue("enabled") as bool? ?? true;
        var takeTickSource = ctx.GetPortValue("take_tick_source") as bool? ?? false;
        D3D12Window.RequestedSize = ctx.GetPortValue("resolution") is double r ? (int)r : 0;
        D3D12Window.EnableDebugLayer = ctx.GetPortValue("enable_debug_layer") as bool? ?? false;

        if (!enabled)
        {
            D3D12Window.CancelRegistration();
            ctx.SetPortValue("status", "stop requested");
            return;
        }

        if (D3D12Window.InUse)
        {
            ctx.SetPortValue("status",
                $"already running (stop it first); driver={(NgolTickSource.Active ? "node" : "core")}");
            return;
        }

        // 1 つの HWND に載せられるスワップチェーンは 1 つ。前回の解放を待たずに張り直すと
        // 2 つ目を作ることになるため、Execute では作らず OnUpdate 側で組み立てる。
        D3D12Window.Arm();
        ctx.Logger.LogInfo($"[D3D12Cube v{Version}] arming; init happens on the update thread");

        D3D12Window.Register(ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () => D3D12Window.Frame(ctx),
            OnStop = () => D3D12Window.Shutdown(ctx),
        }));

        // 駆動元を移すのは頼まれたときだけ。ホストが既に Tick を呼んでいる環境で奪うと、
        // 同じ役目の機構が 2 つになる。
        // Execute はその駆動スレッド自身の上で走ることがあるため、掴む操作は別スレッドで行う。
        if (takeTickSource)
            NgolTickSource.RequestBind(ctx, m => ctx.Logger.LogInfo("[D3D12Cube] " + m));

        // 掴めたかどうかは別スレッドの結果なので、ここでは頼んだことしか言えない。
        // 実際にどちらが回しているかは、走っている間にもう一度実行すると返る。
        ctx.SetPortValue("status", takeTickSource
            ? $"armed (v{Version}); the tick source was requested - run this again to see which driver ended up turning"
            : $"armed (v{Version})");
    }
}

/// <summary>
/// 子ウィンドウと D3D12 の一式。生成も破棄も更新スレッドから行う。
/// </summary>
internal static class D3D12Window
{
    // --- Win32 ---
    private delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, char[] buf, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEventW(IntPtr attr, bool manualReset, bool initial, string name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const int GWL_STYLE = -16;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint PM_REMOVE = 0x0001;

    // --- D3D12 / DXGI ---
    [DllImport("d3d12.dll")] private static extern int D3D12CreateDevice(IntPtr adapter, uint minFeatureLevel, ref D3D12Com.GUID riid, out IntPtr device);
    [DllImport("d3d12.dll")] private static extern int D3D12GetDebugInterface(ref D3D12Com.GUID riid, out IntPtr debug);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory2(uint flags, ref D3D12Com.GUID riid, out IntPtr factory);

    private static readonly D3D12Com.GUID IID_ID3D12Device = new D3D12Com.GUID(0x189819f1, 0x1db6, 0x4b57, 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7);
    private static readonly D3D12Com.GUID IID_ID3D12CommandQueue = new D3D12Com.GUID(0x0ec870a6, 0x5d7e, 0x4c22, 0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed);
    private static readonly D3D12Com.GUID IID_ID3D12CommandAllocator = new D3D12Com.GUID(0x6102dee4, 0xaf59, 0x4b09, 0xb9, 0x99, 0xb4, 0x4d, 0x73, 0xf0, 0x9b, 0x24);
    private static readonly D3D12Com.GUID IID_ID3D12GraphicsCommandList = new D3D12Com.GUID(0x5b160d0f, 0xac1b, 0x4185, 0x8b, 0xa8, 0xb3, 0xae, 0x42, 0xa5, 0xa4, 0x55);
    private static readonly D3D12Com.GUID IID_ID3D12Fence = new D3D12Com.GUID(0x0a753dcf, 0xc4d8, 0x4b91, 0xad, 0xf6, 0xbe, 0x5a, 0x60, 0xd9, 0x5a, 0x76);
    private static readonly D3D12Com.GUID IID_ID3D12Resource = new D3D12Com.GUID(0x696442be, 0xa72e, 0x4059, 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad);
    private static readonly D3D12Com.GUID IID_ID3D12DescriptorHeap = new D3D12Com.GUID(0x8efb471d, 0x616c, 0x4f49, 0x90, 0xf7, 0x12, 0x7b, 0xb7, 0x63, 0xfa, 0x51);
    private static readonly D3D12Com.GUID IID_IDXGIFactory2 = new D3D12Com.GUID(0x50c83a1c, 0xe072, 0x4c48, 0x87, 0xb0, 0x36, 0x30, 0xfa, 0x36, 0xa6, 0xd0);
    private static readonly D3D12Com.GUID IID_IDXGISwapChain1 = new D3D12Com.GUID(0x790a45f7, 0x0d42, 0x4876, 0x98, 0x3a, 0x0a, 0x55, 0xcf, 0xe6, 0xf4, 0xaa);

    // vtable スロット（SDK ヘッダーの vtable 定義順から取得）
    private const int DEV_CreateCommandQueue = 8, DEV_CreateCommandAllocator = 9, DEV_CreateCommandList = 12,
                      DEV_CreateDescriptorHeap = 14, DEV_CreateRenderTargetView = 20, DEV_CreateFence = 36;
    private const int HEAP_GetCPUDescriptorHandleForHeapStart = 9;
    private const int ALLOC_Reset = 8;
    private const int CL_Close = 9, CL_Reset = 10, CL_ResourceBarrier = 26, CL_OMSetRenderTargets = 46, CL_ClearRenderTargetView = 48;
    private const int Q_ExecuteCommandLists = 10, Q_Signal = 14;
    private const int FENCE_GetCompletedValue = 8, FENCE_SetEventOnCompletion = 9;
    private const int FAC_CreateSwapChainForHwnd = 15, FAC_MakeWindowAssociation = 8;
    private const int SC_Present = 8, SC_GetBuffer = 9;

    [StructLayout(LayoutKind.Sequential)] private struct MEMORY_BASIC_INFORMATION
    { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public int __align; public IntPtr RegionSize; public uint State, Protect, Type; }
    [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr addr, out MEMORY_BASIC_INFORMATION buf, UIntPtr len);

    [StructLayout(LayoutKind.Sequential)] private struct D3D12_COMMAND_QUEUE_DESC { public uint Type; public int Priority; public uint Flags, NodeMask; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D12_DESCRIPTOR_HEAP_DESC { public uint Type, NumDescriptors, Flags, NodeMask; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D12_RESOURCE_TRANSITION_BARRIER { public IntPtr pResource; public uint Subresource, StateBefore, StateAfter; }
    [StructLayout(LayoutKind.Sequential)] private struct D3D12_RESOURCE_BARRIER { public uint Type, Flags; public D3D12_RESOURCE_TRANSITION_BARRIER Transition; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality;
        public uint BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    private const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
    private const uint DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;
    private const uint D3D_FEATURE_LEVEL_11_0 = 0xb000;
    private const uint D3D12_COMMAND_LIST_TYPE_DIRECT = 0;
    // CBV_SRV_UAV=0 / SAMPLER=1 / RTV=2 / DSV=3（d3d12.h）
    private const uint D3D12_DESCRIPTOR_HEAP_TYPE_RTV = 2;
    private const uint D3D12_RESOURCE_STATE_PRESENT = 0;
    private const uint D3D12_RESOURCE_STATE_RENDER_TARGET = 0x4;
    private const uint DXGI_MWA_NO_WINDOW_CHANGES = 1;

    // com-abi: ID3D12Debug vtable slot 3 EnableDebugLayer（d3d12sdklayers.h の vtable 定義順）
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void EnableDebugLayerFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCommandQueueFn(IntPtr self, ref D3D12_COMMAND_QUEUE_DESC d, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCommandAllocatorFn(IntPtr self, uint type, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCommandListFn(IntPtr self, uint node, uint type, IntPtr alloc, IntPtr pso, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDescriptorHeapFn(IntPtr self, ref D3D12_DESCRIPTOR_HEAP_DESC d, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CreateRenderTargetViewFn(IntPtr self, IntPtr res, IntPtr desc, IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFenceFn(IntPtr self, ulong initial, uint flags, ref D3D12Com.GUID riid, out IntPtr o);
    // C ヘッダーは構造体を戻り値で宣言しているが、実装は隠し出力ポインタで返す。
    // 宣言どおり「戻り値で受ける」形にすると、この呼び出しでプロセスごと落ちる。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetCpuHandleFn(IntPtr self, out IntPtr ret);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ResetAllocFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ResetListFn(IntPtr self, IntPtr alloc, IntPtr pso);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CloseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BarrierFn(IntPtr self, uint num, ref D3D12_RESOURCE_BARRIER b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void OMSetRenderTargetsFn(IntPtr self, uint num, ref IntPtr rtv, int single, IntPtr dsv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearRtvFn(IntPtr self, IntPtr handle, ref float color, uint numRects, IntPtr rects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ExecuteCommandListsFn(IntPtr self, uint num, ref IntPtr lists);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SignalFn(IntPtr self, IntPtr fence, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong GetCompletedValueFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetEventOnCompletionFn(IntPtr self, ulong value, IntPtr evt);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSwapChainForHwndFn(IntPtr self, IntPtr queue, IntPtr hwnd, ref DXGI_SWAP_CHAIN_DESC1 d, IntPtr fs, IntPtr restrict, out IntPtr sc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MakeWindowAssociationFn(IntPtr self, IntPtr hwnd, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentFn(IntPtr self, uint sync, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferFn(IntPtr self, uint i, ref D3D12Com.GUID riid, out IntPtr o);

    private const int BufferCount = 2;

    private static volatile bool s_armed;
    private static bool s_initialized;
    private static string s_failure;
    private static IPersistentRegistration s_registration;

    private static WndProcFn s_wndProc;   // GC に回収されると WndProc が消える
    private static IntPtr s_parent, s_child;
    private static IntPtr s_prevParentStyle;
    private static IntPtr s_device, s_queue, s_factory, s_swapChain, s_rtvHeap, s_alloc, s_cmdList, s_fence, s_fenceEvent;
    private static D3D12Cube.Pipeline s_pipeline;
    private static readonly IntPtr[] s_backBuffers = new IntPtr[BufferCount];
    private static IntPtr s_rtvStart;
    private static uint s_rtvStride;
    private static int s_width, s_height, s_frameIndex;
    private static int s_childLeft, s_childTop, s_requestedSize;

    /// <summary>描画面の一辺。0 なら既定値を使う。組み立ては更新スレッドで行うのでここに預ける。</summary>
    internal static int RequestedSize { set { s_requestedSize = value; } }

    /// <summary>
    /// デバッグ層を有効にするか。既定は無効。
    /// 有効化はプロセス全体に効き、無効へ戻す口が無い。同じプロセスに別の描画 API が
    /// 居る場合、そちらのデバイスが除去されることがある。組み立ては更新スレッドで
    /// 行うのでここに預ける。
    /// </summary>
    internal static bool EnableDebugLayer { set { s_enableDebugLayer = value; } }
    private static bool s_enableDebugLayer;
    private static ulong s_fenceValue;
    private static long s_frames;
    private static DateTime s_lastReport, s_startedAt, s_lastFpsAt;
    private static string s_fpsText = "";

    private const double RadiansPerSecond = 0.8;

    internal static bool InUse => s_armed || s_initialized;

    internal static void Arm() { s_failure = null; s_armed = true; }

    internal static void Register(IPersistentRegistration reg) => s_registration = reg;

    /// <summary>
    /// 停止は登録の取り消しで行う。取り消すと更新の呼び出しは止まり、次の更新で OnStop が
    /// ホストのメインスレッドから呼ばれる。作った資源はそこで解放する。
    /// 取り消しはバックグラウンドスレッドから呼んでよい。
    /// </summary>
    internal static void CancelRegistration() => s_registration?.Cancel();

    internal static void Frame(IExecutionContext ctx)
    {
        if (!s_armed) return;

        if (!s_initialized)
        {
            if (s_failure != null) return;      // 一度失敗したら毎フレーム試さない
            if (!Initialize(ctx)) return;
        }
        else if (SurfaceOutOfDate())
        {
            // 親の大きさが変わった。ウィンドウメッセージは拾わず、毎周この 1 回の問い合わせで見る。
            // 対象のウィンドウプロシージャに手を入れずに済み、戻す約束も増えない。
            Rebuild(ctx);
            return;
        }

        PumpOwnMessages();
        Render(ctx);
    }

    /// <summary>
    /// 親のクライアント領域から、描画面の一辺と左上位置を決める。
    /// 描画面は常に正方形にする。縦横比が 1 なら投影の歪みが入らないので、
    /// 別の環境で出した絵と並べても形がそのまま比べられる。
    /// 既定はクライアント領域に入る最大の正方形で、指定があればその一辺を使う。
    /// </summary>
    private static void ComputeSurface(out int side, out int left, out int top)
    {
        GetClientRect(s_parent, out var rc);
        var clientW = Math.Max(rc.Right - rc.Left, 8);
        var clientH = Math.Max(rc.Bottom - rc.Top, 8);

        var want = s_requestedSize > 0 ? s_requestedSize : Math.Min(clientW, clientH);
        side = Math.Max(Math.Min(want, Math.Min(clientW, clientH)), 8);
        left = (clientW - side) / 2;
        top = (clientH - side) / 2;
    }

    private static bool SurfaceOutOfDate()
    {
        if (s_parent == IntPtr.Zero) return false;
        ComputeSurface(out var side, out var left, out var top);
        return side != s_width || left != s_childLeft || top != s_childTop;
    }

    /// <summary>
    /// 描画面を作り直す。スワップチェーンだけを差し替えず、確認済みの組み立てと後始末を
    /// そのまま通す。作り直しは大きさが変わった周だけなので、頻度で困ることはない。
    /// </summary>
    private static void Rebuild(IExecutionContext ctx)
    {
        var hadTickSource = NgolTickSource.Active;

        Shutdown(ctx);
        Arm();

        if (hadTickSource)
            NgolTickSource.RequestBind(ctx, m => ctx.Logger.LogInfo("[D3D12Cube] " + m));
    }

    private static string s_diagPath;

    private static void Diag(string what)
    {
        try
        {
            if (s_diagPath == null)
            {
                var dir = System.IO.Path.GetDirectoryName(typeof(D3D12Window).Assembly.Location);
                s_diagPath = System.IO.Path.Combine(string.IsNullOrEmpty(dir) ? System.IO.Path.GetTempPath() : dir, "d3d12-diag.log");
            }
            System.IO.File.AppendAllText(s_diagPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + what + Environment.NewLine);
        }
        catch { }
    }

    private static bool Initialize(IExecutionContext ctx)
    {
        var step = "find parent window";
        Diag("--- Initialize start ---");
        try
        {
            s_parent = FindOwnMainWindow();
            if (s_parent == IntPtr.Zero) return Fail(ctx, step, "no visible top-level window");

            ComputeSurface(out var side, out var left, out var top);
            s_width = s_height = side;
            s_childLeft = left;
            s_childTop = top;

            step = "child window";

            Diag("step: child window");
            // 親が子の領域を塗り潰さないようにする。停止時に戻す。
            s_prevParentStyle = GetWindowLongPtr(s_parent, GWL_STYLE);
            SetWindowLongPtr(s_parent, GWL_STYLE, (IntPtr)(s_prevParentStyle.ToInt64() | WS_CLIPCHILDREN));

            var hInst = GetModuleHandleW(null);
            s_wndProc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = hInst,
                lpszClassName = "NgolD3D12Surface",
            };
            RegisterClassExW(ref wc);   // 既に登録済みでも続行する

            s_child = CreateWindowExW(0, "NgolD3D12Surface", "", WS_CHILD | WS_VISIBLE,
                                      s_childLeft, s_childTop, s_width, s_height, s_parent, IntPtr.Zero, hInst, IntPtr.Zero);
            if (s_child == IntPtr.Zero) return Fail(ctx, step, "CreateWindowExW failed err=" + Marshal.GetLastWin32Error());

            // 有効にするならデバイス生成より前でなければ効かない。
            // 頼まれたときだけ有効にする。有効化はプロセス全体に効き、戻す口が無いので、
            // 同じプロセスの他の描画 API のデバイスを巻き添えにしうる。
            if (s_enableDebugLayer)
            {
                var iidDebug = new D3D12Com.GUID(0x344488b7, 0x6846, 0x474b, 0xb9, 0x89, 0xf0, 0x27, 0x44, 0x82, 0x45, 0xe0);
                if (D3D12GetDebugInterface(ref iidDebug, out var dbg) == 0 && dbg != IntPtr.Zero)
                {
                    Call<EnableDebugLayerFn>(dbg, 3)(dbg);
                    Diag("debug layer enabled");
                }
            }

            step = "D3D12CreateDevice";

            Diag("step: D3D12CreateDevice");
            var iidDev = IID_ID3D12Device;
            var hr = D3D12CreateDevice(IntPtr.Zero, D3D_FEATURE_LEVEL_11_0, ref iidDev, out s_device);
            if (hr != 0 || s_device == IntPtr.Zero) return Fail(ctx, step, $"hr=0x{hr:X}");

            step = "CreateCommandQueue";

            Diag("step: CreateCommandQueue");
            var qd = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE_DIRECT };
            var iidQ = IID_ID3D12CommandQueue;
            hr = Call<CreateCommandQueueFn>(s_device, DEV_CreateCommandQueue)(s_device, ref qd, ref iidQ, out s_queue);
            if (hr != 0) return Fail(ctx, step, $"hr=0x{hr:X}");

            step = "CreateDXGIFactory2";

            Diag("step: CreateDXGIFactory2");
            var iidF = IID_IDXGIFactory2;
            hr = CreateDXGIFactory2(0, ref iidF, out s_factory);
            if (hr != 0) return Fail(ctx, step, $"hr=0x{hr:X}");

            step = "CreateSwapChainForHwnd";

            Diag("step: CreateSwapChainForHwnd");
            var scd = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)s_width, Height = (uint)s_height,
                Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleCount = 1, SampleQuality = 0,
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = BufferCount,
                SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
            };
            hr = Call<CreateSwapChainForHwndFn>(s_factory, FAC_CreateSwapChainForHwnd)(
                s_factory, s_queue, s_child, ref scd, IntPtr.Zero, IntPtr.Zero, out s_swapChain);
            if (hr != 0 || s_swapChain == IntPtr.Zero) return Fail(ctx, step, $"hr=0x{hr:X}");

            // DXGI に子ウィンドウのメッセージを監視させない（Alt+Enter 等の横取りを避ける）。
            Call<MakeWindowAssociationFn>(s_factory, FAC_MakeWindowAssociation)(s_factory, s_child, DXGI_MWA_NO_WINDOW_CHANGES);

            step = "CreateDescriptorHeap";

            Diag("step: CreateDescriptorHeap");
            var hd = new D3D12_DESCRIPTOR_HEAP_DESC { Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV, NumDescriptors = BufferCount };
            var iidH = IID_ID3D12DescriptorHeap;
            hr = Call<CreateDescriptorHeapFn>(s_device, DEV_CreateDescriptorHeap)(s_device, ref hd, ref iidH, out s_rtvHeap);
            if (hr != 0) return Fail(ctx, step, $"hr=0x{hr:X}");

            Diag("after CreateDescriptorHeap hr ok, heap=0x" + s_rtvHeap.ToInt64().ToString("x"));
            Diag("calling GetCPUDescriptorHandleForHeapStart");
            Call<GetCpuHandleFn>(s_rtvHeap, HEAP_GetCPUDescriptorHandleForHeapStart)(s_rtvHeap, out s_rtvStart);
            Diag("rtvStart=0x" + s_rtvStart.ToInt64().ToString("x"));
            Diag("calling GetDescriptorHandleIncrementSize");
            s_rtvStride = GetDescriptorHandleIncrementSize(s_device, D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
            Diag("rtvStride=" + s_rtvStride);

            step = "GetBuffer / CreateRenderTargetView";

            Diag("step: GetBuffer / CreateRenderTargetView");
            var iidRes = IID_ID3D12Resource;
            for (uint i = 0; i < BufferCount; i++)
            {
                Diag($"GetBuffer {i}");
                hr = Call<GetBufferFn>(s_swapChain, SC_GetBuffer)(s_swapChain, i, ref iidRes, out s_backBuffers[i]);
                if (hr != 0) return Fail(ctx, step, $"buffer {i} hr=0x{hr:X}");
                Diag($"  buffer {i}=0x{s_backBuffers[i].ToInt64():x}, rtv=0x{Rtv((int)i).ToInt64():x}");
                // 命令列によると pResource の +0x150 を辿るので、その手前まで確かめる。
                var res = s_backBuffers[i];
                var mbi = new MEMORY_BASIC_INFORMATION();
                VirtualQuery(res, out mbi, (UIntPtr)(uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
                Diag($"  res mem: state=0x{mbi.State:X} protect=0x{mbi.Protect:X}");
                var vtbl = Marshal.ReadIntPtr(res);
                Diag($"  res vtbl=0x{vtbl.ToInt64():x}");
                var f150 = Marshal.ReadIntPtr(res, 0x150);
                Diag($"  res[+0x150]=0x{f150.ToInt64():x}");
                if (f150 != IntPtr.Zero)
                {
                    VirtualQuery(f150, out mbi, (UIntPtr)(uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
                    Diag($"  [+0x150] mem: state=0x{mbi.State:X} protect=0x{mbi.Protect:X}");
                }
                Diag($"CreateRenderTargetView {i}");
                Call<CreateRenderTargetViewFn>(s_device, DEV_CreateRenderTargetView)(
                    s_device, s_backBuffers[i], IntPtr.Zero, Rtv((int)i));
                Diag($"  rtv {i} done");
            }

            step = "CreateCommandAllocator";

            Diag("step: CreateCommandAllocator");
            var iidA = IID_ID3D12CommandAllocator;
            hr = Call<CreateCommandAllocatorFn>(s_device, DEV_CreateCommandAllocator)(s_device, D3D12_COMMAND_LIST_TYPE_DIRECT, ref iidA, out s_alloc);
            if (hr != 0) return Fail(ctx, step, $"hr=0x{hr:X}");

            step = "CreateCommandList";

            Diag("step: CreateCommandList");
            var iidL = IID_ID3D12GraphicsCommandList;
            hr = Call<CreateCommandListFn>(s_device, DEV_CreateCommandList)(s_device, 0, D3D12_COMMAND_LIST_TYPE_DIRECT, s_alloc, IntPtr.Zero, ref iidL, out s_cmdList);
            if (hr != 0) return Fail(ctx, step, $"hr=0x{hr:X}");
            Call<CloseFn>(s_cmdList, CL_Close)(s_cmdList);

            step = "CreateFence";

            Diag("step: CreateFence");
            var iidFence = IID_ID3D12Fence;
            hr = Call<CreateFenceFn>(s_device, DEV_CreateFence)(s_device, 0, 0, ref iidFence, out s_fence);
            if (hr != 0) return Fail(ctx, step, $"hr=0x{hr:X}");
            s_fenceEvent = CreateEventW(IntPtr.Zero, false, false, null);

            step = "pipeline";

            Diag("step: pipeline");
            s_pipeline = D3D12Cube.Create(s_device);
            if (!s_pipeline.Ok) return Fail(ctx, step, s_pipeline.Error);
            Diag("pipeline OK");

            Diag("initialized OK");
            s_initialized = true;
            s_startedAt = s_lastReport = s_lastFpsAt = DateTime.UtcNow;
            s_fpsText = "";
            ctx.Logger.LogInfo($"[D3D12Cube] ready {s_width}x{s_height} child=0x{s_child.ToInt64():x}");
            return true;
        }
        catch (Exception ex)
        {
            return Fail(ctx, step, ex.Message);
        }
    }

    private static void Render(IExecutionContext ctx)
    {
        try
        {
            Call<ResetAllocFn>(s_alloc, ALLOC_Reset)(s_alloc);
            Call<ResetListFn>(s_cmdList, CL_Reset)(s_cmdList, s_alloc, IntPtr.Zero);

            var back = s_backBuffers[s_frameIndex];
            var toTarget = Barrier(back, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
            Call<BarrierFn>(s_cmdList, CL_ResourceBarrier)(s_cmdList, 1, ref toTarget);

            var rtv = Rtv(s_frameIndex);
            Call<OMSetRenderTargetsFn>(s_cmdList, CL_OMSetRenderTargets)(s_cmdList, 1, ref rtv, 1, IntPtr.Zero);

            var color = new float[4] { 0.09f, 0.10f, 0.14f, 1f };
            Call<ClearRtvFn>(s_cmdList, CL_ClearRenderTargetView)(s_cmdList, rtv, ref color[0], 0, IntPtr.Zero);

            // 回転はフレーム数ではなく経過時間から作る。フレーム数で作ると、駆動が速くなった分
            // そのまま速く回ってしまう。
            var angle = (float)((DateTime.UtcNow - s_startedAt).TotalSeconds * RadiansPerSecond);
            D3D12Cube.Draw(s_cmdList, ref s_pipeline, s_width, s_height, angle);

            // 測った速さを絵の中に出す。絵を見れば動いているかと速さが同時に分かる。
            var aspect = s_height == 0 ? 1f : (float)s_width / s_height;
            D3D12Cube.DrawNumber(s_cmdList, ref s_pipeline, s_fpsText, -0.94f, 0.94f, 0.14f, aspect);

            var toPresent = Barrier(back, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
            Call<BarrierFn>(s_cmdList, CL_ResourceBarrier)(s_cmdList, 1, ref toPresent);

            Call<CloseFn>(s_cmdList, CL_Close)(s_cmdList);

            var list = s_cmdList;
            Call<ExecuteCommandListsFn>(s_queue, Q_ExecuteCommandLists)(s_queue, 1, ref list);

            // SyncInterval=1。この呼び出しが垂直同期まで戻らないので、これが駆動の周期になる。
            var hr = Call<PresentFn>(s_swapChain, SC_Present)(s_swapChain, 1, 0);
            if (hr != 0) { Fail(ctx, "Present", $"hr=0x{hr:X}"); return; }
            NgolTickSource.FrameWaited = true;

            WaitForGpu();
            s_frameIndex = (s_frameIndex + 1) % BufferCount;
            s_frames++;

            // 画面に出す値は短い窓で作り直す。200 フレームに 1 度だと、止まっても数秒間は
            // 直前の速さを表示し続けてしまう。
            if (s_frames % 15 == 0)
            {
                var now = DateTime.UtcNow;
                var span = (now - s_lastFpsAt).TotalSeconds;
                if (span > 0) s_fpsText = (15.0 / span).ToString("F1");
                s_lastFpsAt = now;
            }

            if (s_frames % 200 == 0)
            {
                var now = DateTime.UtcNow;
                var fps = 200.0 / (now - s_lastReport).TotalSeconds;
                s_lastReport = now;
                ctx.Logger.LogInfo($"[D3D12Cube] frames={s_frames} fps={fps:F1} driver={(NgolTickSource.Active ? "node" : "core")}");
            }
        }
        catch (Exception ex)
        {
            Fail(ctx, "Render", ex.Message);
        }
    }

    private static void WaitForGpu()
    {
        var target = ++s_fenceValue;
        Call<SignalFn>(s_queue, Q_Signal)(s_queue, s_fence, target);
        if (Call<GetCompletedValueFn>(s_fence, FENCE_GetCompletedValue)(s_fence) < target)
        {
            Call<SetEventOnCompletionFn>(s_fence, FENCE_SetEventOnCompletion)(s_fence, target, s_fenceEvent);
            WaitForSingleObject(s_fenceEvent, 1000);
        }
    }

    internal static void Shutdown(IExecutionContext ctx)
    {
        if (!s_armed && !s_initialized) return;

        // 駆動元を返さないと、この後 NGOL を回す主体が居なくなる。掴んでいないときは何もしない。
        if (NgolTickSource.Active)
            NgolTickSource.RequestUnbind(m => ctx?.Logger.LogInfo("[D3D12Cube] " + m));

        try { if (s_initialized) WaitForGpu(); } catch { }

        D3D12Cube.ReleasePipeline(ref s_pipeline);

        foreach (var b in s_backBuffers) SafeRelease(b);
        Array.Clear(s_backBuffers, 0, s_backBuffers.Length);
        SafeRelease(s_fence); SafeRelease(s_cmdList); SafeRelease(s_alloc);
        SafeRelease(s_rtvHeap); SafeRelease(s_swapChain); SafeRelease(s_factory);
        SafeRelease(s_queue); SafeRelease(s_device);
        s_fence = s_cmdList = s_alloc = s_rtvHeap = s_swapChain = s_factory = s_queue = s_device = IntPtr.Zero;

        if (s_fenceEvent != IntPtr.Zero) { CloseHandle(s_fenceEvent); s_fenceEvent = IntPtr.Zero; }
        if (s_child != IntPtr.Zero) { DestroyWindow(s_child); s_child = IntPtr.Zero; }
        if (s_parent != IntPtr.Zero && s_prevParentStyle != IntPtr.Zero)
        {
            SetWindowLongPtr(s_parent, GWL_STYLE, s_prevParentStyle);
            s_prevParentStyle = IntPtr.Zero;
        }

        s_initialized = false; s_armed = false; s_frames = 0; s_frameIndex = 0; s_fenceValue = 0;
        ctx?.Logger.LogInfo("[D3D12Cube] stopped");
    }

    // --- 小道具 ---

    private static T Call<T>(IntPtr obj, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(D3D12Com.GetVtableSlot(obj, slot));

    private static IntPtr Rtv(int index) => (IntPtr)(s_rtvStart.ToInt64() + (long)index * s_rtvStride);

    private static D3D12_RESOURCE_BARRIER Barrier(IntPtr res, uint before, uint after) => new D3D12_RESOURCE_BARRIER
    {
        Type = 0, Flags = 0,
        Transition = new D3D12_RESOURCE_TRANSITION_BARRIER { pResource = res, Subresource = 0xffffffff, StateBefore = before, StateAfter = after },
    };

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint GetIncrementFn(IntPtr self, uint type);
    private static uint GetDescriptorHandleIncrementSize(IntPtr device, uint type)
        => Marshal.GetDelegateForFunctionPointer<GetIncrementFn>(D3D12Com.GetVtableSlot(device, 15))(device, type);

    private static void SafeRelease(IntPtr o) { if (o != IntPtr.Zero) D3D12Com.Release(o); }

    private static bool Fail(IExecutionContext ctx, string step, string detail)
    {
        s_failure = $"failed at '{step}': {detail}";
        ctx.Logger.LogError("[D3D12Cube] " + s_failure);

        // 作りかけを残すと、対象の窓に子ウィンドウと style が残り、持ち主のスレッドが
        // 止まった場合はその窓に触る操作が全部待たされる。後始末は Shutdown に揃えてある。
        // 再入は Shutdown 側の入口で弾かれる。
        try { CancelRegistration(); } catch { }
        try { Shutdown(ctx); } catch { }
        return false;
    }

    private static void PumpOwnMessages()
    {
        // 子ウィンドウはこのスレッドの持ち物。誰も回さないので自分で回す。
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static IntPtr FindOwnMainWindow()
    {
        var own = GetCurrentProcessId();
        var found = IntPtr.Zero;
        var buf = new char[256];
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != own || !IsWindowVisible(hwnd)) return true;
            var len = GetClassNameW(hwnd, buf, buf.Length);
            var cls = len > 0 ? new string(buf, 0, len) : string.Empty;
            if (cls == "ConsoleWindowClass" || cls.StartsWith("IME") || cls == "MSCTFIME UI") return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
