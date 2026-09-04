using System;
using System.Runtime.InteropServices;
using NgolExt.NativeHook;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// このノードは「見るだけ」。元関数は必ず呼ばれ、対象の挙動は変わらない。
///   止めたい場合は ngol.hook.skip_function を使う。
///
/// 履歴は持たない。保持されるのは通過回数と、直近 1 回分の引数だけ。
///   1000 回通ったあとに読めば 1000 回目の値が出る。呼び出し列を取りたい用途には向かない。
///
/// 指定モジュールの RVA にネイティブフックを設置し、呼び出し引数と hit_count を記録する。
/// CLR 非依存の ngol_native.dll（MinHook ベース）経由でフックするため、
/// ランタイムに登録されていないスレッドから呼ばれる関数にも安全に設置できる。
/// module は任意のモジュール名を指定可能。省略時はプロセスの主モジュールを対象にする。
/// ngol.ext.native-hook 拡張（Api/Impl）がロードされている必要がある。
///
/// 入力:
///   module               -> モジュール名（省略時: プロセスの主モジュール）。absolute_address_hex 指定時は無視。
///   rva              -> RVA hex（例: "0x12340"）。事前に ngol.code.disasm で先頭バイト確認必須。
///   absolute_address_hex -> 解決済み絶対アドレスを直接指定する場合に使う。空でなければ優先。
///   enabled              -> true=フック設置 / false=解除
///   extra_stack_args     -> レジスタ4個(a0-a3)を超える追加引数の個数(0-8)。x64呼び出し規約ではこの分はスタック経由で
///                          渡されるため、5引数以上を持つ関数をフックする場合は必ず指定する。
///                          省略時(0)は従来通り4引数のみ転送され、5引数目以降は不定値になる（クラッシュはしないが誤動作する）。
///                          追加引数は全て8バイトのポインタサイズ値であることが前提（浮動小数点・大きい値型構造体は非対応）。
///   float_slot_mask      -> レジスタ渡し4引数(スロット0-3)のうちXMM(浮動小数点)渡しのスロットをビットマスクで指定(0-15)。
///                          bit iが1ならスロットiはfloat/doubleとして捕捉・転送される。省略時(0)は全スロットGPレジスタ扱い
///                          （従来通り）。**設置時のみ有効**--既に設置済みのフックに対して値を変えても反映されないため、
///                          変更する場合はenabled=falseで解除してから再度enabled=trueで設置し直すこと。extra_stack_args
///                          との併用は非対応（同時に0以外を指定するとエラーになる）。
///   watch                 -> true でバックグラウンド監視(RegisterPersistent)を開始し、hit_countが変化するたびに
///                          check_job_statusのmessageへ報告する。変化がない間はmessageを更新しない(スパム防止)。
///                          監視対象のフックがuninstallされると自動停止し、その旨をmessageに残す。falseで監視停止。
///
/// 出力:
///   hit_count    -> 累計呼び出し回数
///   last_a0〜a3  -> 直近の rcx/rdx/r8/r9
///
/// 捕まえた値がオブジェクトを指しているとき、その中身まで見たい場合は
/// ngol.mem.read_ptr でポインタを辿り、ngol.mem.read_string で読む。
///   last_extra   -> 直近の第5引数以降（extra_stack_args個数分、カンマ区切りhex、同上）
///   hook_active  -> フックが現在有効か
///   watch_active -> バックグラウンド監視が現在有効か
///   result       -> ステータス文字列またはエラーメッセージ
///
/// 主な使い方:
///   enabled=true で設置 -> hit_count が増加すればコードパスを通過している。
///   rva に 0x00 パディング RVA を指定すると result に "ERR: RVA_PADDING" が返る（+1 した正しい先頭を使うこと）。
///   フックハンドルは AppDomain.SetData で保持するため hot-reload をまたいで維持される。
///   watch=true にすると、手動でのhit_countポーリングなしにcheck_job_statusだけで変化を検知できる。
/// </summary>
[NodeType("ngol.hook.watch_function", "Hook", "Watch Function",
    Version = "2.1.1",
    Description =
        "Watch a native function: install a hook at its entry, count how often it is reached and keep the arguments of "
      + "the most recent call. The original function always runs, so behaviour is not changed - use ngol.hook.skip_function "
      + "to stop it instead. Only the LATEST call is kept, not a history: reading after 1000 calls gives the 1000th one. "
      + "The hook body is native, so the function may be reached from any thread, including threads the runtime does not "
      + "know about. Requires the native-hook extension.")]
