using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ホストのウィンドウを探す共通実装。
///
/// 「列挙してプロセス・題名・クラスで絞る」という手順は、撮る・動かす・読む
/// のどの用途でも同じで、書くたびに少しずつ違う実装が増えていた。
/// 探索そのものはここに 1 つだけ置き、各ノードは用途の部分だけを書く。
///
/// 呼び出し側が判断できるように、絞り込みの前後の件数を返す。
/// 0 件だったとき「対象プロセスが無い」のか「条件に一致しない」のかを
/// 区別できないと、原因の違うものを同じ結果として扱ってしまう。
/// </summary>
internal static class NgolWindowFind
{
    internal struct WindowInfo
    {
        public IntPtr Handle;
        public uint ProcessId;
        public uint ThreadId;      // そのウィンドウを作ったスレッド。UI を触ってよいのはここ
        public string Title;
        public string ClassName;

        // GetWindowRect が返す矩形。Vista 以降は見えないリサイズ枠と影を含むので、
        // 画面上の見た目より大きい。並べたり位置を合わせたりする用途には使えない。
        public int Left, Top, Right, Bottom;

        // 実際に描かれている範囲。DwmGetWindowAttribute から取る。
        // 一度も表示されていないウィンドウでは取れないので、その場合は上と同じ値を入れる。
        public int FrameLeft, FrameTop, FrameRight, FrameBottom;
        public bool FrameBoundsAvailable;

        public int ClientWidth, ClientHeight;
        public bool Visible;

        // 表示されていることになっているが画面には出ていない状態。
        // 別の仮想デスクトップにある窓や、停止中のストアアプリがこれになる。
        // Visible だけを見ると「出ている」と誤って読む。
        public bool Cloaked;

        public bool Minimized;

        // 確認画面が出ている間、その持ち主の窓は無効化される。
        // 絵を見なくても、待ち状態かどうかがここで分かる。
        public bool Enabled;
        public uint Dpi;           // 0 = 取れなかった（この OS には API が無い）

        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int FrameWidth => FrameRight - FrameLeft;
        public int FrameHeight => FrameBottom - FrameTop;
    }

    internal struct Query
    {
        public uint ProcessId;          // 0 = 問わない
        public string TitleContains;    // 空 = 問わない
        public string ClassContains;    // 空 = 問わない
        public bool VisibleOnly;
        public bool TopLevelOnly;       // false なら子ウィンドウも辿る
    }

    internal struct Outcome
    {
        public List<WindowInfo> Windows;
        public int TotalTopLevel;       // 走査した最上位ウィンドウの総数
        public int InProcess;           // うちプロセス条件に一致した数

        // うち、見えないという理由だけで外した数。VisibleOnly のときしか増えない。
        // 題名とクラスの照合はこれを外した後なので、この数を持たないと
        // 「見えないので落ちた」を「題名で落ちた」として説明してしまう。
        public int SkippedInvisible;

        /// <summary>
        /// 0 件のときに、何が原因かを言えるようにする。
        /// 呼び出し側がこれをそのまま出力へ載せれば、利用者が次の一手を決められる。
        /// </summary>
        public string Explain(Query query)
        {
            if (Windows.Count > 0) return "";
            if (TotalTopLevel == 0) return "no top level window was found at all";
            if (query.ProcessId != 0 && InProcess == 0)
                return $"no window belongs to process {query.ProcessId}";

            int reachedFilters = InProcess - SkippedInvisible;
            if (reachedFilters <= 0)
                return SkippedInvisible == 1
                    ? "the one candidate window is not visible, and only visible windows are looked at"
                    : $"none of the {SkippedInvisible} candidate windows is visible, "
                      + "and only visible windows are looked at";

            // 「見えている候補」と呼んでよいのは、見えることを条件にしたときだけ。
            string kind = query.VisibleOnly ? "visible candidate" : "candidate";
            string conditions = Conditions(query);
            string head = reachedFilters == 1
                ? "the one " + kind
                : $"all {reachedFilters} {kind}s";
            string body = conditions.Length > 0
                ? $"{head} failed to match {conditions}"
                : $"{head} could not be read";

            return SkippedInvisible > 0
                ? $"{body}; {SkippedInvisible} more were skipped for not being visible"
                : body;
        }

