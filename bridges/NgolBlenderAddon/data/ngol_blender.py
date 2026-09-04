"""NGOL のノードから使う Blender 側の土台。

置き場所: ``<ngolRoot>/Nodes/CustomNodes/py/ngol_blender.py``

**ここはアドオンではなく NGOL の territory。**
    `.cs` のノードと同じ扱いで、**Blender を再起動せずに書き換えられる**。

    書き換えたら `reload_modules(["ngol_blender"])` を呼ぶこと。
      Python は import 済みを覚えているので、**黙って古い実装が動き続ける**。

---

## ここに何を書くかの判断 - 「重い処理」で決めてはいけない

**「重い処理は Python 側」は誤り。** むしろ**逆になることが多い**。

**ここは Blender のメインスレッドで走る。**
=> ここに時間のかかる処理を書くと、**その間 Blender が固まる**。
  （だからポンプは 1 回あたり 0.1 秒で切り上げて次へ譲る作りになっている）

**C# のノード側は NGOL 自前のスレッドで走る**ので、
  長い計算をしても **Blender は止まらない**。GIL も無く、JIT で速い。

### 判断の軸は 3 つ。どれも「重さ」ではない

| 問い | 答えが Yes なら |
|---|---|
| **`bpy` に触る必要があるか** | **ここ（Python）一択。** 性能の話ではなく、CPython のメインスレッドからしか届かないから |
| **境界を何度も跨ぐか** | **跨ぐ側をまとめる。** 1 往復 約 200ms。bpy 操作を 1000 回するなら、**1 回の呼び出しの中でループする** |
| **メインスレッドを長く占有するか** | **C# へ逃がす**か、ここで**分割する**。固まった Blender は `bpy.app.timers` も回らないので、ブリッジごと応答しなくなる |

### 具体例

| 処理 | どちら | なぜ |
|---|---|---|
| オブジェクトを 100 個作る | **Python**（1 回の呼び出しの中でループ） | bpy が要る。ノードから 100 回呼ぶと 20 秒かかる |
| 頂点座標から重心・バウンディングボックスを出す | **読むのは Python、計算は C#** でもよい | 読みは bpy 必須。計算だけなら渡してしまえる |
| PE 解析・逆アセンブル・メモリ走査 | **C#**（`ngol.code.*` / `ngol.mem.*`） | bpy からは原理的に届かない |
| 大量の文字列整形・JSON 組み立て | **C#** | bpy が要らない。ここでやると Blender を止める |
| 数百万頂点のメッシュ生成 | **Python だが分割する** | bpy が要るので逃がせない。一度にやると固まる |

呼ばれ方（アドオンの汎用ポンプが exec する Python から）:

    import ngol_blender as nb
    result = nb.spawn_ring(**args)

ここは必ず **Blender のメインスレッド** で走る（ポンプがそう呼ぶ）。
    逆に言うと、ここを別スレッドから呼んではいけない。
"""

from __future__ import annotations

import colorsys
import math
import os

import bpy

__all__ = (
    "ping", "scene_stat", "list_objects", "get_object",
    "spawn_ring", "clear_prefix", "move_prefix",
    "capture_window",
    "redraw", "hex_to_rgb",
)


# ======================================================================================
# 下ごしらえ
# ======================================================================================

def redraw() -> None:
    """ビューポートを描き直させる。

    これを忘れると、**出来ているのに画面が変わらない**。
       「効かない」と読み違える典型なので、絵が変わる操作の最後に必ず呼ぶ。
    """
    try:
        for window in bpy.context.window_manager.windows:
            for area in window.screen.areas:
                if area.type in ("VIEW_3D", "OUTLINER", "PROPERTIES"):
                    area.tag_redraw()
    except Exception:
        pass


