using System;
using System.Linq;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ngol.hook.patch_bytes が書き込んだ番地を、元のバイト列へ戻す。
///
/// 同じ絶対アドレスを指定すること。module+rva から計算した番地と
///   absolute_address_hex で直接指定した番地が一致していれば、どちらでも戻せる。
/// 控えはこのプロセス内にしか無い。再起動すると戻す手段が無くなる
///   （その番地はディスク上のファイルには影響していないので、対象を再起動すれば
///   元のコードで起動し直すことにはなる）。
/// </summary>
[NodeType("ngol.hook.patch_revert", "Hook", "Patch Revert",
    Version = "1.0.2",
    Description =
        "Restore the bytes at an address to what they were before ngol.hook.patch_bytes wrote to it. Removes no hook: "
      + "this pair rewrites instruction bytes, and nothing was ever installed at the function's entry. Requires the "
      + "same address (module+rva or absolute_address_hex resolves to the same value patch_bytes was called with). "
      + "The saved original only exists within this process - it does not survive a restart.")]
[NodePort("module",               PortDirection.Input,  "string", Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("rva",                  PortDirection.Input,  "string", Description = "RVA hex (e.g. 0x12340)")]
[NodePort("absolute_address_hex", PortDirection.Input,  "string", Description = "Pre-resolved absolute address. Takes priority over module/rva when non-empty")]
[NodePort("reverted",             PortDirection.Output, "boolean", Description = "true when the original bytes were written back. false also when there was nothing saved to restore")]
[NodePort("result",               PortDirection.Output, "string", Description = "How many bytes were restored, where, and what they were - or the reason nothing was restored")]
public sealed class PatchRevertNode : INode
{
    private const string SavedBytesKeyPrefix = "NgolPatchBytesOriginal_";

    public void Execute(IExecutionContext ctx)
    {
        var moduleName  = NgolModuleDefault.Resolve(ReadString(ctx, "module", ""));
        var rvaHex      = ReadString(ctx, "rva", "");
        var absoluteHex = ReadString(ctx, "absolute_address_hex", "");

        var useAbsolute = !string.IsNullOrWhiteSpace(absoluteHex);
        if (!useAbsolute && string.IsNullOrWhiteSpace(rvaHex))
        {
            SetOutputs(ctx, false, "rva is empty (and no absolute_address_hex given)");
            return;
        }

        if (!NgolAddressResolve.TryResolveTarget(useAbsolute, moduleName, rvaHex, absoluteHex, out var target, out var resolveError))
        {
            SetOutputs(ctx, false, resolveError);
            return;
        }

        var key = SavedBytesKeyPrefix + target.ToInt64().ToString("x");
        if (AppDomain.CurrentDomain.GetData(key) is not byte[] original)
        {
            SetOutputs(ctx, false, $"no saved original for 0x{target.ToInt64():x} (nothing to revert)");
            return;
        }

        if (!NgolSafeMemory.Write(target, original))
        {
            SetOutputs(ctx, false, $"write failed at 0x{target.ToInt64():x}");
            return;
        }

        AppDomain.CurrentDomain.SetData(key, null);
        var origHex = string.Join(" ", original.Select(b => b.ToString("x2")));
        SetOutputs(ctx, true, $"reverted {original.Length} byte(s) at 0x{target.ToInt64():x}: {origHex}");
    }

    private static string ReadString(IExecutionContext ctx, string name, string fallback)
        => ctx.GetPortValue(name) as string ?? ctx.GetParam<string>(name) ?? fallback;

    private static void SetOutputs(IExecutionContext ctx, bool reverted, string result)
    {
        ctx.SetPortValue("reverted", reverted);
        ctx.SetPortValue("result", result);
    }
}
