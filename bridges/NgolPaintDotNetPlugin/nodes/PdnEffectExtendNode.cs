using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using MonoMod.RuntimeDetour;
using PaintDotNet.Direct2D1;
using PaintDotNet.Direct2D1.Effects;
using PaintDotNet.Effects;
using PaintDotNet.Effects.Gpu;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストに元から入っている効果へ、色ごとに効きを変える設定を足す。
/// 効果は決め打ちにせず、下の表に 1 行足せば増える。
///
/// 組み立ての形は 2 つしかない。どちらになるかは効果の中身で決まる。
///
///   split  元の絵（または途中の絵）から 3 本へ枝分かれし、ホストの部品の写しを
///          色ごとに違う強さで走らせ、1 色ずつ取り出して足し戻す。色を混ぜないので
///          倍率が 3 つとも 1.0 ならホストの出す絵と一致する。
///          締めの部品が要る効果は、最後にもう 1 つだけ写しを置く。
///
///            元の絵 : 写し(赤の強さ) -- 赤だけ残す --.
///                   : 写し(緑の強さ) -- 緑だけ残す --+-- 足す -- (締め) -- これを返す
///                   : 写し(青の強さ) -- 青だけ残す --'
///
///   blend  歪み系はこの手が使えない。同じ絵の中で枝分かれさせると升目状に
///          描き落とされるため、ホストの出す絵をそのまま材料にして、
///          元の絵との混ぜ具合を色ごとに変える。
///
///            元の絵         -- 色ごとに (1 - 割合) 倍 --.
///            ホストの出す絵 -- 色ごとに   割合   倍 --+-- 足す -- これを返す
///
/// 歪み系かどうかは実装を読めば分かる。PdnDistortionEffect の入口を使っていれば
/// そちらで、ホストの GPU 効果ではその一群だけが該当する。
///
/// 効果の型そのものは別アセンブリの internal なので名指しできない。
/// 引き取る側は公開されている土台の型で受ける。
/// </summary>
[NodeType("pdn.fx.extend", "Paint.NET", "Extend A Built-in Effect",
    Version = "1.11.0",
    Description = "Give one of the host's own effects something it does not have: a separate setting per "
                + "colour channel. Three sliders are added to that effect's own dialog and the output is "
                + "rebuilt from the host's own parts, so at 1.0 on all three the picture is the host's own. "
                + "Pick the effect with the effect port. While patched the effect carries the host's own "
                + "plugin mark in the menu, and the added sliders sit under a rule of their own. "
                + "Run with effect=off to take every patch away.")]
[NodePort("effect", PortDirection.Input, "string",
    Description = "Which effect to patch: sharpen (default), motion_blur, gaussian_blur or frosted_glass. "
                + "\"off\" takes every patch away. Anything else lists what is known")]
[NodePort("enabled", PortDirection.Input, "boolean",
    Description = "true (default) = install the patch. false = remove it and report how often it ran")]
[NodePort("status", PortDirection.Output, "string",
    Description = "\"patched\" / \"replaced\" / \"removed\" / \"not patched\", or the step that could not be taken")]
[NodePort("dialogs", PortDirection.Output, "number",
    Description = "How many times that effect's dialog was assembled, while patched")]
[NodePort("renders", PortDirection.Output, "number",
    Description = "How many times the host asked the effect to assemble its output")]
[NodePort("builds", PortDirection.Output, "number",
    Description = "How many of those assemblies came back as the per-channel output")]
[NodePort("refreshes", PortDirection.Output, "number",
    Description = "How many times the host asked the effect to refresh its output")]
[NodePort("note", PortDirection.Output, "string",
    Description = "The last step inside the drawing entries that could not be taken, if any")]
public sealed class PdnEffectExtendNode : INode
{
    private const string Split = "split";
    private const string Blend = "blend";

    /// <summary>
    /// 扱える効果。どの値も実物から読んだもので、推測は入っていない。
    ///   Field      枝分かれさせる部品（blend では使わない）
    ///   Strength   色ごとに倍率を掛ける設定
    ///   SplitFrom  枝分かれの起点。空なら効果へ入ってくる元の絵
    ///   Tail       足し戻したあとに置く締めの部品。空なら要らない
    /// </summary>
    private sealed class Target
    {
        public string Key = "";
        public string Effect = "";
        public string Field = "";
        public string Strength = "";
        public string Label = "";
        public string Shape = Split;
        public string SplitFrom = "";
        public string Tail = "";
        public double Max = 4.0;
    }

