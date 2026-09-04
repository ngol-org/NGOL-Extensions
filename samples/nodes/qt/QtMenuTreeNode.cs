using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// Qt で作られたアプリのメニューを、名前で引けるように並べる。
///
/// Win32 のメニュー列挙はここでは何も返さない。Qt はメニューを自分で描いており、
/// OS のメニューを 1 つも持たないため。代わりに Qt 自身に聞く。
///
/// 同じプロセスの中に居るからできること。外からでは、画面を撮って字を読むしかない。
///
/// 何をしているか:
///   QApplication::topLevelWidgets()  最上位の窓を並べる
///     -> QObject::children()         その下を辿って QMenuBar を探す
///       -> QWidget::actions()        メニューバーの項目
///         -> QAction::text()         項目の名前
///         -> QMenu::menuInAction()   その項目がぶら下げている下位メニュー
///
/// 番地は 1 つも持っていない。関数は名前で解決している。
/// </summary>
[NodeType("qt.menu.tree", "Qt", "Menu Tree",
    Version = "1.0.0",
    Description = "Lists the menus of a Qt application by name. Enumerating menus the Win32 way returns nothing here, because Qt paints its menus itself and owns no OS menu at all; this asks Qt instead. It works only from inside the same process - from outside there is nothing to do but photograph the screen and read the letters. Useful for confirming that a plugin's menu entry landed where its documentation implies.")]
[NodePort("contains", PortDirection.Input, "string", Description = "Only report items whose path contains this, case-insensitive. Empty = every item, which is large for a full application menu")]
[NodePort("max_depth", PortDirection.Input, "number", Description = "How deep to follow submenus (default 5)")]
[NodePort("paths", PortDirection.Output, "string", Description = "One item per line, as 'Top > Sub > Item'")]
[NodePort("count", PortDirection.Output, "number", Description = "How many items were reported")]
[NodePort("menu_bars", PortDirection.Output, "number", Description = "How many menu bars were found. Zero means this application has none, which is different from having none that match")]
[NodePort("top_level_windows", PortDirection.Output, "number", Description = "How many top-level windows Qt reported")]
[NodePort("reason", PortDirection.Output, "string", Description = "Why nothing was reported. Empty when something was")]
public sealed class QtMenuTreeNode : INode
{
    // disasm-verified 2026-08-20 (ngol.code.disasm、稼働中の Qt 6.11.1 に対して実測):
    //   topLevelWidgets  Qt6Widgets RVA 0x18410
    //     mov rsi,rcx / mov [rcx],rbp / [rcx+8] / [rcx+10h] -> rcx は 24 バイトの戻り置き場。
    //     静的関数なので this は無い。=> 引数 1 個。戻りは rax=rcx。
    //   QWidget::actions Qt6Widgets RVA 0x51AF0
    //     mov r8,[rcx+8] (this->d) / mov [rdx],rcx / [rdx+8] / [rdx+10h] / mov rax,rdx
    //     => rcx=this、rdx=24 バイトの戻り置き場。引数 2 個。
    //     末尾の lock inc dword [rcx] が、QList が {d,ptr,size} の 24 バイトで
    //        参照カウントが d の先頭 32bit であることを直接示している。
    //   QMenu::menuInAction Qt6Widgets RVA 0x3270
    //     rcx を触らずに call [import] -> mov rdx,rax / lea rcx,[静的] / jmp [import]
    //     (QMetaObject::cast(&QMenu::staticMetaObject, ...)) => 引数 1 個 (rcx=QAction*)。
    //   QObject::children Qt6Core RVA 0x3950
    //     mov rax,[rcx+8] / add rax,18h / ret => 引数 1 個。QList を「参照で」返す
    //     (複製しないので参照カウントが動かない)。
    //   QObject::inherits Qt6Core RVA 0x4C50
    //     vtable を引いて CFG 経由で呼び、test rax,rax / setne al => 引数 2 個・戻りは 8bit。
    //   QAction::text Qt6Gui RVA 0x4270B0
    //     mov rdi,[rcx+8] / mov rbx,rdx / ... / mov rax,rbx => rcx=this、rdx=24 バイトの
    //     戻り置き場。引数 2 個。
    //   RVA は版で動く。ここでは 1 つも使わず、名前で解決している。
    private const string Core = "Qt6Core.dll";
    private const string Gui = "Qt6Gui.dll";
    private const string Widgets = "Qt6Widgets.dll";

    [DllImport(Widgets, EntryPoint = "?topLevelWidgets@QApplication@@SA?AV?$QList@PEAVQWidget@@@@XZ")]
    private static extern IntPtr TopLevelWidgets(IntPtr outList);

    [DllImport(Widgets, EntryPoint = "?actions@QWidget@@QEBA?AV?$QList@PEAVQAction@@@@XZ")]
    private static extern IntPtr WidgetActions(IntPtr self, IntPtr outList);

    [DllImport(Widgets, EntryPoint = "?menuInAction@QMenu@@SAPEAV1@PEBVQAction@@@Z")]
    private static extern IntPtr MenuInAction(IntPtr action);

    [DllImport(Core, EntryPoint = "?children@QObject@@QEBAAEBV?$QList@PEAVQObject@@@@XZ")]
    private static extern IntPtr ObjectChildren(IntPtr self);

