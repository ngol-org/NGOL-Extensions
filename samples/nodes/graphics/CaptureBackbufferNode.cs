using System;
using System.Runtime.InteropServices;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

using GUID = NgolCom.GUID;
using GetDeviceFn = NgolCom.GetDeviceFn;

/// <summary>
/// 生きたスワップチェーンからバックバッファを読み出す。
///
/// 世代（D3D11 / D3D12）は利用者に選ばせず、こちらで判別する。
/// 対象アプリがどちらで描いているかは、外から見て分かるものではない。
///
/// com-abi: 判別は IDXGISwapChain::GetDevice に ID3D11Device を要求して行う。
/// D3D12 のデバイスは IDXGIDevice を実装しないため E_NOINTERFACE になる。
/// 失敗を即 D3D12 とみなさず、ID3D12Device でも引き直して確かめる
/// --そうしないと、ただの誤ったポインタを D3D12 と誤判定する。
/// </summary>
[NodeType("ngol.gfx.capture_backbuffer", "Graphics", "Capture Backbuffer",
    Version = "2.0.1",
    Description = "Read pixels from a live swapchain's backbuffer, given its 'this' pointer (e.g. recorded by a Present hook). Detects D3D11 vs D3D12 and uses the matching path. Returns the image as Base64, and writes it to save_path when given. The format follows save_path's extension: .png writes a PNG, anything else writes a BMP. PNG is the safer choice for handing the result to other tools, since some readers do not accept BMP. "
      + "KNOWN RISK on the D3D11 path: this borrows the target's immediate context from a worker thread. The node turns on multithread protection and takes the lock, but a call the render thread was ALREADY inside never took that lock, so a window remains on a target that draws continuously. A target running with the D3D11 debug layer reports this as exception 0x0000087d and dies immediately (measured); without the debug layer the same race is simply not reported. The D3D12 path does not share the target's context and is not affected.")]
