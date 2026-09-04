using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストの編集操作を名前で呼ぶ。
///
/// 編集操作の入口は関数ポインタの並びで、名前は実行時には残っていない。
/// 並び順さえ分かればオフセットが決まる（並び順 x 8）ので、下の一覧に 1 行足せば
/// 新しい操作を呼べる。ネイティブ側の作り直しは要らない。
///
/// 並びは公式ヘッダーの宣言順そのもの。先頭が関数ではない点に注意（info が 0 番）。
/// </summary>
[NodeType("aviutl.edit.section_call", "AviUtl2", "Edit Section Call",
    Version = "1.8.0",
    Description = "Calls one of the host's editing operations by name. The operations live as an array of function pointers with no names at runtime, so the order is kept here as text: offset is index times 8. Adding a new operation means adding one line to that list - no native rebuild. Nothing is changed unless apply is set.")]
[NodePort("function", PortDirection.Input, "string", Description = "Which operation to call. See the 'available' output for the list")]
[NodePort("layer", PortDirection.Input, "number", Description = "Layer number counted from 0. The host's UI counts from 1")]
[NodePort("frame", PortDirection.Input, "number", Description = "Frame number counted from 0")]
[NodePort("index", PortDirection.Input, "number", Description = "Extra argument, used by the operations that take one (selection index, target layer, ...)")]
[NodePort("name", PortDirection.Input, "string", Description = "Extra text argument, used by the operations that take a name (effect name, ...)")]
[NodePort("index_shift", PortDirection.Input, "number", Description = "Shifts the slot number. Use it to reproduce what a plugin built against an older header would do: if a function was inserted before this one since then, that plugin is off by one. Read-only functions only")]
[NodePort("apply", PortDirection.Input, "boolean", Description = "false (default) resolves and reports without changing anything. true performs the operation")]
[NodePort("value", PortDirection.Output, "number", Description = "What the operation returned, when it returns a number")]
[NodePort("exact", PortDirection.Output, "number", Description = "What the operation returned when the answer is not a whole number, such as the value a trackbar holds partway between points")]
[NodePort("ok", PortDirection.Output, "boolean", Description = "true when the call went through")]
[NodePort("offset", PortDirection.Output, "number", Description = "Where the operation sat in the array, in bytes. Useful when the host is updated and the order shifts")]
[NodePort("list", PortDirection.Input, "boolean", Description = "Also return the whole list of operations this node knows. Off by default: the list is long and returning it every time costs the caller far more than the answer they asked for")]
[NodePort("available", PortDirection.Output, "string", Description = "The operations this node knows, one per line. Empty unless list is set, or unless the name given was not one of them")]
[NodePort("text", PortDirection.Output, "string", Description = "What the operation returned, when it returns text")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2SectionCallNode : INode
{
    // 公式ヘッダー（plugin2.h）の宣言順をそのまま写したもの。手で書くと数え間違える。
    // 先頭の info を数え落とすと、全部が 1 つずつずれる。
    // 先頭は関数ではないので 0 番を空けてある。
    static readonly string[] Order =
    {
        "info",
        "create_object_from_alias",
        "find_object",
        "count_object_effect",
        "get_object_layer_frame",
        "get_object_alias",
        "get_object_item_value",
        "set_object_item_value",
        "move_object",
        "delete_object",
        "get_focus_object",
        "set_focus_object",
        "get_project_file",
        "get_selected_object",
        "get_selected_object_num",
        "get_mouse_layer_frame",
        "pos_to_layer_frame",
        "is_support_media_file",
        "get_media_info",
        "create_object_from_media_file",
        "create_object",
        "set_cursor_layer_frame",
        "set_display_layer_frame",
        "set_select_range",
        "set_grid_bpm",
        "get_object_name",
        "set_object_name",
        "get_layer_name",
        "set_layer_name",
        "get_scene_name",
        "set_scene_name",
        "set_scene_size",
        "set_scene_frame_rate",
        "set_scene_sample_rate",
        "get_layer_enable",
        "set_layer_enable",
        "get_layer_lock",
        "set_layer_lock",
        "get_object_section_num",
        "get_focus_object_section",
        "get_object_section_frame",
        "get_object_track_value",
        "get_object_check_value",
        "get_object_track_info",
        "get_palette_name",
        "get_palette_info",
        "get_font",
        "get_object_track_group_names",
        "deprecated_get_grid_bpm_list",
        "deprecated_set_grid_bpm_list",
        "find_effect",
        "get_effect_list",
        "get_effect_name",
        "get_effect_enable",
        "set_effect_enable",
        "get_effect_lock",
        "set_effect_lock",
        "get_effect_item_value",
        "set_effect_item_value",
        "get_effect_track_value",
        "get_effect_check_value",
        "get_effect_track_info",
        "get_grid_bpm_list",
        "set_grid_bpm_list",
        "create_effect",
        "delete_effect",
        "create_object_section",
        "delete_object_section",
        "move_object_section",
        "move_effect",
        "get_effect_data_value",
        "set_effect_data_value",
        "set_edited_state",
        "get_mark_frame_list",
        "get_mark_frame_memo",
        "set_mark_frame",
        "clear_mark_frame",
        "move_mark_frame",
        "set_palette_info",
    };

    // 一覧に無い特別な名前。全スロットの行き先を出す（版ごとの照合用）。
    const string DumpName = "dump";

    // disasm-verified: find_object が +0x10、set_object_item_value が +0x38 に居ることを
    // 既存 export のコールバックの命令列で確認した。上の並びと一致する。
    const int SlotSize = 8;

    // 編集の入口を保持しているプラグイン内の場所。
    // disasm-verified: mov rax,[rel ...] が指す先。作り直すと動くので、外れたら測り直す。
    // 編集ハンドルは名前で受け取る。
    //
    // 以前はこの変数の位置を番地で覚えていたが、番地はプラグインをビルドし直すたびに
    // 動く。古い値のままだと無関係なメモリを編集ハンドルとして扱い、そこから
    // 関数ポインタのつもりで読んだ値へ飛んで落ちる。
    //
    // disasm-verified: Ngol_GetEditHandle RVA 0x8a70 は mov rax,[rel ...] と ret の 2 命令。
    // 引数 0 個 / 戻り値は rax の 64bit
    [DllImport("NgolForAviUtl2.aux2")]
    private static extern IntPtr Ngol_GetEditHandle();

    // EDIT_HANDLE の並び。call_edit_section_param が 1 番（disasm で +0x08 を確認）。
    const int CallEditSectionParamOffset = 0x08;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void SectionProc(IntPtr param, IntPtr section);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool CallEditSectionParam(IntPtr param, IntPtr proc);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr FindObject(int layer, int frame);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void DeleteObject(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool MoveObject(IntPtr obj, int layer, int frame);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr GetFocusObject();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr CreateEffect(IntPtr obj, [MarshalAs(UnmanagedType.LPWStr)] string effect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr FindEffect(IntPtr obj, [MarshalAs(UnmanagedType.LPWStr)] string effect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool DeleteEffect(IntPtr obj, IntPtr effect);

    // 一覧の格納先を渡さないと個数だけが返る。2 度呼んで数を決めてから受け取る。
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int GetEffectList(IntPtr obj, IntPtr list, int num);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr GetEffectName(IntPtr effect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool GetEffectEnable(IntPtr effect);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int GetSelectedObjectNum();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr GetObjectAlias(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate bool CreateObjectSection(IntPtr obj, int frame);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate bool DeleteObjectSection(IntPtr obj, int section);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate bool MoveObjectSection(IntPtr obj, int section, int frame);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int GetObjectSectionNum(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int GetObjectSectionFrame(IntPtr obj, int section);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate bool GetObjectTrackValue(IntPtr obj,
        [MarshalAs(UnmanagedType.LPWStr)] string effect,
        [MarshalAs(UnmanagedType.LPWStr)] string item,
        double frame, out double value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr GetSceneName();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr GetLayerName(int layer);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void SetCursorLayerFrame(int layer, int frame);

    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("function") as string ?? "").Trim();
        int layer = ctx.GetPortValue("layer") is double l ? (int)l : 0;
        int frame = ctx.GetPortValue("frame") is double f ? (int)f : 0;
        int index = ctx.GetPortValue("index") is double i ? (int)i : 0;
        string text = ctx.GetPortValue("name") as string ?? "";
        bool apply = ctx.GetPortValue("apply") is bool b && b;
        int shift = ctx.GetPortValue("index_shift") is double sh ? (int)sh : 0;

        // 版で切り替えたいところだが、ホストの実行ファイルには版情報が入っていない（実測）。
        // 版を知るには export が要るので、ここでは並びが生きているかを直前に確かめる。
        // 0 番は関数ではなく編集情報へのポインタ。番号を合わせるために表には要るが、
        // 呼べる一覧に出すと呼ばれてしまい、ホストを落とす。
        string[] order = Order;

        // 一覧は長い。毎回返すと、聞かれた答えより桁違いに大きいものを
        //   毎回押し付けることになる。=> 求められたときと、名前が見つからなかった
        //   ときだけ返す（後者は、返さないと次に何を渡せばよいか分からないため）。
        bool wantList = ctx.GetPortValue("list") is bool showAll && showAll;
        string listing = string.Join(((char)10).ToString(), order, 1, order.Length - 1);
        ctx.SetPortValue("available", wantList ? listing : "");
        ctx.SetPortValue("value", 0d);
        ctx.SetPortValue("exact", 0d);
        ctx.SetPortValue("ok", false);
        ctx.SetPortValue("offset", 0d);
        ctx.SetPortValue("text", "");

        int slot = name == DumpName ? 0 : Array.IndexOf(order, name);
        if (slot < 0)
        {
            // 名前が違ったときだけは一覧を返す。返さないと、次に何を渡せばよいか分からない。
            ctx.SetPortValue("available", listing);
            ctx.SetPortValue("result", "unknown operation '" + name + "'. See the available output");
            return;
        }
        slot += shift;
        if (slot < 0 || slot >= order.Length)
        {
            ctx.SetPortValue("result", "the shifted slot falls outside the table");
            return;
        }
        if (slot == 0 && name != DumpName)
        {
            // ここへ来たら、データへのポインタを関数として呼ぶことになる。
            ctx.SetPortValue("result",
                "slot 0 holds the editing information, not a function. Calling it would jump into data");
            return;
        }
        ctx.SetPortValue("offset", (double)(slot * SlotSize));

        try
        {
            IntPtr edit = ReadEditHandle(out string why);
            if (edit == IntPtr.Zero)
            {
                ctx.SetPortValue("result", why);
                return;
            }

            IntPtr callPtr = Marshal.ReadIntPtr(edit, CallEditSectionParamOffset);
            if (callPtr == IntPtr.Zero)
            {
                ctx.SetPortValue("result", "the host is not offering an editing entry point right now");
                return;
            }

            if (!apply)
            {
                ctx.SetPortValue("ok", true);
                ctx.SetPortValue("result", $"'{name}' sits at offset 0x{slot * SlotSize:x}. Set apply to perform it");
                return;
            }

            var state = new CallState { Name = name, Slot = slot, Layer = layer, Frame = frame, Index = index, Name2 = text, Order = order };
            var handle = GCHandle.Alloc(state);
            SectionProc proc = OnSection;   // 呼び出しの間だけ生かしておく

            try
            {
                var call = Marshal.GetDelegateForFunctionPointer<CallEditSectionParam>(callPtr);
                bool entered = call(GCHandle.ToIntPtr(handle), Marshal.GetFunctionPointerForDelegate(proc));

                ctx.SetPortValue("ok", entered && state.Ok);
                ctx.SetPortValue("value", (double)state.Value);
                ctx.SetPortValue("exact", state.Exact);
                ctx.SetPortValue("text", state.Text);
                ctx.SetPortValue("result", entered
                    ? (state.Ok ? state.Message : "entered the editing section but " + state.Message)
                    : "the host refused to enter the editing section (it may be writing a file)");
            }
            finally
            {
                GC.KeepAlive(proc);
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("result", ex.GetType().Name + ": " + ex.Message);
        }
    }

    sealed class CallState
    {
        public string Name = "";
        public int Slot;
        public int Layer;
        public int Frame;
        public int Index;
        public string Name2 = "";
        public bool Ok;
        public long Value;
        public double Exact;
        public string Message = "";
        public string Text = "";
        public string[] Order = Array.Empty<string>();

    }

    static void OnSection(IntPtr param, IntPtr section)
    {
        var state = (CallState)GCHandle.FromIntPtr(param).Target;
        try
        {
            IntPtr Slot(string n) => Marshal.ReadIntPtr(section, Array.IndexOf(state.Order, n) * SlotSize);

            IntPtr find = Slot("find_object");
            IntPtr target = IntPtr.Zero;
            if (find != IntPtr.Zero)
            {
                target = Marshal.GetDelegateForFunctionPointer<FindObject>(find)(state.Layer, state.Frame);
            }

            // 引くのはポインタを 1 つ読むだけなので、控えずに毎回引く。
            // 控えると「古い値を使い続ける」経路が増えるだけで、得るものが無い。
            IntPtr fn = Marshal.ReadIntPtr(section, state.Slot * SlotSize);
            if (fn == IntPtr.Zero)
            {
                state.Message = "the host left that operation empty";
                return;
            }

            switch (state.Name)
            {
                case "find_object":
                    state.Value = target.ToInt64();
                    state.Ok = target != IntPtr.Zero;
                    state.Message = target != IntPtr.Zero
                        ? "found an object"
                        : "no object at that layer and frame or later";
                    break;

                case "delete_object":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to delete at that layer and frame or later";
                        break;
                    }
                    Marshal.GetDelegateForFunctionPointer<DeleteObject>(fn)(target);
                    state.Ok = true;
                    state.Message = "deleted the object";
                    break;

                case "move_object":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to move at that layer and frame or later";
                        break;
                    }
                    state.Ok = Marshal.GetDelegateForFunctionPointer<MoveObject>(fn)(target, state.Index, state.Frame);
                    state.Message = state.Ok ? "moved the object" : "the host refused the move";
                    break;

                case "get_object_alias":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to read at that layer and frame or later";
                        break;
                    }
                    // 返る文字列は次の呼び出しまでしか有効ではないので、その場で写す
                    IntPtr text = Marshal.GetDelegateForFunctionPointer<GetObjectAlias>(fn)(target);
                    if (text == IntPtr.Zero)
                    {
                        state.Message = "the host would not hand out the alias";
                        break;
                    }
                    state.Text = Marshal.PtrToStringUTF8(text) ?? "";
                    state.Ok = true;
                    state.Message = "read the alias (" + state.Text.Length + " chars)";
                    break;

                case "dump":
                {
                    var sb = new StringBuilder();
                    IntPtr host = GetModuleHandleW("aviutl2.exe");
                    for (int k = 0; k < state.Order.Length; k++)
                    {
                        IntPtr fp = Marshal.ReadIntPtr(section, k * SlotSize);
                        long rva = (host != IntPtr.Zero && fp != IntPtr.Zero)
                            ? fp.ToInt64() - host.ToInt64() : 0;
                        sb.Append(state.Order[k]).Append('	')
                          .Append("0x").Append((k * SlotSize).ToString("x")).Append('	')
                          .Append(rva > 0 ? "aviutl2.exe+0x" + rva.ToString("x") : "?")
                          .Append((char)10);
                    }
                    state.Text = sb.ToString();
                    state.Ok = true;
                    state.Message = "listed where every slot points";
                    break;
                }

                case "get_scene_name":
                {
                    IntPtr w = Marshal.GetDelegateForFunctionPointer<GetSceneName>(fn)();
                    state.Text = w == IntPtr.Zero ? "" : (Marshal.PtrToStringUni(w) ?? "");
                    state.Ok = true;
                    state.Message = "read the scene name";
                    break;
                }

                case "get_layer_name":
                {
                    IntPtr w = Marshal.GetDelegateForFunctionPointer<GetLayerName>(fn)(state.Index);
                    state.Text = w == IntPtr.Zero ? "" : (Marshal.PtrToStringUni(w) ?? "");
                    state.Ok = true;
                    state.Message = "read the layer name";
                    break;
                }

                case "get_focus_object":
                {
                    // 返るのはハンドルだけなので、layer / frame も渡しておくと
                    // find_object の結果と突き合わせられる。
                    IntPtr focus = Marshal.GetDelegateForFunctionPointer<GetFocusObject>(fn)();
                    state.Value = focus.ToInt64();
                    state.Ok = focus != IntPtr.Zero;
                    state.Text = "focus=0x" + focus.ToInt64().ToString("x")
                               + " find_object=0x" + target.ToInt64().ToString("x")
                               + (focus != IntPtr.Zero && focus == target ? " same" : " different");
                    // ハンドルが今も生きているかは、そこからエイリアスを引けるかで分かる。
                    IntPtr aliasFn = Slot("get_object_alias");
                    if (focus != IntPtr.Zero && aliasFn != IntPtr.Zero)
                    {
                        IntPtr a = Marshal.GetDelegateForFunctionPointer<GetObjectAlias>(aliasFn)(focus);
                        string alias = a == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(a) ?? "");
                        state.Text += (char)10 + "alias(" + alias.Length + "): "
                                    + (alias.Length > 400 ? alias.Substring(0, 400) : alias);
                    }
                    state.Message = focus != IntPtr.Zero
                        ? "read the object selected in the settings window"
                        : "nothing is selected in the settings window";
                    break;
                }

                case "create_effect":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing at that layer and frame or later";
                        break;
                    }
                    if (state.Name2.Length == 0)
                    {
                        state.Message = "give the effect name in the name port";
                        break;
                    }
                    IntPtr made = Marshal.GetDelegateForFunctionPointer<CreateEffect>(fn)(target, state.Name2);
                    state.Value = made.ToInt64();
                    state.Ok = made != IntPtr.Zero;
                    state.Message = made != IntPtr.Zero
                        ? "added the effect"
                        : "the host refused to add that effect";
                    break;

                case "delete_effect":
                {
                    IntPtr findEffect = Slot("find_effect");
                    if (target == IntPtr.Zero || findEffect == IntPtr.Zero)
                    {
                        state.Message = "nothing at that layer and frame or later";
                        break;
                    }
                    if (state.Name2.Length == 0)
                    {
                        state.Message = "give the effect name in the name port";
                        break;
                    }
                    IntPtr found = Marshal.GetDelegateForFunctionPointer<FindEffect>(findEffect)(target, state.Name2);
                    if (found == IntPtr.Zero)
                    {
                        state.Message = "that object does not carry that effect";
                        break;
                    }
                    state.Ok = Marshal.GetDelegateForFunctionPointer<DeleteEffect>(fn)(target, found);
                    state.Message = state.Ok ? "removed the effect" : "the host refused to remove it";
                    break;
                }

                // オブジェクトが持っている効果を、順番のまま名前で並べる。
                // 人の目にはウィンドウで見えているが、ノードからは見る手が無かった。
                case "get_effect_list":
                {
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to read at that layer and frame or later";
                        break;
                    }

                    var list = Marshal.GetDelegateForFunctionPointer<GetEffectList>(fn);
                    int count = list(target, IntPtr.Zero, 0);
                    if (count <= 0)
                    {
                        state.Ok = true;
                        state.Message = "the object holds no effect";
                        break;
                    }

                    IntPtr buffer = Marshal.AllocHGlobal(count * IntPtr.Size);
                    try
                    {
                        int got = list(target, buffer, count);
                        var nameOf = Marshal.GetDelegateForFunctionPointer<GetEffectName>(Slot("get_effect_name"));
                        var enabledOf = Marshal.GetDelegateForFunctionPointer<GetEffectEnable>(Slot("get_effect_enable"));

                        var sb2 = new StringBuilder();
                        for (int k = 0; k < got; k++)
                        {
                            IntPtr eff = Marshal.ReadIntPtr(buffer, k * IntPtr.Size);
                            IntPtr w = nameOf(eff);
                            sb2.Append(k).Append((char)9)
                               .Append(w == IntPtr.Zero ? "?" : (Marshal.PtrToStringUni(w) ?? "?")).Append((char)9)
                               .Append(enabledOf(eff) ? "enabled" : "disabled")
                               .Append((char)10);
                        }
                        state.Text = sb2.ToString();
                        state.Value = got;
                        state.Ok = true;
                        state.Message = "listed " + got + " effect(s)";
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    break;
                }

                case "get_object_track_value":
                {
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to read at that layer and frame or later";
                        break;
                    }
                    // 効果と項目の 2 つが要るので、name に縦棒で並べて渡してもらう。
                    // 3 つ目に終わりのフレームを足すと、そこまでを等間隔に並べて返す。
                    // 1 点ずつ聞くと、動きを見るだけで何度も往復することになる。
                    var parts = state.Name2.Split((char)124);
                    if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
                    {
                        state.Message = "give name as effect|item, and optionally |lastFrame to get a run of values";
                        break;
                    }
                    var read = Marshal.GetDelegateForFunctionPointer<GetObjectTrackValue>(fn);

                    if (parts.Length < 3 || !int.TryParse(parts[2], out int last) || last <= state.Index)
                    {
                        state.Ok = read(target, parts[0], parts[1], state.Index, out double one);
                        state.Exact = one;
                        state.Message = state.Ok
                            ? parts[1] + " is " + one.ToString("0.###") + " at frame " + state.Index
                            : "no such effect or item on that object";
                        break;
                    }

                    const int Points = 11;
                    var line = new StringBuilder();
                    double lowest = double.MaxValue, highest = double.MinValue;
                    bool all = true;
                    for (int k = 0; k < Points; k++)
                    {
                        int at = state.Index + (last - state.Index) * k / (Points - 1);
                        if (!read(target, parts[0], parts[1], at, out double v)) { all = false; break; }
                        if (line.Length > 0) line.Append(' ');
                        line.Append(at).Append(':').Append(v.ToString("0.##"));
                        if (v < lowest) lowest = v;
                        if (v > highest) highest = v;
                    }
                    state.Ok = all;
                    if (!all)
                    {
                        state.Message = "no such effect or item on that object";
                        break;
                    }
                    state.Text = line.ToString();
                    state.Exact = highest - lowest;
                    state.Message = parts[1] + " runs between " + lowest.ToString("0.##")
                                  + " and " + highest.ToString("0.##") + " over those frames";
                    break;
                }

                case "create_object_section":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to add a point to at that layer and frame or later";
                        break;
                    }
                    state.Ok = Marshal.GetDelegateForFunctionPointer<CreateObjectSection>(fn)(target, state.Index);
                    state.Message = state.Ok
                        ? "added a point at frame " + state.Index
                        : "the host refused; the frame must sit inside the object and not on a point already there";
                    break;

                case "delete_object_section":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to remove a point from at that layer and frame or later";
                        break;
                    }
                    state.Ok = Marshal.GetDelegateForFunctionPointer<DeleteObjectSection>(fn)(target, state.Index);
                    state.Message = state.Ok ? "removed point " + state.Index : "the host refused the removal";
                    break;

                case "move_object_section":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to move a point on at that layer and frame or later";
                        break;
                    }
                    state.Ok = Marshal.GetDelegateForFunctionPointer<MoveObjectSection>(fn)(target, state.Index, state.Frame);
                    state.Message = state.Ok ? "moved point " + state.Index : "the host refused; points cannot cross each other";
                    break;

                case "get_object_section_num":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to count at that layer and frame or later";
                        break;
                    }
                    state.Value = Marshal.GetDelegateForFunctionPointer<GetObjectSectionNum>(fn)(target);
                    state.Ok = true;
                    state.Message = state.Value + " stretch(es), so " + (state.Value - 1) + " point(s) inside";
                    break;

                case "get_object_section_frame":
                    if (target == IntPtr.Zero)
                    {
                        state.Message = "nothing to read at that layer and frame or later";
                        break;
                    }
                    state.Value = Marshal.GetDelegateForFunctionPointer<GetObjectSectionFrame>(fn)(target, state.Index);
                    state.Ok = state.Value >= 0;
                    state.Message = state.Ok ? "stretch " + state.Index + " starts at frame " + state.Value
                                             : "there is no stretch numbered " + state.Index;
                    break;

                case "get_selected_object_num":
                    state.Value = Marshal.GetDelegateForFunctionPointer<GetSelectedObjectNum>(fn)();
                    state.Ok = true;
                    state.Message = "read how many objects are selected";
                    break;

                case "set_cursor_layer_frame":
                    Marshal.GetDelegateForFunctionPointer<SetCursorLayerFrame>(fn)(state.Layer, state.Frame);
                    state.Ok = true;
                    state.Message = "moved the cursor";
                    break;

                default:
                    state.Message = "'" + state.Name + "' is in the list but this node does not know its arguments yet";
                    break;
            }
        }
        catch (Exception ex)
        {
            state.Message = ex.GetType().Name + ": " + ex.Message;
        }
    }

    static IntPtr ReadEditHandle(out string why)
    {
        why = "";
        IntPtr edit;
        try
        {
            edit = Ngol_GetEditHandle();
        }
        catch (DllNotFoundException)
        {
            why = "the bridge module is not loaded in this process";
            return IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            why = "this build of the bridge does not hand out the editing handle";
            return IntPtr.Zero;
        }

        if (edit == IntPtr.Zero)
        {
            why = "no editing handle yet. It appears once a project is open";
            return IntPtr.Zero;
        }
        return edit;
    }
}
