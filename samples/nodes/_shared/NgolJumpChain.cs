using System;
using System.Collections.Generic;
using System.Text;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 関数の先頭に置かれた飛び越しを辿り、最終的にどこへ行くのかを組み立てる。
///
/// インラインフックは対象の先頭数バイトを飛び越しへ書き換えるため、
/// 先頭が飛び越しになっている関数は、既に別のソフトウェアが横取りしている。
/// 先に張った側のトランポリンが元のバイトを持ち、関数の途中へ戻るので、
/// 後から先頭に張った側は呼び出しの経路から外れる。
/// => 誰が先に居るのかは、掴む前に知る必要がある。
/// </summary>
internal static class NgolJumpChain
{
    /// <summary>辿った 1 段分。</summary>
    public struct Hop
    {
        public long Address;
        /// <summary>その番地を含むモジュール名。どのモジュールにも属さないときは空。</summary>
        public string Module;
        /// <summary>モジュール先頭からの位置。<see cref="Module"/> が空のときは意味を持たない。</summary>
        public long Rva;
        /// <summary>ここに置かれていた飛び越しの形（"jmp rel32" / "jmp [rip]"）。終点では空。</summary>
        public string Form;
    }

    /// <summary>先頭のバイト列が飛び越しかどうかだけを見る。</summary>
    public static bool LooksLikeJump(IntPtr address)
    {
        var b = new byte[2];
        if (NgolSafeMemory.Read(address, b, 0, 2) < 2) return false;
        return b[0] == 0xE9 || (b[0] == 0xFF && b[1] == 0x25);
    }

    /// <summary>
    /// 辿った並びが「他所へ持って行かれている」かを判定する。
    ///
    /// 先頭の飛び越しをそのままフックの証拠にしてはいけない。
    /// リンカは同じ実装を 1 つにまとめる際などに、モジュール内へ飛ぶだけのサンクを置く。
    /// 分かれ目は行き先で、同じモジュールの中で完結していればサンク、
    /// 外のモジュールやどこにも属さない領域へ出て行けば横取りである。
    /// </summary>
    public static bool IsForeignRedirect(IReadOnlyList<Hop> hops)
    {
        if (hops == null || hops.Count <= 1) return false;
        var from = hops[0].Module;
        var to = hops[hops.Count - 1].Module;
        if (to.Length == 0) return true;                     // どのモジュールにも属さない = 確保された領域
        return !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="start"/> から飛び越しを辿る。返るのは通った番地の並びで、
    /// 先頭が <paramref name="start"/>、末尾が飛び越しでなくなった場所。
    /// 飛び越しが 1 つも無ければ 1 件だけ返る。
    /// </summary>
    public static List<Hop> Follow(IntPtr start, IReadOnlyList<NgolModuleDefault.ModuleEntry> modules, int maxHops = 8)
    {
        var hops = new List<Hop>();
        var seen = new HashSet<long>();
        var addr = start.ToInt64();
        var buf = new byte[14];

        for (int i = 0; i <= maxHops; i++)
        {
            if (!seen.Add(addr)) break;   // 環になっていたら止める

            var hop = new Hop { Address = addr, Form = "" };
            var owner = FindModule(modules, addr);
            hop.Module = owner.Name ?? "";
            hop.Rva = owner.Name == null ? 0 : addr - owner.Base;

            var read = NgolSafeMemory.Read(new IntPtr(addr), buf, 0, 14);
            long next = 0;
            if (read >= 5 && buf[0] == 0xE9)
            {
                int rel = BitConverter.ToInt32(buf, 1);
                next = addr + 5 + rel;
                hop.Form = "jmp rel32";
            }
            else if (read >= 6 && buf[0] == 0xFF && buf[1] == 0x25)
            {
                // ff 25 disp32 は「disp32 の先に置かれた番地へ飛べ」の意味。
                int disp = BitConverter.ToInt32(buf, 2);
                var slot = addr + 6 + disp;
                var p = new byte[8];
                if (NgolSafeMemory.Read(new IntPtr(slot), p, 0, 8) == 8)
                {
                    next = BitConverter.ToInt64(p, 0);
                    hop.Form = "jmp [rip]";
                }
            }

            hops.Add(hop);
            if (hop.Form.Length == 0 || next == 0) break;
            addr = next;
        }

        return hops;
    }

    /// <summary>辿った並びを 1 行の文にする。横取りされていなければ空文字。</summary>
    public static string Describe(IReadOnlyList<Hop> hops)
    {
        if (!IsForeignRedirect(hops)) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < hops.Count; i++)
        {
            if (i > 0) sb.Append(" -> ");
            sb.Append(Format(hops[i]));
            if (hops[i].Form.Length > 0) sb.Append(" (").Append(hops[i].Form).Append(')');
        }
        return sb.ToString();
    }

    /// <summary>並びの終点。横取りされていれば、そこが実際に走るコードの在処。</summary>
    public static Hop Last(IReadOnlyList<Hop> hops) => hops[hops.Count - 1];

    public static string Format(Hop h)
    {
        if (h.Module.Length == 0) return $"0x{h.Address:X} (no module)";
        return $"0x{h.Address:X} = {h.Module}+0x{h.Rva:x}";
    }

    static NgolModuleDefault.ModuleEntry FindModule(
        IReadOnlyList<NgolModuleDefault.ModuleEntry> modules, long addr)
    {
        if (modules != null)
        {
            for (int i = 0; i < modules.Count; i++)
            {
                var m = modules[i];
                if (addr >= m.Base && addr < m.Base + m.Size) return m;
            }
        }
        return default;
    }
}