[NodePort("module",          PortDirection.Input,  "string",  Description = "Module name. Empty = the process's main module. Ignored when absolute_address_hex is set")]
[NodePort("rva",              PortDirection.Input,  "string",  Description = "RVA hex (e.g. 0x12340)")]
[NodePort("absolute_address_hex", PortDirection.Input,  "string",  Description = "Pre-resolved absolute address. Takes priority over module/rva when non-empty")]
[NodePort("enabled",              PortDirection.Input,  "boolean", Description = "Install when true, uninstall when false")]
[NodePort("extra_stack_args",     PortDirection.Input,  "number",  Description = "Count (0-8) of args beyond the 4 register args (rcx/rdx/r8/r9), passed via stack per x64 calling convention. Required on functions with 5+ pointer-sized args, otherwise the original receives garbage for args 5 and up. Default 0")]
[NodePort("float_slot_mask",      PortDirection.Input,  "number",  Description = "Bitmask (0-15) of which of the 4 register slots (0-3) are XMM/float-double-passed rather than GP. Install-time only (re-toggle enabled to change on an existing hook). Cannot combine with extra_stack_args != 0")]
[NodePort("watch",                PortDirection.Input,  "boolean", Description = "true = keep monitoring hit_count in the background (RegisterPersistent) and report each change via check_job_status message. false = stop watching. Default false")]
[NodePort("watch_active",         PortDirection.Output, "boolean", Description = "Whether background watching is currently active")]
[NodePort("hit_count",            PortDirection.Output, "number",  Description = "Number of hook invocations")]
[NodePort("last_a0",              PortDirection.Output, "string",  Description = "Last rcx / first arg as hex")]
[NodePort("last_a1",              PortDirection.Output, "string",  Description = "Last rdx / second arg as hex")]
[NodePort("last_a2",              PortDirection.Output, "string",  Description = "Last r8 / third arg as hex")]
[NodePort("last_a3",              PortDirection.Output, "string",  Description = "Last r9 / fourth arg as hex")]
[NodePort("last_extra",           PortDirection.Output, "string",  Description = "Last args 5..N (comma-separated hex, count = extra_stack_args)")]
[NodePort("last_return_address",  PortDirection.Output, "string",  Description = "Address the most recent call returns to, as hex - that is, who called this function. Subtract a module's load address to get module+RVA and feed it to ngol.code.disasm or ngol.code.xref_find. Only the first level is returned here. For more levels use ngol.hook.trace_calls with frames above 0, which unwinds and so goes deep through native callers but stops at generated code (JIT, trampolines). Scanning the stack instead needs no metadata but yields false positives. For kernel frames as well, use ETW (WPR)")]
[NodePort("hook_active",          PortDirection.Output, "boolean", Description = "Whether the hook is currently installed")]
[NodePort("result",               PortDirection.Output, "string",  Description = "Status or error message")]
public sealed class WatchFunctionNode : INode
{
    private const string HookHandleKeyPrefix = "NgolNativeHookHandle_";
    private const string WatchStateKeyPrefix = "NgolNativeHookWatchState_";

    private sealed class WatchState
    {
        public IntPtr Hook;
        public long LastCount;
        public IPersistentRegistration Reg;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string moduleName);

