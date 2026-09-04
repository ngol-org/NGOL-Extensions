using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 編集操作の並びが、いま動いているホストと合っているかを、呼ばずに確かめる。
///
/// 関数ポインタの並びには名前が無いので番号で引くしかない。番号はヘッダーの宣言順で決まるが、
/// ホストが更新されて途中に関数が挿入されると、それ以降が全部ずれる。
/// ずれても落ちず、もっともらしい値が返るため、呼んでからでは気づけない。
///
/// そこで各スロットの指す先を逆アセンブルし、引数の個数を数えて宣言と照合する。
/// 数え方は x64 の呼び出し規約による（第1〜4は rcx/rdx/r8/r9、第5以降は rsp+0x28 から 8 刻み）。
///
/// 使われない引数は見えないので、観測が宣言より少ないのは異常ではない。
/// 観測が宣言を超えたときだけ、並びが動いた疑いとして扱う。
/// </summary>
[NodeType("aviutl.edit.section_verify", "AviUtl2", "Verify Edit Section Order",
    Version = "1.0.0",
    Description = "Checks that the editing operations sit where the header says, without calling any of them. Each slot's target is disassembled and its argument count compared with the declaration. A shifted table shows up as functions taking more arguments than declared. Unused arguments are invisible, so observing fewer than declared is not a fault: only observing more is.")]
