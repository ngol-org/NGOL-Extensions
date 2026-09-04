using System;
using System.Globalization;
using NgolExt.NativeHook;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ネイティブ関数を「呼ばれても実行しない」状態にする。フックは設置するが元関数へ進まない。
///   見るだけでよい場合は ngol.hook.watch_function を使う（あちらは挙動を変えない）。
///
/// 挙動を変えるノードなので、観測系とは名前を分けてある。
///   対象が何をしていたかを知らないまま止めると、呼び出し元は「成功した」と思って進む。
///
/// -- 戻り値について（ここが要点）-----------------------
/// 元関数を実行しない以上、呼び出し元へ返す値は**こちらが決めるしかない**。
///   決めなければ不定値が渡り、呼び出し元はそれを正常な戻り値として扱う。
/// return_value を指定しない場合は 0 を返す。
///   「0＝失敗/NULL」と解釈する関数なら安全側に倒れるが、逆の意味を持つ関数もある
///   （0 を成功とみなす規約・0 が有効な識別子である場合）。**対象の規約を確認すること。**
/// 反映されるのは整数・ポインタとして返る値だけ。浮動小数点は別のレジスタで返るため効かない。
///
/// -- 使う前に -----------------------------------
/// 関数の先頭以外を指すとプロセスごと落ちる。ngol.hook.safety_check で確認してから設置する。
/// 先頭バイトが 0x00 のときはパディングで、実体は +1 バイト先にある。
/// 解除は enabled=false。フックハンドルは実行をまたいで保持されるので、
///   確認が済んだら必ず外すこと（外し忘れると止めたままになる）。
///
/// このノードは「呼ばれた回数」を数えるが、引数は記録しない。
///   引数も見たい場合は watch_function で観測してから、こちらで止める。
/// </summary>
[NodeType("ngol.hook.skip_function", "Hook", "Skip Native Function",
    Version = "1.0.2",
    Description =
        "Stop a native function from running: a hook is installed at its entry and the original is never reached. "
      + "This CHANGES the behaviour of the target - use ngol.hook.watch_function when you only want to observe. "
      + "Because the original never runs, the value handed back to the caller is chosen here: return_value, or 0 when "
      + "left empty. Only values returned as an integer or pointer are affected; floating-point returns are not. "
      + "Check the address with ngol.hook.safety_check first - an address that is not a function entry crashes the "
      + "process. Set enabled=false to restore the function. Requires the native-hook extension.")]
