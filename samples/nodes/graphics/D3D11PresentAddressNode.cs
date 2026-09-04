using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 自前のダミーウィンドウ+D3D11デバイス+スワップチェーンを作成し、
/// 提示関数の実アドレスを vtable から読み取る。
/// vtable はランタイム DLL 全体で共有される(インスタンスごとに変わらない)ため、
/// このダミーで読んだアドレスは、対象アプリのスワップチェーンにも有効。
/// ダミーは読み取り後すぐ解放し、対象アプリには一切干渉しない。
///
/// 提示関数は 2 つある。フリップモデルのアプリは Present ではなく Present1 を呼ぶため、
/// 片方だけを掴んでも一度も発火しないことがある。
/// 先頭が飛び越しになっているスロットは、別のソフトウェアが先に横取りしている。
/// 先に張った側のトランポリンが関数の途中へ戻るので、後から先頭に張っても経路から外れる。
/// => どちらの事実も出力に載せる。どの番地を掴むかは利用者が決める。
/// </summary>
[NodeType("ngol.gfx.present_address", "Graphics", "Present Address",
    Version = "2.1.1",
    Description = "Create a throw-away device+swapchain (dummy window) to read the presentation functions' real addresses from the swapchain vtable: Present (slot 8) and Present1 (slot 22). Does not touch the target's own swapchain. Flip-model applications call Present1, not Present, so a hook on Present alone can never fire - check both. Each entry is also inspected for an existing inline hook: when it starts with a jump, other software got there first, its trampoline returns into the middle of the function, and a later hook placed at the entry is bypassed entirely. The jump is followed and the owning module of the final target is reported, so you can decide what to hook.")]
[NodePort("dump_vtable", PortDirection.Input, "boolean", Description = "Also return the full vtable listing (slot, address, first bytes). Default false")]
[NodePort("present_address_hex", PortDirection.Output, "string", Description = "Absolute address of IDXGISwapChain::Present (vtable slot 8), hex string")]
[NodePort("present_slot_address_hex", PortDirection.Output, "string", Description = "Absolute address of the vtable entry itself (slot 8), hex string. The call table is shared by the class, so this is the same entry the target's own swapchain goes through")]
[NodePort("present1_address_hex", PortDirection.Output, "string", Description = "Absolute address of IDXGISwapChain1::Present1 (vtable slot 22), hex string. Empty when the swapchain does not expose IDXGISwapChain1")]
[NodePort("present_hook_chain", PortDirection.Output, "string", Description = "Empty when Present's entry is untouched. Otherwise the jump chain from its entry to the code that actually runs, with the owning module of each step")]
[NodePort("present1_hook_chain", PortDirection.Output, "string", Description = "Same for Present1")]
[NodePort("hooked_slots", PortDirection.Output, "string", Description = "Comma-separated vtable slot numbers whose entry starts with a jump, i.e. already hooked by other software")]
[NodePort("vtable", PortDirection.Output, "string", Description = "Full vtable listing when dump_vtable is true, otherwise empty")]
[NodePort("result", PortDirection.Output, "string", Description = "Status or error message")]
public sealed class D3D11PresentAddressNode : INode
{
    // --- Win32 window creation (dummy, invisible) ---
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    // --- DXGI / D3D11 swap chain creation ---
    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_MODE_DESC
    {
        public uint Width, Height;
        public DXGI_RATIONAL RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_SAMPLE_DESC { public uint Count; public uint Quality; }

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_SWAP_CHAIN_DESC
    {
        public DXGI_MODE_DESC BufferDesc;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint BufferUsage;
        public uint BufferCount;
        public IntPtr OutputWindow;
        [MarshalAs(UnmanagedType.Bool)] public bool Windowed;
        public uint SwapEffect;
        public uint Flags;
    }

    // disasm-verified: d3d11.dll RVA 0x82470 / 引数 12 個。
    //   ずれ S = push rdi(8) + sub rsp,70h = 0x78。
    //   [rsp+0A0h]-S=+0x28 が第5、[rsp+0D8h]-S=+0x60 が第12（8バイト刻み）。
    //   幅は mov ecx,[rsp+0A8h] / [rsp+0B0h] が 32bit（第6・第7）、他のスタック引数は 64bit。
    //   第2 mov esi,edx=32bit、第4 mov ebx,r9d=32bit。戻り値は HRESULT(32bit)。
    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3D11CreateDeviceAndSwapChain(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        ref DXGI_SWAP_CHAIN_DESC pSwapChainDesc,
        out IntPtr ppSwapChain, out IntPtr ppDevice,
        out int pFeatureLevel, out IntPtr ppImmediateContext);

    const int D3D_DRIVER_TYPE_HARDWARE = 1;
    const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 1 << 5;
    const uint DXGI_SWAP_EFFECT_DISCARD = 0;

    // --- generic COM vtable read ---
    static IntPtr GetVtableSlot(IntPtr comObject, int slot)
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(IntPtr.Add(vtable, slot * IntPtr.Size));
    }

