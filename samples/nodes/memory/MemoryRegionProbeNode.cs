using NodeGraphModLab.NodeAPI;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NodeGraphModLab.CustomNodes;

// Read-only survey of a memory range: walks page by page (VirtualQuery) and reports
// which regions are committed & readable, without touching the bytes.
// Use this before reading a range larger than a few pages, to avoid the process-killing
// access violation that an uncommitted/guarded page causes (it is not a catchable exception).
//
// The range can start either at a module base (module port) or at any absolute address
// (address_hex port). Arbitrary addresses are supported on purpose: runtime-generated code,
// hook trampolines and manually mapped images belong to no module, and inspecting them is
// often the point of the analysis.
//
// Also reports the PE SizeOfImage via GetModuleInformation (psapi.dll), which is
// commonly larger than the on-disk file size due to section alignment padding.
// Basing a range on file size alone can silently truncate the actual in-memory
// image and miss target addresses near the end of the module.
[NodeType("ngol.mem.region_probe", "Memory", "Region Probe",
    Version = "1.0.2",
    Description = "Survey a memory range page by page and report which parts are readable, plus how many bytes can be read contiguously from the start address.")]
[NodePort("module",          PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module. Ignored when address_hex is given")]
[NodePort("address_hex",     PortDirection.Input,  "string", Description = "Absolute start address as hex. Empty = the module's base address")]
[NodePort("size_hex",        PortDirection.Input,  "string", Description = "Range to survey from the start address, e.g. '0xF80000'. Empty = the module's SizeOfImage, or 0x100000 for an arbitrary address")]
[NodePort("summary",         PortDirection.Output, "string", Description = "Human-readable survey: image size, readable/unreadable totals and the first gaps")]
[NodePort("start_hex",       PortDirection.Output, "string", Description = "Absolute address the survey started at (hex)")]
[NodePort("readable_length", PortDirection.Output, "number", Description = "Bytes readable contiguously from start_hex, capped by the surveyed range. 0 means the start address itself is not readable")]
public class MemoryRegionProbeNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_NOACCESS = 0x01;
    const uint PAGE_GUARD = 0x100;

    static long ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    static bool IsReadable(uint protect)
    {
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & 0xFF) == PAGE_NOACCESS) return false;
        return true;
    }

    public void Execute(IExecutionContext ctx)
    {
        var addressHex = ((string?)ctx.GetPortValue("address_hex") ?? "").Trim();
        var sizeHex = (string?)ctx.GetPortValue("size_hex");
        var sb = new StringBuilder();

        long baseAddr;
        long imageSize = 0;

        if (addressHex.Length > 0)
        {
            baseAddr = ParseHex(addressHex);
            if (baseAddr == 0)
            {
                SetFailed(ctx, $"could not parse address_hex: '{addressHex}'");
                return;
            }
            sb.AppendLine($"address_hex=0x{baseAddr:X16} (arbitrary address; not resolved through a module)");
        }
        else
        {
            var moduleName = NgolModuleDefault.Resolve((string?)ctx.GetPortValue("module"));
            var handle = GetModuleHandleA(moduleName);
            if (handle == IntPtr.Zero)
            {
                SetFailed(ctx, $"GetModuleHandleA('{moduleName}') returned NULL");
                return;
            }

            baseAddr = (long)handle;

            // Ask Windows for the authoritative in-memory image size (PE SizeOfImage),
            // rather than assuming it matches the on-disk file size - the two commonly
            // differ due to section alignment padding and .bss-like uninitialized data.
            if (GetModuleInformation(GetCurrentProcess(), handle, out var modInfo, (uint)Marshal.SizeOf<MODULEINFO>()))
            {
                imageSize = modInfo.SizeOfImage;
                sb.AppendLine($"module={moduleName}  SizeOfImage=0x{imageSize:X} ({imageSize / 1024.0 / 1024.0:F2} MB)");
            }
            else
            {
                sb.AppendLine($"module={moduleName}  GetModuleInformation failed - falling back to size_hex/default for survey range");
            }
        }

        long surveySize = ParseHex(sizeHex);
        if (surveySize <= 0) surveySize = imageSize > 0 ? imageSize : 0x100000;
        long endAddr = baseAddr + surveySize;

        sb.AppendLine($"start=0x{baseAddr:X16}  surveySize=0x{surveySize:X} ({surveySize / 1024.0 / 1024.0:F2} MB)");

        // 同じ判定で読む側（NgolSafeMemory）と同じ長さを返す。
        // 測る手段と読む手段が別実装だと、測って安全と出た範囲で読み手が止まりうる。
        long readableFromStart = NgolSafeMemory.ReadableLength((IntPtr)baseAddr, surveySize);
        sb.AppendLine($"readableFromStart=0x{readableFromStart:X} ({readableFromStart} bytes)");

        long cur = baseAddr;
        long readableTotal = 0;
        long unreadableTotal = 0;
        int regionCount = 0;
        int gapCount = 0;
        long firstGapOffset = -1;
        var mbiSize = (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

        while (cur < endAddr)
        {
            IntPtr result;
            MEMORY_BASIC_INFORMATION mbi;
            try
            {
                result = VirtualQuery((IntPtr)cur, out mbi, mbiSize);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"VirtualQuery threw at 0x{cur:X16}: {ex.Message}");
                break;
            }

            if (result == IntPtr.Zero)
            {
                sb.AppendLine($"VirtualQuery failed at 0x{cur:X16} (GetLastError not captured)");
                break;
            }

            long regionSize = (long)mbi.RegionSize;
            if (regionSize <= 0) regionSize = 0x1000; // safety fallback to avoid infinite loop

            // Clip the region to the survey window for accounting purposes.
            long regionStart = Math.Max(cur, (long)mbi.BaseAddress);
            long regionEnd = Math.Min(endAddr, (long)mbi.BaseAddress + regionSize);
            long clippedSize = Math.Max(0, regionEnd - regionStart);

            bool committed = mbi.State == MEM_COMMIT;
            bool readable = committed && IsReadable(mbi.Protect);

            regionCount++;
            if (readable)
            {
                readableTotal += clippedSize;
            }
            else
            {
                unreadableTotal += clippedSize;
                gapCount++;
                if (firstGapOffset < 0) firstGapOffset = regionStart - baseAddr;
                if (gapCount <= 20)
                {
                    sb.AppendLine($"  GAP  off=+0x{regionStart - baseAddr:X}  size=0x{clippedSize:X}  state=0x{mbi.State:X}  protect=0x{mbi.Protect:X}");
                }
            }

            cur = (long)mbi.BaseAddress + regionSize;
            if (cur <= regionStart) cur = regionStart + 0x1000; // guard against non-advancing loop
        }

        sb.AppendLine($"regions={regionCount}  gaps={gapCount}");
        sb.AppendLine($"readable=0x{readableTotal:X} ({readableTotal / 1024.0 / 1024.0:F2} MB)");
        sb.AppendLine($"unreadable=0x{unreadableTotal:X} ({unreadableTotal / 1024.0 / 1024.0:F2} MB)");
        if (firstGapOffset >= 0)
            sb.AppendLine($"firstGapOffset=+0x{firstGapOffset:X}");
        if (gapCount > 20)
            sb.AppendLine($"  ... {gapCount - 20} more gaps not shown");

        ctx.SetPortValue("summary", sb.ToString());
        ctx.SetPortValue("start_hex", $"0x{baseAddr:X}");
        ctx.SetPortValue("readable_length", (double)readableFromStart);
    }

    static void SetFailed(IExecutionContext ctx, string reason)
    {
        ctx.SetPortValue("summary", "ERROR: " + reason);
        ctx.SetPortValue("start_hex", "");
        ctx.SetPortValue("readable_length", 0d);
    }
}
