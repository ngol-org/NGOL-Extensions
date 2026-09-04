using System;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

[NodeType("ngol.code.export_address", "Code", "Export Address",
    Version = "1.0.1",
    Description = "Resolve exported function names in a loaded module to absolute addresses and RVAs, the way the loader itself would. Names that are not exported come back as not found rather than as an address, so a typo cannot turn into a wrong address.")]
[NodePort("module", PortDirection.Input, "string", Description = "Module name as loaded, e.g. D3DTargetApp.exe or d3d11.dll")]
[NodePort("names", PortDirection.Input, "string", Description = "Export names, comma separated")]
[NodePort("base_hex", PortDirection.Output, "string", Description = "Module base address")]
[NodePort("found", PortDirection.Output, "number", Description = "How many of the names resolved")]
[NodePort("result", PortDirection.Output, "string", Description = "One line per name: name / absolute / rva")]
public sealed class ExportAddressNode : INode
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    public void Execute(IExecutionContext ctx)
    {
        string module = ctx.GetPortValue("module") as string ?? "";
        string names = ctx.GetPortValue("names") as string ?? "";

        ctx.SetPortValue("base_hex", "");
        ctx.SetPortValue("found", 0d);

        IntPtr handle = GetModuleHandleW(module);
        if (handle == IntPtr.Zero)
        {
            ctx.SetPortValue("result", module + " is not loaded in this process");
            return;
        }

        long b = handle.ToInt64();
        ctx.SetPortValue("base_hex", "0x" + b.ToString("x"));

        var report = new StringBuilder();
        int found = 0;
        foreach (string raw in names.Split(','))
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;

            IntPtr addr = GetProcAddress(handle, name);
            if (addr == IntPtr.Zero)
            {
                report.Append(name).Append("  not exported\n");
                continue;
            }

            found++;
            long a = addr.ToInt64();
            report.Append(name).Append("  0x").Append(a.ToString("x"))
                  .Append("  rva 0x").Append((a - b).ToString("x")).Append('\n');
        }

        ctx.SetPortValue("found", (double)found);
        ctx.SetPortValue("result", report.ToString());
    }
}