    static void ComRelease(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        var releaseFn = GetVtableSlot(comObject, 2); // IUnknown::Release is always slot 2
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releaseFn);
        release(comObject);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint ReleaseDelegate(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    struct IID
    {
        public uint A; public ushort B, C;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] D;
        public IID(uint a, ushort b, ushort c, params byte[] d) { A = a; B = b; C = c; D = d; }
    }

    // com-abi: IUnknown slot 0 QueryInterface（公開ヘッダー通り）。
    // 生きたオブジェクト越しの呼び出しなので、呼ぶ前に disasm できる番地が無い。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceDelegate(IntPtr self, ref IID riid, out IntPtr ppv);

    // com-abi: IID_IDXGISwapChain1（公開ヘッダー通り）
    static readonly IID IID_IDXGISwapChain1 =
        new IID(0x790a45f7, 0x0d42, 0x4876, 0x98, 0x3a, 0x0a, 0x55, 0xcf, 0xe6, 0xf4, 0xaa);

    // com-abi: IDXGISwapChain slot 8 Present / IDXGISwapChain1 slot 22 Present1（公開ヘッダー通り）。
    // IDXGISwapChain1 は IDXGISwapChain を継承するので、同じ vtable の後ろに並ぶ。
    const int SlotPresent = 8, SlotPresent1 = 22;
    const int LastSlotWithoutSwapChain1 = 17;   // IDXGISwapChain の最後は GetLastPresentCount

    // vtable のスロットを差し替えたノードが、控えた番地をここへ置く。
    // 差し替えている間は使い捨てを作れないので、その番地をそのまま答えにする。
    const string SwapNoticeKey = "ngol.gfx.present_vtable_swap.v1";

