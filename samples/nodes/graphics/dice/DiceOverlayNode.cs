using System;
using System.Threading;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 対象アプリが描き終えた絵の上に、回転するサイコロを重ねる。
/// 対象は絵の素材も描画のしかけも一切持たなくてよい。テクスチャも形もこのノードが組み立てる。
///
/// 描画先は対象自身のバックバッファで、装置も対象のものを借りる。
/// 別のウィンドウを作らないので、対象の絵が隠れることもない。
///
/// 呼ぶ場所には条件がある。対象がその周の絵を描き終えていて、まだ画面へ出していない
/// 時点でなければならない。割り込んで描けばどの構成でもその時点になる。
/// 更新コールバックから描けるのは、その更新が画面へ出す呼び出しから回っている構成だけ。
///
/// 割り込む場合、必要なのは vtable のスロットの番地だけで、対象の番地は要らない。
/// vtable はクラスで共有されているので、スロットを差し替えれば対象の呼び出しがここへ来る。
/// その第 1 引数が対象自身にあたる。
/// </summary>
[NodeType("ngol.gfx.overlay_dice", "Graphics", "Overlay Dice On App",
    Version = "0.3.3",
    Description =
        "Draw a rotating, textured die on top of whatever the target application already rendered, by drawing "
      + "directly into its own backbuffer with its own device. The target needs no graphics code and no image "
      + "files of its own; the texture and the mesh are built in memory by this node. With hook_present on, all "
      + "it needs is the address of the presentation function's call table entry: the call table is shared by the "
      + "class, so the first call that arrives identifies the target itself. Without it, pass the live "
      + "IDXGISwapChain 'this' pointer instead, and note that drawing then only works where the update callback "
      + "runs after the target finished drawing and before it presents. Because the target's own device is "
      + "borrowed, the target must present through Direct3D 11; where it does not, this node unhooks itself and "
      + "says so. ngol.gfx.draw_cube is the opposite trade - it brings its own D3D12 device in a child window "
      + "over the target, so it works whatever the target uses, at the cost of covering the frame instead of "
      + "compositing with it.")]
[NodePort("slot_address_hex", PortDirection.Input, "string", Description = "Address of the vtable entry holding IDXGISwapChain::Present, hex string, as reported by Present Address. With hook_present on this is all that is needed - the target is learned from the first call")]
[NodePort("swapchain_hex", PortDirection.Input,  "string",  Description = "Live IDXGISwapChain 'this' pointer, hex string. Required when hook_present is off; otherwise use slot_address_hex")]
[NodePort("enabled",       PortDirection.Input,  "boolean", Description = "true starts drawing, false stops it")]
[NodePort("hook_present",  PortDirection.Input,  "boolean", Description = "Draw from inside the target's present call instead of the host's update callback. Needed wherever the update callback runs before the target renders, which is the case in most engines. Default false")]
[NodePort("show_fps",      PortDirection.Input,  "boolean", Description = "Draw the measured frame rate in the corner. Default true")]
[NodePort("status",        PortDirection.Output, "string",  Description = "What happened, or which step the setup failed at")]
public sealed class DiceOverlayNode : INode
{
    private const string Version = "0.3.3";

    public void Execute(IExecutionContext ctx)
    {
        var enabled = ctx.GetPortValue("enabled") as bool? ?? true;
        OverlayState.ShowFps = ctx.GetPortValue("show_fps") as bool? ?? true;

        if (!enabled)
        {
            OverlayState.CancelRegistration();
            ctx.SetPortValue("status", "stop requested");
            return;
        }

        if (OverlayState.InUse)
        {
            ctx.SetPortValue("status", "already running (stop it first)");
            return;
        }

        var hookPresent = ctx.GetPortValue("hook_present") as bool? ?? false;
        var slot  = ParseHex(ctx.GetPortValue("slot_address_hex") as string);
        var chain = ParseHex(ctx.GetPortValue("swapchain_hex") as string);

        // 割り込んで描くなら、対象の番地は要らない。vtable のスロットを差し替えれば、
        // 最初に入ってきた呼び出しがその相手を教えてくれる。
        // 更新から描く場合は自分から呼びに行くので、対象の番地が要る。
        if (hookPresent ? (slot == 0 && chain == 0) : chain == 0)
        {
            ctx.SetPortValue("status", hookPresent
                ? "give either slot_address_hex or swapchain_hex"
                : "swapchain_hex is required unless hook_present is on");
            return;
        }

        // 対象の装置を触るので、組み立ては描く場所と同じスレッドで行う。ここでは預けるだけ。
        OverlayState.Arm(new IntPtr(slot), new IntPtr(chain), hookPresent);
        ctx.Logger.LogInfo($"[DiceOverlay v{Version}] arming " +
                           (slot != 0 ? $"on call table entry 0x{slot:X}" : $"for swapchain 0x{chain:X}") + "; " +
                           (hookPresent ? "drawing from inside the target's present call" : "drawing from the update callback"));

        // 割り込む場合でも登録は必ず持つ。停止のときに控えた値を戻す必要があり、
        // 戻さないまま型が入れ替わると次の呼び出しで行き先が無くなって落ちる。
        OverlayState.Register(ctx.RegisterPersistent(new PersistentCallbacks
        {
            OnUpdate = () => OverlayState.Frame(ctx),
            OnStop = () => OverlayState.Shutdown(ctx),
        }));

        ctx.SetPortValue("status", $"armed (v{Version}{(hookPresent ? ", hooking present" : "")})");
    }