[NodePort("swapchain_hex", PortDirection.Input, "string", Description = "Live IDXGISwapChain 'this' pointer, hex string")]
[NodePort("state_before_hex", PortDirection.Input, "string", Description = "D3D12 only: the backbuffer's resource state just before the copy (hex). Default 0x0 (PRESENT/COMMON). Try 0x4 (RENDER_TARGET) or 0x8 (UNORDERED_ACCESS) when the copy fails")]
[NodePort("save_path", PortDirection.Input, "string", Description = "If non-empty, also write the image to this local file path. Ending it with .png writes a PNG; anything else writes a BMP - the extension and the contents always agree")]
[NodePort("allow_enable_multithread_protection", PortDirection.Input, "boolean", Description = "D3D11 only, default true. Reading the backbuffer borrows the target's immediate context, which is not thread-safe, and this node does not run on the target's render thread. Multithread protection is therefore turned on for the duration of the capture and restored afterwards. This narrows the window but does not close it: a call the render thread had already entered did not take the lock (see the node description). Set false to refuse instead of touching the target's protection setting - the capture is then only attempted when the target already had protection on, which is the safer choice on a target you cannot afford to lose")]
[NodePort("api", PortDirection.Output, "string", Description = "Which path was used: d3d11 or d3d12. Empty when detection failed")]
[NodePort("bmp_base64", PortDirection.Output, "string", Description = "Base64-encoded image of the captured backbuffer. BMP by default; PNG when save_path ends with .png (see image_format)")]
[NodePort("image_format", PortDirection.Output, "string", Description = "Which format the bytes are in: bmp or png")]
[NodePort("width", PortDirection.Output, "number", Description = "Captured image width")]
[NodePort("height", PortDirection.Output, "number", Description = "Captured image height")]
[NodePort("format", PortDirection.Output, "number", Description = "DXGI format of the backbuffer. Decides the channel order: 28 (R8G8B8A8_UNORM) is stored R,G,B,A and gets swapped into the B,G,R,A a BMP expects; 87 (B8G8R8A8_UNORM) is copied as-is")]
[NodePort("result", PortDirection.Output, "string", Description = "\"ok\", or which step failed")]
public sealed class CaptureBackbufferNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        var swapChainHex = (ctx.GetPortValue("swapchain_hex") as string ?? "").Trim();
        if (!long.TryParse(swapChainHex.Replace("0x", "").Replace("0X", ""),
                System.Globalization.NumberStyles.HexNumber, null, out var scAddr) || scAddr == 0)
        {
            Report(ctx, "", NgolCaptureResult.Failed($"invalid swapchain_hex: {swapChainHex}"));
            return;
        }
        var swapChain = new IntPtr(scAddr);

        // 拡張子と中身を食い違わせない。.png と名乗るなら PNG を書く。
        var savePathEarly = (ctx.GetPortValue("save_path") as string ?? "").Trim();
        var wantPng = savePathEarly.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        var api = DetectApi(swapChain, out var detail);
        if (api.Length == 0)
        {
            Report(ctx, "", NgolCaptureResult.Failed("could not tell D3D11 from D3D12: " + detail));
            return;
        }
        ctx.Logger.LogInfo($"[CaptureBackbuffer] detected {api}");

        NgolCaptureResult result;
        if (api == "d3d11")
        {
            // 既定は true。保護が切れたまま借りると落ちる環境が実在する。
            var allow = ctx.GetPortValue("allow_enable_multithread_protection") as bool? ?? true;
            result = D3D11Capture.Capture(swapChain, ctx, allow, wantPng);
        }
        else
        {
            var stateHex = (ctx.GetPortValue("state_before_hex") as string ?? "").Trim();
            uint stateBefore = 0;
            if (stateHex.Length > 0)
                uint.TryParse(stateHex.Replace("0x", "").Replace("0X", ""),
                    System.Globalization.NumberStyles.HexNumber, null, out stateBefore);
            result = D3D12Capture.Capture(swapChain, stateBefore, ctx, wantPng);
        }

        if (result.Ok)
        {
            var savePath = ctx.GetPortValue("save_path") as string;
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                System.IO.File.WriteAllBytes(savePath, result.Bmp);
                ctx.Logger.LogInfo($"[CaptureBackbuffer] saved to {savePath}");
            }
            ctx.Logger.LogInfo($"[CaptureBackbuffer] captured {result.Width}x{result.Height} via {api}, fmt={result.Format}, {result.ImageFormat}, {result.Bmp.Length} bytes");
        }

        Report(ctx, api, result);
    }

    /// <summary>
    /// どちらの世代かを返す。判別できなければ空文字と、両方の HRESULT を返す。
    /// </summary>
    static string DetectApi(IntPtr swapChain, out string detail)
    {
        detail = "";
        // 誤ったポインタでも読み取りに入る前に止める。そうしないと無効な参照が
        //   例外にならずプロセスごと落ち、フレームワーク側に理由の分からない失敗が残る。
        if (!TryReadVtableSlot(swapChain, NgolCom.SC_GetDevice, out var slot))
        {
            detail = $"swapchain pointer 0x{swapChain.ToInt64():X} is not a readable COM object "
                   + "(its vtable could not be read). Pass a live IDXGISwapChain 'this' pointer "
                   + "(e.g. captured by a Present hook), not an arbitrary address.";
            return "";
        }
        if (!NgolCom.LooksLikeCode(slot))
        {
            detail = "GetDevice vtable slot doesn't look like code";
            return "";
        }
        var getDevice = Marshal.GetDelegateForFunctionPointer<GetDeviceFn>(slot);

        var riid11 = NgolCom.IID_ID3D11Device;
        var hr11 = getDevice(swapChain, ref riid11, out var dev11);
        if (hr11 == 0 && dev11 != IntPtr.Zero)
        {
            NgolCom.Release(dev11);
            return "d3d11";
        }

        var riid12 = NgolCom.IID_ID3D12Device;
        var hr12 = getDevice(swapChain, ref riid12, out var dev12);
        if (hr12 == 0 && dev12 != IntPtr.Zero)
        {
            NgolCom.Release(dev12);
            return "d3d12";
        }

        detail = $"ID3D11Device hr=0x{hr11:X}, ID3D12Device hr=0x{hr12:X}";
        return "";
    }

    /// <summary>
    /// vtable の slot を、参照する前に読めるか確かめてから取る。
    /// 生きたオブジェクトのポインタを外から受け取るため、誤った番地が来る。
    ///   二段参照をそのまま行うと、無効な番地の読み取りが例外ではなくプロセス終了に
    ///   なりうる（.NET は AccessViolation を捕まえられない）。=> 先に読めるかを測る。
    /// </summary>
    static bool TryReadVtableSlot(IntPtr comObject, int slot, out IntPtr slotValue)
    {
        slotValue = IntPtr.Zero;
        if (comObject == IntPtr.Zero) return false;

        // オブジェクトの先頭には vtable ポインタがある。まずそこが読めるか。
        if (NgolSafeMemory.ReadableLength(comObject, IntPtr.Size) < IntPtr.Size) return false;
        var vtable = Marshal.ReadIntPtr(comObject);
        if (vtable == IntPtr.Zero) return false;

        // vtable の slot 番目が読めるか。
        var entry = vtable + slot * IntPtr.Size;
        if (NgolSafeMemory.ReadableLength(entry, IntPtr.Size) < IntPtr.Size) return false;

        slotValue = Marshal.ReadIntPtr(entry);
        return true;
    }

    static void Report(IExecutionContext ctx, string api, NgolCaptureResult r)
    {
        ctx.SetPortValue("api", api);
        ctx.SetPortValue("bmp_base64", r.Ok ? Convert.ToBase64String(r.Bmp) : "");
        ctx.SetPortValue("width", (double)(r.Ok ? r.Width : 0));
        ctx.SetPortValue("height", (double)(r.Ok ? r.Height : 0));
        ctx.SetPortValue("format", (double)(r.Ok ? r.Format : 0));
        ctx.SetPortValue("image_format", r.Ok ? (r.ImageFormat ?? "bmp") : "");
        ctx.SetPortValue("result", r.Message);
        if (!r.Ok) ctx.Logger.LogError($"[CaptureBackbuffer] {r.Message}");
    }
}