    public void Execute(IExecutionContext ctx)
    {
        // 誰かがスロットを差し替えているなら、使い捨てのスワップチェーンを作ってはいけない。
        // 作ると、先に割り込んでいるソフトウェアが表を読み直し、そこに入っている
        // 差し替え側のコードの先頭を書き換える。書き換えられた側は自分の控えを呼ぶと
        // 呼び返され、呼び出しが再帰して対象は画を出せなくなる。
        // 答えは差し替え側が控えているので、作らずに済ませられる。
        if (AppDomain.CurrentDomain.GetData(SwapNoticeKey) is long[] notice
            && notice.Length >= 1 && notice[0] != 0)
        {
            var kept = new IntPtr(notice[0]);
            ctx.SetPortValue("present_address_hex", $"0x{kept.ToInt64():X}");
            ctx.SetPortValue("present_slot_address_hex",
                notice.Length >= 3 && notice[2] != 0 ? $"0x{notice[2]:X}" : "");
            ctx.SetPortValue("present_hook_chain",
                NgolJumpChain.Describe(NgolJumpChain.Follow(kept, NgolModuleDefault.List(1024, out _))));
            ctx.SetPortValue("present1_address_hex", "");
            ctx.SetPortValue("present1_hook_chain", "");
            ctx.SetPortValue("hooked_slots", "");
            ctx.SetPortValue("vtable", "");
            ctx.SetPortValue("result",
                "reported the address kept by the node that replaced the call table entry; no swapchain was created. "
              + "Creating one makes other overlay software re-read the table and patch that node's code, which makes the calls loop. "
              + "Present1 and the slot survey are not available in this state.");
            ctx.Logger.LogInfo($"[PresentAddress] a call table replacement is in effect; reporting the kept address 0x{kept.ToInt64():X} without creating a swapchain");
            return;
        }

        IntPtr hwnd = IntPtr.Zero;
        IntPtr swapChain = IntPtr.Zero, swapChain1 = IntPtr.Zero, device = IntPtr.Zero, context = IntPtr.Zero;
        try
        {
            var hInstance = GetModuleHandleW(null);
            const string className = "NgolDummyD3D11Wnd";

            var defWindowProc = GetProcAddress(GetModuleHandleA("user32.dll"), "DefWindowProcW");
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = defWindowProc,
                hInstance = hInstance,
                lpszClassName = className,
            };
            RegisterClassExW(ref wc); // ignore failure (already registered case)

            hwnd = CreateWindowExW(0, className, "NgolDummy", 0, 0, 0, 64, 64, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Fail(ctx, $"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            var desc = new DXGI_SWAP_CHAIN_DESC
            {
                BufferDesc = new DXGI_MODE_DESC { Width = 64, Height = 64, Format = DXGI_FORMAT_R8G8B8A8_UNORM, RefreshRate = new DXGI_RATIONAL { Numerator = 0, Denominator = 1 } },
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 1,
                OutputWindow = hwnd,
                Windowed = true,
                SwapEffect = DXGI_SWAP_EFFECT_DISCARD,
                Flags = 0,
            };

            var hr = D3D11CreateDeviceAndSwapChain(
                IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                IntPtr.Zero, 0, 7 /*D3D11_SDK_VERSION*/,
                ref desc, out swapChain, out device, out _, out context);

            if (hr != 0 || swapChain == IntPtr.Zero)
            {
                Fail(ctx, $"D3D11CreateDeviceAndSwapChain failed: hr=0x{hr:X}");
                return;
            }

            // Present1 は IDXGISwapChain1 のスロットなので、取れたときだけ後ろのスロットが読める。
            var riid = IID_IDXGISwapChain1;
            var qhr = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(GetVtableSlot(swapChain, 0))(
                swapChain, ref riid, out swapChain1);
            bool has1 = qhr == 0 && swapChain1 != IntPtr.Zero;
            var table = has1 ? swapChain1 : swapChain;
            int lastSlot = has1 ? SlotPresent1 : LastSlotWithoutSwapChain1;

            var modules = NgolModuleDefault.List(1024, out _);

            var presentAddr = GetVtableSlot(table, SlotPresent);
            ctx.SetPortValue("present_address_hex", $"0x{presentAddr.ToInt64():X}");
            var slotAddr = Marshal.ReadIntPtr(swapChain) + SlotPresent * IntPtr.Size;
            ctx.SetPortValue("present_slot_address_hex", $"0x{slotAddr.ToInt64():X}");
            ctx.SetPortValue("present_hook_chain",
                NgolJumpChain.Describe(NgolJumpChain.Follow(presentAddr, modules)));

            var present1Addr = IntPtr.Zero;
            if (has1)
            {
                present1Addr = GetVtableSlot(table, SlotPresent1);
                ctx.SetPortValue("present1_address_hex", $"0x{present1Addr.ToInt64():X}");
                ctx.SetPortValue("present1_hook_chain",
                    NgolJumpChain.Describe(NgolJumpChain.Follow(present1Addr, modules)));
            }
            else
            {
                ctx.SetPortValue("present1_address_hex", "");
                ctx.SetPortValue("present1_hook_chain", "");
            }

            // 横取りされているスロットは Present / Present1 に限らないので、vtable 全体を見る。
            var hooked = new System.Text.StringBuilder();
            var dump = new System.Text.StringBuilder();
            bool wantDump = ctx.GetPortValue("dump_vtable") as bool? ?? false;
            var probe = new byte[6];
            for (int slot = 0; slot <= lastSlot; slot++)
            {
                IntPtr fn;
                try { fn = GetVtableSlot(table, slot); }
                catch { break; }
                if (fn == IntPtr.Zero) continue;

                // 先頭が飛び越しでも、モジュール内で完結するものはリンカの置いたサンクで
                //    フックではない。行き先まで辿ってから数える。
                bool redirected = NgolJumpChain.LooksLikeJump(fn)
                    && NgolJumpChain.IsForeignRedirect(NgolJumpChain.Follow(fn, modules));
                if (redirected)
                {
                    if (hooked.Length > 0) hooked.Append(',');
                    hooked.Append(slot);
                }
                if (!wantDump) continue;

                var read = NgolSafeMemory.Read(fn, probe, 0, 6);
                dump.Append($"  [{slot,2}] 0x{fn.ToInt64():X}");
                if (read > 0)
                {
                    dump.Append("  ");
                    for (int k = 0; k < read; k++) dump.Append(probe[k].ToString("x2")).Append(' ');
                }
                if (redirected) dump.Append(" <== already hooked");
                dump.Append('\n');
            }

            ctx.SetPortValue("hooked_slots", hooked.ToString());
            ctx.SetPortValue("vtable", dump.ToString());
            ctx.SetPortValue("result", has1 ? "ok" : "ok (IDXGISwapChain1 unavailable: Present1 not reported)");
            ctx.Logger.LogInfo(
                $"[PresentAddress] Present @ 0x{presentAddr.ToInt64():X}" +
                (has1 ? $", Present1 @ 0x{present1Addr.ToInt64():X}" : ", Present1 unavailable") +
                (hooked.Length > 0 ? $", already-hooked slots: {hooked}" : ", no slot is hooked"));
        }
        catch (Exception ex)
        {
            Fail(ctx, $"exception: {ex.Message}");
            ctx.Logger.LogError($"[PresentAddress] {ex}");
        }
        finally
        {
            ComRelease(swapChain1);
            ComRelease(swapChain);
            ComRelease(context);
            ComRelease(device);
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        }
    }

    static void Fail(IExecutionContext ctx, string message)
    {
        ctx.SetPortValue("present_address_hex", "0x0");
        ctx.SetPortValue("present_slot_address_hex", "");
        ctx.SetPortValue("present1_address_hex", "");
        ctx.SetPortValue("present_hook_chain", "");
        ctx.SetPortValue("present1_hook_chain", "");
        ctx.SetPortValue("hooked_slots", "");
        ctx.SetPortValue("vtable", "");
        ctx.SetPortValue("result", message);
    }
}
