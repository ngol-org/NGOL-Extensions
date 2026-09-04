using System;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 割り込みの最中に起きた、放置できない出来事を外へ伝える口。
/// 割り込みは対象の描画スレッドで走り、記録の手段を持たないのでここで受け取る。
/// 受け手が居なければ何もしない。
/// </summary>
internal static class OverlayReport
{
    internal static Action<string> Warn;

    internal static void W(string what)
    {
        var sink = Warn;
        if (sink == null) return;
        try { sink(what); } catch { }
    }
}

/// <summary>
/// vtable 越しに COM を呼ぶための最小限の道具。
///
/// 生きたオブジェクトのポインタを外から受け取って叩くため、型情報は当てにできない。
/// com-abi: slot 2 は IUnknown::Release（公開インターフェース定義のとおり）。
/// </summary>
internal static class OverlayCom
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GUID
    {
        public uint Data1; public ushort Data2; public ushort Data3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data4;
        public GUID(uint a, ushort b, ushort c, params byte[] d) { Data1 = a; Data2 = b; Data3 = c; Data4 = d; }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);

    internal static IntPtr GetVtableSlot(IntPtr comObject, int slot)
        => Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    internal static void Release(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Marshal.GetDelegateForFunctionPointer<ReleaseFn>(GetVtableSlot(obj, 2))(obj);
    }
}

/// <summary>
/// 対象アプリが描き終えた絵の上に、D3D11 でサイコロを重ねる。
///
/// 描画先は対象自身のバックバッファで、装置も文脈も対象のものを借りる。
/// そのため、こちらでは装置もスワップチェーンも作らない。
///
/// 呼ぶ場所には条件がある。対象がその周の絵を描き終えていて、まだ画面へ出していない
/// 時点でなければならない。このサンプルでは橋渡し側が対象の Present を捕まえて
/// 更新を回しているので、更新コールバックがちょうどその時点になる。
///
/// com-abi: vtable スロット・IID・enum 値はすべて Windows SDK のヘッダーから取得したもの。
/// スロットはヘッダーの Vtbl 定義の並び順で、既存の取り込み実装の値とも突き合わせてある。
/// </summary>
internal static class DiceOverlay
{
    // --- enum（d3d11.h / d3dcommon.h / dxgiformat.h より） ---
    private const uint USAGE_DEFAULT = 0, USAGE_DYNAMIC = 2;
    private const uint BIND_VERTEX_BUFFER = 0x1, BIND_CONSTANT_BUFFER = 0x4, BIND_SHADER_RESOURCE = 0x8;
    private const uint CPU_ACCESS_WRITE = 0x10000;
    private const uint MAP_WRITE_DISCARD = 4;
    private const uint INPUT_PER_VERTEX_DATA = 0;
    private const uint FILL_SOLID = 3, CULL_BACK = 3;
    private const uint FILTER_MIN_MAG_MIP_LINEAR = 0x15;
    private const uint TEXTURE_ADDRESS_CLAMP = 3;
    private const uint COMPARISON_ALWAYS = 8;
    private const uint DEPTH_WRITE_MASK_ZERO = 0;
    private const uint STENCIL_OP_KEEP = 1;
    private const uint BLEND_ZERO = 1, BLEND_ONE = 2, BLEND_OP_ADD = 1;
    private const byte COLOR_WRITE_ENABLE_ALL = 15;
    private const uint TOPOLOGY_TRIANGLELIST = 4;
    private const uint FORMAT_R32G32_FLOAT = 16, FORMAT_R32G32B32_FLOAT = 6, FORMAT_R8G8B8A8_UNORM = 28;

