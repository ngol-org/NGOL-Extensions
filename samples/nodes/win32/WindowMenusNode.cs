using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

// 汎用デバッグノード(プロジェクト非依存): ウィンドウのメニューを読み出す。
// 対象アプリには一切手を入れず、NGOL(C#)側のWin32 P/Invokeだけで完結する。
//
// プラグインがメニュー項目を登録したとき、「登録できたか」はログで分かるが
// 「どこに出たか」は分からない。開いて見るしかなく、画面を見られない側からは確かめられない。
// 出力はツリーではなくパス("設定 > プラグイン設定 > NGOL")にしてある。
// 知りたいのは階層全体ではなく、その項目がどの経路に付いたかなので。
[NodeType(
    "ngol.win32.window_menus",
    "Win32",
    "Window Menus",
    Version = "1.1.1",
    Description =
        "Read the menus of a running application's windows, without touching the application. "
      + "Answers where a menu item ended up: each hit is reported as a path like 'File > Export > Something'. "
      + "Use it to confirm a plugin's menu registration landed where the documentation implies, which the "
      + "application's own log cannot tell you. "
      + "A window whose menu bar is drawn by the application itself has no menu to read, and an owner-drawn "
      + "item keeps its text in the application rather than in the menu: both are reported instead of being "
      + "silently dropped, so 'no match' is never confused with 'nothing readable'.")]