    private static long ParseHex(string s)
    {
        s = (s ?? "").Trim().Replace("0x", "").Replace("0X", "");
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>
/// 重ね描きの持ち物と手順。作るのも捨てるのも更新スレッドから行う。
/// </summary>
internal static class OverlayState
{
    private static volatile bool s_armed;
    private static bool s_initialized;
    private static string s_failure;
    private static IPersistentRegistration s_registration;
    private static IntPtr s_swapChain, s_slotAddress;
    private static DiceOverlay.Resources s_res;
    private static bool s_hookPresent;
    private static IExecutionContext s_ctx;   // 割り込み先から記録を残すため
    private static long s_frames;
    private static DateTime s_startedAt, s_lastFpsAt;
    private static string s_fpsText = "";
    private static volatile bool s_showFps = true;

    // Whether the replaced entry has ever been called. A swapchain exposes two ways to
    // present, and an application that uses one never touches the other, so hooking the
    // wrong entry produces neither a picture nor an error - it produces silence. One
    // counter on the drawing path is enough to tell silence apart from failure.
    private static long s_arrivals;
    private static DateTime s_hookedAt;
    private static bool s_silenceReported;

    private const double RadiansPerSecond = 0.8;

    internal static bool InUse => s_armed || s_initialized;

    /// <summary>測った速さを絵の中に出すかどうか。絵を見れば動いているかと速さが同時に分かる。</summary>
    internal static bool ShowFps { set { s_showFps = value; } }

    /// <summary>
    /// 描く支度をする。割り込んで描く場合は vtable のスロットさえ分かればよく、対象の番地は
    /// 最初に入ってきた呼び出しが教えてくれる。更新から描く場合は対象の番地が要る。
    /// </summary>
    internal static void Arm(IntPtr slotAddress, IntPtr swapChain, bool hookPresent)
    {
        s_failure = null;
        PresentHook.ForgetLoop();
        s_slotAddress = slotAddress != IntPtr.Zero ? slotAddress : PresentHook.SlotAddressOf(swapChain);
        s_swapChain = swapChain;
        s_hookPresent = hookPresent;
        Interlocked.Exchange(ref s_arrivals, 0);
        s_hookedAt = default;
        s_silenceReported = false;
        s_armed = true;
    }

    internal static void Register(IPersistentRegistration reg) => s_registration = reg;

    /// <summary>
    /// 停止は登録の取り消しで行う。取り消すと更新の呼び出しは止まり、次の更新で OnStop が
    /// ホストのメインスレッドから呼ばれる。作った資源はそこで解放する。
    /// 取り消しはバックグラウンドスレッドから呼んでよい。
    /// </summary>
    internal static void CancelRegistration() => s_registration?.Cancel();

    /// <summary>
    /// ホストの更新から呼ばれる。割り込む設定のときは、ここでは描かずに差し替えだけ行う。
    /// </summary>
    internal static void Frame(IExecutionContext ctx)
    {
        if (!s_armed) return;
        s_ctx = ctx;

        // 割り込みの最中に起きたことは、この登録が生きている間だけ記録できる。
        OverlayReport.Warn = m => ctx.Logger.LogWarning("[DiceOverlay] " + m);
        PresentHook.CheckSlot();
        PresentHook.CheckEntryCode();

        if (s_hookPresent)
        {
            ReportSilence(ctx);
            if (PresentHook.Installed || s_failure != null) return;

            // 一度でも呼び出しが再帰したなら、張り直しても同じことが起きる。
            // 先客がこちらの入口を画面へ出す関数だと見なしている状態は、こちらでは解けない。
            if (PresentHook.LoopDetected)
            {
                s_failure = "another overlay took our entry for the presentation function; not hooking again";
                ctx.Logger.LogWarning("[DiceOverlay] " + s_failure);
                return;
            }

            var hookErr = PresentHook.Install(s_slotAddress, s_swapChain, DrawFromPresent);
            if (hookErr != null)
            {
                s_failure = hookErr;
                ctx.Logger.LogError("[DiceOverlay] hook failed: " + hookErr);
                return;
            }
            ctx.Logger.LogInfo("[DiceOverlay] hooked the target's present call");
            s_hookedAt = DateTime.UtcNow;
            return;
        }

        DrawOnce(s_swapChain);
    }

    /// <summary>
    /// 仕掛けたのに一度も呼ばれない、を言葉にする。呼ばれていれば成功も失敗も記録に出るが、
    /// 呼ばれないままだと何も出ないため、待っても無駄な状態と区別がつかない。
    /// </summary>
    private static void ReportSilence(IExecutionContext ctx)
    {
        if (s_silenceReported || !s_hookPresent || !PresentHook.Installed) return;
        if (Interlocked.Read(ref s_arrivals) != 0) return;
        if (s_hookedAt == default || (DateTime.UtcNow - s_hookedAt).TotalSeconds < 3.0) return;

        s_silenceReported = true;
        ctx.Logger.LogWarning(
            "[DiceOverlay] the entry has not been called once in 3 seconds. A swapchain has two "
          + "ways to present and this one is not the way the target uses; try the other entry "
          + "(Present Address reports both addresses, and the entries are 14 slots apart)");
    }

    /// <summary>対象が画面へ出す直前に、対象の描画スレッドから呼ばれる。</summary>
    private static void DrawFromPresent(IntPtr swapChain) => DrawOnce(swapChain);

    private static void DrawOnce(IntPtr swapChain)
    {
        Interlocked.Increment(ref s_arrivals);
        DrawOnceCore(swapChain);
    }

    private static void DrawOnceCore(IntPtr swapChain)
    {
        if (!s_initialized)
        {
            if (s_failure != null) return;      // 一度失敗したら毎周試さない
            s_res = DiceOverlay.Create(swapChain);
            if (!s_res.Ok)
            {
                s_failure = s_res.Error;
                s_ctx?.Logger.LogError("[DiceOverlay] setup failed: " + s_failure);

                // A failed setup must not leave the entry replaced. What stays behind is a
                // detour into code that will never draw, and the target keeps calling it
                // every frame. Cancelling the registration runs the ordinary stop path,
                // which puts the original entry back and waits for the call to go idle.
                if (s_res.Error != null && s_res.Error.StartsWith("GetDevice"))
                    s_ctx?.Logger.LogError(
                        "[DiceOverlay] the target does not present through Direct3D 11; unhooking");
                CancelRegistration();
                return;
            }
            s_initialized = true;
            s_startedAt = s_lastFpsAt = DateTime.UtcNow;
            s_fpsText = "";
            s_frames = 0;
            s_ctx?.Logger.LogInfo($"[DiceOverlay] ready, drawing into the target's backbuffer ({s_res.VertexCount} vertices)");
        }

        // 回転はフレーム数ではなく経過時間から作る。フレーム数で作ると、駆動が速くなった分
        // そのまま速く回ってしまう。
        var angle = (float)((DateTime.UtcNow - s_startedAt).TotalSeconds * RadiansPerSecond);
        var err = DiceOverlay.Draw(swapChain, ref s_res, angle, s_showFps ? s_fpsText : null);
        if (err != null)
        {
            s_failure = err;
            s_ctx?.Logger.LogError("[DiceOverlay] draw failed: " + err);
            return;
        }

        s_frames++;

        // 画面に出す値は短い窓で作り直す。窓を広げると、描画が止まってからも
        // 直前の速さを表示し続ける。
        if (s_frames % 15 == 0)
        {
            var now = DateTime.UtcNow;
            var span = (now - s_lastFpsAt).TotalSeconds;
            if (span > 0) s_fpsText = (15.0 / span).ToString("F1");
            s_lastFpsAt = now;
        }

        // 速さは絵の中に出しているので、記録には残さない。
        // 毎秒何度も書くと、対象自身のログが読めなくなる。
    }

    internal static void Shutdown(IExecutionContext ctx)
    {

        // 控えた値を戻すのが最優先。戻さないままこの型が入れ替わると、
        // 次に画面へ出すときの行き先が無くなって落ちる。
        PresentHook.Restore();

        // 戻した後も、割り込み先を通っている最中の呼び出しが残っていることがある。
        // 対象の描画は別のスレッドで動いているので、ここで手放すと解放済みの資源を
        // 触りにいく。抜けきるのを待ってから片づける。
        if (!PresentHook.WaitUntilIdle(1000))
            ctx?.Logger.LogWarning("[DiceOverlay] the present call did not go idle in time; keeping the resources");
        else if (s_armed || s_initialized)
            DiceOverlay.Release(ref s_res);

        if (!s_armed && !s_initialized) { return; }

        s_initialized = false;
        s_armed = false;
        s_swapChain = IntPtr.Zero;
        s_frames = 0;
        s_ctx = null;
        OverlayReport.Warn = null;
        ctx?.Logger.LogInfo("[DiceOverlay] stopped");
    }
}
