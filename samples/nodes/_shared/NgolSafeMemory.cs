using System;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// プロセス内のメモリを、読めるところまでだけ読む。
///
/// 読めない番地へ触ると、この環境では例外ではなくプロセスが即座に落ちる
/// （アクセス違反は catch できない）。try/catch では守れないため、
/// 触る前に `VirtualQuery` で読めるかを確かめる以外に手が無い。
///
/// 範囲を「モジュールの内側」へ狭める形は採らない。
/// 実行時に生成されたコード・確保された領域はどのモジュールにも属さず、
/// それらを読むこと自体が解析の目的になりうるため。
/// 読める場所はすべて読み、読めない場所で止まる。
/// </summary>
internal static class NgolSafeMemory
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    private static bool IsReadable(uint state, uint protect)
    {
        if (state != MEM_COMMIT) return false;
        // PAGE_GUARD は上位ビットの修飾子。`protect & 0xFF` のように下位だけを見ると
        //    ガードページを読める領域と誤判定する。修飾子は個別に落とすこと。
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & 0xFF) == PAGE_NOACCESS) return false;
        return true;
    }

    /// <summary>
    /// address から続けて読める長さを返す（最大 limit）。
    /// 読める領域が隣接していれば繋いで数える。
    /// </summary>
    public static long ReadableLength(IntPtr address, long limit)
    {
        if (limit <= 0) return 0;

        long total = 0;
        var cursor = (long)address;
        var mbiSize = (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

        while (total < limit)
        {
            if (VirtualQuery((IntPtr)cursor, out var mbi, mbiSize) == IntPtr.Zero) break;
            if (!IsReadable(mbi.State, mbi.Protect)) break;

            // 問い合わせた番地は領域の途中でありうる。残りは領域末尾までの分。
            var regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            var remainInRegion = regionEnd - cursor;
            if (remainInRegion <= 0) break;

            total += remainInRegion;
            cursor = regionEnd;
        }

        return total < limit ? total : limit;
    }

    /// <summary>
    /// address から count バイトを destination へ写す。読めない領域に当たったらそこで止める。
    /// 戻り値は実際に写したバイト数（count に満たないことがある）。
    /// </summary>
    public static int Read(IntPtr address, byte[] destination, int destinationOffset, int count)
    {
        if (destination == null) return 0;
        if (destinationOffset < 0 || count <= 0) return 0;
        if (destinationOffset + count > destination.Length) count = destination.Length - destinationOffset;
        if (count <= 0) return 0;

        var readable = ReadableLength(address, count);
        if (readable <= 0) return 0;

        // 1 回の Marshal.Copy が領域の境界をまたぐと、そこから先が読めない場合に落ちる。
        //    ReadableLength は「続けて読める長さ」なので、その範囲内であれば安全にまとめて写せる。
        var total = (int)readable;
        Marshal.Copy(address, destination, destinationOffset, total);
        return total;
    }

    /// <summary>
    /// address へ data を書き込む。書き込む前に「続けて読める（＝有効な）長さ」を確認し、
    /// 足りなければ書かない（部分書き込みで壊すよりは、何もしないほうを選ぶ）。
    ///
    /// コード領域は既定で書き込み不可（PAGE_EXECUTE_READ 等）なので、
    /// VirtualProtect で一時的に書き込み可にしてから書き、元の保護属性へ戻す。
    /// 戻し忘れるとページが実行可能+書き込み可のまま残り、意図しない改変の窓になる。
    /// </summary>
    public static bool Write(IntPtr address, byte[] data)
    {
        if (data == null || data.Length == 0) return false;
        if (ReadableLength(address, data.Length) < data.Length) return false;

        if (!VirtualProtect(address, (UIntPtr)data.Length, PAGE_EXECUTE_READWRITE, out var oldProtect))
            return false;

        try
        {
            Marshal.Copy(data, 0, address, data.Length);
        }
        finally
        {
            VirtualProtect(address, (UIntPtr)data.Length, oldProtect, out _);
        }

        // 書いた先が命令かどうかをこの関数は知らないため、常に呼ぶ（データ領域への呼び出しは無害）。
        //   x86/x64 はハードウェアがキャッシュ一貫性を保つため実害は起きにくいが、
        //   これが Microsoft の定める正しい手順であり、他 ISA（ARM 等）を見据えると省略できない。
        FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)data.Length);
        return true;
    }
}
