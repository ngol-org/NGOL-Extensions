"""編集操作を呼ぶための C# を、公式ヘッダーから起こす。

番号と引数の宣言は同じヘッダーから同時に出す。別々に書き写すと、片方だけ間違えても
気づけない（正しい引数で隣の関数を呼ぶので、落ちずにもっともらしい値が返る）。

    py gen_edit_section_binding.py <plugin2.h> [関数名 ...]

関数名を省くと一覧を出す。
"""
import io
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

NL = chr(10)

# 先頭のメンバーは関数ではない。ここを数え落とすと以降が全部 1 つずれる。
FIRST_MEMBER_IS_NOT_A_FUNCTION = True


def load(path):
    text = io.open(path, encoding="cp932", errors="replace").read()
    m = re.search(r"struct\s+EDIT_SECTION\s*\{", text)
    if m is None:
        raise SystemExit("EDIT_SECTION がヘッダーに見当たらない: " + path)
    body = text[m.start():text.index(NL + "};", m.start())]

    members = []
    for decl in re.finditer(r"([\w\* ]+?)\s*\(\*(\w+)\)\s*\(([^)]*)\)", body):
        ret, name, args = decl.group(1).strip(), decl.group(2), decl.group(3).strip()
        parts = [] if args in ("", "void") else [a.strip() for a in args.split(",")]
        members.append((ret, name, parts))

    # 関数ポインタでないメンバーも 1 枠を占めるので、宣言順にそのまま数える
    slot0 = 1 if FIRST_MEMBER_IS_NOT_A_FUNCTION else 0
    return [(slot0 + i, r, n, p) for i, (r, n, p) in enumerate(members)], body


# C の型を C# へ写す。危険なものは注意書きを付けて返す。
def as_param(ctype, cname):
    t = ctype.strip()
    if t == "LPCWSTR":
        return "string " + cname, None
    if t == "LPCSTR":
        return "IntPtr " + cname, "%s は UTF-8 の char*。string で渡すと ANSI として写され日本語が化ける" % cname
    if t in ("OBJECT_HANDLE", "EFFECT_HANDLE"):
        return "IntPtr " + cname, None
    if t in ("int", "float", "double"):
        return t + " " + cname, None
    if t == "bool":
        return "[MarshalAs(UnmanagedType.U1)] bool " + cname, None
    if t.endswith("*"):
        return "IntPtr " + cname, "%s は %s。受け皿を確保して渡す" % (cname, t)
    return "IntPtr " + cname, "%s の型 %s は写し方を確認すること" % (cname, t)


def as_return(ctype):
    t = ctype.strip()
    if t == "void":
        return "void", "", None
    if t in ("LPCSTR", "LPCWSTR"):
        note = ("戻り値を string で受けない。マーシャラが解放しようとする。IntPtr で受けて写す。"
                + NL + "//   さらにこの文字列は「次に同じスレッドで文字列を返す関数を呼ぶまで」しか"
                + NL + "//   有効でない。区間を抜けてから読まないこと")
        if t == "LPCSTR":
            note += NL + "//   * UTF-8 なので Marshal.PtrToStringUTF8 で写す"
        return "IntPtr", "", note
    if t == "bool":
        return "bool", "[return: MarshalAs(UnmanagedType.U1)]" + NL, "既定の bool は 4 バイト。U1 を付けないと隣の値を読む"
    if t in ("int", "float", "double"):
        return t, "", None
    if t in ("OBJECT_HANDLE", "EFFECT_HANDLE"):
        return "IntPtr", "", None
    if t.endswith("*"):
        return "IntPtr", "", None
    return t, "", ("%s は構造体を値で返す。x64 では戻り値の置き場が隠れた第 1 引数として付くので、"
                   % t) + "宣言した引数の数と実際の数が 1 ずれる"


def pascal(name):
    return "".join(w[:1].upper() + w[1:] for w in name.split("_"))


def emit(slot, ret, name, params):
    cs_ret, ret_attr, ret_note = as_return(ret)
    notes = [n for n in [ret_note] if n]

    decl = []
    for p in params:
        m = re.match(r"^(.*?)(\w+)$", p.strip())
        ctype, cname = (m.group(1), m.group(2)) if m else (p, "arg")
        if cname in ("object", "lock", "event", "base", "params", "string"):
            cname = cname + "_"
        text, note = as_param(ctype, cname)
        decl.append(text)
        if note:
            notes.append(note)

    out = []
    out.append("// %s  ->  %d 番 (offset 0x%x)" % (name, slot, slot * 8))
    out.append("// 宣言: %s %s(%s)" % (ret, name, ", ".join(params) if params else "void"))
    for n in notes:
        out.append("// " + n)
    out.append("[UnmanagedFunctionPointer(CallingConvention.Winapi)]")
    if ret_attr:
        out.append(ret_attr.rstrip(NL))
    out.append("delegate %s %s(%s);" % (cs_ret, pascal(name), ", ".join(decl)))
    out.append("")
    out.append("// 区間の中で（call_edit_section_param のコールバックの中でのみ有効）")
    out.append("IntPtr fn = Marshal.ReadIntPtr(section, %d * 8);" % slot)
    out.append("var %s = Marshal.GetDelegateForFunctionPointer<%s>(fn);"
               % (name, pascal(name)))
    return NL.join(out)


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    members, _ = load(sys.argv[1])
    wanted = sys.argv[2:]

    if not wanted:
        print("EDIT_SECTION %d 本（番号は宣言順。先頭の非関数メンバーを 0 番として数える）" % len(members))
        for slot, ret, name, params in members:
            mark = "  廃止" if name.startswith("deprecated_") else ""
            print("  %3d  %-34s %s" % (slot, name, mark))
        return

    for want in wanted:
        hit = [m for m in members if m[2] == want]
        if not hit:
            near = [m[2] for m in members if want in m[2]]
            print("// '%s' は EDIT_SECTION に無い。近いもの: %s"
                  % (want, ", ".join(near) if near else "なし"))
            continue
        slot, ret, name, params = hit[0]
        if name.startswith("deprecated_"):
            print("// %s は廃止された枠。呼べば動く（新しい実装への転送になっている）が、" % name)
            print("//    差し替え先が別に用意されている。そちらを使うこと")
        print(emit(slot, ret, name, params))
        print()


main()
