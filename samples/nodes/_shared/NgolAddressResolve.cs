using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// module+rva または absolute_address_hex のどちらかから絶対アドレスを組み立てる、
/// 複数のノードで共通の手順。
/// </summary>
internal static class NgolAddressResolve
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string moduleName);

    public static bool TryResolveTarget(bool useAbsolute, string moduleName, string rvaHex, string absoluteHex,
                                         out IntPtr target, out string error)
    {
        target = IntPtr.Zero;
        error  = string.Empty;

        if (useAbsolute)
        {
            if (!TryParseHex(absoluteHex, out var abs))
            {
                error = $"absolute_address_hex could not be read: '{absoluteHex}'";
                return false;
            }
            target = new IntPtr(unchecked((long)abs));
            return true;
        }

        var baseAddr = GetModuleHandleA(moduleName);
        if (baseAddr == IntPtr.Zero)
        {
            error = $"module not found: '{moduleName}'";
            return false;
        }
        if (!TryParseHex(rvaHex, out var rva))
        {
            error = $"rva could not be read: '{rvaHex}'";
            return false;
        }
        target = new IntPtr(baseAddr.ToInt64() + (long)rva);
        return true;
    }

    public static bool TryParseHex(string text, out ulong value)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