    // --- 構造体（ヘッダーの宣言順どおり） ---
    [StructLayout(LayoutKind.Sequential)]
    private struct BUFFER_DESC { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleCount, SampleQuality;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SUBRESOURCE_DATA { public IntPtr pSysMem; public uint SysMemPitch, SysMemSlicePitch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_ELEMENT_DESC
    {
        public IntPtr SemanticName;
        public uint SemanticIndex, Format, InputSlot, AlignedByteOffset, InputSlotClass, InstanceDataStepRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SAMPLER_DESC
    {
        public uint Filter, AddressU, AddressV, AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy, ComparisonFunc;
        public float BorderColor0, BorderColor1, BorderColor2, BorderColor3;
        public float MinLOD, MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RT_BLEND_DESC
    {
        public int BlendEnable;
        public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
        public byte RenderTargetWriteMask;
    }

    // RenderTarget[8] は配列にせず 8 個並べる（値渡しの構造体として寸法を合わせるため）。
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND_DESC
    {
        public int AlphaToCoverageEnable, IndependentBlendEnable;
        public RT_BLEND_DESC RT0, RT1, RT2, RT3, RT4, RT5, RT6, RT7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEPTH_STENCILOP_DESC { public uint StencilFailOp, StencilDepthFailOp, StencilPassOp, StencilFunc; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEPTH_STENCIL_DESC
    {
        public int DepthEnable;
        public uint DepthWriteMask, DepthFunc;
        public int StencilEnable;
        public byte StencilReadMask, StencilWriteMask;
        public DEPTH_STENCILOP_DESC FrontFace, BackFace;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RASTERIZER_DESC
    {
        public uint FillMode, CullMode;
        public int FrontCounterClockwise, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VIEWPORT { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

    // --- シェーダー ---
    // 行ベクトル規約で書くので row_major を明示する（既定は列優先で、転置し忘れると何も映らない）。
    private const string Hlsl = @"
cbuffer Constants : register(b0) { row_major float4x4 mvp; };
Texture2D    tex : register(t0);
SamplerState smp : register(s0);
struct VSIn  { float3 pos : POSITION; float3 col : COLOR; float2 uv : TEXCOORD; };
struct VSOut { float4 pos : SV_POSITION; float3 col : COLOR; float2 uv : TEXCOORD; };
VSOut VSMain(VSIn i) { VSOut o; o.pos = mul(float4(i.pos, 1.0), mvp); o.col = i.col; o.uv = i.uv; return o; }
float4 PSMain(VSOut i) : SV_TARGET { return float4(tex.Sample(smp, i.uv).rgb * i.col, 1.0); }
";

    // --- 呼び出し先 ---

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(IntPtr pSrcData, IntPtr srcDataSize, IntPtr pSourceName,
        IntPtr pDefines, IntPtr pInclude, [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget, uint flags1, uint flags2,
        out IntPtr ppCode, out IntPtr ppErrorMsgs);

    // com-abi: スロットはヘッダーの Vtbl 定義の並び順。既存の取り込み実装が使っている
    // CreateTexture2D=5 / GetImmediateContext=40 / Map=14 / Unmap=15 と一致することを確かめてある。
    private const int SC_GetDevice = 7, SC_GetBuffer = 9;
    private const int DEV_CreateBuffer = 3, DEV_CreateTexture2D = 5, DEV_CreateShaderResourceView = 7,
                      DEV_CreateRenderTargetView = 9, DEV_CreateInputLayout = 11, DEV_CreateVertexShader = 12,
                      DEV_CreatePixelShader = 15, DEV_CreateBlendState = 20, DEV_CreateDepthStencilState = 21,
                      DEV_CreateRasterizerState = 22, DEV_CreateSamplerState = 23, DEV_GetImmediateContext = 40;
    private const int CTX_VSSetConstantBuffers = 7, CTX_PSSetShaderResources = 8, CTX_PSSetShader = 9,
                      CTX_PSSetSamplers = 10, CTX_VSSetShader = 11, CTX_Draw = 13, CTX_Map = 14, CTX_Unmap = 15,
                      CTX_IASetInputLayout = 17, CTX_IASetVertexBuffers = 18, CTX_IASetPrimitiveTopology = 24,
                      CTX_OMSetRenderTargets = 33, CTX_OMSetBlendState = 35, CTX_OMSetDepthStencilState = 36,
                      CTX_RSSetState = 43, CTX_RSSetViewports = 44;
    private const int TEX_GetDesc = 10;
    private const int BLOB_GetBufferPointer = 3, BLOB_GetBufferSize = 4;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDeviceFn(IntPtr self, ref OverlayCom.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferFn(IntPtr self, uint buffer, ref OverlayCom.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTexDescFn(IntPtr self, out TEXTURE2D_DESC desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBufferFn(IntPtr self, ref BUFFER_DESC desc, IntPtr initial, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBufferInitFn(IntPtr self, ref BUFFER_DESC desc, ref SUBRESOURCE_DATA initial, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DFn(IntPtr self, ref TEXTURE2D_DESC desc, ref SUBRESOURCE_DATA initial, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateViewFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateInputLayoutFn(IntPtr self, IntPtr elements, uint count, IntPtr shaderBytecode, IntPtr bytecodeLength, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShaderFn(IntPtr self, IntPtr bytecode, IntPtr length, IntPtr linkage, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBlendStateFn(IntPtr self, ref BLEND_DESC desc, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDepthStencilStateFn(IntPtr self, ref DEPTH_STENCIL_DESC desc, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateRasterizerStateFn(IntPtr self, ref RASTERIZER_DESC desc, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSamplerStateFn(IntPtr self, ref SAMPLER_DESC desc, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetImmediateContextFn(IntPtr self, out IntPtr o);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetArrayFn(IntPtr self, uint startSlot, uint num, ref IntPtr items);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetShaderFn(IntPtr self, IntPtr shader, IntPtr classInstances, uint numClassInstances);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawFn(IntPtr self, uint vertexCount, uint startVertex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, IntPtr resource, uint sub, uint mapType, uint flags, out MAPPED_SUBRESOURCE mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetObjectFn(IntPtr self, IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetVertexBuffersFn(IntPtr self, uint startSlot, uint num, ref IntPtr buffers, ref uint strides, ref uint offsets);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTopologyFn(IntPtr self, uint topology);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void OMSetRenderTargetsFn(IntPtr self, uint num, ref IntPtr rtvs, IntPtr dsv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void OMSetBlendStateFn(IntPtr self, IntPtr blend, IntPtr blendFactor, uint sampleMask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void OMSetDepthStencilStateFn(IntPtr self, IntPtr state, uint stencilRef);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewportsFn(IntPtr self, uint num, ref VIEWPORT vp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPtrFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobSizeFn(IntPtr self);

    private static readonly OverlayCom.GUID IID_ID3D11Device = new OverlayCom.GUID(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);
    private static readonly OverlayCom.GUID IID_ID3D11Texture2D = new OverlayCom.GUID(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);

    private static T Call<T>(IntPtr obj, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(OverlayCom.GetVtableSlot(obj, slot));

    /// <summary>対象から借りたもの、およびこちらで作ったもの。<see cref="Error"/> が null でなければどの段で落ちたかが入る。</summary>
    internal struct Resources
    {
        public IntPtr Device, Context;                 // 借り物。解放しない
        public IntPtr VertexBuffer, ConstantBuffer, Texture, Srv, Sampler;
        public IntPtr InputLayout, VertexShader, PixelShader;
        public IntPtr BlendState, DepthState, RasterState;
        public uint VertexCount, QuadStart;
        public string Error;
        public bool Ok => Error == null;
    }

    /// <summary>
    /// 対象の装置を借りて、描画に要るものを一式作る。
    /// 装置とスワップチェーンは対象のものなので、こちらでは作らないし解放もしない。
    /// </summary>
    internal static Resources Create(IntPtr swapChain)
    {
        var r = new Resources();
        var src = IntPtr.Zero;
        var semantics = IntPtr.Zero;
        var elements = IntPtr.Zero;
        var vsBlob = IntPtr.Zero; var psBlob = IntPtr.Zero;

        try
        {
            var iidDev = IID_ID3D11Device;
            var hr = Call<GetDeviceFn>(swapChain, SC_GetDevice)(swapChain, ref iidDev, out r.Device);
            if (hr != 0 || r.Device == IntPtr.Zero) { r.Error = $"GetDevice hr=0x{hr:X}"; return r; }

            Call<GetImmediateContextFn>(r.Device, DEV_GetImmediateContext)(r.Device, out r.Context);
            if (r.Context == IntPtr.Zero) { r.Error = "GetImmediateContext returned null"; return r; }

            // --- シェーダー ---
            var bytes = System.Text.Encoding.ASCII.GetBytes(Hlsl);
            src = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, src, bytes.Length);

            if (!Compile(src, bytes.Length, "VSMain", "vs_5_0", out vsBlob, out var vsErr)) { r.Error = "VS: " + vsErr; return r; }
            if (!Compile(src, bytes.Length, "PSMain", "ps_5_0", out psBlob, out var psErr)) { r.Error = "PS: " + psErr; return r; }

            hr = Call<CreateShaderFn>(r.Device, DEV_CreateVertexShader)(r.Device, BlobPtr(vsBlob), BlobSize(vsBlob), IntPtr.Zero, out r.VertexShader);
            if (hr != 0) { r.Error = $"CreateVertexShader hr=0x{hr:X}"; return r; }
            hr = Call<CreateShaderFn>(r.Device, DEV_CreatePixelShader)(r.Device, BlobPtr(psBlob), BlobSize(psBlob), IntPtr.Zero, out r.PixelShader);
            if (hr != 0) { r.Error = $"CreatePixelShader hr=0x{hr:X}"; return r; }

            // --- 入力レイアウト ---
            // セマンティック名は LPCSTR なので、呼び出しが終わるまで生きた番地が要る。
            var posName = Marshal.StringToHGlobalAnsi("POSITION");
            var colName = Marshal.StringToHGlobalAnsi("COLOR");
            var uvName = Marshal.StringToHGlobalAnsi("TEXCOORD");
            semantics = posName;
            var elemSize = Marshal.SizeOf<INPUT_ELEMENT_DESC>();
            elements = Marshal.AllocHGlobal(elemSize * 3);
            Marshal.StructureToPtr(new INPUT_ELEMENT_DESC { SemanticName = posName, Format = FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 0, InputSlotClass = INPUT_PER_VERTEX_DATA }, elements, false);
            Marshal.StructureToPtr(new INPUT_ELEMENT_DESC { SemanticName = colName, Format = FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 12, InputSlotClass = INPUT_PER_VERTEX_DATA }, elements + elemSize, false);
            Marshal.StructureToPtr(new INPUT_ELEMENT_DESC { SemanticName = uvName, Format = FORMAT_R32G32_FLOAT, AlignedByteOffset = 24, InputSlotClass = INPUT_PER_VERTEX_DATA }, elements + elemSize * 2, false);

            hr = Call<CreateInputLayoutFn>(r.Device, DEV_CreateInputLayout)(r.Device, elements, 3, BlobPtr(vsBlob), BlobSize(vsBlob), out r.InputLayout);
            if (hr != 0) { r.Error = $"CreateInputLayout hr=0x{hr:X}"; return r; }

            // --- 頂点バッファ（内容は変わらないので既定の置き場に固定で持つ） ---
            var verts = DiceGeometry.Build();
            r.VertexCount = (uint)(verts.Length / DiceGeometry.FloatsPerVertex) - DiceGeometry.QuadVertexCount;
            r.QuadStart = r.VertexCount;

            var vbBytes = verts.Length * sizeof(float);
            var vbMem = Marshal.AllocHGlobal(vbBytes);
            try
            {
                Marshal.Copy(verts, 0, vbMem, verts.Length);
                var vbDesc = new BUFFER_DESC { ByteWidth = (uint)vbBytes, Usage = USAGE_DEFAULT, BindFlags = BIND_VERTEX_BUFFER };
                var vbData = new SUBRESOURCE_DATA { pSysMem = vbMem };
                hr = Call<CreateBufferInitFn>(r.Device, DEV_CreateBuffer)(r.Device, ref vbDesc, ref vbData, out r.VertexBuffer);
                if (hr != 0) { r.Error = $"CreateBuffer(vertex) hr=0x{hr:X}"; return r; }
            }
            finally { Marshal.FreeHGlobal(vbMem); }

            // --- 定数バッファ（毎フレーム書き換えるので書き込みできる置き場に） ---
            var cbDesc = new BUFFER_DESC
            {
                ByteWidth = 64, Usage = USAGE_DYNAMIC,
                BindFlags = BIND_CONSTANT_BUFFER, CPUAccessFlags = CPU_ACCESS_WRITE,
            };
            hr = Call<CreateBufferFn>(r.Device, DEV_CreateBuffer)(r.Device, ref cbDesc, IntPtr.Zero, out r.ConstantBuffer);
            if (hr != 0) { r.Error = $"CreateBuffer(constant) hr=0x{hr:X}"; return r; }

            // --- 絵 ---
            // 転送用の中継も境界合わせも要らない。作るときに中身をそのまま渡せる。
            var px = DiceGeometry.BuildAtlas();
            var texMem = Marshal.AllocHGlobal(px.Length);
            try
            {
                Marshal.Copy(px, 0, texMem, px.Length);
                var texDesc = new TEXTURE2D_DESC
                {
                    Width = DiceGeometry.AtlasWidth, Height = DiceGeometry.AtlasHeight,
                    MipLevels = 1, ArraySize = 1, Format = FORMAT_R8G8B8A8_UNORM,
                    SampleCount = 1, SampleQuality = 0,
                    Usage = USAGE_DEFAULT, BindFlags = BIND_SHADER_RESOURCE,
                };
                var texData = new SUBRESOURCE_DATA { pSysMem = texMem, SysMemPitch = DiceGeometry.AtlasWidth * 4 };
                hr = Call<CreateTexture2DFn>(r.Device, DEV_CreateTexture2D)(r.Device, ref texDesc, ref texData, out r.Texture);
                if (hr != 0) { r.Error = $"CreateTexture2D hr=0x{hr:X}"; return r; }
            }
            finally { Marshal.FreeHGlobal(texMem); }

            // 既定の見え方でよいので、説明は渡さない。
            hr = Call<CreateViewFn>(r.Device, DEV_CreateShaderResourceView)(r.Device, r.Texture, IntPtr.Zero, out r.Srv);
            if (hr != 0) { r.Error = $"CreateShaderResourceView hr=0x{hr:X}"; return r; }

            var sampDesc = new SAMPLER_DESC
            {
                Filter = FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = TEXTURE_ADDRESS_CLAMP, AddressV = TEXTURE_ADDRESS_CLAMP, AddressW = TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = COMPARISON_ALWAYS, MaxLOD = float.MaxValue,
            };
            hr = Call<CreateSamplerStateFn>(r.Device, DEV_CreateSamplerState)(r.Device, ref sampDesc, out r.Sampler);
            if (hr != 0) { r.Error = $"CreateSamplerState hr=0x{hr:X}"; return r; }

            // --- 状態 ---
            // 混ぜずに上書きする。奥行きの判定も使わない（凸なので裏面を捨てれば足りる）。
            var rt = new RT_BLEND_DESC
            {
                SrcBlend = BLEND_ONE, DestBlend = BLEND_ZERO, BlendOp = BLEND_OP_ADD,
                SrcBlendAlpha = BLEND_ONE, DestBlendAlpha = BLEND_ZERO, BlendOpAlpha = BLEND_OP_ADD,
                RenderTargetWriteMask = COLOR_WRITE_ENABLE_ALL,
            };
            var blendDesc = new BLEND_DESC { RT0 = rt, RT1 = rt, RT2 = rt, RT3 = rt, RT4 = rt, RT5 = rt, RT6 = rt, RT7 = rt };
            hr = Call<CreateBlendStateFn>(r.Device, DEV_CreateBlendState)(r.Device, ref blendDesc, out r.BlendState);
            if (hr != 0) { r.Error = $"CreateBlendState hr=0x{hr:X}"; return r; }

            var op = new DEPTH_STENCILOP_DESC
            {
                StencilFailOp = STENCIL_OP_KEEP, StencilDepthFailOp = STENCIL_OP_KEEP,
                StencilPassOp = STENCIL_OP_KEEP, StencilFunc = COMPARISON_ALWAYS,
            };
            var dsDesc = new DEPTH_STENCIL_DESC
            {
                DepthEnable = 0, DepthWriteMask = DEPTH_WRITE_MASK_ZERO, DepthFunc = COMPARISON_ALWAYS,
                StencilEnable = 0, StencilReadMask = 0xff, StencilWriteMask = 0xff,
                FrontFace = op, BackFace = op,
            };
            hr = Call<CreateDepthStencilStateFn>(r.Device, DEV_CreateDepthStencilState)(r.Device, ref dsDesc, out r.DepthState);
            if (hr != 0) { r.Error = $"CreateDepthStencilState hr=0x{hr:X}"; return r; }

            var rsDesc = new RASTERIZER_DESC
            {
                FillMode = FILL_SOLID, CullMode = CULL_BACK,
                FrontCounterClockwise = 0, DepthClipEnable = 1,
            };
            hr = Call<CreateRasterizerStateFn>(r.Device, DEV_CreateRasterizerState)(r.Device, ref rsDesc, out r.RasterState);
            if (hr != 0) { r.Error = $"CreateRasterizerState hr=0x{hr:X}"; return r; }

            return r;
        }
        catch (Exception ex)
        {
            r.Error = ex.GetType().Name + ": " + ex.Message;
            return r;
        }
        finally
        {
            if (vsBlob != IntPtr.Zero) OverlayCom.Release(vsBlob);
            if (psBlob != IntPtr.Zero) OverlayCom.Release(psBlob);
            if (src != IntPtr.Zero) Marshal.FreeHGlobal(src);
            if (elements != IntPtr.Zero) Marshal.FreeHGlobal(elements);
            if (semantics != IntPtr.Zero) Marshal.FreeHGlobal(semantics);
        }
    }

    /// <summary>
    /// 対象のバックバッファへ 1 周ぶん描き足す。
    /// バックバッファは周ごとに入れ替わるので、その都度取り直す。
    /// </summary>
    internal static string Draw(IntPtr swapChain, ref Resources r, float angle, string fpsText)
    {
        var backBuffer = IntPtr.Zero;
        var rtv = IntPtr.Zero;
        try
        {
            var iidTex = IID_ID3D11Texture2D;
            var hr = Call<GetBufferFn>(swapChain, SC_GetBuffer)(swapChain, 0, ref iidTex, out backBuffer);
            if (hr != 0 || backBuffer == IntPtr.Zero) return $"GetBuffer hr=0x{hr:X}";

            Call<GetTexDescFn>(backBuffer, TEX_GetDesc)(backBuffer, out var desc);

            hr = Call<CreateViewFn>(r.Device, DEV_CreateRenderTargetView)(r.Device, backBuffer, IntPtr.Zero, out rtv);
            if (hr != 0 || rtv == IntPtr.Zero) return $"CreateRenderTargetView hr=0x{hr:X}";

            var ctx = r.Context;

            // 対象の絵は消さない。奥行きも渡さないので、こちらの描画は素通しで重なる。
            var rtvLocal = rtv;
            Call<OMSetRenderTargetsFn>(ctx, CTX_OMSetRenderTargets)(ctx, 1, ref rtvLocal, IntPtr.Zero);

            var vp = new VIEWPORT { Width = desc.Width, Height = desc.Height, MinDepth = 0f, MaxDepth = 1f };
            Call<SetViewportsFn>(ctx, CTX_RSSetViewports)(ctx, 1, ref vp);

            Call<SetObjectFn>(ctx, CTX_RSSetState)(ctx, r.RasterState);
            Call<OMSetBlendStateFn>(ctx, CTX_OMSetBlendState)(ctx, r.BlendState, IntPtr.Zero, 0xFFFFFFFF);
            Call<OMSetDepthStencilStateFn>(ctx, CTX_OMSetDepthStencilState)(ctx, r.DepthState, 0);

            Call<SetObjectFn>(ctx, CTX_IASetInputLayout)(ctx, r.InputLayout);
            Call<SetTopologyFn>(ctx, CTX_IASetPrimitiveTopology)(ctx, TOPOLOGY_TRIANGLELIST);

            var vb = r.VertexBuffer;
            uint stride = DiceGeometry.FloatsPerVertex * sizeof(float), offset = 0;
            Call<SetVertexBuffersFn>(ctx, CTX_IASetVertexBuffers)(ctx, 0, 1, ref vb, ref stride, ref offset);

            Call<SetShaderFn>(ctx, CTX_VSSetShader)(ctx, r.VertexShader, IntPtr.Zero, 0);
            Call<SetShaderFn>(ctx, CTX_PSSetShader)(ctx, r.PixelShader, IntPtr.Zero, 0);

            var cb = r.ConstantBuffer;
            Call<SetArrayFn>(ctx, CTX_VSSetConstantBuffers)(ctx, 0, 1, ref cb);
            var srv = r.Srv;
            Call<SetArrayFn>(ctx, CTX_PSSetShaderResources)(ctx, 0, 1, ref srv);
            var samp = r.Sampler;
            Call<SetArrayFn>(ctx, CTX_PSSetSamplers)(ctx, 0, 1, ref samp);

            var aspect = desc.Height == 0 ? 1f : (float)desc.Width / desc.Height;

            // サイコロ
            if (!SetMatrix(ctx, r.ConstantBuffer, DiceGeometry.BuildMvp(angle, aspect), out var mapErr)) return mapErr;
            Call<DrawFn>(ctx, CTX_Draw)(ctx, r.VertexCount, 0);

            // 速さの表示。単位クアッドを矩形へ変形して並べる。
            foreach (var m in DiceGeometry.LayOutNumber(fpsText, -0.94f, 0.94f, 0.14f, aspect))
            {
                if (!SetMatrix(ctx, r.ConstantBuffer, m, out mapErr)) return mapErr;
                Call<DrawFn>(ctx, CTX_Draw)(ctx, DiceGeometry.QuadVertexCount, r.QuadStart);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (rtv != IntPtr.Zero) OverlayCom.Release(rtv);
            if (backBuffer != IntPtr.Zero) OverlayCom.Release(backBuffer);
        }
    }

    private static bool SetMatrix(IntPtr ctx, IntPtr buffer, float[] m, out string error)
    {
        error = null;
        var hr = Call<MapFn>(ctx, CTX_Map)(ctx, buffer, 0, MAP_WRITE_DISCARD, 0, out var mapped);
        if (hr != 0) { error = $"Map(constant) hr=0x{hr:X}"; return false; }
        Marshal.Copy(m, 0, mapped.pData, m.Length);
        Call<UnmapFn>(ctx, CTX_Unmap)(ctx, buffer, 0);
        return true;
    }

    /// <summary>こちらで作ったものだけ手放す。装置と文脈は借り物なので触らない。</summary>
    internal static void Release(ref Resources r)
    {
        var names = new[] { "Raster", "Depth", "Blend", "Sampler", "Srv", "Texture",
                            "ConstantBuffer", "VertexBuffer", "PixelShader", "VertexShader", "InputLayout" };
        var objs = new[] { r.RasterState, r.DepthState, r.BlendState, r.Sampler, r.Srv, r.Texture,
                           r.ConstantBuffer, r.VertexBuffer, r.PixelShader, r.VertexShader, r.InputLayout };
        for (int i = 0; i < objs.Length; i++)
        {
            if (objs[i] == IntPtr.Zero) continue;
            OverlayCom.Release(objs[i]);
        }

        // 借りた側も、取得のときに参照が 1 つ増えている。
        if (r.Context != IntPtr.Zero) { OverlayCom.Release(r.Context); }
        if (r.Device != IntPtr.Zero) { OverlayCom.Release(r.Device); }

        r = default;
    }

    // --- 小道具 ---

    private static bool Compile(IntPtr src, int len, string entry, string target, out IntPtr blob, out string error)
    {
        var hr = D3DCompile(src, (IntPtr)len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, entry, target, 0, 0, out blob, out var errBlob);
        error = hr == 0 ? null : $"hr=0x{hr:X} {BlobText(errBlob)}";
        if (errBlob != IntPtr.Zero) OverlayCom.Release(errBlob);
        return hr == 0;
    }

    private static IntPtr BlobPtr(IntPtr blob) => Call<BlobPtrFn>(blob, BLOB_GetBufferPointer)(blob);
    private static IntPtr BlobSize(IntPtr blob) => Call<BlobSizeFn>(blob, BLOB_GetBufferSize)(blob);

    private static string BlobText(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return "";
        var ptr = BlobPtr(blob);
        var size = BlobSize(blob).ToInt32();
        return size <= 0 ? "" : Marshal.PtrToStringAnsi(ptr, size);
    }
}

/// <summary>
/// 対象が画面へ出す直前に割り込む。vtable の該当スロットを自前の入口へ差し替えるだけで、
/// フックの仕組みには依存しない。
///
/// これが要るのは、ホストの更新コールバックが「対象が描き終えた後・画面へ出す前」に
/// 来るとは限らないため。更新が描画より前に走る環境では、そこで描いても対象の絵に
/// 上書きされてしまう。割り込めば、どの環境でも正しい時点になる。
///
/// vtable はクラスで共有されているので、差し替えは同じ種類のすべてに効く。
/// 先に誰かが割り込んでいても、元の値を控えて渡すので繋がったまま残る。
///
/// 入口は対象の描画スレッドから呼ばれる。実行環境から見ると初めて来るスレッドだが、
/// ネイティブから関数ポインタ越しにマネージドへ入る経路では実行環境が入口で結び付けるので、
/// こちらで用意することは何もない。
///
/// 復元は最優先。控えた値を戻さないままこの型が入れ替わると、次の呼び出しで行き先が
/// 無くなって落ちる。呼び出し側は必ず永続登録を持ち、停止時に Restore を通すこと。
/// </summary>
internal static class PresentHook
{
    // vtable の 8 番目が画面へ出す関数（IUnknown 3 ・IDXGIObject 4 ・IDXGIDeviceSubObject 1 の後）
    private const int SlotPresent = 8;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentFn(IntPtr self, uint sync, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);

    private const uint PAGE_READWRITE = 0x04;

    // 一度作った入口は二度と手放さない。
    // vtable を元へ戻しても、そのとき既に入口の中にいる呼び出しがありうるし、
    // 別の誰かが入口の番地を控えていることもある。手放して回収されると、
    // その後の呼び出しが行き先を失い、実行環境の内側で落ちる。
    // 差し替え先を変える必要は無いので、作り直しもしない。
    private static PresentFn s_entry;
    private static IntPtr s_entryPtr;

    private static PresentFn s_original;
    private static IntPtr s_slotAddress, s_originalPtr;
    private static Action<IntPtr> s_before;

    // 控えた本来の関数は、差し替えを戻した後も手放さない。
    // 戻した瞬間にも、まだ入口の中を進んでいる呼び出しが残っている。そこで渡す先を
    // 失うと「画面へ出した」という嘘を返すことになり、対象は出たものとして次へ進む。
    // 出るはずのフレームが永久に来ず、描画側が待ちに入ったまま固まる。
    private static PresentFn s_fallback;

    // vtable はクラスで共有されているので、差し替えるとプロセス内の
    // すべてのスワップチェーンが入口を通る。描くのは頼まれた 1 つだけにする。
    private static IntPtr s_target;
    private static int s_foreignLogs;

    // 割り込んだ先で描くと、その描画がまた画面へ出す呼び出しに入ることがある。
    // 同じスレッドで二度入らないようにする。
    [ThreadStatic] private static bool t_inside;

    // 入口を通っている最中の数。戻したあとも 0 になるまでは資源を手放せない。
    private static int s_active;

    private static volatile bool s_installed;

    // 入口が入口を呼び返していないか。先客が「元の関数」として我々の番地を控えると、
    // 呼び出しが再帰して戻ってこなくなり、積み上がった末に対象ごと落ちる。
    [ThreadStatic] private static int t_depth;
    private static int s_depthLogs;

    // 再帰したと判断する深さ。正常時は 1 段しか入らない。
    // 深くすると、断つまでに積み上がる分だけスタックを削る。
    private const int MaxDepth = 3;

    // 何も映さずに返るときの値。成功として扱われるので対象は次へ進む。
    private const int DXGI_STATUS_OCCLUDED = 0x087A0001;
    private static int s_loopLogs;
    private static volatile bool s_loopDetected;

    /// <summary>呼び出しが再帰したのを見つけて降りたかどうか。見つけたら描くのはやめている。</summary>
    internal static bool LoopDetected => s_loopDetected;

    /// <summary>
    /// 再帰を見たという記憶を消す。利用者が明示的に始め直したときだけ呼ぶ。
    /// 自動で消すと、同じ状況で張り直しては再帰する、を繰り返す。
    /// </summary>
    internal static void ForgetLoop()
    {
        s_loopDetected = false;
        s_loopLogs = 0;
        s_entryPrologue = null;
        s_entryLogs = 0;
    }

    // vtable の値が誰かに書き換えられていないか。更新のたびに 1 回だけ読む。
    private static IntPtr s_lastSeenSlot;
    private static int s_slotLogs;

    // 入口そのもののコードが書き換えられていないか。
    // 先客は「表に入っている番地」を画面へ出す関数の実体だと見なすので、
    // こちらが差し替えた後に先客が張り直すと、我々のコードの先頭が飛び越しに変えられる。
    private static byte[] s_entryPrologue;
    private static int s_entryLogs;

    // vtable のスロットを差し替えている間、その事実と控えた番地をプロセス全体へ知らせる。
    // 使い捨てのスワップチェーンを作るノードがここを見て、作らずに済ませられる。
    // 作ると、先に割り込んでいるソフトウェアが表を読み直し、そこに入っているこちらの
    // コードの先頭を書き換えるので、呼び出しが再帰して対象は画を出せなくなる。
    // 入れ物は framework の型だけで作る。実装が入れ替わっても読めるようにするため。
    private const string SwapNoticeKey = "ngol.gfx.present_vtable_swap.v1";
    private static long[] s_swapNotice;

    private static void PublishSwapNotice()
    {
        s_swapNotice = new[] { s_originalPtr.ToInt64(), s_entryPtr.ToInt64(), s_slotAddress.ToInt64() };
        AppDomain.CurrentDomain.SetData(SwapNoticeKey, s_swapNotice);
    }

    private static void WithdrawSwapNotice()
    {
        // 古い世代の片づけが、新しい世代の知らせを消さないようにする。
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData(SwapNoticeKey), s_swapNotice))
            AppDomain.CurrentDomain.SetData(SwapNoticeKey, null);
        s_swapNotice = null;
    }

    /// <summary>入口を今まさに通っている呼び出しがあるかどうか。</summary>
    internal static bool Busy => System.Threading.Volatile.Read(ref s_active) > 0;

    internal static int ActiveCount => System.Threading.Volatile.Read(ref s_active);

    /// <summary>
    /// 控えたスロットが今も自分の入口を指しているかを見る。先客が張り直すと、そのとき
    /// 表に入っている「我々の入口」が先客側の「元の関数」として控えられ、
    /// 呼び出しが互いを呼び合って終わらなくなる。
    /// </summary>
    internal static void CheckSlot()
    {
        if (s_slotAddress == IntPtr.Zero) return;
        IntPtr cur;
        try { cur = Marshal.ReadIntPtr(s_slotAddress); } catch { return; }
        if (cur == s_lastSeenSlot) return;
        s_lastSeenSlot = cur;

        // 自分の入口のままなのは正常。ここを知らせると、正常な状態が
        // 毎回警告として出ることになり、本物の横取りと見分けが付かなくなる。
        if (cur == s_entryPtr) return;

        if (s_slotLogs++ >= 20) return;
        OverlayReport.W(cur == s_originalPtr
            ? $"SLOT RESTORED  the call table points at the original again (0x{cur.ToInt64():X}); "
              + "something put it back, so nothing reaches this overlay any more"
            : $"SLOT TAKEN  now=0x{cur.ToInt64():X} ours=0x{s_entryPtr.ToInt64():X} "
              + $"orig=0x{s_originalPtr.ToInt64():X}; someone else replaced the entry after this one did");
    }

    /// <summary>
    /// 入口のコードそのものが書き換えられていないかを見る。
    /// 先頭が飛び越しに変えられていたら、先客がこちらを画面へ出す関数だと見なして
    /// 割り込んだということで、そのまま動かし続けると呼び出しが再帰する。
    /// </summary>
    internal static void CheckEntryCode()
    {
        if (s_entryPtr == IntPtr.Zero) return;
        var cur = new byte[16];
        try { Marshal.Copy(s_entryPtr, cur, 0, cur.Length); } catch { return; }

        if (s_entryPrologue == null)
        {
            s_entryPrologue = cur;
            return;
        }
        for (int i = 0; i < cur.Length; i++)
        {
            if (cur[i] == s_entryPrologue[i]) continue;
            if (s_entryLogs++ < 5)
                OverlayReport.W($"ENTRY CODE OVERWRITTEN  was={Hex(s_entryPrologue)}  now={Hex(cur)}");
            s_entryPrologue = cur;
            return;
        }
    }

    private static string Hex(byte[] b)
    {
        var sb = new System.Text.StringBuilder(b.Length * 3);
        foreach (var x in b) sb.Append(x.ToString("x2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    internal static bool Installed => s_installed;

    /// <summary>そのスワップチェーンの vtable で、画面へ出す関数が入っているスロットの番地。</summary>
    internal static IntPtr SlotAddressOf(IntPtr swapChain)
    {
        if (swapChain == IntPtr.Zero) return IntPtr.Zero;
        return Marshal.ReadIntPtr(swapChain) + SlotPresent * IntPtr.Size;
    }

    /// <summary>
    /// 差し替える。before は元の呼び出しの直前に、対象の番地を受け取って呼ばれる。
    /// target が空なら、最初に入ってきた呼び出しの相手を対象として覚える。
    /// </summary>
    internal static string Install(IntPtr slotAddress, IntPtr target, Action<IntPtr> before)
    {
        if (s_installed) return "already installed";
        if (slotAddress == IntPtr.Zero) return "call table entry address is null";

        s_target = target;
        s_slotAddress = slotAddress;
        s_originalPtr = Marshal.ReadIntPtr(s_slotAddress);
        if (s_originalPtr == IntPtr.Zero) return "call table entry is null";

        // 入口は最初の一度だけ作る。以後は同じものを使い回す。
        if (s_entry == null)
        {
            s_entry = Entry;
            s_entryPtr = Marshal.GetFunctionPointerForDelegate(s_entry);
        }

        // 控えた値が自分の入口では、呼び出しが自分へ戻り続けて終わらない。
        // vtable を読み直す種類のノードと組み合わせると実際に起こりうる。
        if (s_originalPtr == s_entryPtr)
            return "the call table already points at our own entry";

        s_original = Marshal.GetDelegateForFunctionPointer<PresentFn>(s_originalPtr);
        s_fallback = s_original;
        s_before = before;

        // vtable は書き込めない場所に置かれていることがある。
        if (!VirtualProtect(s_slotAddress, (UIntPtr)(uint)IntPtr.Size, PAGE_READWRITE, out var old))
            return "VirtualProtect failed err=" + Marshal.GetLastWin32Error();
        Marshal.WriteIntPtr(s_slotAddress, s_entryPtr);
        VirtualProtect(s_slotAddress, (UIntPtr)(uint)IntPtr.Size, old, out _);

        s_installed = true;
        PublishSwapNotice();
        return null;
    }

    private static int Entry(IntPtr self, uint sync, uint flags)
    {
        // ここは対象の描画スレッド。こちらの実行環境から見ると初めて来るスレッドだが、
        // ネイティブから関数ポインタ越しにマネージドへ入る経路では、実行環境が入口で
        // 自分に結び付けるので、こちらで用意することは何もない。
        //
        // 対象側の実行環境へこのスレッドを登録する必要も無い。ここから触るのは
        // 描画装置と OS の API だけで、対象の管理オブジェクトには一切触れないため。
        var n = System.Threading.Interlocked.Increment(ref s_active);
        t_depth++;
        if (t_depth >= 2 && s_depthLogs++ < 40)
            OverlayReport.W($"RE-ENTRY depth={t_depth} self=0x{self.ToInt64():X} active={n}");
        try
        {
            // 一度再帰を見たら、以後は何も呼ばずに返る。控えの先が呼び返してくるので、
            // 通せば毎周また積み上がる。
            if (s_loopDetected) return DXGI_STATUS_OCCLUDED;

            // 呼び出しが再帰している。積み上がるとスタックを使い切り、例外を配送する
            // スロットすら取れずに対象ごと落ちる。浅いうちに断つ。
            //
            // ここで控えを呼んではいけない。控えの先は先に割り込んでいる側の実装で、
            // それがこちらを呼び返しているので、呼べば 1 段深いところで同じ判断を
            // やり直すだけになる。断つには呼ばずに返るしかない。
            //
            // 返す値は「何も映さなかった」を表すもの。画は止まるが対象は生きたまま残り、
            // 差し替えを戻してあるので、停止すれば元へ戻る。
            if (t_depth > MaxDepth)
            {
                if (s_loopLogs++ < 3)
                    OverlayReport.W($"CALL LOOP detected at depth={t_depth}; not calling through, reporting nothing was shown");
                s_loopDetected = true;
                Restore();
                return DXGI_STATUS_OCCLUDED;
            }

            var original = s_original;

            // 対象を渡されずにスロットだけ差し替えた場合、ここが最初の手がかりになる。
            // vtable は共有なので誰の呼び出しも来るが、画面へ出し続けているのは対象なので
            // 最初に入ってきた相手を対象として覚える。
            if (s_target == IntPtr.Zero)
                System.Threading.Interlocked.CompareExchange(ref s_target, self, IntPtr.Zero);

            // 頼まれた相手でなければ何もせず通す。表は共有なので、別の誰かが作った
            // スワップチェーンもここへ来る。他人の絵に、他人の装置で作った物を使うと壊れる。
            if (self != s_target)
            {
                if (s_foreignLogs++ < 20)
                    OverlayReport.W($"FOREIGN swapchain self=0x{self.ToInt64():X} target=0x{s_target.ToInt64():X} - passing through");
                return original == null ? 0 : original(self, sync, flags);
            }

            if (!t_inside && s_before != null)
            {
                t_inside = true;
                // ここで投げると対象が画面へ出せなくなる。握って、以後は描かないようにする。
                try { s_before(self); }
                catch (Exception ex) { OverlayReport.W("entry before THREW " + ex); s_before = null; }
                finally { t_inside = false; }
            }
            // 戻された後に入ってきた呼び出しにも、控えがあるので必ず渡せる。
            // ここで何もせず返すと「画面へ出した」という嘘になり、対象が固まる。
            var target = original ?? s_fallback;
            return target == null ? 0 : target(self, sync, flags);
        }
        finally
        {
            t_depth--;
            var left = System.Threading.Interlocked.Decrement(ref s_active);
        }
    }

    /// <summary>
    /// 控えた値を戻す。何度呼んでもよい。
    /// 戻した後も、入口を通っている最中の呼び出しが残っていることがある。
    /// 呼び出し側は Busy が下りてから資源を手放すこと。
    /// </summary>
    internal static void Restore()
    {
        if (!s_installed) { return; }
        s_installed = false;
        s_before = null;
        WithdrawSwapNotice();

        if (s_slotAddress != IntPtr.Zero && s_originalPtr != IntPtr.Zero
            && VirtualProtect(s_slotAddress, (UIntPtr)(uint)IntPtr.Size, PAGE_READWRITE, out var old))
        {
            Marshal.WriteIntPtr(s_slotAddress, s_originalPtr);
            VirtualProtect(s_slotAddress, (UIntPtr)(uint)IntPtr.Size, old, out _);
        }

        s_slotAddress = IntPtr.Zero;
        s_originalPtr = IntPtr.Zero;
        s_original = null;
        s_target = IntPtr.Zero;
        // 入口そのものは手放さない。まだ中にいる呼び出しや、番地を控えている誰かがいる。
    }

    /// <summary>入口を通っている呼び出しが抜けるのを待つ。抜けきったら true。</summary>
    internal static bool WaitUntilIdle(int timeoutMs)
    {
        var until = Environment.TickCount + timeoutMs;
        while (Busy)
        {
            if (Environment.TickCount - until >= 0) { return false; }
            System.Threading.Thread.Sleep(1);
        }
        return true;
    }
}
