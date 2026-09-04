using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ウィンドウのメニュー項目を名前で探し、その機能を起こす。
///
/// メニュー項目には識別子が振られていて、それをウィンドウへ送ればクリックと同じことが起きる。
/// 画面を操作せずにホストの機能を呼べるので、検証の往復から手作業が消える。
///
/// 識別子は版が変われば動く。覚えずに、名前で引いてそのとき返った値を送ること。
/// </summary>
[NodeType("ngol.win32.menu_command", "Win32", "Menu Command",
    Version = "1.0.1",
    Description = "Finds a menu item by its text and optionally invokes it, which does the same thing as clicking it. Item ids move between builds, so the id is looked up every time instead of being remembered. Nothing is sent unless send is set, and a name that matches more than one item stops instead of guessing: read matchedPath and matchCount first. Only the menu bar is searched: a window that has none reports that and sends nothing, even for items ngol.win32.window_menus listed from the title bar's system menu.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Only windows of this process (0 = every process). Titles collide across applications, so prefer setting this")]
[NodePort("windowTitleContains", PortDirection.Input, "string", Description = "Only windows whose title contains this, case-insensitive. Empty = any window of the process that has a menu bar")]
[NodePort("itemText", PortDirection.Input, "string", Description = "Text of the item to find. An exact match wins; otherwise a unique item containing this text is used")]
[NodePort("send", PortDirection.Input, "boolean", Description = "false (default) only reports what was found. true invokes it")]
[NodePort("commandId", PortDirection.Output, "number", Description = "The id that was found. 0 when nothing matched")]
[NodePort("matchedPath", PortDirection.Output, "string", Description = "Where the item sits, as 'Top > Sub > Item'")]
[NodePort("matchCount", PortDirection.Output, "number", Description = "How many items matched. More than one means nothing was sent")]
[NodePort("sent", PortDirection.Output, "boolean", Description = "true when the command was actually sent")]
[NodePort("candidates", PortDirection.Output, "string", Description = "The matching items, one per line, when the name was not unique")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was found or sent. Empty on success")]
public sealed class MenuCommandNode : INode
{
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr menu, int pos);
    [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr menu, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);

    const uint MF_BYPOSITION = 0x400;
    const uint WM_COMMAND = 0x111;

    sealed class Hit
    {
        public uint Id;
        public string Path = "";
        public bool Exact;
    }

    public void Execute(IExecutionContext ctx)
    {
        int processId = ctx.GetPortValue("processId") is double d ? (int)d : 0;
        string titlePart = (ctx.GetPortValue("windowTitleContains") as string ?? "").Trim();
        string itemText = (ctx.GetPortValue("itemText") as string ?? "").Trim();
        bool send = ctx.GetPortValue("send") is bool b && b;

        ctx.SetPortValue("commandId", 0d);
        ctx.SetPortValue("matchedPath", "");
        ctx.SetPortValue("matchCount", 0d);
        ctx.SetPortValue("sent", false);
        ctx.SetPortValue("candidates", "");

        if (itemText.Length == 0)
        {
            ctx.SetPortValue("reason", "itemText is empty");
            return;
        }

        IntPtr target = FindWindowWithMenu(processId, titlePart);
        if (target == IntPtr.Zero)
        {
            ctx.SetPortValue("reason", "no window with a menu bar matched. The application may draw its menus itself");
            return;
        }

        var hits = new List<Hit>();
        Walk(GetMenu(target), "", itemText, hits, 0);

        // 完全一致があるなら部分一致は捨てる。
        // 「保存」と「別名で保存」のように、片方がもう片方を含む形は普通にある。
        var exact = hits.FindAll(h => h.Exact);
        if (exact.Count > 0) hits = exact;

        ctx.SetPortValue("matchCount", (double)hits.Count);

        if (hits.Count == 0)
        {
            ctx.SetPortValue("reason", "no menu item matched '" + itemText + "'");
            return;
        }

        if (hits.Count > 1)
        {
            var lines = new StringBuilder();
            foreach (var h in hits)
            {
                lines.Append(h.Path).Append(" (id=").Append(h.Id).Append(')').Append((char)10);
            }
            ctx.SetPortValue("candidates", lines.ToString());
            ctx.SetPortValue("reason", "the name is not unique, so nothing was sent");
            return;
        }

        var only = hits[0];
        ctx.SetPortValue("commandId", (double)only.Id);
        ctx.SetPortValue("matchedPath", only.Path);
        ctx.SetPortValue("reason", "");

        if (send)
        {
            SendMessageW(target, WM_COMMAND, (IntPtr)only.Id, IntPtr.Zero);
            ctx.SetPortValue("sent", true);
        }
    }

    static IntPtr FindWindowWithMenu(int processId, string titlePart)
    {
        IntPtr found = IntPtr.Zero;
        var buffer = new StringBuilder(512);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetMenu(hwnd) == IntPtr.Zero) return true;

            if (processId != 0)
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner != processId) return true;
            }

            if (titlePart.Length > 0)
            {
                buffer.Clear();
                GetWindowTextW(hwnd, buffer, buffer.Capacity);
                if (buffer.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            }

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    static void Walk(IntPtr menu, string prefix, string needle, List<Hit> hits, int depth)
    {
        if (menu == IntPtr.Zero || depth > 6) return;

        int count = GetMenuItemCount(menu);
        var buffer = new StringBuilder(512);

        for (int i = 0; i < count; i++)
        {
            buffer.Clear();
            GetMenuStringW(menu, (uint)i, buffer, buffer.Capacity, MF_BYPOSITION);

            // 加速キーの表示と下線用の記号は名前の一部ではない
            string text = buffer.ToString();
            int tab = text.IndexOf((char)9);
            if (tab >= 0) text = text.Substring(0, tab);
            text = text.Replace("&", "");

            string path = prefix.Length == 0 ? text : prefix + " > " + text;

            IntPtr sub = GetSubMenu(menu, i);
            if (sub != IntPtr.Zero)
            {
                Walk(sub, path, needle, hits, depth + 1);
                continue;
            }

            if (text.Length == 0) continue;

            bool exact = string.Equals(text, needle, StringComparison.OrdinalIgnoreCase);
            bool partial = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!exact && !partial) continue;

            uint id = GetMenuItemID(menu, i);
            if (id == 0 || id == 0xFFFFFFFF) continue;

            hits.Add(new Hit { Id = id, Path = path, Exact = exact });
        }
    }
}