    private static readonly Target[] Known =
    {
        new Target { Key = "sharpen", Effect = "SharpenGpuEffect", Field = "sharpenEffect",
                     Strength = "Sharpness", Label = "sharpen" },
        new Target { Key = "motion_blur", Effect = "MotionBlurGpuEffect", Field = "motionBlurEffect",
                     Strength = "Distance", Label = "blur" },
        new Target { Key = "gaussian_blur", Effect = "GaussianBlurGpuEffect", Field = "blurEffect",
                     Strength = "StandardDeviation", Label = "radius",
                     SplitFrom = "gammaEffect", Tail = "invGammaEffect" },
        new Target { Key = "frosted_glass", Effect = "FrostedGlassGpuEffect", Label = "frosting",
                     Shape = Blend, Max = 1.0 },
    };

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string[] Added = { "NgolRed", "NgolGreen", "NgolBlue" };

    private delegate PropertyCollection MakePropertiesOrig(GpuImageEffect self);
    private delegate ControlInfo MakeDialogOrig(GpuImageEffect self, PropertyCollection props);
    private delegate IDeviceImage CreateOutputOrig(GpuImageEffect self, IDeviceContext dc);
    private delegate void UpdateOutputOrig(GpuImageEffect self, IDeviceContext dc);
    private delegate InspectTokenAction InspectOrig(GpuImageEffect self,
        PropertyBasedEffectConfigToken oldToken, PropertyBasedEffectConfigToken newToken);

    /// <summary>効果ごとに組んだもの。効果が捨てられれば一緒に消える持ち方にする。</summary>
    private static readonly ConditionalWeakTable<object, Built> Assembled = new();

    private sealed class Built
    {
        public object[] Parts = Array.Empty<object>();
        public Target Of = new();
        public List<IDisposable> Owned = new();
    }

    // 対象ごとに別の鍵で持つ。いくつも同時に当てられるようにするため。
    private static string StateKey(string key) => "pdn.fx.extend." + key + ".hooks.v1";
    private static string CountKey(string key) => "pdn.fx.extend." + key + ".counts.v1";
    private static string NoteKey(string key) => "pdn.fx.extend." + key + ".note.v1";

    private static int[] Counts(string key)
    {
        // 大きさも見る。前の世代が短い入れ物を置いていると、範囲の外を触って例外になり、
        // その先の処理ごと落ちる。数える器の事故は、数えている対象の不具合に見える。
        if (AppDomain.CurrentDomain.GetData(CountKey(key)) is int[] held && held.Length >= 4) return held;
        var made = new int[4];
        AppDomain.CurrentDomain.SetData(CountKey(key), made);
        return made;
    }

    /// <summary>描画の途中で降りた理由。降りた回数だけでは、どこで降りたか分からない。</summary>
    private static void Note(string key, string text) => AppDomain.CurrentDomain.SetData(NoteKey(key), text);

    public void Execute(IExecutionContext ctx)
    {
        var wanted = (ctx.GetPortValue("effect") as string ?? "sharpen").Trim().ToLowerInvariant();

        // 外す側は 1 つの口で済ませる。ツールバーの項目は値を 1 つしか渡せない。
        if (wanted == "off")
        {
            var taken = Known.Count(k => Remove(k.Key));
            ctx.SetPortValue("status", taken == 0 ? "not patched" : "removed from " + taken);
            return;
        }

        var target = Known.FirstOrDefault(k => k.Key == wanted);
        if (target == null)
        {
            ctx.SetPortValue("status", "unknown effect. known: off, " + string.Join(", ", Known.Select(k => k.Key)));
            return;
        }

        var enabled = !(ctx.GetPortValue("enabled") is bool b) || b;

        string status;
        if (!enabled)
        {
            status = Remove(target.Key) ? "removed" : "not patched";
        }
        else
        {
            // 既に当たっていても付け直す。フックは当てた時点のコードを握ったままなので、
            // このファイルを直して読み込み直しても、外して当て直さなければ古いほうが走り続ける。
            var replaced = Remove(target.Key);
            var made = Install(target);
            status = replaced && made == "patched" ? "replaced" : made;
        }

        var counts = Counts(target.Key);
        var dialogs = Volatile.Read(ref counts[0]);
        var renders = Volatile.Read(ref counts[1]);
        var builds = Volatile.Read(ref counts[2]);
        var refreshes = Volatile.Read(ref counts[3]);

        ctx.SetPortValue("status", status);
        ctx.SetPortValue("dialogs", (double)dialogs);
        ctx.SetPortValue("renders", (double)renders);
        ctx.SetPortValue("builds", (double)builds);
        ctx.SetPortValue("refreshes", (double)refreshes);
        ctx.SetPortValue("note", AppDomain.CurrentDomain.GetData(NoteKey(target.Key)) as string ?? "");
        ctx.Store.Set("pdn.fx.extend.result",
            string.Format(CultureInfo.InvariantCulture,
                "effect    : {0}\nstatus    : {1}\ndialogs   : {2}\nrenders   : {3}\nbuilds    : {4}\nrefreshes : {5}",
                target.Key, status, dialogs, renders, builds, refreshes));
    }

