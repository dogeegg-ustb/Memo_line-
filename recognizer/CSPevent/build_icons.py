"""Manage and build the icon database in csp_strings.sqlite3 for CSPevent.

This provides the icon definitions for Clip Studio Paint layer panel toolbar
and common buttons, enabling image-based template matching and highlight confirmation
when OCR cannot recognize pure icon buttons.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sqlite3

DEFAULT_DATABASE = Path(__file__).with_name("csp_strings.sqlite3")

DEFAULT_ICONS = [
    {
        "icon_id": "layer_new_raster",
        "name_zh": "新建栅格图层",
        "name_tc": "新增圖層 (點陣圖層)",
        "name_ja": "新規ラスターレイヤー",
        "name_en": "New Raster Layer",
        "category": "layer_toolbar",
        "command_hint": "Layer.NewRaster",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..############.."
            "..#..........#.."
            "..#..........#.."
            "..#..........#.."
            "..#..........#.."
            "..#..........#.."
            "..#.......#..#.."
            "..#.......#..#.."
            "..#.....#####..."
            "..#.......#....."
            "..#.......#..#.."
            "..#..........#.."
            "..############.."
            "................"
            "................"
        ),
        "description": "新建栅格图层按钮（矩形图层纸张右下方带加号）",
    },
    {
        "icon_id": "layer_new_vector",
        "name_zh": "新建矢量图层",
        "name_tc": "新增向量圖層",
        "name_ja": "新規ベクターレイヤー",
        "name_en": "New Vector Layer",
        "category": "layer_toolbar",
        "command_hint": "Layer.NewVector",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..############.."
            "..#..........#.."
            "..#..#....#..#.."
            "..#..#....#..#.."
            "..#...#..#...#.."
            "..#...#..#...#.."
            "..#....##....#.."
            "..#....##....#.."
            "..#....##....#.."
            "..#.....#....#.."
            "..#..........#.."
            "..#..........#.."
            "..############.."
            "................"
            "................"
        ),
        "description": "新建矢量图层按钮（矩形图层纸张中央带V型路径锚点笔尖）",
    },
    {
        "icon_id": "layer_new_folder",
        "name_zh": "新建图层组",
        "name_tc": "新增圖層資料夾",
        "name_ja": "新規レイヤーフォルダー",
        "name_en": "New Layer Folder",
        "category": "layer_toolbar",
        "command_hint": "Layer.NewFolder",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..#####........."
            "..#...#........."
            "..#...########.."
            "..#..........#.."
            "..#....#.....#.."
            "..#....#.....#.."
            "..#..#####...#.."
            "..#....#.....#.."
            "..#....#.....#.."
            "..#..........#.."
            "..#..........#.."
            "..############.."
            "................"
            "................"
            "................"
        ),
        "description": "新建图层文件夹按钮（文件夹轮廓中央带加号）",
    },
    {
        "icon_id": "layer_transfer_down",
        "name_zh": "向下转写",
        "name_tc": "向下轉寫",
        "name_ja": "下のレイヤーに転写",
        "name_en": "Transfer to Lower Layer",
        "category": "layer_toolbar",
        "command_hint": "Layer.TransferDown",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..########......"
            "..#......#......"
            "..########......"
            "......#........."
            "......#........."
            ".....###........"
            "......#........."
            "................"
            "....########...."
            "....#......#...."
            "....########...."
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "向下转写按钮（上下两图层中间带向下单箭头指向下层）",
    },
    {
        "icon_id": "layer_merge_down",
        "name_zh": "向下合并",
        "name_tc": "向下結合",
        "name_ja": "下のレイヤーと結合",
        "name_en": "Merge with Layer Below",
        "category": "layer_toolbar",
        "command_hint": "Layer.MergeDown",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "...########....."
            "...#......#....."
            "...#..##..#....."
            "......##........"
            ".....####......."
            "......##........"
            "................"
            "...########....."
            "...#......#....."
            "...########....."
            "................"
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "向下合并按钮（上下两图层压实合并双箭头）",
    },
    {
        "icon_id": "layer_mask",
        "name_zh": "创建图层蒙版",
        "name_tc": "建立圖層蒙版",
        "name_ja": "レイヤーマスクを作成",
        "name_en": "Create Layer Mask",
        "category": "layer_toolbar",
        "command_hint": "Layer.CreateMask",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..############.."
            "..#..........#.."
            "..#....##....#.."
            "..#..######..#.."
            "..#.########.#.."
            "..#.########.#.."
            "..#.########.#.."
            "..#.########.#.."
            "..#..######..#.."
            "..#....##....#.."
            "..#..........#.."
            "..#..........#.."
            "..############.."
            "................"
            "................"
        ),
        "description": "创建图层蒙版按钮（矩形图层中带有中心圆形遮罩蒙版）",
    },
    {
        "icon_id": "layer_apply_mask",
        "name_zh": "套用图层蒙版",
        "name_tc": "套用圖層蒙版",
        "name_ja": "レイヤーマスクを適用",
        "name_en": "Apply Layer Mask",
        "category": "layer_toolbar",
        "command_hint": "Layer.ApplyMask",
        "grid_size": 16,
        "matrix_data": (
            "................"
            ".....######....."
            "...##########..."
            "..############.."
            "..############.."
            "..############.."
            "...##########..."
            ".....######....."
            ".......#........"
            "......###......."
            ".......#........"
            "..############.."
            "..#..........#.."
            "..############.."
            "................"
            "................"
        ),
        "description": "套用图层蒙版按钮（实心蒙版圆向下箭头融入图层矩形）",
    },
    {
        "icon_id": "layer_clip",
        "name_zh": "用下一图层剪裁",
        "name_tc": "用下一圖層剪裁",
        "name_ja": "下のレイヤーでクリッピング",
        "name_en": "Clip at Layer Below",
        "category": "layer_toolbar",
        "command_hint": "Layer.Clip",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "......########.."
            "......#......#.."
            "......#......#.."
            "..#...#......#.."
            "..#...########.."
            "..#............."
            "..#...########.."
            "..#...#......#.."
            "..#...#......#.."
            "..#...#......#.."
            "..#...########.."
            "..#............."
            "..#............."
            "................"
            "................"
        ),
        "description": "用下一图层剪裁按钮（错位图层带有向下向内转折竖直连接指示）",
    },
    {
        "icon_id": "layer_lock",
        "name_zh": "锁定图层",
        "name_tc": "鎖定圖層",
        "name_ja": "レイヤーをロック",
        "name_en": "Lock Layer",
        "category": "layer_toolbar",
        "command_hint": "Layer.Lock",
        "grid_size": 16,
        "matrix_data": (
            "................"
            ".....######....."
            "....#......#...."
            "....#......#...."
            "....#......#...."
            "...##########..."
            "...##########..."
            "...####..####..."
            "...####..####..."
            "...#####.####..."
            "...##########..."
            "...##########..."
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "锁定图层按钮（半圆形锁环与矩形锁体结构）",
    },
    {
        "icon_id": "layer_lock_transparent",
        "name_zh": "锁定透明像素",
        "name_tc": "鎖定透明像素",
        "name_ja": "透明ピクセルをロック",
        "name_en": "Lock Transparent Pixels",
        "category": "layer_toolbar",
        "command_hint": "Layer.LockTransparent",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..##..##..##...."
            "..##..##..##...."
            "....##..##......"
            "....##..##.##..."
            "..##..##..#..#.."
            "..##..##..#..#.."
            "....##...######."
            "....##...######."
            "..##..##.##..##."
            "..##..##.######."
            "....##..########"
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "锁定透明像素按钮（棋盘方格透明底纹与小锁组合）",
    },
    {
        "icon_id": "layer_eye",
        "name_zh": "图层眼睛",
        "name_tc": "顯示/隱藏圖層",
        "name_ja": "レイヤーの表示/非表示",
        "name_en": "Show/Hide Layer",
        "category": "layer_row",
        "command_hint": "Layer.ToggleVisibility",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "................"
            "......####......"
            "...##########..."
            "..####.##.####.."
            ".#####.##.#####."
            ".##############."
            ".##############."
            ".#####.##.#####."
            "..####.##.####.."
            "...##########..."
            "......####......"
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "图层行左侧可见性眼睛图标（橄榄形轮廓与中心眼球）",
    },
    {
        "icon_id": "layer_delete",
        "name_zh": "删除图层",
        "name_tc": "刪除圖層",
        "name_ja": "レイヤーを削除",
        "name_en": "Delete Layer",
        "category": "layer_toolbar",
        "command_hint": "Layer.Delete",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "......####......"
            "...##########..."
            ".....######....."
            "....########...."
            "....#..##..#...."
            "....#..##..#...."
            "....#..##..#...."
            "....#..##..#...."
            "....#..##..#...."
            ".....######....."
            "......####......"
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "删除图层按钮（垃圾桶盖与桶身条纹）",
    },
    {
        "icon_id": "action_undo",
        "name_zh": "撤销",
        "name_tc": "復原",
        "name_ja": "取り消し",
        "name_en": "Undo",
        "category": "action_toolbar",
        "command_hint": "Edit.Undo",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..###..........."
            ".####..........."
            "#########......."
            "############...."
            ".####......##..."
            "..###.......#..."
            "...##.......##.."
            ".............#.."
            ".............#.."
            "............##.."
            "............##.."
            "...........##..."
            ".........###...."
            "###########....."
            "................"
        ),
        "description": "撤销按钮（左上方尖端，顺时针向右下环绕返回箭头）",
    },
    {
        "icon_id": "action_redo",
        "name_zh": "重做",
        "name_tc": "重做",
        "name_ja": "やり直し",
        "name_en": "Redo",
        "category": "action_toolbar",
        "command_hint": "Edit.Redo",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "...........###.."
            "...........####."
            ".......#########"
            "....############"
            "...##......####."
            "...#.......###.."
            "..##.......##..."
            "..#............."
            "..#............."
            "..##............"
            "..##............"
            "...##..........."
            "....###........."
            ".....###########"
            "................"
        ),
        "description": "重做按钮（右上方尖端，逆时针向左下环绕前进箭头）",
    },
    {
        "icon_id": "action_clear",
        "name_zh": "清空画布",
        "name_tc": "清除",
        "name_ja": "消去",
        "name_en": "Clear",
        "category": "action_toolbar",
        "command_hint": "Edit.Clear",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "...##......##..."
            "....##....##...."
            ".....##..##....."
            "......####......"
            ".......##......."
            "......####......"
            ".....##..##....."
            "....##....##...."
            "...##......##..."
            "..############.."
            "..############.."
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "清除画布/选区内容（叉号/橡皮擦清空标识）",
    },
    {
        "icon_id": "action_fill",
        "name_zh": "填充",
        "name_tc": "填色",
        "name_ja": "塗りつぶし",
        "name_en": "Fill",
        "category": "action_toolbar",
        "command_hint": "Edit.Fill",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "......####......"
            ".....######....."
            "....##....##...."
            "...##......##..."
            "..####....####.."
            "..############.."
            "...##########..."
            "....########...."
            ".....######....."
            "......####......"
            "................"
            ".......##......."
            "......####......"
            ".......##......."
            "................"
        ),
        "description": "填充按钮（倾斜颜料桶倒出墨滴）",
    },
    {
        "icon_id": "canvas_flip_h",
        "name_zh": "水平翻转",
        "name_tc": "左右反轉",
        "name_ja": "左右反転",
        "name_en": "Flip Horizontal",
        "category": "canvas_control",
        "command_hint": "View.FlipHorizontal",
        "grid_size": 16,
        "matrix_data": (
            "................"
            ".......##......."
            "......####......"
            ".....######....."
            "....########...."
            "...##########..."
            "..############.."
            ".......##......."
            ".......##......."
            "..############.."
            "...##########..."
            "....########...."
            ".....######....."
            "......####......"
            ".......##......."
            "................"
        ),
        "description": "画布水平翻转按钮（左右相对镜像三角）",
    },
    {
        "icon_id": "canvas_flip_v",
        "name_zh": "垂直翻转",
        "name_tc": "上下反轉",
        "name_ja": "上下反転",
        "name_en": "Flip Vertical",
        "category": "canvas_control",
        "command_hint": "View.FlipVertical",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..#..........#.."
            "..##........##.."
            "..###......###.."
            "..####....####.."
            "..#####..#####.."
            "..############.."
            "..############.."
            "................"
            "................"
            "..############.."
            "..############.."
            "..#####..#####.."
            "..####....####.."
            "..###......###.."
            "................"
        ),
        "description": "画布垂直翻转按钮（上下镜像梯形）",
    },
    {
        "icon_id": "canvas_rotate_reset",
        "name_zh": "重置旋转",
        "name_tc": "重設旋轉",
        "name_ja": "回転のリセット",
        "name_en": "Reset Rotation",
        "category": "canvas_control",
        "command_hint": "View.ResetRotation",
        "grid_size": 16,
        "matrix_data": (
            "................"
            ".....######....."
            "...##########..."
            "..####....####.."
            "..###..##..###.."
            ".###...##...###."
            ".###...##...###."
            ".###...##...###."
            ".###...##...###."
            ".###...##...###."
            "..###..##..###.."
            "..####....####.."
            "...##########..."
            ".....######....."
            "................"
            "................"
        ),
        "description": "重置画布旋转按钮（圆形居中基准指针对齐）",
    },
    {
        "icon_id": "canvas_rotate_left",
        "name_zh": "向左旋转",
        "name_tc": "向左旋轉",
        "name_ja": "左回転",
        "name_en": "Rotate Left",
        "category": "canvas_control",
        "command_hint": "View.RotateLeft",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "......######...."
            "....##########.."
            "...###......###."
            "..###........##."
            ".###...##....##."
            ".###..####...##."
            ".###.######....."
            ".##########....."
            ".###.######....."
            ".###..####......"
            ".###...##......."
            "..###..........."
            "...###......##.."
            "....##########.."
            "......######...."
        ),
        "description": "向左逆时针旋转画布",
    },
    {
        "icon_id": "canvas_rotate_right",
        "name_zh": "向右旋转",
        "name_tc": "向右旋轉",
        "name_ja": "右回転",
        "name_en": "Rotate Right",
        "category": "canvas_control",
        "command_hint": "View.RotateRight",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "....######......"
            "..##########...."
            ".###......###..."
            ".##........###.."
            ".##....##...###."
            ".##...####..###."
            ".....######.###."
            ".....##########."
            ".....######.###."
            "......####..###."
            ".......##...###."
            "...........###.."
            "..##......###..."
            "..##########...."
            "....######......"
        ),
        "description": "向右顺时针旋转画布",
    },
    {
        "icon_id": "view_zoom_in",
        "name_zh": "放大",
        "name_tc": "放大",
        "name_ja": "ズームイン",
        "name_en": "Zoom In",
        "category": "canvas_control",
        "command_hint": "View.ZoomIn",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "....######......"
            "...########....."
            "..##########...."
            "..###.##.###...."
            "..###.##.###...."
            "..##########...."
            "..##########...."
            "..###.##.###...."
            "..###.##.###...."
            "..##########...."
            "...########....."
            "....######.##..."
            "............##.."
            ".............##."
            "..............##"
        ),
        "description": "视图放大放大镜（带加号）",
    },
    {
        "icon_id": "view_zoom_out",
        "name_zh": "缩小",
        "name_tc": "縮小",
        "name_ja": "ズームアウト",
        "name_en": "Zoom Out",
        "category": "canvas_control",
        "command_hint": "View.ZoomOut",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "....######......"
            "...########....."
            "..##########...."
            "..###....###...."
            "..###....###...."
            "..##########...."
            "..##########...."
            "..###....###...."
            "..###....###...."
            "..##########...."
            "...########....."
            "....######.##..."
            "............##.."
            ".............##."
            "..............##"
        ),
        "description": "视图缩小放大镜（带横杠）",
    },
    {
        "icon_id": "view_fit_window",
        "name_zh": "适合屏幕",
        "name_tc": "配合視窗大小",
        "name_ja": "全体表示",
        "name_en": "Fit to Window",
        "category": "canvas_control",
        "command_hint": "View.FitWindow",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..####....####.."
            "..###......###.."
            "..##...##...##.."
            "..#....##....#.."
            ".......##......."
            "..###########..."
            "..###########..."
            ".......##......."
            "..#....##....#.."
            "..##...##...##.."
            "..###......###.."
            "..####....####.."
            "................"
            "................"
            "................"
        ),
        "description": "适合屏幕大小（对角展开全屏适配）",
    },
    {
        "icon_id": "selection_invert",
        "name_zh": "反选",
        "name_tc": "反轉選取範圍",
        "name_ja": "選択範囲を反転",
        "name_en": "Invert Selection",
        "category": "selection_control",
        "command_hint": "Selection.Invert",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..############.."
            "..#..........#.."
            "..#..######..#.."
            "..#..######..#.."
            "..#..######..#.."
            "..#..######..#.."
            "..#..######..#.."
            "..#..######..#.."
            "..#..........#.."
            "..############.."
            "................"
            "................"
            "................"
            "................"
            "................"
        ),
        "description": "反选选择范围（反转选区内外嵌套边框）",
    },
    {
        "icon_id": "selection_deselect",
        "name_zh": "取消选择",
        "name_tc": "取消選取",
        "name_ja": "選択を解除",
        "name_en": "Deselect",
        "category": "selection_control",
        "command_hint": "Selection.Deselect",
        "grid_size": 16,
        "matrix_data": (
            "................"
            "..##..##..##...."
            "..#............#"
            "........##......"
            "...#...####...#."
            "......######...."
            "..#..########..#"
            ".....########..."
            ".....########..."
            "..#..########..#"
            "......######...."
            "...#...####...#."
            "........##......"
            "..#............#"
            "..##..##..##...."
            "................"
        ),
        "description": "取消选区（虚线矩形选框与叉号）",
    },
]


def ensure_icon_schema(db: sqlite3.Connection) -> None:
    db.executescript("""
        CREATE TABLE IF NOT EXISTS icon_templates (
            icon_id TEXT PRIMARY KEY,
            name_zh TEXT NOT NULL,
            name_tc TEXT NOT NULL,
            name_ja TEXT NOT NULL,
            name_en TEXT NOT NULL,
            category TEXT NOT NULL,
            command_hint TEXT,
            grid_size INTEGER NOT NULL,
            matrix_data TEXT NOT NULL,
            description TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_icon_category ON icon_templates(category);
    """)


def populate_icons(db: sqlite3.Connection, icons: list[dict] | None = None) -> int:
    ensure_icon_schema(db)
    icons = icons or DEFAULT_ICONS
    query = """
        INSERT OR REPLACE INTO icon_templates
        (icon_id, name_zh, name_tc, name_ja, name_en, category, command_hint, grid_size, matrix_data, description)
        VALUES (:icon_id, :name_zh, :name_tc, :name_ja, :name_en, :category, :command_hint, :grid_size, :matrix_data, :description)
    """
    db.executemany(query, icons)
    db.commit()
    return len(icons)


def list_icons(db: sqlite3.Connection) -> list[tuple]:
    ensure_icon_schema(db)
    return db.execute("""
        SELECT icon_id, name_zh, name_tc, name_ja, name_en, category, command_hint
        FROM icon_templates
        ORDER BY category, icon_id
    """).fetchall()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, default=DEFAULT_DATABASE)
    parser.add_argument("--list", action="store_true", help="List all icons currently in database")
    args = parser.parse_args()

    if not args.database.exists():
        parser.error(f"Database file not found: {args.database}")

    with sqlite3.connect(args.database) as db:
        if args.list:
            icons = list_icons(db)
            print(f"Total icons: {len(icons)}")
            for row in icons:
                print(f"[{row[5]}] {row[0]}: {row[1]} / {row[2]} / {row[3]} / {row[4]} ({row[6]})")
        else:
            count = populate_icons(db)
            print(f"Successfully integrated {count} icons into {args.database}")


if __name__ == "__main__":
    main()
