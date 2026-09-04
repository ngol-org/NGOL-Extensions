using System;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// vtable 越しに COM オブジェクトを呼ぶための最小限の道具。
/// 生きたオブジェクトのポインタを外から受け取って叩くため、型情報は当てにできない。
///
/// com-abi: 以下のスロット番号とシグネチャは公開インターフェース定義のとおり。
///   IUnknown            slot 0 QueryInterface / slot 2 Release
///   IDXGIDeviceSubObject slot 7 GetDevice（IDXGISwapChain が継承）
///   IDXGISwapChain       slot 9 GetBuffer
/// 呼ぶ前に disasm できる番地が無い（vtable は生きたオブジェクトの中にある）。
/// </summary>
internal static class NgolCom
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GUID
    {
        public uint Data1; public ushort Data2; public ushort Data3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data4;
        public GUID(uint a, ushort b, ushort c, params byte[] d) { Data1 = a; Data2 = b; Data3 = c; Data4 = d; }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int QueryInterfaceFn(IntPtr self, ref GUID riid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDeviceFn(IntPtr self, ref GUID riid, out IntPtr ppDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetBufferFn(IntPtr self, uint buffer, ref GUID riid, out IntPtr ppSurface);

    internal const int SlotQueryInterface = 0, SlotRelease = 2;
    internal const int SC_GetDevice = 7, SC_GetBuffer = 9;

    // com-abi: ID3D10Multithread（D3D11 の即時コンテキストからも取れる）
    //   slot 3 Enter / slot 4 Leave / slot 5 SetMultithreadProtected / slot 6 GetMultithreadProtected
    // 既定では保護が切れており、そのとき Enter/Leave は何も守らない。
    internal static readonly GUID IID_ID3D10Multithread = new GUID(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);
    internal const int MT_Enter = 3, MT_Leave = 4, MT_SetProtected = 5, MT_GetProtected = 6;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void MtEnterFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void MtLeaveFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate bool MtSetProtectedFn(IntPtr self, bool enable);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate bool MtGetProtectedFn(IntPtr self);

    internal static readonly GUID IID_ID3D11Device = new GUID(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);
    internal static readonly GUID IID_ID3D12Device = new GUID(0x189819f1, 0x1db6, 0x4b57, 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7);

    internal static IntPtr GetVtableSlot(IntPtr comObject, int slot)
        => Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    /// <summary>
    /// 呼ぶ前に、その番地が命令に見えるかだけ確かめる。
    /// 誤ったポインタでプロセスごと落ちるのを減らすためのゲートで、
    /// これを通ったことは正しい関数である保証にはならない。
    /// </summary>
    internal static bool LooksLikeCode(IntPtr addr)
    {
        try
        {
            var b = new byte[2];
            Marshal.Copy(addr, b, 0, 2);
            return !(b[0] == 0x00 && b[1] == 0x00) && b[0] != 0xCC;
        }
        catch { return false; }
    }

    internal static void Release(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Marshal.GetDelegateForFunctionPointer<ReleaseFn>(GetVtableSlot(obj, SlotRelease))(obj);
    }

    /// <summary>
    /// その形式を 32bpp の BMP へそのまま写せるかを返す。写せる場合は
    /// <paramref name="swapRedBlue"/> に、R と B を入れ替える必要があるかが入る。
    ///
    /// BMP の BI_RGB 32bpp は 1 画素を B,G,R,A の順で並べる決まりなので、
    /// メモリ上が R,G,B,A で並ぶ形式（R8G8B8A8 系）は入れ替えないと色が入れ替わる。
    /// 1 成分 8bit・4 成分でない形式（R16G16B16A16_FLOAT / R10G10B10A2 など）は
    /// 写す先が無いので false を返す--黙って写すと絵にならない画像ができる。
    /// </summary>
    internal static bool TryGetBmpChannelOrder(uint format, out bool swapRedBlue)
    {
        switch (format)
        {
            // R8G8B8A8 系（TYPELESS / UNORM / UNORM_SRGB / UINT / SNORM / SINT）
            case 27: case 28: case 29: case 30: case 31: case 32:
                swapRedBlue = true;
                return true;
            // B8G8R8A8 / B8G8R8X8 系（TYPELESS / UNORM / UNORM_SRGB）
            case 87: case 88: case 90: case 91: case 92: case 93:
                swapRedBlue = false;
                return true;
            default:
                swapRedBlue = false;
                return false;
        }
    }

    /// <summary>
    /// 32bpp の BMP へ写せない形式に付ける説明。番号だけでは何を直せばよいか分からないため。
    /// </summary>
    internal static string DescribeFormat(uint format)
    {
        switch (format)
        {
            case 10: return "R16G16B16A16_FLOAT (64bpp HDR)";
            case 24: return "R10G10B10A2_UNORM (10bit HDR)";
            case 2:  return "R32G32B32A32_FLOAT (128bpp)";
            default: return "not an 8-bit 4-channel format";
        }
    }

    /// <summary>
    /// マップ済みのメモリから 32bit の BMP を組み立てる。
    /// BMP は最下行から並べる形式なので行を逆順に写す。行の間隔は幅ではなく
    /// <paramref name="rowPitch"/> で決まる。
    /// <paramref name="swapRedBlue"/> は <see cref="TryGetBmpChannelOrder"/> で決める。
    /// </summary>
    /// <summary>
    /// マップ中の画素から、要求された形式の画像を作る。
    /// ここを抜けたらポインタは使えなくなるので、必要な形式はここで決めておく。
    /// </summary>
    internal static byte[] BuildImage(IntPtr pData, int width, int height, uint rowPitch,
                                      bool swapRedBlue, bool wantPng)
        => wantPng
            ? NgolPng.Build(pData, width, height, rowPitch, bottomUp: false, swapRedBlue: swapRedBlue)
            : BuildBmp(pData, width, height, rowPitch, swapRedBlue);

    internal static byte[] BuildBmp(IntPtr pData, int width, int height, uint rowPitch, bool swapRedBlue)
    {
        int rowBytes = width * 4;
        int imageSize = rowBytes * height;
        const int headerSize = 14 + 40;

        var result = new byte[headerSize + imageSize];
        using (var ms = new System.IO.MemoryStream(result))
        using (var w = new System.IO.BinaryWriter(ms))
        {
            // BITMAPFILEHEADER
            w.Write((byte)'B'); w.Write((byte)'M');
            w.Write((uint)(headerSize + imageSize));
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((uint)headerSize);
            // BITMAPINFOHEADER
            w.Write((uint)40);
            w.Write((int)width);
            w.Write((int)height);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)0); // BI_RGB
            w.Write((uint)imageSize);
            w.Write((int)0); w.Write((int)0);
            w.Write((uint)0); w.Write((uint)0);
        }

        var rowBuf = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            var srcRow = new IntPtr(pData.ToInt64() + (long)(height - 1 - y) * rowPitch);
            Marshal.Copy(srcRow, rowBuf, 0, rowBytes);
            if (swapRedBlue)
            {
                for (int x = 0; x < rowBytes; x += 4)
                {
                    var t = rowBuf[x];
                    rowBuf[x] = rowBuf[x + 2];
                    rowBuf[x + 2] = t;
                }
            }
            Array.Copy(rowBuf, 0, result, headerSize + y * rowBytes, rowBytes);
        }

        return result;
    }
}

/// <summary>
/// 取り込みの結果。失敗したときは <see cref="Message"/> にどの段で止まったかが入る。
/// </summary>
internal struct NgolCaptureResult
{
    public bool Ok;
    public string Message;
    /// <summary>
    /// 組み立て済みの画像。形式は <see cref="ImageFormat"/> を見ること--
    /// 画素のポインタはマップを解いた時点で無効になるので、必要な形式はマップ中に作る。
    /// </summary>
    public byte[] Bmp;
    /// <summary>"bmp" または "png"。</summary>
    public string ImageFormat;
    public int Width;
    public int Height;
    /// <summary>取り込んだバックバッファの DXGI 形式。色の並びはここで決まる。</summary>
    public uint Format;

    internal static NgolCaptureResult Failed(string message)
        => new NgolCaptureResult { Ok = false, Message = message };
}