[NodePort("processId", PortDirection.Input, "number", Description = "Only windows of this process (0 = every process)")]
[NodePort("windowTitleContains", PortDirection.Input, "string", Description = "Only windows whose title contains this, case-insensitive. Empty = every visible window")]
[NodePort("itemFilter", PortDirection.Input, "string", Description = "Only report items whose text contains this, case-insensitive. Empty = report every item, which is large for a full application menu")]
[NodePort("includeSystemMenu", PortDirection.Input, "boolean", Description = "Also read the system menu (the one on the title bar). Default true")]
[NodePort("maxDepth", PortDirection.Input, "number", Description = "How deep to follow submenus (default 5)")]
[NodePort("paths", PortDirection.Output, "string", Description = "One matching item per line, as 'Top > Sub > Item'. The window title and menu kind lead each line")]
[NodePort("matchCount", PortDirection.Output, "number", Description = "How many items were reported")]
[NodePort("windowsScanned", PortDirection.Output, "number", Description = "How many windows were looked at")]
[NodePort("windowsWithMenu", PortDirection.Output, "number", Description = "How many of those had a menu bar. Zero means the application draws its menus itself and nothing here can be read")]
[NodePort("unreadableItems", PortDirection.Output, "number", Description = "Items that exist but whose text could not be read (owner-drawn items keep their text in the application). Non-zero means the listing is incomplete")]
[NodePort("truncated", PortDirection.Output, "boolean", Description = "true when the output hit the size limit and lines were dropped. Narrow itemFilter")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was reported. Empty when something was")]
public sealed class WindowMenusNode : INode
{
    const int OutputLimit = 60000;

    const uint MIIM_STATE = 0x00000001;
    const uint MIIM_FTYPE = 0x00000100;
    const uint MIIM_STRING = 0x00000040;
    const uint MFT_SEPARATOR = 0x00000800;
    const uint MFT_OWNERDRAW = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")] static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetMenuItemInfoW(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFOW info);

    sealed class Collector
    {
        public string Filter = "";
        public int MaxDepth = 5;
        public readonly List<string> Lines = new List<string>();
        public int Length;
        public bool Truncated;
        public int Unreadable;

        public void Add(string line)
        {
            if (Length + line.Length > OutputLimit) { Truncated = true; return; }
            Lines.Add(line);
            Length += line.Length + 1;
        }

        public bool Wanted(string text) =>
            Filter.Length == 0 ||
            text.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // 加速キーはタブ区切りで付く("閉じる(&C)\tAlt+F4")。パスに混ぜると読みにくい。
    // & は下線の指定なので落とす。
    static string Clean(string raw)
    {
        int tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw.Substring(0, tab);
        return raw.Replace("&", "").Trim();
    }

    /// <summary>
    /// 項目の文字を読む。読めなかった場合は空を返し、区切りかどうかを別に伝える。
    ///
    /// 公式は GetMenuString ではなく GetMenuItemInfo を使うよう案内している。
    /// 長さを聞いてから確保する 2 回呼びで、文字列型でない項目は長さ 0 で返る。
    /// </summary>
    static string TextOfItem(IntPtr menu, int position, out bool isSeparator, out bool unreadable)
    {
        isSeparator = false;
        unreadable = false;

        var info = new MENUITEMINFOW
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
            fMask = MIIM_STRING | MIIM_FTYPE | MIIM_STATE,
            dwTypeData = IntPtr.Zero,
            cch = 0,
        };

        if (!GetMenuItemInfoW(menu, (uint)position, true, ref info)) { unreadable = true; return ""; }

        if ((info.fType & MFT_SEPARATOR) != 0) { isSeparator = true; return ""; }

        if (info.cch == 0)
        {
            // 文字列を持たない項目。オーナードローは自分の中に文字を持つので、
            // ここからは読めない。空として捨てず、読めなかったことを数える。
            unreadable = (info.fType & MFT_OWNERDRAW) != 0;
            return "";
        }

        uint size = info.cch + 1;
        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            info.cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>();
            info.fMask = MIIM_STRING;
            info.dwTypeData = buffer;
            info.cch = size;
            if (!GetMenuItemInfoW(menu, (uint)position, true, ref info)) { unreadable = true; return ""; }
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static void Walk(IntPtr menu, string prefix, Collector collector, int depth)
    {
        if (menu == IntPtr.Zero || depth > collector.MaxDepth) return;

        int count = GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            string text = TextOfItem(menu, i, out bool isSeparator, out bool unreadable);
            if (unreadable) collector.Unreadable++;
            if (isSeparator) continue;

            string label = text.Length > 0 ? Clean(text) : "(no readable text)";
            string path = prefix + " > " + label;

            if (text.Length > 0 && collector.Wanted(label)) collector.Add(path);

            Walk(GetSubMenu(menu, i), path, collector, depth + 1);
        }
    }

    public void Execute(IExecutionContext ctx)
    {
        var query = new NgolWindowFind.Query
        {
            ProcessId = ctx.GetPortValue("processId") is double pid && pid > 0 ? (uint)pid : 0,
            TitleContains = ctx.GetPortValue("windowTitleContains") as string ?? "",
            ClassContains = "",
            VisibleOnly = true,
            TopLevelOnly = true,
        };

        bool includeSystem = ctx.GetPortValue("includeSystemMenu") is not bool s || s;

        var collector = new Collector
        {
            Filter = (ctx.GetPortValue("itemFilter") as string ?? "").Trim(),
            MaxDepth = ctx.GetPortValue("maxDepth") is double d && d >= 1 ? (int)d : 5,
        };

        var outcome = NgolWindowFind.Find(query);
        int withMenu = 0;

        foreach (var w in outcome.Windows)
        {
            IntPtr bar = GetMenu(w.Handle);
            if (bar != IntPtr.Zero)
            {
                withMenu++;
                Walk(bar, "[" + w.Title + "] menu", collector, 0);
            }
            if (includeSystem) Walk(GetSystemMenu(w.Handle, false), "[" + w.Title + "] system", collector, 0);
        }

        string reason = "";
        if (collector.Lines.Count == 0)
        {
            reason = outcome.Windows.Count == 0
                ? outcome.Explain(query)
                : withMenu == 0
                    ? "the matched windows have no menu bar to read (the application draws its own)"
                    : "the menus were read but no item matched the filter";
        }

        ctx.SetPortValue("paths", string.Join("\n", collector.Lines));
        ctx.SetPortValue("matchCount", (double)collector.Lines.Count);
        ctx.SetPortValue("windowsScanned", (double)outcome.Windows.Count);
        ctx.SetPortValue("windowsWithMenu", (double)withMenu);
        ctx.SetPortValue("unreadableItems", (double)collector.Unreadable);
        ctx.SetPortValue("truncated", collector.Truncated);
        ctx.SetPortValue("reason", reason);
    }
}
