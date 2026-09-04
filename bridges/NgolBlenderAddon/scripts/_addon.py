"""検証スクリプトが、導入されている NGOL for Blender を見つけるための小さな解決器。

Extension 形式で入れるとモジュール名が `bl_ext.<リポジトリ>.ngol_for_blender` になり、
旧来のアドオン形式では `ngol_for_blender` のままになる。

リポジトリ名（`user_default` 等）を直書きしない。利用者が別のリポジトリへ入れたら外れるうえ、
   公式のガイドラインも「`bl_ext` の直書きは、たいてい良くない兆候」として挙げている。
   => 列挙して末尾一致で探す。

これは検証・開発用のスクリプト側の道具であって、配布物（addon/）には入らない。
"""

from __future__ import annotations

import os

import addon_utils

NAME = "ngol_for_blender"


def resolve_module(prefer: str = "") -> str:
    """導入済みのモジュール名を返す。見つからなければ素の名前を返す。

    prefer に文字列を渡すと、それを含む候補を優先する。
    環境変数 ``NGOL_ADDON_MODULE`` があれば、それを最優先で使う。
    """
    forced = os.environ.get("NGOL_ADDON_MODULE", "").strip()
    if forced:
        return forced

    found = [m.__name__ for m in addon_utils.modules()
             if m.__name__ == NAME or m.__name__.endswith("." + NAME)]
    if not found:
        return NAME

    if prefer:
        for n in found:
            if prefer in n:
                return n

    # Extension 形式を先に採る。旧来形式は残っていても過去のものであることが多い。
    for n in found:
        if n != NAME:
            return n
    return found[0]


def describe(module_name: str) -> str:
    kind = "Extension" if module_name != NAME else "旧来のアドオン"
    return "%s  (%s)" % (module_name, kind)