    // ---------------------------------------------------------------- 付け外し

    private static string Install(Target target)
    {
        var effect = FindType(target.Effect);
        if (effect == null) return "the host's " + target.Effect + " was not found";

        var makeProperties = effect.GetMethod("OnCreatePropertyCollection", Any, null, Type.EmptyTypes, null);
        var makeDialog = effect.GetMethod("OnCreateConfigUI", Any, null, new[] { typeof(PropertyCollection) }, null);
        var makeOutput = effect.GetMethod("OnCreateOutput", Any, null, new[] { typeof(IDeviceContext) }, null);
        var pushOutput = effect.GetMethod("OnUpdateOutput", Any, null, new[] { typeof(IDeviceContext) }, null);
        var inspect = effect.GetMethod("OnInspectTokenChanges", Any, null,
            new[] { typeof(PropertyBasedEffectConfigToken), typeof(PropertyBasedEffectConfigToken) }, null);

        foreach (var entry in new[] { makeProperties, makeDialog, makeOutput, pushOutput, inspect })
        {
            if (entry == null) return "one of the effect's entries was not found";

            // 本体を持たないメソッドは掴まない。掴もうとすると作りかけの物体が残り、
            // それを回収するときにファイナライザの中で失敗して、無関係なスレッドの
            // 未処理例外としてホストごと落ちる。失敗してから片付ける手は無い。
            if (entry.IsAbstract || entry.GetMethodBody() == null) return "one of the effect's entries has no body";
        }

        var made = new List<IDisposable>();
        try
        {
            made.Add(new Hook(makeProperties,
                (Func<MakePropertiesOrig, GpuImageEffect, PropertyCollection>)
                ((orig, self) => PropertiesHook(orig, self, target))));
            made.Add(new Hook(makeDialog,
                (Func<MakeDialogOrig, GpuImageEffect, PropertyCollection, ControlInfo>)
                ((orig, self, props) => DialogHook(orig, self, props, target))));
            made.Add(new Hook(makeOutput,
                (Func<CreateOutputOrig, GpuImageEffect, IDeviceContext, IDeviceImage>)
                ((orig, self, dc) => CreateOutputHook(orig, self, dc, target))));
            made.Add(new Hook(pushOutput,
                (Action<UpdateOutputOrig, GpuImageEffect, IDeviceContext>)
                ((orig, self, dc) => UpdateOutputHook(orig, self, dc, target.Key))));
            made.Add(new Hook(inspect,
                (Func<InspectOrig, GpuImageEffect, PropertyBasedEffectConfigToken,
                      PropertyBasedEffectConfigToken, InspectTokenAction>)
                ((orig, self, oldToken, newToken) => InspectHook(orig, self, oldToken, newToken))));
            AppDomain.CurrentDomain.SetData(StateKey(target.Key), made);
            Mark.Add(effect, "NGOL Extended: a separate " + target.Label + " per colour channel");
            return "patched";
        }
        catch (Exception ex)
        {
            // 一部だけ当たった状態で残さない。外せるものは外す。
            foreach (var hook in made) { try { hook.Dispose(); } catch { } }
            return "could not patch: " + ex.GetBaseException().Message;
        }
    }

    private static bool Remove(string key)
    {
        if (AppDomain.CurrentDomain.GetData(StateKey(key)) is not List<IDisposable> hooks) return false;
        AppDomain.CurrentDomain.SetData(StateKey(key), null);
        foreach (var hook in hooks) { try { hook.Dispose(); } catch { } }

        var target = Known.FirstOrDefault(k => k.Key == key);
        if (target != null) Mark.Remove(FindType(target.Effect));
        return true;
    }