[NodePort("module",               PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("rva",                  PortDirection.Input,  "string",  Description = "RVA hex (e.g. 0x12340)")]
[NodePort("absolute_address_hex", PortDirection.Input,  "string",  Description = "Pre-resolved absolute address. Takes priority over module/rva when non-empty")]
[NodePort("enabled",              PortDirection.Input,  "boolean", Description = "true = stop the function, false = restore it")]
[NodePort("return_value",         PortDirection.Input,  "string",  Description = "Value handed to the caller instead of running the function, as decimal or 0x hex. Empty = 0. Integer/pointer returns only")]
[NodePort("skipped_count",        PortDirection.Output, "number",  Description = "How many calls were stopped since the hook was installed")]
[NodePort("return_value_used",    PortDirection.Output, "string",  Description = "The value actually being handed back, as hex")]
[NodePort("hook_active",          PortDirection.Output, "boolean", Description = "Whether the function is currently being stopped")]
[NodePort("result",               PortDirection.Output, "string",  Description = "Status or error message")]
public sealed class SkipFunctionNode : INode
{
    // ハンドルは実行をまたいで保持する。持ち越さないと解除できなくなる。
    private const string HookHandleKeyPrefix = "NgolSkipFunctionHandle_";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string moduleName);

    public void Execute(IExecutionContext ctx)
    {
        var svc = ctx.GetExtensionService<INativeHookService>();
        if (svc == null)
        {
            SetOutputs(ctx, 0, "0x0", false, "native hook service is not available (extension not loaded)");
            return;
        }

        var moduleName  = NgolModuleDefault.Resolve(ReadString(ctx, "module", ""));
        var rvaHex      = ReadString(ctx, "rva", "");
        var absoluteHex = ReadString(ctx, "absolute_address_hex", "");
        var enabled     = ctx.GetPortValue("enabled") as bool? ?? false;

        var useAbsolute = !string.IsNullOrWhiteSpace(absoluteHex);
        if (!useAbsolute && string.IsNullOrWhiteSpace(rvaHex))
        {
            SetOutputs(ctx, 0, "0x0", false, "rva is empty (and no absolute_address_hex given)");
            return;
        }

        if (!TryParseReturnValue(ReadString(ctx, "return_value", ""), out var returnValue, out var parseError))
        {
            SetOutputs(ctx, 0, "0x0", false, parseError);
            return;
        }

        var stateKey = useAbsolute
            ? $"{HookHandleKeyPrefix}abs_{absoluteHex.Trim().ToLowerInvariant()}"
            : $"{HookHandleKeyPrefix}{moduleName}_{rvaHex.Trim().ToLowerInvariant()}";
        var hook = GetStoredHandle(stateKey);

        if (!enabled)
        {
            if (hook != IntPtr.Zero)
            {
                svc.Uninstall(hook);
                SetStoredHandle(stateKey, IntPtr.Zero);
                SetOutputs(ctx, 0, "0x0", false, "restored: the function runs again");
                return;
            }
            SetOutputs(ctx, 0, "0x0", false, "not installed");
            return;
        }

        if (hook == IntPtr.Zero)
        {
            if (!TryResolveTarget(useAbsolute, moduleName, rvaHex, absoluteHex, out var target, out var resolveError))
            {
                SetOutputs(ctx, 0, "0x0", false, resolveError);
                return;
            }
            if (!svc.Install(target, out hook))
            {
                var err = svc.GetLastError();
                SetOutputs(ctx, 0, "0x0", false, string.IsNullOrEmpty(err) ? "install failed" : err);
                return;
            }
            SetStoredHandle(stateKey, hook);
        }

        // 順序が重要: 先に返す値を決めてから、元関数を呼ばない設定にする。
        //   逆にすると、値を決める前に呼ばれた分へ不定値が渡りうる。
        if (!svc.SetReturnValue(hook, returnValue))
        {
            var err = svc.GetLastError();
            SetOutputs(ctx, 0, "0x0", false, string.IsNullOrEmpty(err) ? "SetReturnValue failed" : err);
            return;
        }
        svc.SetCallOriginal(hook, false);

        svc.Read(hook, out var count, out _, out _, out _, out _);
        SetOutputs(ctx, count, "0x" + returnValue.ToString("x"), svc.IsActive(hook),
            "stopped: the function is hooked and never runs (set enabled=false to restore)");
    }

    // 10 進と 0x 付き 16 進の両方を受ける。負の値も 64bit のビット列として扱う。
    private static bool TryParseReturnValue(string text, out long value, out string error)
    {
        value = 0;
        error = string.Empty;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return true;

        var negative = s.StartsWith("-", StringComparison.Ordinal);
        if (negative) s = s.Substring(1);

        bool ok;
        ulong raw;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ok = ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw);
        else
            ok = ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw);

        if (!ok)
        {
            error = $"return_value could not be read: '{text}' (use decimal or 0x hex)";
            return false;
        }

        value = unchecked((long)raw);
        if (negative) value = -value;
        return true;
    }

    private static IntPtr GetStoredHandle(string key)
    {
        var data = AppDomain.CurrentDomain.GetData(key);
        return data is long value && value != 0 ? new IntPtr(value) : IntPtr.Zero;
    }

    private static void SetStoredHandle(string key, IntPtr hook)
        => AppDomain.CurrentDomain.SetData(key, hook.ToInt64());

    private static bool TryResolveTarget(bool useAbsolute, string moduleName, string rvaHex, string absoluteHex,
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

    private static bool TryParseHex(string text, out ulong value)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string ReadString(IExecutionContext ctx, string name, string fallback)
        => ctx.GetPortValue(name) as string ?? ctx.GetParam<string>(name) ?? fallback;

    private static void SetOutputs(IExecutionContext ctx, long skipped, string returnUsed, bool active, string result)
    {
        ctx.SetPortValue("skipped_count", (double)skipped);
        ctx.SetPortValue("return_value_used", returnUsed);
        ctx.SetPortValue("hook_active", active);
        ctx.SetPortValue("result", result);
    }
}