        /// <summary>効いていた絞り込みだけを並べる。効いていないものを挙げると読み手が迷う。</summary>
        private static string Conditions(Query query)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(query.TitleContains))
                parts.Add($"a title containing \"{query.TitleContains}\"");
            if (!string.IsNullOrEmpty(query.ClassContains))
                parts.Add($"a class containing \"{query.ClassContains}\"");
            return string.Join(" and ", parts);
        }
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, out int value, int size);

    // OS が古いと存在しない。呼べなかったことと 0 を返したことを区別するため、
    // 宣言はするが失敗を握って 0 を返す。
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int DWMWA_CLOAKED = 14;
    const uint WM_NULL = 0x0000;
    const uint WM_GETTEXT = 0x000D;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    internal static WindowInfo Describe(IntPtr hwnd)
    {
        // 別プロセスのトップレベルに対しては、キャプションを読むだけで
        // WM_GETTEXT は送られない。相手が固まっていても巻き込まれない。
        // 別プロセスのコントロールの文字はこれでは取れないので TextOf を使う。
        var title = new StringBuilder(512);
        GetWindowTextW(hwnd, title, title.Capacity);

        var cls = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);

        GetWindowRect(hwnd, out RECT window);
        GetClientRect(hwnd, out RECT client);

        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);

        var info = new WindowInfo
        {
            Handle = hwnd,
            ProcessId = processId,
            ThreadId = threadId,
            Title = title.ToString(),
            ClassName = cls.ToString(),
            Left = window.Left,
            Top = window.Top,
            Right = window.Right,
            Bottom = window.Bottom,
            ClientWidth = client.Right - client.Left,
            ClientHeight = client.Bottom - client.Top,
            Visible = IsWindowVisible(hwnd),
            Minimized = IsIconic(hwnd),
            Enabled = IsWindowEnabled(hwnd),
        };

        // 見えている矩形。取れなければ GetWindowRect の値をそのまま入れ、
        // 取れなかったことを別に伝える（同じ値が入っているのを成功と読ませない）。
        info.FrameBoundsAvailable = false;
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
            {
                info.FrameLeft = frame.Left;
                info.FrameTop = frame.Top;
                info.FrameRight = frame.Right;
                info.FrameBottom = frame.Bottom;
                info.FrameBoundsAvailable = true;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        if (!info.FrameBoundsAvailable)
        {
            info.FrameLeft = window.Left;
            info.FrameTop = window.Top;
            info.FrameRight = window.Right;
            info.FrameBottom = window.Bottom;
        }

        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                info.Cloaked = cloaked != 0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        try { info.Dpi = GetDpiForWindow(hwnd); }
        catch (EntryPointNotFoundException) { info.Dpi = 0; }

        return info;
    }

    /// <summary>
    /// ウィンドウの文字を読む。
    ///
    /// 別プロセスのコントロールは GetWindowText では取れない（空が返る）ので、
    /// WM_GETTEXT を送る。相手が固まっている場合に巻き込まれないよう、
    /// 待ち時間を切って諦める。
    /// </summary>
    internal static string TextOf(IntPtr hwnd, uint timeoutMs, out bool answered)
    {
        answered = true;

        var direct = new StringBuilder(512);
        GetWindowTextW(hwnd, direct, direct.Capacity);
        if (direct.Length > 0) return direct.ToString();

        const int Capacity = 4096;
        var buffer = Marshal.AllocHGlobal(Capacity * sizeof(char));
        try
        {
            var sent = SendMessageTimeoutW(hwnd, WM_GETTEXT, (IntPtr)Capacity, buffer,
                                           SMTO_ABORTIFHUNG, timeoutMs, out _);
            // 空が返ったのか、返事そのものが無かったのかを呼び出し側で分けられるようにする。
            // 見た目はどちらも「文字が無い」だが、意味はまったく違う。
            if (sent == IntPtr.Zero) { answered = false; return ""; }
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>直下の子ウィンドウ。階層を辿る側が深さを決められるよう、1 段だけ返す。</summary>
    internal static List<IntPtr> Children(IntPtr parent)
    {
        var found = new List<IntPtr>();
        EnumChildWindows(parent, (child, _) =>
        {
            // EnumChildWindows は孫まで列挙する。1 段だけ欲しいので親で絞る。
            if (GetParent(child) == parent) found.Add(child);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// そのウィンドウがメッセージを処理できる状態かを見る。
    ///
    /// ウィンドウが出ていることと、応答できることは別。起動直後は前者だけが
    /// 真になる時間があるので、起動を待つ側はこちらまで見る必要がある。
    /// </summary>
    internal static bool Responds(IntPtr hwnd, uint timeoutMs = 500)
        => SendMessageTimeoutW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                               SMTO_ABORTIFHUNG, timeoutMs, out _) != IntPtr.Zero;

    /// <summary>コントロールの識別子。ダイアログ以外では 0 のことが多い。</summary>
    internal static int ControlIdOf(IntPtr hwnd) => GetDlgCtrlID(hwnd);

    // 持ち主を持つ窓は、その窓に対する確認画面。
    internal static IntPtr OwnerOf(IntPtr hwnd) => GetWindow(hwnd, 4 /* GW_OWNER */);

    /// <summary>
    /// その窓が画面のどれだけを占めて見えているか。碁盤目に点を取り、
    /// その位置をクリックしたら誰に当たるかを聞いて数える。
    ///
    /// 様式（WS_VISIBLE）は隠されているかを言わない。公式にも「他の窓に完全に
    /// 隠されていても 0 以外を返しうる」とある。クリッピングに聞く古い手法も、
    /// 合成が入った今の Windows では効かない（各窓が自分の面へ描くので、
    /// 他の窓が可視領域を削らない。実測で「隠れているのに 100%」と答えた）。
    /// 当たり判定は合成の影響を受けないので、これが素直に効く。
    /// </summary>
    /// <param name="onTop">最初に手前で見つかった窓の題。誰にも遮られていなければ空</param>
    /// <returns>見えている点の割合（0 = どの点も他の窓に取られた、1 = 全部自分）</returns>
    internal static double VisibleShare(IntPtr hwnd, out string onTop, int grid = 3)
    {
        const uint GA_ROOT = 2;
        onTop = "";
        if (grid < 1) grid = 1;

        if (!GetClientRect(hwnd, out RECT client)) return 0;
        int w = client.Right - client.Left;
        int h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0) return 0;

        int mine = 0;
        int total = grid * grid;

        for (int gy = 0; gy < grid; gy++)
        {
            for (int gx = 0; gx < grid; gx++)
            {
                var point = new POINT
                {
                    X = w * (gx * 2 + 1) / (grid * 2),
                    Y = h * (gy * 2 + 1) / (grid * 2),
                };
                if (!ClientToScreen(hwnd, ref point)) continue;

                IntPtr at = WindowFromPoint(point);
                if (at == IntPtr.Zero) continue;

                if (at == hwnd || GetAncestor(at, GA_ROOT) == hwnd) { mine++; continue; }

                if (onTop.Length == 0)
                {
                    var root = GetAncestor(at, GA_ROOT);
                    var title = new StringBuilder(512);
                    GetWindowTextW(root, title, title.Capacity);
                    onTop = title.Length > 0 ? title.ToString() : "(untitled window)";
                }
            }
        }

        return (double)mine / total;
    }

    /// <summary>
    /// その窓を常に手前へ出す。戻すときは keepOnTop を false にする。
    ///
    /// 活性化は奪えない（SetForegroundWindow は今フォアグラウンドに居る側しか呼べない）が、
    /// 手前へ出すことはできる。Z 順の属性であって活性化ではないので権利が要らない。
    /// 実測: 完全に隠れていた確認画面が、これだけで見えるようになった（利用者の目でも確認）。
    ///
    /// 出しっぱなしにしない。戻さないとその窓は常に手前に居続ける。
    /// </summary>
    internal static bool KeepOnTop(IntPtr hwnd, bool keepOnTop)
    {
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        IntPtr after = new IntPtr(keepOnTop ? -1 : -2);   // HWND_TOPMOST / HWND_NOTOPMOST
        return SetWindowPos(hwnd, after, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    // 押すのに送信を使うと、相手の返事を待つことになる。投げて戻る。
    internal static bool ClickAsync(IntPtr button) =>
        PostMessageW(button, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);

    internal static Outcome Find(Query query)
    {
        var result = new Outcome { Windows = new List<WindowInfo>() };
        string title = query.TitleContains ?? "";
        string cls = query.ClassContains ?? "";

        var pending = new List<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            result.TotalTopLevel++;
            pending.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        // 子ウィンドウは列挙のコールバックの中で辿らない。
        // 列挙中に別の列挙を始めると、途中で作られたり消えたりしたウィンドウの
        // 扱いが実装依存になる。先に最上位を集めてから辿る。
        if (!query.TopLevelOnly)
        {
            var children = new List<IntPtr>();
            foreach (var parent in pending)
            {
                EnumChildWindows(parent, (child, _) => { children.Add(child); return true; }, IntPtr.Zero);
            }
            pending.AddRange(children);
        }

        foreach (var hwnd in pending)
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (query.ProcessId != 0 && processId != query.ProcessId) continue;
            result.InProcess++;

            if (query.VisibleOnly && !IsWindowVisible(hwnd)) { result.SkippedInvisible++; continue; }

            var info = Describe(hwnd);

            if (title.Length > 0 &&
                info.Title.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (cls.Length > 0 &&
                info.ClassName.IndexOf(cls, StringComparison.OrdinalIgnoreCase) < 0) continue;

            result.Windows.Add(info);
        }

        return result;
    }

    /// <summary>
    /// 1 つに決まることを前提にした呼び出し向け。
    /// 複数一致したときに黙って先頭を選ぶと、別のウィンドウを撮ったり動かしたり
    /// してしまうので、決まらなかったことを候補つきで返す。
    /// </summary>
    internal static bool FindOne(Query query, out WindowInfo window, out string problem)
    {
        var outcome = Find(query);
        window = default;
        problem = "";

        if (outcome.Windows.Count == 1) { window = outcome.Windows[0]; return true; }

        if (outcome.Windows.Count == 0)
        {
            problem = outcome.Explain(query);
            return false;
        }

        var sb = new StringBuilder();
        sb.Append(outcome.Windows.Count).Append(" windows matched: ");
        for (int i = 0; i < outcome.Windows.Count && i < 8; i++)
        {
            if (i > 0) sb.Append(" / ");
            sb.Append('"').Append(outcome.Windows[i].Title).Append('"');
        }
        problem = sb.ToString();
        return false;
    }
}