def hex_to_rgb(text: str, fallback=(0.90, 0.35, 0.15)):
    text = (text or "").strip().lstrip("#")
    if len(text) != 6:
        return fallback
    try:
        return tuple(int(text[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
    except ValueError:
        return fallback


def make_material(name: str, rgb):
    """色が **Solid 表示でも** 出るマテリアルを作る。

    `Principled BSDF` だけ設定すると、Material Preview に切り替えるまで白いままで、
       「色が付かない」と読み違える。`diffuse_color` も一緒に入れる。
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.diffuse_color = (rgb[0], rgb[1], rgb[2], 1.0)
    principled = mat.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        principled.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    return mat


def make_mesh(shape: str, size: float):
    """プリミティブのメッシュを作る。

    `bpy.ops.mesh.primitive_*_add` は呼び出し時の context に依る。
       タイマーから呼ぶと環境によって通らないので、**context を要求しない bmesh** で組む。
    """
    import bmesh

    shape = (shape or "cube").strip().lower()
    bm = bmesh.new()
    try:
        if shape == "cube":
            bmesh.ops.create_cube(bm, size=size)
        elif shape in ("sphere", "uvsphere"):
            # 引数名は版で変わっている（diameter -> radius）。両方試す。
            try:
                bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=16, radius=size * 0.5)
            except TypeError:
                bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=16, diameter=size * 0.5)
        elif shape == "icosphere":
            try:
                bmesh.ops.create_icosphere(bm, subdivisions=2, radius=size * 0.5)
            except TypeError:
                bmesh.ops.create_icosphere(bm, subdivisions=2, diameter=size * 0.5)
        elif shape == "cone":
            bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=True, segments=24,
                                  radius1=size * 0.5, radius2=0.0, depth=size)
        elif shape == "cylinder":
            bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=True, segments=24,
                                  radius1=size * 0.5, radius2=size * 0.5, depth=size)
        elif shape == "monkey":
            bmesh.ops.create_monkey(bm)
            bmesh.ops.scale(bm, vec=(size, size, size), verts=bm.verts)
        else:
            raise ValueError(
                "unknown shape %r (cube/sphere/icosphere/cone/cylinder/monkey)" % shape)

        mesh = bpy.data.meshes.new("NgolMesh_" + shape)
        bm.to_mesh(mesh)
        mesh.update()
        return mesh
    finally:
        if bm.is_valid:
            bm.free()


# ======================================================================================
# 読む - 平らな行で返す。前後 2 回を突き合わせて「効いたか」を見るための形。
#        シーンの構造を厚く説明するのはここの仕事ではない（公式 Blender MCP の領分）。
# ======================================================================================

def ping() -> dict:
    import os as _os
    return {
        "ok": True,
        "blender": bpy.app.version_string,
        "background": bpy.app.background,
        "pid": _os.getpid(),
    }


def scene_stat(prefix: str = "") -> dict:
    by_type = {}
    for o in bpy.data.objects:
        by_type[o.type] = by_type.get(o.type, 0) + 1
    active = bpy.context.view_layer.objects.active
    matched = [o.name for o in bpy.data.objects if prefix and o.name.startswith(prefix)]
    return {
        "ok": True,
        "scene_name": bpy.context.scene.name,
        "blend_file": bpy.data.filepath or "(unsaved)",
        "frame": bpy.context.scene.frame_current,
        "object_count": len(bpy.data.objects),
        "mesh_count": by_type.get("MESH", 0),
        "by_type": by_type,
        "active_object": active.name if active else None,
        "matched": len(matched),
    }


def object_row(obj) -> dict:
    """1 件ぶんの平らな情報。入れ子にしない--差分を目で取れる形にしておく。"""
    row = {
        "name": obj.name,
        "type": obj.type,
        "location": [round(v, 4) for v in obj.location],
        "rotation_z_deg": round(math.degrees(obj.rotation_euler.z), 3),
        "scale": [round(v, 4) for v in obj.scale],
        "visible": obj.visible_get(),
        "material": (obj.material_slots[0].material.name
                     if obj.material_slots and obj.material_slots[0].material else None),
    }
    data = obj.data
    if data is not None and hasattr(data, "vertices"):
        row["verts"] = len(data.vertices)
        row["polys"] = len(data.polygons)
        row["data_name"] = data.name
        row["data_users"] = data.users
    return row


def list_objects(prefix: str = "", type: str = "", limit: int = 50) -> dict:
    type_filter = (type or "").strip().upper()
    limit = max(1, min(int(limit or 50), 500))
    rows = [object_row(o) for o in bpy.data.objects
            if (not prefix or o.name.startswith(prefix))
            and (not type_filter or o.type == type_filter)]
    rows.sort(key=lambda r: r["name"])
    return {"ok": True, "matched": len(rows), "shown": min(len(rows), limit),
            "objects": rows[:limit]}


def get_object(name: str) -> dict:
    """1 件を名前で引く。無いときは黙って空を返さず、理由と候補を返す。"""
    name = (name or "").strip()
    if not name:
        return {"ok": False, "error": "name が空です"}
    obj = bpy.data.objects.get(name)
    if obj is None:
        near = [o.name for o in bpy.data.objects if name.lower() in o.name.lower()][:10]
        return {"ok": False,
                "error": "'%s' というオブジェクトはありません" % name,
                "did_you_mean": near}
    return {"ok": True, "object": object_row(obj)}


# ======================================================================================
# 作る・動かす・消す
# ======================================================================================

def spawn_ring(shape: str = "cube", count: int = 8, radius: float = 4.0,
               size: float = 1.0, height: float = 0.0, wave: float = 0.0,
               spin: float = 0.0, rainbow: bool = True, color: str = "",
               prefix: str = "NGOL") -> dict:
    """円周に並べて作る。数・半径・大きさを変えると画面が明確に変わる。

    メッシュは全部で 1 つを共有する。個数を増やしても重くしない。
    そのぶんマテリアルは **オブジェクト側** に付ける。
      データ側に付けると共有しているので全部同じ色になり、虹色にならない。
    """
    count = max(1, min(int(count or 8), 500))
    prefix = (prefix or "NGOL").strip() or "NGOL"
    base_rgb = hex_to_rgb(color)

    mesh = make_mesh(shape, float(size or 1.0))
    collection = bpy.context.scene.collection
    created = []

    for i in range(count):
        angle = (2.0 * math.pi * i / count) + math.radians(float(spin or 0.0))
        # 1 個だけのときは中心に置く（円周に並べても意味が無い）
        x = radius * math.cos(angle) if count > 1 else 0.0
        y = radius * math.sin(angle) if count > 1 else 0.0
        z = float(height or 0.0) + float(wave or 0.0) * math.sin(angle * 2.0)

        obj = bpy.data.objects.new("%s_%s_%03d" % (prefix, shape, i), mesh)
        obj.location = (x, y, z)
        obj.rotation_euler = (0.0, 0.0, angle)

        rgb = colorsys.hsv_to_rgb(i / float(count), 0.85, 1.0) if rainbow else base_rgb
        if not obj.material_slots:
            obj.data.materials.append(None)
        obj.material_slots[0].link = "OBJECT"
        obj.material_slots[0].material = make_material("%s_mat_%03d" % (prefix, i), rgb)

        collection.objects.link(obj)
        created.append(obj.name)

    if created:
        bpy.context.view_layer.objects.active = bpy.data.objects[created[-1]]

    redraw()
    return {"ok": True, "created": len(created), "names": created[:100],
            "shape": shape, "total_objects": len(bpy.data.objects)}


def move_prefix(prefix: str = "NGOL", spin: float = 15.0,
                dz: float = 0.0, scale: float = 1.0) -> dict:
    """まとめて回す・上下させる。実行するたびに画面が変わるので、効いたかが目で分かる。"""
    prefix = (prefix or "NGOL").strip()
    targets = [o for o in bpy.data.objects if prefix and o.name.startswith(prefix)]
    if not targets:
        return {"ok": False, "error": "'%s' で始まるオブジェクトがありません" % prefix}

    rad = math.radians(float(spin or 0.0))
    c, s = math.cos(rad), math.sin(rad)
    for obj in targets:
        x, y, z = obj.location
        obj.location = (x * c - y * s, x * s + y * c, z + float(dz or 0.0))
        obj.rotation_euler.z += rad
        if scale and scale != 1.0:
            obj.scale = tuple(v * float(scale) for v in obj.scale)

    redraw()
    return {"ok": True, "moved": len(targets)}


def clear_prefix(prefix: str = "NGOL") -> dict:
    """接頭辞に一致するものを消す。**カンマ区切りで複数指定できる。**

    接頭辞が空のときは **何もしない**。頼まれていない全消しをしない。
    使われなくなったメッシュとマテリアルも片付ける。残すと `.blend` が太り続ける。

    試すたびに前回の残骸が邪魔になるので、**片付けは 1 回で済む形**にしてある
       （`"NGOL,GRID,CONE,PY"` のように並べる）。
    """
    prefixes = [p.strip() for p in (prefix or "").split(",") if p.strip()]
    if not prefixes:
        return {"ok": False, "error": "prefix が空です。全消しはしません"}

    def matches(name: str) -> bool:
        return any(name.startswith(p) for p in prefixes)

    victims = [o for o in bpy.data.objects if matches(o.name)]
    names = [o.name for o in victims]
    for obj in victims:
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if isinstance(data, bpy.types.Mesh) and data.users == 0:
            bpy.data.meshes.remove(data)

    removed_materials = 0
    for mat in [m for m in bpy.data.materials if matches(m.name) and m.users == 0]:
        bpy.data.materials.remove(mat)
        removed_materials += 1

    # 誰も使わなくなったメッシュは、接頭辞が違っても残る（共有していたぶん）。
    #    数だけは返して、増え続けていないか見えるようにしておく。
    orphan_meshes = len([m for m in bpy.data.meshes if m.users == 0])

    redraw()
    return {"ok": True, "removed": len(names), "names": names[:100],
            "removed_materials": removed_materials,
            "orphan_meshes": orphan_meshes,
            "prefixes": prefixes,
            "total_objects": len(bpy.data.objects)}


# ======================================================================================
# 画面を撮る
# ======================================================================================

def capture_window(path: str, editor_only: bool = False) -> dict:
    """Blender 自身に画面を書き出させる。

    自前で画面を撮る仕組みを書かない。Blender 自身がその口を持っている。
    ホストが描いた絵をそのまま貰うので、他の窓が重なっていても写り込まない。
    background モードでは撮れない。
    """
    import os as _os

    if bpy.app.background:
        return {"ok": False, "error": "background モードでは画面を撮れません"}

    _os.makedirs(_os.path.dirname(path), exist_ok=True)
    try:
        if editor_only:
            bpy.ops.screen.screenshot_area(filepath=path)
        else:
            bpy.ops.screen.screenshot(filepath=path)
    except (RuntimeError, AttributeError) as ex:
        return {"ok": False, "error": str(ex)}

    if not _os.path.isfile(path):
        return {"ok": False, "error": "撮ったはずのファイルがありません: " + path}

    width = height = 0
    try:
        image = bpy.data.images.load(path)
        width, height = image.size[0], image.size[1]
        bpy.data.images.remove(image)
    except Exception:
        pass

    return {"ok": True, "path": path, "bytes": _os.path.getsize(path),
            "width": width, "height": height}


def spawn_grid(shape: str = "cube", cols: int = 6, rows: int = 6, gap: float = 2.0,
               size: float = 1.0, height: float = 0.0, prefix: str = "GRID") -> dict:
    """格子状に並べて作る。

    cols x rows 個を、中心を原点として gap 間隔で並べる。
    shape は make_mesh が知る形（cube/sphere/...）。size は 1 辺の目安、
    height は z 位置、prefix は名前の先頭に付く文字列。
    """
    cols = max(1, min(int(cols or 6), 60))
    rows = max(1, min(int(rows or 6), 60))
    prefix = (prefix or "GRID").strip() or "GRID"

    mesh = make_mesh(shape, float(size or 1.0))
    collection = bpy.context.scene.collection
    created = []

    for r in range(rows):
        for c in range(cols):
            x = (c - (cols - 1) / 2.0) * gap
            y = (r - (rows - 1) / 2.0) * gap
            obj = bpy.data.objects.new("%s_%s_%02d_%02d" % (prefix, shape, r, c), mesh)
            obj.location = (x, y, float(height or 0.0))

            # 位置で色を決める。並びが目で確かめられる（どこが端か分かる）
            rgb = colorsys.hsv_to_rgb(
                (c / float(cols)) * 0.7, 0.4 + 0.6 * (r / float(max(rows - 1, 1))), 1.0)
            if not obj.material_slots:
                obj.data.materials.append(None)
            obj.material_slots[0].link = "OBJECT"
            obj.material_slots[0].material = make_material(
                "%s_mat_%02d_%02d" % (prefix, r, c), rgb)

            collection.objects.link(obj)
            created.append(obj.name)

    redraw()
    return {"ok": True, "created": len(created), "cols": cols, "rows": rows,
            "names": created[:100], "total_objects": len(bpy.data.objects)}


# 外部ファイルを指しうるデータブロック。
# ここに無い種類は見落とす。bpy に「外部参照を持つもの」を一覧する口は無い。
_PATH_KINDS = ("images", "libraries", "sounds", "movieclips",
               "fonts", "cache_files", "volumes")

# 未使用かどうかを見る対象。
# 保存すると 0 ユーザーのものは捨てられるので、これが意味を持つのは
#    生きているセッションだけ。読み込んだ直後のファイルでは常に 0 になる。
_UNUSED_KINDS = ("images", "materials", "meshes", "textures", "node_groups", "actions")


def audit_paths(limit: int = 50) -> dict:
    """外部参照と未使用データを洗い出す。

    欠落した参照・絶対パス・埋め込み・未使用を数えて返す。
    埋め込み済み（packed）は実体が無くても欠落ではない。
    相対パスは .blend の位置が基準なので、未保存のファイルでは解決できない。
    """
    limit = max(1, min(int(limit), 500))

    rows = []
    for kind in _PATH_KINDS:
        coll = getattr(bpy.data, kind, None)
        if coll is None:
            continue
        for db in coll:
            # 生成物・内部の受け皿は外部ファイルではない
            if getattr(db, "source", "") in ("VIEWER", "GENERATED"):
                continue
            raw = getattr(db, "filepath", "") or ""
            packed = bool(getattr(db, "packed_file", None))
            if not raw and not packed:
                continue
            resolved = bpy.path.abspath(raw) if raw else ""
            rows.append({
                "kind": kind,
                "name": db.name,
                "path": raw,
                "resolved": resolved,
                "relative": raw.startswith("//"),
                "packed": packed,
                "missing": bool(raw) and not packed and not os.path.exists(resolved),
                "users": getattr(db, "users", 0),
            })

    unused = []
    for kind in _UNUSED_KINDS:
        coll = getattr(bpy.data, kind, None)
        if coll is None:
            continue
        for db in coll:
            if db.users == 0 and not db.use_fake_user:
                unused.append(kind + ":" + db.name)

    missing = [r for r in rows if r["missing"]]
    absolute = [r for r in rows if r["path"] and not r["relative"]]
    packed = [r for r in rows if r["packed"]]

    lines = []
    for r in rows[:limit]:
        mark = "MISSING" if r["missing"] else ("packed" if r["packed"] else "ok")
        lines.append("%-7s %-11s %-24s %s" % (mark, r["kind"], r["name"][:24], r["path"]))

    saved = bool(bpy.data.filepath)
    return {
        "ok": True,
        "blend_file": bpy.data.filepath or "(unsaved)",
        "saved": saved,
        "external_refs": len(rows),
        "missing": len(missing),
        "absolute": len(absolute),
        "packed": len(packed),
        "libraries": len(bpy.data.libraries),
        "unused": len(unused),
        "missing_names": [r["name"] for r in missing][:limit],
        "absolute_names": [r["name"] for r in absolute][:limit],
        "unused_names": unused[:limit],
        "listing": chr(10).join(lines),
        "rows": rows[:limit],
        "truncated": len(rows) > limit,
    }