    public void Execute(IExecutionContext ctx)
    {
        // 通っていない・設置できていない場合でも空にせず 0 を出す。
        //    空だと「読めなかった」のか「まだ一度も通っていない」のか区別できない。
        ctx.SetPortValue("last_return_address", "0x0");

        var svc = ctx.GetExtensionService<INativeHookService>();
        if (svc == null)
        {
            SetOutputs(ctx, 0, "0x0", "0x0", "0x0", "0x0", "", false, false, "ngol.ext.native-hook extension not loaded");
            return;
        }

        var moduleName  = NgolModuleDefault.Resolve(ReadString(ctx, "module", ""));
        var rvaHex      = ReadString(ctx, "rva", "");
        var absoluteHex = ReadString(ctx, "absolute_address_hex", "");
        var enabled      = ReadBool(ctx, "enabled", false);
        var extraStackArgs = ReadInt(ctx, "extra_stack_args", 0);
        var floatSlotMask  = ReadInt(ctx, "float_slot_mask", 0);
        var watch          = ReadBool(ctx, "watch", false);

        var useAbsolute = !string.IsNullOrWhiteSpace(absoluteHex);
        if (!useAbsolute && string.IsNullOrWhiteSpace(rvaHex))
        {
            SetOutputs(ctx, 0, "0x0", "0x0", "0x0", "0x0", "", false, false, "rva is empty (and no absolute_address_hex given)");
            return;
        }

        var stateKey = useAbsolute ? MakeStateKeyAbsolute(absoluteHex) : MakeStateKey(moduleName, rvaHex);
        var watchKey = WatchStateKeyPrefix + stateKey;
        var hook = GetStoredHandle(stateKey);

        if (!enabled)
        {
            if (hook != IntPtr.Zero)
            {
                svc.Uninstall(hook);
                SetStoredHandle(stateKey, IntPtr.Zero);
            }
            StopWatch(watchKey);
            SetOutputs(ctx, 0, "0x0", "0x0", "0x0", "0x0", "", false, false, "disabled");
            return;
        }

        if (hook == IntPtr.Zero)
        {
            if (!TryResolveTarget(useAbsolute, moduleName, rvaHex, absoluteHex, out var target, out var resolveError))
            {
                SetOutputs(ctx, 0, "0x0", "0x0", "0x0", "0x0", "", false, false, resolveError);
                return;
            }

            var installed = floatSlotMask != 0
                ? svc.InstallTyped(target, floatSlotMask, out hook)
                : svc.Install(target, out hook);
            if (!installed)
            {
                var err = svc.GetLastError();
                SetOutputs(ctx, 0, "0x0", "0x0", "0x0", "0x0", "", false, false,
                    string.IsNullOrEmpty(err) ? "install failed" : err);
                return;
            }

            SetStoredHandle(stateKey, hook);
        }

        // このノードは観測に徹する。元関数は必ず呼ぶ。
        //   設置しただけで挙動が変わると、観測しているつもりで対象を壊すことになる。
        //   止めたい場合は ngol.hook.skip_function を使う（責任の所在が名前に出る）。
        svc.SetCallOriginal(hook, true);
        if (!svc.SetExtraStackArgs(hook, extraStackArgs))
        {
            var err = svc.GetLastError();
            SetOutputs(ctx, 0, "0x0", "0x0", "0x0", "0x0", "", false, false,
                string.IsNullOrEmpty(err) ? "SetExtraStackArgs failed" : err);
            return;
        }

        var watchActive = watch ? StartOrResumeWatch(ctx, svc, watchKey, hook) : StopWatch(watchKey);

        svc.Read(hook, out var count, out var a0, out var a1, out var a2, out var a3);
        ctx.SetPortValue("last_return_address", FormatPtr(new IntPtr(svc.ReadReturnAddress(hook))));
        var extra = extraStackArgs > 0 ? svc.ReadExtra(hook, extraStackArgs) : Array.Empty<long>();
        var active = svc.IsActive(hook);
        var targetHex = FormatPtr(TryResolveTarget(useAbsolute, moduleName, rvaHex, absoluteHex, out var targetPtr, out _)
            ? targetPtr
            : IntPtr.Zero);

        SetOutputs(ctx, count,
            FormatPtr(new IntPtr(a0)),
            FormatPtr(new IntPtr(a1)),
            FormatPtr(new IntPtr(a2)),
            FormatPtr(new IntPtr(a3)),
            FormatExtra(extra),
            active,
            watchActive,
            active ? $"hooked @ {targetHex}" : "hook inactive");
    }