[NodePort("max_bytes", PortDirection.Input, "number", Description = "How far into each function to look (default 96). Only the entry sequence is needed")]
[NodePort("mismatches", PortDirection.Output, "number", Description = "How many slots take more arguments than declared. Non-zero means the order should not be trusted")]
[NodePort("checked_count", PortDirection.Output, "number", Description = "How many slots could be read and decoded")]
[NodePort("report", PortDirection.Output, "string", Description = "One line per slot: name, declared, observed, verdict")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class AviUtl2SectionVerifyNode : INode
{
    // plugin2.h の宣言から生成したもの。手で写すと数え間違えるため。
    static readonly Dictionary<string, int> Declared = new()
    {
        ["create_object_from_alias"] = 4,
        ["find_object"] = 2,
        ["count_object_effect"] = 2,
        ["get_object_layer_frame"] = 1,
        ["get_object_alias"] = 1,
        ["get_object_item_value"] = 3,
        ["set_object_item_value"] = 4,
        ["move_object"] = 3,
        ["delete_object"] = 1,
        ["get_focus_object"] = 0,
        ["set_focus_object"] = 1,
        ["get_project_file"] = 1,
        ["get_selected_object"] = 1,
        ["get_selected_object_num"] = 0,
        ["get_mouse_layer_frame"] = 2,
        ["pos_to_layer_frame"] = 4,
        ["is_support_media_file"] = 2,
        ["get_media_info"] = 3,
        ["create_object_from_media_file"] = 4,
        ["create_object"] = 4,
        ["set_cursor_layer_frame"] = 2,
        ["set_display_layer_frame"] = 2,
        ["set_select_range"] = 2,
        ["set_grid_bpm"] = 3,
        ["get_object_name"] = 1,
        ["set_object_name"] = 2,
        ["get_layer_name"] = 1,
        ["set_layer_name"] = 2,
        ["get_scene_name"] = 0,
        ["set_scene_name"] = 1,
        ["set_scene_size"] = 2,
        ["set_scene_frame_rate"] = 2,
        ["set_scene_sample_rate"] = 1,
        ["get_layer_enable"] = 1,
        ["set_layer_enable"] = 2,
        ["get_layer_lock"] = 1,
        ["set_layer_lock"] = 2,
        ["get_object_section_num"] = 1,
        ["get_focus_object_section"] = 0,
        ["get_object_section_frame"] = 2,
        ["get_object_track_value"] = 5,
        ["get_object_check_value"] = 5,
        ["get_object_track_info"] = 5,
        ["get_palette_name"] = 0,
        ["get_palette_info"] = 3,
        ["get_font"] = 1,
        ["get_object_track_group_names"] = 5,
        ["deprecated_get_grid_bpm_list"] = 2,
        ["deprecated_set_grid_bpm_list"] = 2,
        ["find_effect"] = 2,
        ["get_effect_list"] = 3,
        ["get_effect_name"] = 1,
        ["get_effect_enable"] = 1,
        ["set_effect_enable"] = 2,
        ["get_effect_lock"] = 1,
        ["set_effect_lock"] = 2,
        ["get_effect_item_value"] = 2,
        ["set_effect_item_value"] = 3,
        ["get_effect_track_value"] = 4,
        ["get_effect_check_value"] = 4,
        ["get_effect_track_info"] = 4,
        ["get_grid_bpm_list"] = 3,
        ["set_grid_bpm_list"] = 3,
        ["create_effect"] = 2,
        ["delete_effect"] = 2,
        ["create_object_section"] = 2,
        ["delete_object_section"] = 2,
        ["move_object_section"] = 3,
        ["move_effect"] = 3,
        ["get_effect_data_value"] = 4,
        ["set_effect_data_value"] = 4,
        ["set_edited_state"] = 0,
        ["get_mark_frame_list"] = 2,
        ["get_mark_frame_memo"] = 1,
        ["set_mark_frame"] = 2,
        ["clear_mark_frame"] = 1,
        ["move_mark_frame"] = 2,
        ["set_palette_info"] = 3,
    };

    // 編集ハンドルは名前で受け取る。番地で覚えるとビルドのたびに動く。
    //
    // disasm-verified: Ngol_GetEditHandle RVA 0x8a70 は mov rax,[rel ...] と ret の 2 命令。
    // 引数 0 個 / 戻り値は rax の 64bit
    [DllImport("NgolForAviUtl2.aux2")]
    private static extern IntPtr Ngol_GetEditHandle();
    const int CallEditSectionParamOffset = 0x08;
    const int SlotSize = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void SectionProc(IntPtr param, IntPtr section);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool CallEditSectionParam(IntPtr param, IntPtr proc);

    sealed class Work
    {
        public int MaxBytes;
        public int Mismatch;
        public int Checked;
        public StringBuilder Report = new();
    }

    public void Execute(IExecutionContext ctx)
    {
        int maxBytes = ctx.GetPortValue("max_bytes") is double d ? (int)d : 96;
        if (maxBytes < 32) maxBytes = 32;

        ctx.SetPortValue("mismatches", 0d);
        ctx.SetPortValue("checked_count", 0d);
        ctx.SetPortValue("report", "");

        try
        {
            IntPtr edit = Ngol_GetEditHandle();
            if (edit == IntPtr.Zero)
            {
                ctx.SetPortValue("result", "no editing handle yet. It appears once a project is open");
                return;
            }

            IntPtr callPtr = Marshal.ReadIntPtr(edit, CallEditSectionParamOffset);
            if (callPtr == IntPtr.Zero)
            {
                ctx.SetPortValue("result", "the host is not offering an editing entry point right now");
                return;
            }

            var work = new Work { MaxBytes = maxBytes };
            var handle = GCHandle.Alloc(work);
            SectionProc proc = OnSection;

            try
            {
                var call = Marshal.GetDelegateForFunctionPointer<CallEditSectionParam>(callPtr);
                bool entered = call(GCHandle.ToIntPtr(handle), Marshal.GetFunctionPointerForDelegate(proc));

                ctx.SetPortValue("mismatches", (double)work.Mismatch);
                ctx.SetPortValue("checked_count", (double)work.Checked);
                ctx.SetPortValue("report", work.Report.ToString());
                ctx.SetPortValue("result", entered
                    ? (work.Mismatch == 0
                        ? "all " + work.Checked + " slots take no more arguments than declared"
                        : work.Mismatch + " of " + work.Checked + " slots take more arguments than declared. Do not trust the order")
                    : "the host refused to enter the editing section");
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

    static void OnSection(IntPtr param, IntPtr section)
    {
        var work = (Work)GCHandle.FromIntPtr(param).Target;
        int slot = 1;   // 0 番は関数ではない

        foreach (var pair in Declared)
        {
            IntPtr fn = Marshal.ReadIntPtr(section, slot * SlotSize);
            slot++;
            if (fn == IntPtr.Zero) continue;

            int observed = CountArguments(fn, work.MaxBytes);
            if (observed < 0) continue;

            work.Checked++;
            bool over = observed > pair.Value;
            if (over) work.Mismatch++;

            work.Report.Append(pair.Key).Append(TABCHAR)
                .Append("declared ").Append(pair.Value).Append(TABCHAR)
                .Append("observed ").Append(observed).Append(TABCHAR)
                .Append(over ? "OVER" : "ok").Append((char)10);
        }
    }

    const char TABCHAR = (char)9;

    /// <summary>
    /// 入口の命令列から引数の個数を数える。
    /// 第1〜4は rcx/rdx/r8/r9、第5以降は関数入口の rsp を基準に +0x28 から 8 バイト刻み。
    /// </summary>
    static int CountArguments(IntPtr fn, int maxBytes)
    {
        var bytes = new byte[maxBytes];
        try { Marshal.Copy(fn, bytes, 0, maxBytes); }
        catch { return -1; }

        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Iced.Intel.Decoder.Create(64, reader);
        decoder.IP = (ulong)fn.ToInt64();

        int highest = 0;
        int shift = 8;      // 呼び出しで積まれた戻り番地

        while (reader.CanReadByte)
        {
            var instr = decoder.Decode();
            if (instr.Code == Code.INVALID) break;
            if (instr.FlowControl == FlowControl.Return) break;

            string code = instr.Code.ToString(); if (code.StartsWith("Push")) shift += 8;
            if (code.StartsWith("Sub_") && instr.Op0Register == Register.RSP)
                shift += (int)instr.Immediate32;

            for (int k = 0; k < instr.OpCount; k++)
            {
                if (instr.GetOpKind(k) == OpKind.Register)
                {
                    int idx = ArgIndexOf(instr.GetOpRegister(k));
                    if (idx > highest) highest = idx;
                }
                else if (instr.GetOpKind(k) == OpKind.Memory && instr.MemoryBase == Register.RSP)
                {
                    int off = (int)instr.MemoryDisplacement64 - shift;
                    if (off >= 0x28)
                    {
                        int idx = 5 + (off - 0x28) / 8;
                        if (idx > highest) highest = idx;
                    }
                }
            }

            // 呼び出しに入ると、その先の引数準備が混ざるので打ち切る
            if (instr.FlowControl == FlowControl.Call || instr.FlowControl == FlowControl.IndirectCall) break;
        }

        return highest;
    }

    static int ArgIndexOf(Register r)
    {
        switch (r)
        {
            case Register.RCX: case Register.ECX: case Register.CX: case Register.CL: return 1;
            case Register.RDX: case Register.EDX: case Register.DX: case Register.DL: return 2;
            case Register.R8: case Register.R8D: case Register.R8W: case Register.R8L: return 3;
            case Register.R9: case Register.R9D: case Register.R9W: case Register.R9L: return 4;
            default: return 0;
        }
    }
}
