using System;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// vtable 越しに COM を呼ぶための最小限の道具。
///
/// 生きたオブジェクトのポインタを外から受け取って叩くため、型情報は当てにできない。
/// com-abi: slot 2 は IUnknown::Release（公開インターフェース定義のとおり）。
/// </summary>
internal static class D3D12Com
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
/// 回転する立方体を描くためのパイプライン一式。
/// 深度バッファは持たない。立方体は凸なので背面カリングだけで正しく見える。
///
/// com-abi: vtable スロット・IID・enum 値はすべて Windows SDK のヘッダーから取得したもの。
/// </summary>
internal static class D3D12Cube
{
    // --- enum（d3d12.h より） ---
    internal const uint HEAP_TYPE_UPLOAD = 2;
    internal const uint RESOURCE_DIMENSION_BUFFER = 1;
    internal const uint TEXTURE_LAYOUT_ROW_MAJOR = 1;
    internal const uint RESOURCE_STATE_GENERIC_READ = 0x1 | 0x2 | 0x40 | 0x80 | 0x200 | 0x800;   // = 0xAC3
    internal const uint ROOT_PARAMETER_TYPE_32BIT_CONSTANTS = 1;
    internal const uint SHADER_VISIBILITY_ALL = 0;
    internal const uint ROOT_SIGNATURE_FLAG_ALLOW_IA_INPUT_LAYOUT = 0x1;
    internal const uint ROOT_SIGNATURE_VERSION_1 = 0x1;
    internal const uint FILL_MODE_SOLID = 3;
    internal const uint CULL_MODE_BACK = 3;
    internal const uint PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE = 3;
    internal const uint PRIMITIVE_TOPOLOGY_TRIANGLELIST = 4;
    internal const uint INPUT_CLASSIFICATION_PER_VERTEX = 0;
    internal const uint FORMAT_R32G32B32_FLOAT = 6;
    internal const uint FORMAT_R8G8B8A8_UNORM = 28;
    internal const uint BLEND_ONE = 2, BLEND_ZERO = 1, BLEND_OP_ADD = 1, LOGIC_OP_NOOP = 4;
    internal const byte COLOR_WRITE_ENABLE_ALL = 15;
    internal const uint COMPARISON_FUNC_LESS = 2, COMPARISON_FUNC_ALWAYS = 8, STENCIL_OP_KEEP = 1;