    private static Type? FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types) { if (t.Name == name) return t; }
        }
        return null;
    }

    // ---------------------------------------------------------------- 調整ウィンドウ

    private static PropertyCollection PropertiesHook(MakePropertiesOrig orig, GpuImageEffect self, Target target)
    {
        var props = orig(self);
        try
        {
            var mine = new PropertyCollection(new Property[]
            {
                Mark.MakeNote(),
                new DoubleProperty(Added[0], 1.0, 0.0, target.Max),
                new DoubleProperty(Added[1], 1.0, 0.0, target.Max),
                new DoubleProperty(Added[2], 1.0, 0.0, target.Max),
            });
            return PropertyCollection.CreateMerged(props, mine);
        }
        catch
        {
            // 足せなければホストの一覧をそのまま返す。返さないと効果が開けなくなる。
            return props;
        }
    }

    private static ControlInfo DialogHook(MakeDialogOrig orig, GpuImageEffect self, PropertyCollection props,
                                          Target target)
    {
        var ui = orig(self, props);
        Interlocked.Increment(ref Counts(target.Key)[0]);
        try
        {
            Mark.PlaceNote(ui, props);

            var names = new[] { "Red " + target.Label, "Green " + target.Label, "Blue " + target.Label };
            var step = target.Max / 50.0;
            for (var channel = 0; channel < Added.Length; channel++)
            {
                var name = Added[channel];

                // ホスト側が既定の組み立てを使っていると、足した設定にはもうつまみが在る。
                // 無いときだけ足す。両方やると同じものが 2 つ並ぶ。
                if (ui.FindControlForPropertyName(name) == null
                    && ui is PanelControlInfo panel
                    && props.TryGetProperty(name, out var property)
                    && property != null)
                {
                    panel.AddChildControl(PropertyControlInfo.CreateFor(property));
                }

                ui.SetPropertyControlValue(name, ControlInfoPropertyNames.DisplayName, names[channel]);

                // 区切り線は見出しの 1 本だけにする。つまみごとに引くと境目が読めない。
                ui.SetPropertyControlValue(name, ControlInfoPropertyNames.ShowHeaderLine, false);
                ui.SetPropertyControlValue(name, ControlInfoPropertyNames.DecimalPlaces, 2);
                ui.SetPropertyControlValue(name, ControlInfoPropertyNames.SliderSmallChange, step);
                ui.SetPropertyControlValue(name, ControlInfoPropertyNames.SliderLargeChange, step * 5.0);
                ui.SetPropertyControlValue(name, ControlInfoPropertyNames.UpDownIncrement, step);
            }
        }
        catch { }
        return ui;
    }

    /// <summary>足した設定だけが動いたとき、ホストは描き直す必要が無いと答える。そこを引き上げる。</summary>
    private static InspectTokenAction InspectHook(InspectOrig orig, GpuImageEffect self,
        PropertyBasedEffectConfigToken oldToken, PropertyBasedEffectConfigToken newToken)
    {
        var action = orig(self, oldToken, newToken);
        try
        {
            if (action != InspectTokenAction.None) return action;
            foreach (var name in Added)
            {
                if (Read(oldToken, name) != Read(newToken, name)) return InspectTokenAction.UpdateOutput;
            }
        }
        catch { }
        return action;
    }

    // ---------------------------------------------------------------- 出す絵

    private static IDeviceImage CreateOutputHook(CreateOutputOrig orig, GpuImageEffect self, IDeviceContext dc,
                                                 Target target)
    {
        // 数えるのは入口。組めたかどうかは別に数える。まとめると「呼ばれていない」と
        // 「呼ばれたが降りた」が同じ 0 になり、次に見る場所が決まらない。
        Interlocked.Increment(ref Counts(target.Key)[1]);

        // ホストにも組ませる。設定はホストが自分の部品へ入れるので、そこから写す。
        var hostImage = orig(self, dc);

        try
        {
            var made = target.Shape == Blend
                ? BuildBlend(self, dc, target, hostImage)
                : BuildSplit(self, dc, target, hostImage);
            if (made == null) return hostImage;

            Interlocked.Increment(ref Counts(target.Key)[2]);
            return made;
        }
        catch (Exception ex)
        {
            // 組めなければホストの絵をそのまま返す。何も返さないと描画が壊れる。
            Note(target.Key, "build: " + ex.GetBaseException().Message);
            return hostImage;
        }
    }

    /// <summary>枝分かれできる効果。ホストの部品の写しを 3 本立てて足し戻す。</summary>
    private static IDeviceImage? BuildSplit(GpuImageEffect self, IDeviceContext dc, Target target,
                                            IDeviceImage hostImage)
    {
        var own = Member(self, target.Field);
        if (own == null) { Note(target.Key, "the host's " + target.Field + " could not be read"); return null; }

        var source = target.SplitFrom.Length == 0
            ? SourceImage(self)
            : Member(self, target.SplitFrom) as IDeviceImage;
        if (source == null) { Note(target.Key, "the image to split from could not be read"); return null; }

        var built = new Built { Parts = new object[3], Of = target };
        var kept = new IDeviceImage[3];
        for (var channel = 0; channel < 3; channel++)
        {
            var part = Activator.CreateInstance(own.GetType(), new object[] { dc });
            if (part is not IDeviceImage image) { Note(target.Key, "a copy of the part is not an image"); return null; }
            built.Parts[channel] = part;
            built.Owned.Add((IDisposable)part);

            if (!SetInput(part, source)) { Note(target.Key, "the copy would not take the input image"); return null; }
            CopySettings(own, part, target.Strength, 1f);

            // 1 色だけ通す。透明度は 3 本で 3 等分し、足すと元に戻る。
            // straight 指定なので色は透明度で割り戻されず、重みがそのまま残る。
            var keep = new ColorMatrixEffect(dc, image, Weights(
                    channel == 0 ? 1f : 0f, channel == 1 ? 1f : 0f, channel == 2 ? 1f : 0f, 1f / 3f),
                ColorMatrixAlphaMode.Straight, false);
            kept[channel] = keep;
            built.Owned.Add(keep);
        }

        var add = new Vector4Float(0f, 1f, 1f, 0f);
        var pair = new ArithmeticCompositeEffect(dc, kept[0], kept[1], add, false);
        built.Owned.Add(pair);
        IDeviceImage summed = new ArithmeticCompositeEffect(dc, pair, kept[2], add, false);
        built.Owned.Add((IDisposable)summed);

        // 締めの部品が要る効果だけ、最後にもう 1 つ写しを置く。
        // 色を混ぜていないので、ここは足したあとで 1 回でよい。
        if (target.Tail.Length > 0)
        {
            var back = Member(self, target.Tail);
            if (back == null) { Note(target.Key, "the host's " + target.Tail + " could not be read"); return null; }

            var closing = Activator.CreateInstance(back.GetType(), new object[] { dc });
            if (closing is not IDeviceImage closingImage)
            {
                Note(target.Key, "a copy of the closing part is not an image");
                return null;
            }
            built.Owned.Add((IDisposable)closing);
            if (!SetInput(closing, summed)) { Note(target.Key, "the closing copy would not take its input"); return null; }
            CopySettings(back, closing, "", 1f);
            summed = closingImage;
        }

        Drop(self);
        Assembled.Add(self, built);
        Push(self, target.Key);
        return summed;
    }

    /// <summary>枝分かれできない歪み系。ホストの出す絵と元の絵を色ごとの重みで混ぜる。</summary>
    private static IDeviceImage? BuildBlend(GpuImageEffect self, IDeviceContext dc, Target target,
                                            IDeviceImage hostImage)
    {
        var source = SourceImage(self);
        if (source == null) { Note(target.Key, "the effect's source image could not be read"); return null; }

        var built = new Built { Parts = new object[2], Of = target };

        // 2 枚とも透明度は半分ずつ持たせる。ここを割合で下げると、
        // 色は透明度を超えられない決まりに掛かり、下げた枝の色ごと切り詰められる。
        var plain = Weighted(dc, source, built);
        var rough = Weighted(dc, hostImage, built);
        built.Parts[0] = plain;
        built.Parts[1] = rough;

        var summed = new ArithmeticCompositeEffect(dc, plain, rough, new Vector4Float(0f, 1f, 1f, 0f), false);
        Precise(summed);
        built.Owned.Add(summed);

        // straight 指定の行列は色を透明度で割り戻さないので、色はもう足すだけで揃っている。
        // 透明度だけ 2 枚で半分ずつ持たせてあり、足すと元に戻る。
        var mixed = new ColorMatrixEffect(dc, summed, Weights(1f, 1f, 1f, 1f),
                                          ColorMatrixAlphaMode.Premultiplied, false);
        Precise(mixed);
        built.Owned.Add(mixed);

        Drop(self);
        Assembled.Add(self, built);
        Push(self, target.Key);
        return mixed;
    }

    private static ColorMatrixEffect Weighted(IDeviceContext dc, IDeviceImage input, Built built)
    {
        var made = new ColorMatrixEffect(dc, input, Weights(1f, 1f, 1f, 0.5f),
                                         ColorMatrixAlphaMode.Straight, false);
        Precise(made);
        built.Owned.Add(made);
        return made;
    }

    private static void Precise(DeviceEffect effect)
    {
        try { effect.Properties.Precision.SetValue(BufferPrecision.Float32); } catch { }
    }

    private static void UpdateOutputHook(UpdateOutputOrig orig, GpuImageEffect self, IDeviceContext dc, string key)
    {
        Interlocked.Increment(ref Counts(key)[3]);
        orig(self, dc);
        try { Push(self, key); }
        catch (Exception ex) { Note(key, "push: " + ex.GetBaseException().Message); }
    }

    /// <summary>つまみの値を、組んだ物へ配る。ホストが自分の部品を直したあとに呼ばれる。</summary>
    private static void Push(GpuImageEffect self, string key)
    {
        if (!Assembled.TryGetValue(self, out var built) || built == null) return;

        var target = built.Of;
        var token = Token(self);
        var scale = new float[3];
        for (var channel = 0; channel < 3; channel++) scale[channel] = Clamp(Read(token, Added[channel]), target.Max);

        if (target.Shape == Blend)
        {
            if (built.Parts.Length < 2) return;
            if (built.Parts[0] is ColorMatrixEffect plain)
            {
                plain.Properties.ColorMatrix.SetValue(Weights(1f - scale[0], 1f - scale[1], 1f - scale[2], 0.5f));
            }
            if (built.Parts[1] is ColorMatrixEffect rough)
            {
                rough.Properties.ColorMatrix.SetValue(Weights(scale[0], scale[1], scale[2], 0.5f));
            }
            return;
        }

        if (built.Parts.Length < 3) return;
        var own = Member(self, target.Field);
        if (own == null) return;
        for (var channel = 0; channel < 3; channel++)
        {
            var part = built.Parts[channel];
            if (part == null) continue;
            CopySettings(own, part, target.Strength, scale[channel]);
        }
    }

    // ---------------------------------------------------------------- 部品の設定

    /// <summary>
    /// ホストの部品が持っている設定を、そのまま写す。名前の合う 1 つだけ倍率を掛ける。
    /// 設定の顔ぶれは効果ごとに違うので、名指しせず並んでいるものを全部写す。
    /// </summary>
    private static void CopySettings(object own, object target, string strength, float scale)
    {
        var from = Properties(own);
        var to = Properties(target);
        if (from == null || to == null) return;

        foreach (var slot in from.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (slot.Name == "Input" || slot.Name == "ClassID") continue;
            if (!slot.PropertyType.IsGenericType) continue;
            if (slot.PropertyType.Name.IndexOf("EffectPropertyAccessor", StringComparison.Ordinal) < 0) continue;

            try
            {
                var source = slot.GetValue(from);
                var sink = slot.GetValue(to);
                if (source == null || sink == null) continue;

                var read = source.GetType().GetMethod("GetValue", Type.EmptyTypes);
                var write = sink.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "SetValue" && m.GetParameters().Length == 1);
                if (read == null || write == null) continue;

                var value = read.Invoke(source, null);
                if (slot.Name == strength && value is float number) value = number * scale;
                write.Invoke(sink, new[] { value });
            }
            catch { }
        }
    }

    private static object? Properties(object effect) => Member(effect, "Properties");

    /// <summary>入ってくる絵を差し込む。呼ぶ相手は設定の入れ物ではなく、入口そのもの。</summary>
    private static bool SetInput(object effect, IDeviceImage image)
    {
        var props = Properties(effect);
        var slot = props?.GetType().GetProperty("Input", BindingFlags.Instance | BindingFlags.Public);
        var accessor = slot?.GetValue(props);
        if (accessor == null) return false;

        var set = accessor.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Set" && m.GetParameters().Length == 2);
        if (set == null) return false;

        try { set.Invoke(accessor, new object?[] { image, true }); return true; }
        catch { return false; }
    }

    private static void Drop(GpuImageEffect self)
    {
        if (!Assembled.TryGetValue(self, out var built) || built == null) return;
        Assembled.Remove(self);
        for (var i = built.Owned.Count - 1; i >= 0; i--)
        {
            try { built.Owned[i].Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 読み取り

    private static PropertyBasedEffectConfigToken? Token(GpuImageEffect self)
        => Member(self, "Token") as PropertyBasedEffectConfigToken;

    /// <summary>
    /// 名前で 1 つ辿る。基底クラスまで自分で降りる必要がある。
    /// 型に問い合わせる素の手は、基底クラスの非 public なものを返さない。
    /// </summary>
    private static object? Member(object owner, string name)
    {
        for (var t = owner.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            var found = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
            if (found != null)
            {
                try { return found.GetValue(owner); } catch { return null; }
            }
            var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                try { return field.GetValue(owner); } catch { return null; }
            }
        }
        return null;
    }

    private static double Read(PropertyBasedEffectConfigToken? token, string name)
    {
        if (token == null) return 1.0;
        try
        {
            var property = token.GetProperty(name);
            return property?.Value is double d ? d : 1.0;
        }
        catch { return 1.0; }
    }

    private static float Clamp(double value, double max)
        => value < 0 ? 0f : value > max ? (float)max : (float)value;

    private static IDeviceImage? SourceImage(GpuImageEffect self)
    {
        var env = Member(self, "Environment") ?? Member(self, "Environment2");
        return env == null ? null : Member(env, "SourceImage") as IDeviceImage;
    }

    /// <summary>色ごとに重みを掛けるだけの行列。</summary>
    private static Matrix5x4Float Weights(float red, float green, float blue, float alpha)
        => new Matrix5x4Float(
            red, 0f, 0f, 0f,
            0f, green, 0f, 0f,
            0f, 0f, blue, 0f,
            0f, 0f, 0f, alpha,
            0f, 0f, 0f, 0f);

    // ================================================================ 手を入れた所を見せる

    /// <summary>
    /// 手を入れた効果に、ホスト自身がプラグインへ付けている印を付ける。
    ///
    /// ホストは項目を組むとき IsPlugin を「組み込みでないこと」で立て、
    /// 項目を描くときそれを見て印を右端へ置く。読む所はその 1 つだけなので、
    /// 立てても効果の並び順や分類は動かない。
    ///
    ///   PdnMenuItem.OnPaint -- IsPlugin -- PdnToolStripRenderer.DrawItemPluginIndicator
    ///
    /// 印の絵はホスト自身のリソースで、描画側がプロセスに 1 つだけ持つ。
    /// こちらが渡すのは真偽値だけで、画像は同梱していない。
    ///
    /// 当てる先は、効果メニューが組み上がった後。
    /// PdnMenuItem.DropDownOpening は、その効果メニューが中身を組んだ後に発火する
    /// （組む処理を呼んでから、この事象を出す順になっている）。
    /// メニューは 1 度だけ組まれて残るので、購読の前に 1 度当てる分も要る。
    ///
    /// 状態は世代をまたいで残す。入れ物は framework の型だけにする。
    /// </summary>
    private static class Mark
    {
        /// <summary>印を付けたい効果。型の完全名 -- 何を足したか。</summary>
        private const string WantedKey = "pdn.fx.extended.wanted.v1";

        /// <summary>実際に付けた効果。付けていないものを外さないため。</summary>
        private const string MarkedKey = "pdn.fx.extended.marked.v1";

        /// <summary>購読しているメニューと、渡した受け口。外すときに要る。</summary>
        private const string WatchKey = "pdn.fx.extended.watch.v1";

        private const string ItemPrefix = "Effect(";

        private const BindingFlags Reach =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>足したつまみの頭へ置く見出し。ホストの設定と地続きに見えないようにする。</summary>
        public const string NoteProperty = "NgolNote";

        private const string NoteText = "NGOL Extended";

        /// <summary>見出しは設定として持つ。ホストは設定の並びからつまみを組むため。</summary>
        public static Property MakeNote() => new StringProperty(NoteProperty, NoteText, 96);

        /// <summary>
        /// 見出しを、値を書けない札として見せる。
        /// 札は「名前（右へ区切り線）」と「値」の 2 行で描かれるので、
        /// 名前を空白 1 文字にして線だけを通し、文字は値のほうへ回す。
        /// 空文字にすると札ごと消える（文字が無い名前は描かれない決まりのため）。
        /// </summary>
        public static void PlaceNote(ControlInfo ui, PropertyCollection props)
        {
            if (ui.FindControlForPropertyName(NoteProperty) == null
                && ui is PanelControlInfo panel
                && props.TryGetProperty(NoteProperty, out var property)
                && property != null)
            {
                panel.AddChildControl(PropertyControlInfo.CreateFor(property));
            }

            ui.SetPropertyControlType(NoteProperty, PropertyControlType.Label);
            ui.SetPropertyControlValue(NoteProperty, ControlInfoPropertyNames.DisplayName, " ");
            ui.SetPropertyControlValue(NoteProperty, ControlInfoPropertyNames.ShowHeaderLine, true);
        }

        public static void Add(Type? effect, string what)
        {
            if (effect?.FullName == null) return;
            Wanted()[effect.FullName] = what;
            Refresh();
        }

        public static void Remove(Type? effect)
        {
            if (effect?.FullName == null) return;
            Wanted().Remove(effect.FullName);
            Refresh();
        }

        private static Dictionary<string, string> Wanted()
        {
            if (AppDomain.CurrentDomain.GetData(WantedKey) is Dictionary<string, string> held) return held;
            var made = new Dictionary<string, string>(StringComparer.Ordinal);
            AppDomain.CurrentDomain.SetData(WantedKey, made);
            return made;
        }

        private static HashSet<string> Marked()
        {
            if (AppDomain.CurrentDomain.GetData(MarkedKey) is HashSet<string> held) return held;
            var made = new HashSet<string>(StringComparer.Ordinal);
            AppDomain.CurrentDomain.SetData(MarkedKey, made);
            return made;
        }

        private static List<object> Watching()
        {
            if (AppDomain.CurrentDomain.GetData(WatchKey) is List<object> held) return held;
            var made = new List<object>();
            AppDomain.CurrentDomain.SetData(WatchKey, made);
            return made;
        }

        private static void Refresh()
        {
            Form? host = null;
            foreach (Form open in Application.OpenForms)
            {
                if (open.Name == "MainForm") { host = open; break; }
            }
            if (host == null) return;

            // メニューはホスト自身のスレッドのものなので、そこへ渡してから触る。
            try { host.Invoke(new Action(() => Apply(host))); } catch { }
        }

        private static void Apply(Form host)
        {
            Unwatch();

            var menus = EffectMenus(host);
            var subscribe = Wanted().Count > 0;
            foreach (var menu in menus)
            {
                Paint(menu);
                if (!subscribe) continue;

                var add = MethodUp(menu.GetType(), "add_DropDownOpening");
                if (add == null) continue;

                EventHandler receiver = (sender, e) => { try { Paint(sender); } catch { } };
                try { add.Invoke(menu, new object[] { receiver }); }
                catch { continue; }

                var held = Watching();
                held.Add(menu);
                held.Add(receiver);
            }
        }

        private static void Unwatch()
        {
            var held = Watching();
            for (var i = 0; i + 1 < held.Count; i += 2)
            {
                var menu = held[i];
                if (menu == null || held[i + 1] is not EventHandler receiver) continue;
                var drop = MethodUp(menu.GetType(), "remove_DropDownOpening");
                try { drop?.Invoke(menu, new object[] { receiver }); } catch { }
            }
            held.Clear();
        }

        private static void Paint(object menu)
        {
            if (menu is not ToolStripDropDownItem drop) return;
            var wanted = Wanted();
            var marked = Marked();
            foreach (ToolStripItem entry in drop.DropDownItems) PaintOne(entry, wanted, marked);
        }

        private static void PaintOne(ToolStripItem entry, Dictionary<string, string> wanted, HashSet<string> marked)
        {
            var name = entry.Name;
            if (name != null
                && name.Length > ItemPrefix.Length + 1
                && name.StartsWith(ItemPrefix, StringComparison.Ordinal)
                && name[name.Length - 1] == ')')
            {
                var effect = name.Substring(ItemPrefix.Length, name.Length - ItemPrefix.Length - 1);
                if (wanted.TryGetValue(effect, out var what)) Set(entry, effect, what, marked);
                else if (marked.Contains(effect)) Clear(entry, effect, marked);
            }

            if (entry is ToolStripDropDownItem sub)
            {
                foreach (ToolStripItem child in sub.DropDownItems) PaintOne(child, wanted, marked);
            }
        }

        private static void Set(ToolStripItem entry, string effect, string what, HashSet<string> marked)
        {
            var slot = PropertyUp(entry.GetType(), "IsPlugin");
            if (slot == null || !slot.CanWrite || !slot.CanRead) return;

            // 元から印が付いているものには触らない。外すとき元へ戻せなくなる。
            if (!marked.Contains(effect) && slot.GetValue(entry) is bool already && already) return;

            try
            {
                slot.SetValue(entry, true);
                entry.ToolTipText = what;
                marked.Add(effect);
            }
            catch { }
        }

        private static void Clear(ToolStripItem entry, string effect, HashSet<string> marked)
        {
            var slot = PropertyUp(entry.GetType(), "IsPlugin");
            try
            {
                if (slot != null && slot.CanWrite) slot.SetValue(entry, false);
                entry.ToolTipText = null;
            }
            catch { }
            marked.Remove(effect);
        }

        private static List<object> EffectMenus(Control root)
        {
            var found = new List<object>();
            Gather(root, found);
            return found;
        }

        private static void Gather(Control root, List<object> found)
        {
            if (root is ToolStrip strip)
            {
                foreach (ToolStripItem entry in strip.Items)
                {
                    if (Derives(entry.GetType(), "EffectMenuBase")) found.Add(entry);
                }
            }
            foreach (Control child in root.Controls) Gather(child, found);
        }

        private static bool Derives(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.Name == name) return true;
            }
            return false;
        }

        /// <summary>
        /// 基底まで 1 段ずつ辿って最初に見つかったものを返す。
        /// 素の問い合わせは、同じ名前が基底にもあると曖昧だとして投げる
        /// （事象を出す口はホスト側と土台側の両方に在る）。
        /// </summary>
        private static MethodInfo? MethodUp(Type type, string name)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var found = t.GetMethod(name, Reach, null, new[] { typeof(EventHandler) }, null);
                if (found != null) return found;
            }
            return null;
        }

        private static PropertyInfo? PropertyUp(Type type, string name)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var found = t.GetProperty(name, Reach);
                if (found != null) return found;
            }
            return null;
        }
    }
}
