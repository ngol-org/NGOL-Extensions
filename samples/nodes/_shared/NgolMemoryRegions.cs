using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// プロセスのコミット済みメモリ領域を列挙する。値スキャン系ノードで共有する。
/// </summary>
internal static class NgolMemoryRegions
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [DllImport("kernel32.dll")]
    private static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_INFO
    {
        public ushort wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

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

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    public struct Region
    {
        public IntPtr Base;
        public long Size;
    }

    /// <summary>
    /// ゲーム側の値（HP・所持金 等）が典型的に置かれる、書き込み可能なコミット済み領域を列挙する。
    /// コード領域（実行可・書き込み不可）は対象外--値スキャンの用途とは別。
    /// maxTotalBytes を超えたら打ち切る（呼び出し側が truncated を判定できるよう、
    /// 打ち切ったかどうかは onTruncated で返す）。
    ///
    /// 上限に当たった領域は「丸ごと捨てる」のではなく残り予算の分だけ返す。
    ///   丸ごと捨てると、指定した上限より大幅に少ないバイト数しか走査せずに終わり、
    ///   利用者から見て上限の意味が変わってしまう。
    /// </summary>
    public static IEnumerable<Region> EnumerateWritableRegions(long maxTotalBytes, Action<bool> onTruncated = null)
    {
        // 上限は決め打ちにせず GetSystemInfo から取る（呼び出し元プロセスが実際に
        //   使える範囲。32bit プロセスや将来のアドレス幅拡張でも正しく動く）。
        GetSystemInfo(out var sysInfo);
        long addressSpaceLimit = sysInfo.lpMaximumApplicationAddress.ToInt64();

        long cursor = 0x10000; // NULL 付近は除外
        long total = 0;
        var mbiSize = (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

        while (cursor < addressSpaceLimit)
        {
            if (VirtualQuery((IntPtr)cursor, out var mbi, mbiSize) == IntPtr.Zero) break;

            var regionEnd = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            if (regionEnd <= cursor) break; // 進まなくなったら異常として止める

            if (IsWritable(mbi.State, mbi.Protect))
            {
                var size = (long)mbi.RegionSize;
                if (total + size > maxTotalBytes)
                {
                    var remain = maxTotalBytes - total;
                    if (remain > 0) yield return new Region { Base = mbi.BaseAddress, Size = remain };
                    onTruncated?.Invoke(true);
                    yield break;
                }
                total += size;
                yield return new Region { Base = mbi.BaseAddress, Size = size };
            }

            cursor = regionEnd;
        }
        onTruncated?.Invoke(false);
    }

    /// <summary>
    /// 書き込み可能なコミット済み領域の合計バイト数を数える（読み取りは行わない）。
    ///
    /// 「上限で打ち切った」ことだけを返すと、利用者はあとどれだけ残っているか分からず
    ///   「この環境には無い」と読んでしまう。全体量を並べて返すために使う。
    /// 同じ EnumerateWritableRegions を通すので、走査側と「書き込み可能」の定義が必ず一致する
    ///   --別々に判定すると、利用者から見て説明のつかない食い違いが出る。
    /// </summary>
    public static long MeasureWritableTotal()
    {
        long total = 0;
        foreach (var region in EnumerateWritableRegions(long.MaxValue)) total += region.Size;
        return total;
    }

    private static bool IsWritable(uint state, uint protect)
    {
        if (state != MEM_COMMIT) return false;
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & 0xFF) == PAGE_NOACCESS) return false;
        var baseProtect = protect & 0xFF;
        return baseProtect == PAGE_READWRITE || baseProtect == PAGE_WRITECOPY
            || baseProtect == PAGE_EXECUTE_READWRITE || baseProtect == PAGE_EXECUTE_WRITECOPY;
    }
}