    // --- 構造体（ヘッダーの宣言順どおり） ---
    [StructLayout(LayoutKind.Sequential)] internal struct SHADER_BYTECODE { public IntPtr pShaderBytecode; public IntPtr BytecodeLength; }
    [StructLayout(LayoutKind.Sequential)] internal struct STREAM_OUTPUT_DESC { public IntPtr pSODeclaration; public uint NumEntries; public IntPtr pBufferStrides; public uint NumStrides, RasterizedStream; }
    [StructLayout(LayoutKind.Sequential)] internal struct CACHED_PIPELINE_STATE { public IntPtr pCachedBlob; public IntPtr CachedBlobSizeInBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT_LAYOUT_DESC { public IntPtr pInputElementDescs; public uint NumElements; }
    [StructLayout(LayoutKind.Sequential)] internal struct SAMPLE_DESC { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT_ELEMENT_DESC
    {
        public IntPtr SemanticName;
        public uint SemanticIndex, Format, InputSlot, AlignedByteOffset, InputSlotClass, InstanceDataStepRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RT_BLEND_DESC
    {
        public int BlendEnable, LogicOpEnable;
        public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha, LogicOp;
        public byte RenderTargetWriteMask;
    }

    // RenderTarget[8] は配列にせず 8 個並べる（値渡しの構造体として寸法を合わせるため）。
    [StructLayout(LayoutKind.Sequential)]
    internal struct BLEND_DESC
    {
        public int AlphaToCoverageEnable, IndependentBlendEnable;
        public RT_BLEND_DESC RT0, RT1, RT2, RT3, RT4, RT5, RT6, RT7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RASTERIZER_DESC
    {
        public uint FillMode, CullMode;
        public int FrontCounterClockwise, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClipEnable, MultisampleEnable, AntialiasedLineEnable;
        public uint ForcedSampleCount, ConservativeRaster;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DEPTH_STENCILOP_DESC { public uint StencilFailOp, StencilDepthFailOp, StencilPassOp, StencilFunc; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEPTH_STENCIL_DESC
    {
        public int DepthEnable;
        public uint DepthWriteMask, DepthFunc;
        public int StencilEnable;
        public byte StencilReadMask, StencilWriteMask;
        public DEPTH_STENCILOP_DESC FrontFace, BackFace;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GRAPHICS_PIPELINE_STATE_DESC
    {
        public IntPtr pRootSignature;
        public SHADER_BYTECODE VS, PS, DS, HS, GS;
        public STREAM_OUTPUT_DESC StreamOutput;
        public BLEND_DESC BlendState;
        public uint SampleMask;
        public RASTERIZER_DESC RasterizerState;
        public DEPTH_STENCIL_DESC DepthStencilState;
        public INPUT_LAYOUT_DESC InputLayout;
        public uint IBStripCutValue, PrimitiveTopologyType, NumRenderTargets;
        public uint RTVFormat0, RTVFormat1, RTVFormat2, RTVFormat3, RTVFormat4, RTVFormat5, RTVFormat6, RTVFormat7;
        public uint DSVFormat;
        public SAMPLE_DESC SampleDesc;
        public uint NodeMask;
        public CACHED_PIPELINE_STATE CachedPSO;
        public uint Flags;
    }

    // ParameterType の後、union は 8 境界から始まり 16 バイト分の場所を取る。
    [StructLayout(LayoutKind.Sequential)]
    internal struct ROOT_PARAMETER
    {
        public uint ParameterType;
        public uint _pad0;
        public uint ShaderRegister, RegisterSpace, Num32BitValues, _pad1;
        public uint ShaderVisibility;
        public uint _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ROOT_SIGNATURE_DESC
    {
        public uint NumParameters;
        public IntPtr pParameters;
        public uint NumStaticSamplers;
        public IntPtr pStaticSamplers;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct VIEWPORT { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct VERTEX_BUFFER_VIEW { public ulong BufferLocation; public uint SizeInBytes, StrideInBytes; }

    [StructLayout(LayoutKind.Sequential)] internal struct HEAP_PROPERTIES { public uint Type, CPUPageProperty, MemoryPoolPreference, CreationNodeMask, VisibleNodeMask; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RESOURCE_DESC
    {
        public uint Dimension;
        public ulong Alignment, Width;
        public uint Height;
        public ushort DepthOrArraySize, MipLevels;
        public uint Format, SampleDescCount, SampleDescQuality, Layout, Flags;
    }

    // --- シェーダー ---
    // 行ベクトル規約で書くので row_major を明示する（既定は列優先で、転置し忘れると何も映らない）。
    internal const string Hlsl = @"
cbuffer Constants : register(b0) { row_major float4x4 mvp; };
struct VSIn  { float3 pos : POSITION; float3 col : COLOR; };
struct VSOut { float4 pos : SV_POSITION; float3 col : COLOR; };
VSOut VSMain(VSIn i) { VSOut o; o.pos = mul(float4(i.pos, 1.0), mvp); o.col = i.col; return o; }
float4 PSMain(VSOut i) : SV_TARGET { return float4(i.col, 1.0); }
";

    /// <summary>
    /// 立方体の 36 頂点（位置＋色）。
    ///
    /// 巻き順は背面カリングの向きと一致していなければならない。面ごとの 2 軸を手で並べると
    /// 向きが揃わず、揃っていない面だけが裏返って見える。ここでは軸の向きを
    /// <c>u x v = n</c> になるよう機械的に直してから頂点を作る。
    /// </summary>
    internal static float[] BuildCube()
    {
        // 面の中心方向と、その面を張る 2 軸
        var faces = new[]
        {
            (n: new[] { 0f, 0f, -1f }, u: new[] { 1f, 0f, 0f }, v: new[] { 0f, 1f, 0f }, c: new[] { 0.95f, 0.35f, 0.35f }),
            (n: new[] { 0f, 0f, 1f }, u: new[] { -1f, 0f, 0f }, v: new[] { 0f, 1f, 0f }, c: new[] { 0.35f, 0.85f, 0.55f }),
            (n: new[] { -1f, 0f, 0f }, u: new[] { 0f, 0f, 1f }, v: new[] { 0f, 1f, 0f }, c: new[] { 0.40f, 0.55f, 0.95f }),
            (n: new[] { 1f, 0f, 0f }, u: new[] { 0f, 0f, -1f }, v: new[] { 0f, 1f, 0f }, c: new[] { 0.95f, 0.80f, 0.35f }),
            (n: new[] { 0f, -1f, 0f }, u: new[] { 1f, 0f, 0f }, v: new[] { 0f, 0f, 1f }, c: new[] { 0.80f, 0.45f, 0.90f }),
            (n: new[] { 0f, 1f, 0f }, u: new[] { 1f, 0f, 0f }, v: new[] { 0f, 0f, -1f }, c: new[] { 0.40f, 0.85f, 0.90f }),
        };

        var data = new float[36 * 6];
        var k = 0;
        foreach (var f in faces)
        {
            var u = f.u; var v = f.v;
            if (Dot(Cross(u, v), f.n) < 0f) { var t = u; u = v; v = t; }

            // 面上の 4 隅（-u-v, +u-v, +u+v, -u+v）
            float[] Corner(float su, float sv) => new[]
            {
                (f.n[0] + su * u[0] + sv * v[0]) * 0.5f,
                (f.n[1] + su * u[1] + sv * v[1]) * 0.5f,
                (f.n[2] + su * u[2] + sv * v[2]) * 0.5f,
            };

            var a = Corner(-1, -1); var b = Corner(1, -1); var c = Corner(1, 1); var d = Corner(-1, 1);
            foreach (var p in new[] { a, b, c, a, c, d })
            {
                data[k++] = p[0]; data[k++] = p[1]; data[k++] = p[2];
                data[k++] = f.c[0]; data[k++] = f.c[1]; data[k++] = f.c[2];
            }
        }
        return data;
    }

    /// <summary>
    /// 頂点バッファの中身。立方体の 36 頂点に続けて、画面に数字を描くための単位クアッドを置く。
    /// クアッドは xy が 0..1 の板で、描くときにルート定数の行列で位置と大きさを与える。
    /// </summary>
    internal static float[] BuildVertices()
    {
        var cube = BuildCube();
        var data = new float[cube.Length + QuadVertexCount * 6];
        Array.Copy(cube, data, cube.Length);

        // 立方体と同じパイプラインで描くので、巻き順も立方体の面と揃えないと背面として捨てられる。
        // 揃っていないと、描画自体は成功しているのに何も出ない（エラーもログも出ない）。
        var k = cube.Length;
        float[][] quad =
        {
            new[] { 0f, 0f }, new[] { 1f, 1f }, new[] { 1f, 0f },
            new[] { 0f, 0f }, new[] { 0f, 1f }, new[] { 1f, 1f },
        };
        foreach (var p in quad)
        {
            data[k++] = p[0]; data[k++] = p[1]; data[k++] = 0f;
            data[k++] = 1f; data[k++] = 1f; data[k++] = 1f;
        }
        return data;
    }

    private static float[] Cross(float[] a, float[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };

    private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    // --- 呼び出し口 ---
    //
    // disasm-verified: D3DCompile（d3dcompiler_47.dll RVA 0x1256e0）は
    //   `sub rsp,78h` の後 [rsp+0A0h..0D0h] を読む。シフトを引くと +0x28..+0x58 で第5〜11引数。
    //   うち [rsp+0B8h] / [rsp+0C0h] だけが `mov eax`（32bit）＝ Flags1 / Flags2。
    // disasm-verified: D3D12SerializeRootSignature（d3d12.dll RVA 0x93a0）は
    //   rcx / edx / r8 / r9 のみを使い [rsp+X] の引数読みが無い。引数 4 個・第2引数は 32bit。
    [DllImport("d3dcompiler_47.dll")]
    private static extern int D3DCompile(IntPtr pSrcData, IntPtr srcDataSize, IntPtr pSourceName,
                                         IntPtr pDefines, IntPtr pInclude,
                                         [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
                                         [MarshalAs(UnmanagedType.LPStr)] string pTarget,
                                         uint flags1, uint flags2, out IntPtr ppCode, out IntPtr ppErrorMsgs);

    [DllImport("d3d12.dll")]
    private static extern int D3D12SerializeRootSignature(ref ROOT_SIGNATURE_DESC pRootSignature, uint version,
                                                          out IntPtr ppBlob, out IntPtr ppErrorBlob);

    // com-abi: スロットは継承の連鎖から求めたもの（IUnknown 0-2 / ID3D12Object 3-6 / ID3D12DeviceChild 7 ...）。
    private const int DEV_CreateGraphicsPipelineState = 10, DEV_CreateRootSignature = 16, DEV_CreateCommittedResource = 27;
    private const int RES_Map = 8, RES_Unmap = 9, RES_GetGPUVirtualAddress = 11;
    private const int BLOB_GetBufferPointer = 3, BLOB_GetBufferSize = 4;
    private const int CL_DrawInstanced = 12, CL_IASetPrimitiveTopology = 20, CL_RSSetViewports = 21,
                      CL_RSSetScissorRects = 22, CL_SetPipelineState = 25, CL_SetGraphicsRootSignature = 30,
                      CL_SetGraphicsRoot32BitConstants = 36, CL_IASetVertexBuffers = 44;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateRootSignatureFn(IntPtr self, uint nodeMask, IntPtr blob, IntPtr blobLen, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePipelineStateFn(IntPtr self, ref GRAPHICS_PIPELINE_STATE_DESC d, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateCommittedResourceFn(IntPtr self, ref HEAP_PROPERTIES props, uint heapFlags, ref RESOURCE_DESC desc, uint initialState, IntPtr clearValue, ref D3D12Com.GUID riid, out IntPtr o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, uint sub, IntPtr readRange, out IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, uint sub, IntPtr writtenRange);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong GetGpuVaFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPtrFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobSizeFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewportsFn(IntPtr self, uint num, ref VIEWPORT vp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetScissorsFn(IntPtr self, uint num, ref RECT r);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetObjectFn(IntPtr self, IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetRootConstantsFn(IntPtr self, uint rootIndex, uint num, ref float src, uint destOffset);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTopologyFn(IntPtr self, uint topology);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetVertexBuffersFn(IntPtr self, uint startSlot, uint num, ref VERTEX_BUFFER_VIEW view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawFn(IntPtr self, uint vertexCount, uint instanceCount, uint startVertex, uint startInstance);

    private static readonly D3D12Com.GUID IID_ID3D12RootSignature = new D3D12Com.GUID(0xc54a6b66, 0x72df, 0x4ee8, 0x8b, 0xe5, 0xa9, 0x46, 0xa1, 0x42, 0x92, 0x14);
    private static readonly D3D12Com.GUID IID_ID3D12PipelineState = new D3D12Com.GUID(0x765a30f3, 0xf624, 0x4c6f, 0xa8, 0x28, 0xac, 0xe9, 0x48, 0x62, 0x24, 0x45);
    private static readonly D3D12Com.GUID IID_ID3D12Resource = new D3D12Com.GUID(0x696442be, 0xa72e, 0x4059, 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad);

    private const int VertexCount = 36;
    private const int QuadVertexStart = VertexCount;
    private const int QuadVertexCount = 6;
    private const int Stride = 6 * sizeof(float);

    private static T Call<T>(IntPtr obj, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(D3D12Com.GetVtableSlot(obj, slot));

    /// <summary>立方体を描くのに要る一式。<see cref="Error"/> が null でなければどの段で落ちたかが入る。</summary>
    internal struct Pipeline
    {
        public IntPtr RootSignature, Pso, VertexBuffer;
        public VERTEX_BUFFER_VIEW Vbv;
        public string Error;
        public bool Ok => Error == null;
    }

    /// <summary>ルートシグネチャ・PSO・頂点バッファを作る。</summary>
    internal static Pipeline Create(IntPtr device)
    {
        var p = new Pipeline();
        var src = IntPtr.Zero;
        var semantics = IntPtr.Zero;
        var elements = IntPtr.Zero;
        var vsBlob = IntPtr.Zero; var psBlob = IntPtr.Zero; var rsBlob = IntPtr.Zero;

        try
        {
            // --- シェーダー ---
            var bytes = System.Text.Encoding.ASCII.GetBytes(Hlsl);
            src = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, src, bytes.Length);

            if (!Compile(src, bytes.Length, "VSMain", "vs_5_0", out vsBlob, out var vsErr)) { p.Error = "VS: " + vsErr; return p; }
            if (!Compile(src, bytes.Length, "PSMain", "ps_5_0", out psBlob, out var psErr)) { p.Error = "PS: " + psErr; return p; }

            // --- ルートシグネチャ（定数 16 個を直接置く） ---
            var param = new ROOT_PARAMETER
            {
                ParameterType = ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
                ShaderRegister = 0, RegisterSpace = 0, Num32BitValues = 16,
                ShaderVisibility = SHADER_VISIBILITY_ALL,
            };
            var paramPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ROOT_PARAMETER>());
            try
            {
                Marshal.StructureToPtr(param, paramPtr, false);
                var rsDesc = new ROOT_SIGNATURE_DESC
                {
                    NumParameters = 1, pParameters = paramPtr,
                    Flags = ROOT_SIGNATURE_FLAG_ALLOW_IA_INPUT_LAYOUT,
                };
                var hr = D3D12SerializeRootSignature(ref rsDesc, ROOT_SIGNATURE_VERSION_1, out rsBlob, out var rsErrBlob);
                if (hr != 0) { p.Error = $"SerializeRootSignature hr=0x{hr:X} {BlobText(rsErrBlob)}"; return p; }
                if (rsErrBlob != IntPtr.Zero) D3D12Com.Release(rsErrBlob);

                var iidRs = IID_ID3D12RootSignature;
                hr = Call<CreateRootSignatureFn>(device, DEV_CreateRootSignature)(
                    device, 0, BlobPtr(rsBlob), BlobSize(rsBlob), ref iidRs, out p.RootSignature);
                if (hr != 0) { p.Error = $"CreateRootSignature hr=0x{hr:X}"; return p; }
            }
            finally { Marshal.FreeHGlobal(paramPtr); }

            // --- 入力レイアウト ---
            // セマンティック名は LPCSTR なので、呼び出しが終わるまで生きた番地が要る。
            var posName = Marshal.StringToHGlobalAnsi("POSITION");
            var colName = Marshal.StringToHGlobalAnsi("COLOR");
            semantics = posName;
            var elemSize = Marshal.SizeOf<INPUT_ELEMENT_DESC>();
            elements = Marshal.AllocHGlobal(elemSize * 2);
            Marshal.StructureToPtr(new INPUT_ELEMENT_DESC
            {
                SemanticName = posName, Format = FORMAT_R32G32B32_FLOAT,
                AlignedByteOffset = 0, InputSlotClass = INPUT_CLASSIFICATION_PER_VERTEX,
            }, elements, false);
            Marshal.StructureToPtr(new INPUT_ELEMENT_DESC
            {
                SemanticName = colName, Format = FORMAT_R32G32B32_FLOAT,
                AlignedByteOffset = 12, InputSlotClass = INPUT_CLASSIFICATION_PER_VERTEX,
            }, elements + elemSize, false);

            // --- PSO ---
            var pso = new GRAPHICS_PIPELINE_STATE_DESC
            {
                pRootSignature = p.RootSignature,
                VS = new SHADER_BYTECODE { pShaderBytecode = BlobPtr(vsBlob), BytecodeLength = BlobSize(vsBlob) },
                PS = new SHADER_BYTECODE { pShaderBytecode = BlobPtr(psBlob), BytecodeLength = BlobSize(psBlob) },
                BlendState = DefaultBlend(),
                SampleMask = 0xFFFFFFFF,
                RasterizerState = new RASTERIZER_DESC
                {
                    FillMode = FILL_MODE_SOLID,
                    // 立方体は凸なので、深度バッファを持たなくても背面を捨てれば正しく見える。
                    CullMode = CULL_MODE_BACK,
                    FrontCounterClockwise = 0,
                    DepthClipEnable = 1,
                },
                DepthStencilState = new DEPTH_STENCIL_DESC
                {
                    DepthEnable = 0, DepthWriteMask = 0, DepthFunc = COMPARISON_FUNC_LESS,
                    StencilEnable = 0, StencilReadMask = 0xff, StencilWriteMask = 0xff,
                    FrontFace = DefaultStencilOp(), BackFace = DefaultStencilOp(),
                },
                InputLayout = new INPUT_LAYOUT_DESC { pInputElementDescs = elements, NumElements = 2 },
                PrimitiveTopologyType = PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
                NumRenderTargets = 1,
                RTVFormat0 = FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new SAMPLE_DESC { Count = 1, Quality = 0 },
            };
            var iidPso = IID_ID3D12PipelineState;
            var hrPso = Call<CreatePipelineStateFn>(device, DEV_CreateGraphicsPipelineState)(device, ref pso, ref iidPso, out p.Pso);
            if (hrPso != 0) { p.Error = $"CreateGraphicsPipelineState hr=0x{hrPso:X}"; return p; }

            // --- 頂点バッファ（CPU から書ける UPLOAD ヒープに置き、そのまま読ませる） ---
            var verts = BuildVertices();
            var sizeBytes = (uint)(verts.Length * sizeof(float));

            var heap = new HEAP_PROPERTIES { Type = HEAP_TYPE_UPLOAD, CreationNodeMask = 1, VisibleNodeMask = 1 };
            var desc = new RESOURCE_DESC
            {
                Dimension = RESOURCE_DIMENSION_BUFFER,
                Width = sizeBytes, Height = 1, DepthOrArraySize = 1, MipLevels = 1,
                Format = 0, SampleDescCount = 1, SampleDescQuality = 0,
                Layout = TEXTURE_LAYOUT_ROW_MAJOR, Flags = 0,
            };
            var iidRes = IID_ID3D12Resource;
            var hrRes = Call<CreateCommittedResourceFn>(device, DEV_CreateCommittedResource)(
                device, ref heap, 0, ref desc, RESOURCE_STATE_GENERIC_READ, IntPtr.Zero, ref iidRes, out p.VertexBuffer);
            if (hrRes != 0) { p.Error = $"CreateCommittedResource hr=0x{hrRes:X}"; return p; }

            var hrMap = Call<MapFn>(p.VertexBuffer, RES_Map)(p.VertexBuffer, 0, IntPtr.Zero, out var mapped);
            if (hrMap != 0) { p.Error = $"Map hr=0x{hrMap:X}"; return p; }
            Marshal.Copy(verts, 0, mapped, verts.Length);
            Call<UnmapFn>(p.VertexBuffer, RES_Unmap)(p.VertexBuffer, 0, IntPtr.Zero);

            p.Vbv = new VERTEX_BUFFER_VIEW
            {
                BufferLocation = Call<GetGpuVaFn>(p.VertexBuffer, RES_GetGPUVirtualAddress)(p.VertexBuffer),
                SizeInBytes = sizeBytes,
                StrideInBytes = Stride,
            };
            return p;
        }
        catch (Exception ex)
        {
            p.Error = ex.GetType().Name + ": " + ex.Message;
            return p;
        }
        finally
        {
            if (vsBlob != IntPtr.Zero) D3D12Com.Release(vsBlob);
            if (psBlob != IntPtr.Zero) D3D12Com.Release(psBlob);
            if (rsBlob != IntPtr.Zero) D3D12Com.Release(rsBlob);
            if (src != IntPtr.Zero) Marshal.FreeHGlobal(src);
            // セマンティック名と要素の配列は CreateGraphicsPipelineState が写し取った後なので手放せる。
            if (elements != IntPtr.Zero) Marshal.FreeHGlobal(elements);
            if (semantics != IntPtr.Zero) Marshal.FreeHGlobal(semantics);
        }
    }

    /// <summary>コマンドリストへ立方体 1 個ぶんの描画を積む。呼ぶ前にレンダーターゲットを設定しておくこと。</summary>
    internal static void Draw(IntPtr cmdList, ref Pipeline p, int width, int height, float angle)
    {
        var vp = new VIEWPORT { Width = width, Height = height, MinDepth = 0f, MaxDepth = 1f };
        Call<SetViewportsFn>(cmdList, CL_RSSetViewports)(cmdList, 1, ref vp);

        var sc = new RECT { Right = width, Bottom = height };
        Call<SetScissorsFn>(cmdList, CL_RSSetScissorRects)(cmdList, 1, ref sc);

        Call<SetObjectFn>(cmdList, CL_SetGraphicsRootSignature)(cmdList, p.RootSignature);
        Call<SetObjectFn>(cmdList, CL_SetPipelineState)(cmdList, p.Pso);

        var mvp = BuildMvp(angle, height == 0 ? 1f : (float)width / height);
        Call<SetRootConstantsFn>(cmdList, CL_SetGraphicsRoot32BitConstants)(cmdList, 0, 16, ref mvp[0], 0);

        Call<SetTopologyFn>(cmdList, CL_IASetPrimitiveTopology)(cmdList, PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        Call<SetVertexBuffersFn>(cmdList, CL_IASetVertexBuffers)(cmdList, 0, 1, ref p.Vbv);
        Call<DrawFn>(cmdList, CL_DrawInstanced)(cmdList, VertexCount, 1, 0, 0);
    }

    // 7 セグメントの点灯パターン。ビットは a,b,c,d,e,f,g の順。
    //   a=上 / b=右上 / c=右下 / d=下 / e=左下 / f=左上 / g=中
    private static readonly byte[] SegmentMasks =
    {
        0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F,
    };

    /// <summary>
    /// 数字をクリップ空間へ直接描く。左上が (x, y) で、1 文字の高さが height。
    /// 座標はどれも -1..1 で、描画先が正方形でなければ x 方向だけ aspect で割って形を保つ。
    ///
    /// 立方体と同じパイプラインを使い、単位クアッドをセグメントごとに矩形へ変形して置く。
    /// 文字ごとの資材を持たないので、フォントもテクスチャも要らない。
    /// </summary>
    internal static void DrawNumber(IntPtr cmdList, ref Pipeline p, string text, float x, float y, float height, float aspect)
    {
        if (string.IsNullOrEmpty(text)) return;

        var thickness = height * 0.16f;
        var digitWidth = height * 0.60f;
        var advance = digitWidth + thickness * 1.4f;
        var dotWidth = thickness * 1.6f;

        Call<SetTopologyFn>(cmdList, CL_IASetPrimitiveTopology)(cmdList, PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        Call<SetVertexBuffersFn>(cmdList, CL_IASetVertexBuffers)(cmdList, 0, 1, ref p.Vbv);

        var cursor = x;
        foreach (var ch in text)
        {
            if (ch == '.')
            {
                Rect(cmdList, cursor, y - height, dotWidth, thickness, aspect);
                cursor += dotWidth + thickness;
                continue;
            }
            if (ch < '0' || ch > '9') { cursor += advance; continue; }

            var mask = SegmentMasks[ch - '0'];
            var w = digitWidth;
            var half = height * 0.5f;

            if ((mask & 0x01) != 0) Rect(cmdList, cursor, y - thickness, w, thickness, aspect);                       // a
            if ((mask & 0x02) != 0) Rect(cmdList, cursor + w - thickness, y - half, thickness, half, aspect);         // b
            if ((mask & 0x04) != 0) Rect(cmdList, cursor + w - thickness, y - height, thickness, half, aspect);       // c
            if ((mask & 0x08) != 0) Rect(cmdList, cursor, y - height, w, thickness, aspect);                          // d
            if ((mask & 0x10) != 0) Rect(cmdList, cursor, y - height, thickness, half, aspect);                       // e
            if ((mask & 0x20) != 0) Rect(cmdList, cursor, y - half, thickness, half, aspect);                         // f
            if ((mask & 0x40) != 0) Rect(cmdList, cursor, y - half - thickness * 0.5f, w, thickness, aspect);         // g

            cursor += advance;
        }
    }

    /// <summary>単位クアッドを、左下が (x, y) で大きさ (w, h) の矩形へ置いて 1 回描く。</summary>
    private static void Rect(IntPtr cmdList, float x, float y, float w, float h, float aspect)
    {
        var m = new float[16];
        m[0] = w / aspect;
        m[5] = h;
        m[10] = 1f;
        m[12] = x / aspect;
        m[13] = y;
        m[15] = 1f;

        Call<SetRootConstantsFn>(cmdList, CL_SetGraphicsRoot32BitConstants)(cmdList, 0, 16, ref m[0], 0);
        Call<DrawFn>(cmdList, CL_DrawInstanced)(cmdList, QuadVertexCount, 1, QuadVertexStart, 0);
    }

    internal static void ReleasePipeline(ref Pipeline p)
    {
        if (p.VertexBuffer != IntPtr.Zero) D3D12Com.Release(p.VertexBuffer);
        if (p.Pso != IntPtr.Zero) D3D12Com.Release(p.Pso);
        if (p.RootSignature != IntPtr.Zero) D3D12Com.Release(p.RootSignature);
        p = default;
    }

    // --- 小道具 ---

    private static bool Compile(IntPtr src, int len, string entry, string target, out IntPtr blob, out string error)
    {
        var hr = D3DCompile(src, (IntPtr)len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                            entry, target, 0, 0, out blob, out var errBlob);
        if (hr == 0 && blob != IntPtr.Zero)
        {
            if (errBlob != IntPtr.Zero) D3D12Com.Release(errBlob);
            error = null;
            return true;
        }
        error = $"hr=0x{hr:X} {BlobText(errBlob)}";
        if (errBlob != IntPtr.Zero) D3D12Com.Release(errBlob);
        return false;
    }

    private static IntPtr BlobPtr(IntPtr blob) => Call<BlobPtrFn>(blob, BLOB_GetBufferPointer)(blob);
    private static IntPtr BlobSize(IntPtr blob) => Call<BlobSizeFn>(blob, BLOB_GetBufferSize)(blob);

    private static string BlobText(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return "(no message)";
        var p = BlobPtr(blob);
        return p == IntPtr.Zero ? "(empty)" : Marshal.PtrToStringAnsi(p, (int)BlobSize(blob)).TrimEnd('\0', '\n', '\r');
    }

    private static DEPTH_STENCILOP_DESC DefaultStencilOp() => new DEPTH_STENCILOP_DESC
    {
        StencilFailOp = STENCIL_OP_KEEP, StencilDepthFailOp = STENCIL_OP_KEEP,
        StencilPassOp = STENCIL_OP_KEEP, StencilFunc = COMPARISON_FUNC_ALWAYS,
    };

    private static BLEND_DESC DefaultBlend()
    {
        var rt = new RT_BLEND_DESC
        {
            SrcBlend = BLEND_ONE, DestBlend = BLEND_ZERO, BlendOp = BLEND_OP_ADD,
            SrcBlendAlpha = BLEND_ONE, DestBlendAlpha = BLEND_ZERO, BlendOpAlpha = BLEND_OP_ADD,
            LogicOp = LOGIC_OP_NOOP, RenderTargetWriteMask = COLOR_WRITE_ENABLE_ALL,
        };
        return new BLEND_DESC { RT0 = rt, RT1 = rt, RT2 = rt, RT3 = rt, RT4 = rt, RT5 = rt, RT6 = rt, RT7 = rt };
    }

    /// <summary>回転と透視投影を掛けた 4x4（行ベクトル規約・行優先で並べる）。</summary>
    internal static float[] BuildMvp(float angle, float aspect)
    {
        var cy = (float)Math.Cos(angle); var sy = (float)Math.Sin(angle);
        var cx = (float)Math.Cos(angle * 0.6f); var sx = (float)Math.Sin(angle * 0.6f);

        // Y 回転のあとに X 回転
        var m = new float[16];
        float m00 = cy, m01 = 0, m02 = -sy;
        float m10 = sy * sx, m11 = cx, m12 = cy * sx;
        float m20 = sy * cx, m21 = -sx, m22 = cy * cx;

        const float dist = 3.2f;
        var fovY = 1.0f;                       // ラジアン
        var f = 1.0f / (float)Math.Tan(fovY * 0.5f);
        const float zn = 0.1f, zf = 100f;

        // world(回転) * view(平行移動 z+dist) * proj を 1 つにまとめる
        m[0] = m00 * f / aspect; m[1] = m01 * f; m[2] = m02 * zf / (zf - zn); m[3] = m02;
        m[4] = m10 * f / aspect; m[5] = m11 * f; m[6] = m12 * zf / (zf - zn); m[7] = m12;
        m[8] = m20 * f / aspect; m[9] = m21 * f; m[10] = m22 * zf / (zf - zn); m[11] = m22;
        m[12] = 0; m[13] = 0;
        m[14] = dist * zf / (zf - zn) - zn * zf / (zf - zn);
        m[15] = dist;
        return m;
    }
}