    /// <summary>
    /// 既に監視中(かつ同一ハンドル)ならそのまま継続、未監視/停止済み/ハンドルが変わった(再設置された)場合は
    /// 現在の hook に対して RegisterPersistent を(再)登録する。戻り値は監視が現在アクティブかどうか。
    /// </summary>
    private static bool StartOrResumeWatch(IExecutionContext ctx, INativeHookService svc, string watchKey, IntPtr hook)
    {
        var wstate = AppDomain.CurrentDomain.GetData(watchKey) as WatchState;
        if (wstate != null && wstate.Reg.IsActive && wstate.Hook == hook)
        {
            return true;
        }

        wstate?.Reg?.Cancel();

        svc.Read(hook, out var initCount, out _, out _, out _, out _);
        var newState = new WatchState { Hook = hook, LastCount = initCount };
        var reg = ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () => WatchTick(svc, newState),
        });
        newState.Reg = reg;
        AppDomain.CurrentDomain.SetData(watchKey, newState);
        return true;
    }

    private static bool StopWatch(string watchKey)
    {
        var wstate = AppDomain.CurrentDomain.GetData(watchKey) as WatchState;
        if (wstate != null)
        {
            wstate.Reg?.Cancel();
            AppDomain.CurrentDomain.SetData(watchKey, null);
        }
        return false;
    }

    private static void WatchTick(INativeHookService svc, WatchState state)
    {
        if (!svc.IsActive(state.Hook))
        {
            state.Reg?.ReportProgress("hook no longer active, stopping watch");
            state.Reg?.Cancel();
            return;
        }

        svc.Read(state.Hook, out var count, out var a0, out var a1, out var a2, out var a3);
        if (count == state.LastCount) return; // 変化なし: ReportProgressを呼ばない(スパム防止)

        var delta = count - state.LastCount;
        state.LastCount = count;
        state.Reg?.ReportProgress(
            $"hit_count changed: {count} (+{delta}) a0={FormatPtr(new IntPtr(a0))} " +
            $"a1={FormatPtr(new IntPtr(a1))} a2={FormatPtr(new IntPtr(a2))} " +
            $"a3={FormatPtr(new IntPtr(a3))}");
    }

    private static string MakeStateKey(string moduleName, string rvaHex)
        => $"{HookHandleKeyPrefix}{moduleName}_{rvaHex.Trim().ToLowerInvariant()}";

    private static string MakeStateKeyAbsolute(string absoluteHex)
        => $"{HookHandleKeyPrefix}abs_{absoluteHex.Trim().ToLowerInvariant()}";

    private static IntPtr GetStoredHandle(string key)
    {
        var data = AppDomain.CurrentDomain.GetData(key);
        return data is long value && value != 0 ? new IntPtr(value) : IntPtr.Zero;
    }

    private static void SetStoredHandle(string key, IntPtr hook)
        => AppDomain.CurrentDomain.SetData(key, hook.ToInt64());

    private static bool TryResolveTarget(bool useAbsolute, string moduleName, string rvaHex, string absoluteHex, out IntPtr target, out string error)
    {
        target = IntPtr.Zero;
        error  = string.Empty;

        if (useAbsolute)
        {
            if (!TryParseRva(absoluteHex, out var abs))
            {
                error = $"invalid absolute_address_hex: {absoluteHex}";
                return false;
            }
            target = new IntPtr(abs);
            return true;
        }

        var module = GetModuleHandleA(moduleName);
        if (module == IntPtr.Zero)
        {
            error = $"module not found: {moduleName}";
            return false;
        }

        if (!TryParseRva(rvaHex, out var rva))
        {
            error = $"invalid rva: {rvaHex}";
            return false;
        }

        target = IntPtr.Add(module, checked((int)rva));
        return true;
    }

    private static bool TryParseRva(string rvaHex, out long rva)
    {
        rva = 0;
        var s = rvaHex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out rva);
    }

    private static string FormatPtr(IntPtr ptr) => $"0x{ptr.ToInt64():X}";


    private static string ReadString(IExecutionContext ctx, string name, string fallback)
        => ctx.GetPortValue(name) as string ?? ctx.GetParam<string>(name) ?? fallback;

    private static bool ReadBool(IExecutionContext ctx, string name, bool fallback)
    {
        if (ctx.GetPortValue(name) is bool b) return b;
        if (ctx.GetPortValue(name) is double d) return d != 0.0;
        return ctx.GetParam<bool?>(name) ?? fallback;
    }

    private static int ReadInt(IExecutionContext ctx, string name, int fallback)
    {
        if (ctx.GetPortValue(name) is double d) return (int)d;
        if (ctx.GetPortValue(name) is int i) return i;
        return ctx.GetParam<int?>(name) ?? fallback;
    }

    private static string FormatExtra(long[] extra)
        => string.Join(",", Array.ConvertAll(extra, v => FormatPtr(new IntPtr(v))));

    private static void SetOutputs(IExecutionContext ctx, long hitCount,
        string a0, string a1, string a2, string a3, string lastExtra, bool hookActive, bool watchActive, string result)
    {
        ctx.SetPortValue("hit_count",    (double)hitCount);
        ctx.SetPortValue("last_a0",      a0);
        ctx.SetPortValue("last_a1",      a1);
        ctx.SetPortValue("last_a2",      a2);
        ctx.SetPortValue("last_a3",      a3);
        ctx.SetPortValue("last_extra",   lastExtra);
        ctx.SetPortValue("hook_active",  hookActive);
        ctx.SetPortValue("watch_active", watchActive);
        ctx.SetPortValue("result",       result);
    }
}