    [DllImport(Core, EntryPoint = "?inherits@QObject@@QEBA_NPEBD@Z")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool ObjectInherits(IntPtr self, byte[] className);

    [DllImport(Gui, EntryPoint = "?text@QAction@@QEBA?AVQString@@XZ")]
    private static extern IntPtr ActionText(IntPtr self, IntPtr outString);

    // Qt6 の QList も QString も {データ, 先頭, 個数} の 24 バイト。
    private const int HandleSize = 24;

    // 壊れた値を渡されたときに、そのまま辿って落ちないための上限。
    private const int SaneCount = 4096;

    public void Execute(IExecutionContext ctx)
    {
        string contains = (ctx.GetPortValue("contains") as string ?? "").Trim();
        int maxDepth = ctx.GetPortValue("max_depth") is double d && d > 0 ? (int)d : 5;

        var lines = new List<string>();
        int menuBars = 0;
        int topLevel = 0;

        ctx.SetPortValue("count", 0d);
        ctx.SetPortValue("menu_bars", 0d);
        ctx.SetPortValue("top_level_windows", 0d);
        ctx.SetPortValue("reason", "");

        IntPtr buffer = Marshal.AllocHGlobal(HandleSize);
        try
        {
            Zero(buffer);
            TopLevelWidgets(buffer);
            var windows = ReadPointerList(buffer);
            topLevel = windows.Count;

            foreach (var window in windows)
            {
                foreach (var bar in FindMenuBars(window, 0, maxDepth))
                {
                    menuBars++;
                    WalkMenu(bar, "", 0, maxDepth, contains, lines);
                }
            }
        }
        catch (DllNotFoundException)
        {
            ctx.SetPortValue("reason", "this application is not built on Qt 6, so there is nothing here to read");
            return;
        }
        catch (EntryPointNotFoundException ex)
        {
            ctx.SetPortValue("reason", "the Qt build in this application does not export what is needed: " + ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ctx.SetPortValue("reason", ex.GetType().Name + ": " + ex.Message);
            return;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        ctx.SetPortValue("paths", string.Join("\n", lines));
        ctx.SetPortValue("count", (double)lines.Count);
        ctx.SetPortValue("menu_bars", (double)menuBars);
        ctx.SetPortValue("top_level_windows", (double)topLevel);

        if (lines.Count == 0)
        {
            // 「無い」と「合わなかった」を混ぜない。読む側が次に何をすべきかが変わる。
            ctx.SetPortValue("reason", menuBars == 0
                ? "no menu bar was found in any of the " + topLevel + " top-level window(s)"
                : "the menu was read but nothing matched '" + contains + "'");
        }
    }

    /// <summary>窓とその下から QMenuBar を集める。</summary>
    private static List<IntPtr> FindMenuBars(IntPtr obj, int depth, int maxDepth)
    {
        var found = new List<IntPtr>();
        if (obj == IntPtr.Zero || depth > maxDepth) return found;

        if (ObjectInherits(obj, Latin1("QMenuBar")))
        {
            found.Add(obj);
            return found;   // メニューバーの中に別のメニューバーは入らない。
        }

        IntPtr children = ObjectChildren(obj);
        foreach (var child in ReadPointerList(children))
        {
            found.AddRange(FindMenuBars(child, depth + 1, maxDepth));
        }
        return found;
    }

    /// <summary>メニューの項目を辿る。ぶら下がっている下位メニューへも降りる。</summary>
    private static void WalkMenu(IntPtr widget, string prefix, int depth, int maxDepth,
                                 string contains, List<string> lines)
    {
        if (widget == IntPtr.Zero || depth > maxDepth) return;

        IntPtr buffer = Marshal.AllocHGlobal(HandleSize);
        try
        {
            Zero(buffer);
            WidgetActions(widget, buffer);
            foreach (var action in ReadPointerList(buffer))
            {
                string text = ReadActionText(action);
                if (text.Length == 0) continue;   // 区切り線には名前が無い。

                string path = prefix.Length == 0 ? text : prefix + " > " + text;
                if (contains.Length == 0 ||
                    path.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lines.Add(path);
                }

                IntPtr submenu = MenuInAction(action);
                if (submenu != IntPtr.Zero)
                {
                    WalkMenu(submenu, path, depth + 1, maxDepth, contains, lines);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadActionText(IntPtr action)
    {
        if (action == IntPtr.Zero) return "";

        IntPtr buffer = Marshal.AllocHGlobal(HandleSize);
        try
        {
            Zero(buffer);
            ActionText(action, buffer);

            IntPtr chars = Marshal.ReadIntPtr(buffer, 8);
            long length = Marshal.ReadInt64(buffer, 16);
            if (chars == IntPtr.Zero || length <= 0 || length > SaneCount) return "";

            // 名前に混ぜてある下線の合図は、読む側には要らない。
            return Marshal.PtrToStringUni(chars, (int)length).Replace("&", "");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>{データ, 先頭, 個数} の並びから、要素のポインタを取り出す。</summary>
    private static List<IntPtr> ReadPointerList(IntPtr list)
    {
        var items = new List<IntPtr>();
        if (list == IntPtr.Zero) return items;

        IntPtr first = Marshal.ReadIntPtr(list, 8);
        long count = Marshal.ReadInt64(list, 16);
        if (first == IntPtr.Zero || count <= 0 || count > SaneCount) return items;

        for (long i = 0; i < count; i++)
        {
            IntPtr item = Marshal.ReadIntPtr(first, (int)(i * IntPtr.Size));
            if (item != IntPtr.Zero) items.Add(item);
        }
        return items;
    }

    private static void Zero(IntPtr buffer)
    {
        for (int i = 0; i < HandleSize; i += 8) Marshal.WriteInt64(buffer, i, 0);
    }

    private static byte[] Latin1(string text)
    {
        var bytes = new byte[text.Length + 1];
        Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }
}
