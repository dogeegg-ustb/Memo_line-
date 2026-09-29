using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace CSPevent;

internal sealed record IconTemplate(
    string IconId,
    string NameZh,
    string NameTc,
    string NameJa,
    string NameEn,
    string Category,
    string CommandHint,
    int GridSize,
    string MatrixData,
    string Description)
{
    private bool[,]? _mask;
    private double? _mass;

    internal bool[,] Mask
    {
        get
        {
            if (_mask is not null) return _mask;
            var mask = new bool[GridSize, GridSize];
            int index = 0;
            for (int r = 0; r < GridSize; r++)
            {
                for (int c = 0; c < GridSize; c++)
                {
                    if (index < MatrixData.Length)
                    {
                        char ch = MatrixData[index++];
                        mask[r, c] = ch == '#' || ch == '1';
                    }
                }
            }
            _mask = mask;
            return mask;
        }
    }

    internal double Mass
    {
        get
        {
            if (_mass.HasValue) return _mass.Value;
            int count = 0;
            bool[,] m = Mask;
            for (int r = 0; r < GridSize; r++)
                for (int c = 0; c < GridSize; c++)
                    if (m[r, c]) count++;
            _mass = (double)count / (GridSize * GridSize);
            return _mass.Value;
        }
    }

    internal string GetLocalizedName(string language) => language switch
    {
        "chinese_tc" => NameTc,
        "japanese" => NameJa,
        "english" => NameEn,
        _ => NameZh
    };
}

internal sealed class IconCatalog
{
    private readonly List<IconTemplate> _icons = new();
    private readonly string _databasePath;

    internal IReadOnlyList<IconTemplate> Templates => _icons;

    internal IconCatalog(string databasePath)
    {
        _databasePath = databasePath;
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("词条与图标数据库不存在", databasePath);

        EnsureSchemaAndDefaults();
        LoadIcons();
    }

    private void EnsureSchemaAndDefaults()
    {
        byte[] path = Encoding.UTF8.GetBytes(_databasePath + "\0");
        Check(Sqlite.Open(path, out IntPtr db, 2 | 4), db, "打开数据库（读写模式）"); // SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE
        try
        {
            const string createSql = @"
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
                CREATE INDEX IF NOT EXISTS idx_icon_category ON icon_templates(category);";
            byte[] createBytes = Encoding.UTF8.GetBytes(createSql + "\0");
            Check(Sqlite.Exec(db, createBytes, IntPtr.Zero, IntPtr.Zero, out IntPtr error), db, "创建图标表");

            // 同步并更新所有内置高精度图标模板到数据库
            InsertOrUpdateDefaultIcons(db);
        }
        finally
        {
            Sqlite.Close(db);
        }
    }

    private void InsertOrUpdateDefaultIcons(IntPtr db)
    {
        const string insertSql = @"
            INSERT OR REPLACE INTO icon_templates
            (icon_id, name_zh, name_tc, name_ja, name_en, category, command_hint, grid_size, matrix_data, description)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);";
        byte[] insertBytes = Encoding.UTF8.GetBytes(insertSql + "\0");
        Check(Sqlite.Prepare(db, insertBytes, -1, out IntPtr stmt, IntPtr.Zero), db, "准备插入图标模板");

        try
        {
            foreach (IconTemplate icon in GetPredefinedDefaultIcons())
            {
                Sqlite.Reset(stmt);
                Sqlite.ClearBindings(stmt);

                BindText(stmt, 1, icon.IconId);
                BindText(stmt, 2, icon.NameZh);
                BindText(stmt, 3, icon.NameTc);
                BindText(stmt, 4, icon.NameJa);
                BindText(stmt, 5, icon.NameEn);
                BindText(stmt, 6, icon.Category);
                BindText(stmt, 7, icon.CommandHint);
                Sqlite.BindInt(stmt, 8, icon.GridSize);
                BindText(stmt, 9, icon.MatrixData);
                BindText(stmt, 10, icon.Description);

                int status = Sqlite.Step(stmt);
                if (status != 101) // SQLITE_DONE
                    Check(status, db, $"插入预置图标 {icon.IconId}");
            }
        }
        finally
        {
            Sqlite.Finalize(stmt);
        }
    }

    private static void BindText(IntPtr stmt, int index, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
        Sqlite.BindText(stmt, index, utf8, utf8.Length, new IntPtr(-1));
    }

    private void LoadIcons()
    {
        byte[] path = Encoding.UTF8.GetBytes(_databasePath + "\0");
        Check(Sqlite.Open(path, out IntPtr db, 1), db, "打开数据库（只读模式）"); // SQLITE_OPEN_READONLY
        try
        {
            const string query = @"
                SELECT icon_id, name_zh, name_tc, name_ja, name_en,
                       category, command_hint, grid_size, matrix_data, description
                FROM icon_templates;";
            byte[] sql = Encoding.UTF8.GetBytes(query + "\0");
            Check(Sqlite.Prepare(db, sql, -1, out IntPtr stmt, IntPtr.Zero), db, "查询图标库");
            try
            {
                while (Sqlite.Step(stmt) == 100)
                {
                    string iconId = Read(stmt, 0);
                    string nameZh = Read(stmt, 1);
                    string nameTc = Read(stmt, 2);
                    string nameJa = Read(stmt, 3);
                    string nameEn = Read(stmt, 4);
                    string category = Read(stmt, 5);
                    string commandHint = Read(stmt, 6);
                    int gridSize = Sqlite.ColumnInt(stmt, 7);
                    string matrixData = Read(stmt, 8);
                    string description = Read(stmt, 9);

                    _icons.Add(new IconTemplate(
                        iconId, nameZh, nameTc, nameJa, nameEn,
                        category, commandHint, gridSize, matrixData, description));
                }
            }
            finally { Sqlite.Finalize(stmt); }
        }
        finally { Sqlite.Close(db); }
    }

    private static string Read(IntPtr statement, int column) =>
        Marshal.PtrToStringUTF8(Sqlite.ColumnText(statement, column)) ?? "";

    private static void Check(int status, IntPtr db, string operation)
    {
        if (status == 0) return;
        throw new InvalidOperationException($"{operation}失败：{Marshal.PtrToStringUTF8(Sqlite.Error(db))}");
    }

    internal static IEnumerable<IconTemplate> GetPredefinedDefaultIcons()
    {
        // 1. 图层面板系列图标
        yield return new IconTemplate(
            "layer_new_raster",
            "新建栅格图层",
            "新增圖層 (點陣圖層)",
            "新規ラスターレイヤー",
            "New Raster Layer",
            "layer_toolbar",
            "Layer.NewRaster",
            16,
            "................" +
            "..############.." +
            "..#..........#.." +
            "..#..........#.." +
            "..#..........#.." +
            "..#..........#.." +
            "..#..........#.." +
            "..#.......#..#.." +
            "..#.......#..#.." +
            "..#.....#####..." +
            "..#.......#....." +
            "..#.......#..#.." +
            "..#..........#.." +
            "..############.." +
            "................" +
            "................",
            "新建栅格图层（矩形图层纸张右下角带加号）"
        );

        yield return new IconTemplate(
            "layer_new_vector",
            "新建矢量图层",
            "新增向量圖層",
            "新規ベクターレイヤー",
            "New Vector Layer",
            "layer_toolbar",
            "Layer.NewVector",
            16,
            "................" +
            "..############.." +
            "..#..........#.." +
            "..#..#....#..#.." +
            "..#..#....#..#.." +
            "..#...#..#...#.." +
            "..#...#..#...#.." +
            "..#....##....#.." +
            "..#....##....#.." +
            "..#....##....#.." +
            "..#.....#....#.." +
            "..#..........#.." +
            "..#..........#.." +
            "..############.." +
            "................" +
            "................",
            "新建矢量图层（矩形图层纸张中央带V型路径锚点笔尖）"
        );

        yield return new IconTemplate(
            "layer_new_folder",
            "新建图层组",
            "新增圖層資料夾",
            "新規レイヤーフォルダー",
            "New Layer Folder",
            "layer_toolbar",
            "Layer.NewFolder",
            16,
            "................" +
            "..#####........." +
            "..#...#........." +
            "..#...########.." +
            "..#..........#.." +
            "..#....#.....#.." +
            "..#....#.....#.." +
            "..#..#####...#.." +
            "..#....#.....#.." +
            "..#....#.....#.." +
            "..#..........#.." +
            "..#..........#.." +
            "..############.." +
            "................" +
            "................" +
            "................",
            "新建图层文件夹（文件夹形状，中央带加号）"
        );

        yield return new IconTemplate(
            "layer_transfer_down",
            "向下转写",
            "向下轉寫",
            "下のレイヤーに転写",
            "Transfer to Lower Layer",
            "layer_toolbar",
            "Layer.TransferDown",
            16,
            "................" +
            "..########......" +
            "..#......#......" +
            "..########......" +
            "......#........." +
            "......#........." +
            ".....###........" +
            "......#........." +
            "................" +
            "....########...." +
            "....#......#...." +
            "....########...." +
            "................" +
            "................" +
            "................" +
            "................",
            "向下转写（上下两图层中间带向下单箭头指向下层）"
        );

        yield return new IconTemplate(
            "layer_merge_down",
            "向下合并",
            "向下結合",
            "下のレイヤーと結合",
            "Merge with Layer Below",
            "layer_toolbar",
            "Layer.MergeDown",
            16,
            "................" +
            "...########....." +
            "...#......#....." +
            "...#..##..#....." +
            "......##........" +
            ".....####......." +
            "......##........" +
            "................" +
            "...########....." +
            "...#......#....." +
            "...########....." +
            "................" +
            "................" +
            "................" +
            "................" +
            "................",
            "向下合并（上下图层结合，向下合并双箭头）"
        );

        yield return new IconTemplate(
            "layer_mask",
            "创建图层蒙版",
            "建立圖層蒙版",
            "レイヤーマスクを作成",
            "Create Layer Mask",
            "layer_toolbar",
            "Layer.CreateMask",
            16,
            "................" +
            "..############.." +
            "..#..........#.." +
            "..#....##....#.." +
            "..#..######..#.." +
            "..#.########.#.." +
            "..#.########.#.." +
            "..#.########.#.." +
            "..#.########.#.." +
            "..#..######..#.." +
            "..#....##....#.." +
            "..#..........#.." +
            "..#..........#.." +
            "..############.." +
            "................" +
            "................",
            "创建图层蒙版（矩形图层中带有圆形蒙版遮罩）"
        );

        yield return new IconTemplate(
            "layer_apply_mask",
            "套用图层蒙版",
            "套用圖層蒙版",
            "レイヤーマスクを適用",
            "Apply Layer Mask",
            "layer_toolbar",
            "Layer.ApplyMask",
            16,
            "................" +
            ".....######....." +
            "...##########..." +
            "..############.." +
            "..############.." +
            "..############.." +
            "...##########..." +
            ".....######....." +
            ".......#........" +
            "......###......." +
            ".......#........" +
            "..############.." +
            "..#..........#.." +
            "..############.." +
            "................" +
            "................",
            "套用图层蒙版（实心蒙版圆向下箭头融入图层矩形）"
        );

        yield return new IconTemplate(
            "layer_clip",
            "用下一图层剪裁",
            "用下一圖層剪裁",
            "下のレイヤーでクリッピング",
            "Clip at Layer Below",
            "layer_toolbar",
            "Layer.Clip",
            16,
            "................" +
            "......########.." +
            "......#......#.." +
            "......#......#.." +
            "..#...#......#.." +
            "..#...########.." +
            "..#............." +
            "..#...########.." +
            "..#...#......#.." +
            "..#...#......#.." +
            "..#...#......#.." +
            "..#...########.." +
            "..#............." +
            "..#............." +
            "................" +
            "................",
            "用下一图层剪裁（两个错开的图层，带有向内折角剪裁连接线）"
        );

        yield return new IconTemplate(
            "layer_lock",
            "锁定图层",
            "鎖定圖層",
            "レイヤーをロック",
            "Lock Layer",
            "layer_toolbar",
            "Layer.Lock",
            16,
            "................" +
            ".....######....." +
            "....#......#...." +
            "....#......#...." +
            "....#......#...." +
            "...##########..." +
            "...##########..." +
            "...####..####..." +
            "...####..####..." +
            "...#####.####..." +
            "...##########..." +
            "...##########..." +
            "................" +
            "................" +
            "................" +
            "................",
            "锁定图层（挂锁外形，半圆锁环与方体锁身）"
        );

        yield return new IconTemplate(
            "layer_lock_transparent",
            "锁定透明像素",
            "鎖定透明像素",
            "透明ピクセルをロック",
            "Lock Transparent Pixels",
            "layer_toolbar",
            "Layer.LockTransparent",
            16,
            "................" +
            "..##..##..##...." +
            "..##..##..##...." +
            "....##..##......" +
            "....##..##.##..." +
            "..##..##..#..#.." +
            "..##..##..#..#.." +
            "....##...######." +
            "....##...######." +
            "..##..##.##..##." +
            "..##..##.######." +
            "....##..########" +
            "................" +
            "................" +
            "................" +
            "................",
            "锁定透明像素（棋盘透明网格与小锁组合）"
        );

        yield return new IconTemplate(
            "layer_eye",
            "图层眼睛",
            "顯示/隱藏圖層",
            "レイヤーの表示/非表示",
            "Show/Hide Layer",
            "layer_row",
            "Layer.ToggleVisibility",
            16,
            "................" +
            "................" +
            "......####......" +
            "...##########..." +
            "..####.##.####.." +
            ".#####.##.#####." +
            ".##############." +
            ".##############." +
            ".#####.##.#####." +
            "..####.##.####.." +
            "...##########..." +
            "......####......" +
            "................" +
            "................" +
            "................" +
            "................",
            "图层眼睛可见性（眼睛轮廓与中心瞳孔）"
        );

        yield return new IconTemplate(
            "layer_delete",
            "删除图层",
            "刪除圖層",
            "レイヤーを削除",
            "Delete Layer",
            "layer_toolbar",
            "Layer.Delete",
            16,
            "................" +
            "......####......" +
            "...##########..." +
            ".....######....." +
            "....########...." +
            "....#..##..#...." +
            "....#..##..#...." +
            "....#..##..#...." +
            "....#..##..#...." +
            "....#..##..#...." +
            ".....######....." +
            "......####......" +
            "................" +
            "................" +
            "................" +
            "................",
            "删除图层（垃圾桶外形）"
        );

        // 2. 主工具栏常用动作按钮（实机高精度像素校准）
        yield return new IconTemplate(
            "action_undo",
            "撤销",
            "復原",
            "取り消し",
            "Undo",
            "action_toolbar",
            "Edit.Undo",
            16,
            "................" +
            "..###..........." +
            ".####..........." +
            "#########......." +
            "############...." +
            ".####......##..." +
            "..###.......#..." +
            "...##.......##.." +
            ".............#.." +
            ".............#.." +
            "............##.." +
            "............##.." +
            "...........##..." +
            ".........###...." +
            "###########....." +
            "................",
            "撤销按钮（左上方尖端，顺时针向右下环绕返回箭头）"
        );

        yield return new IconTemplate(
            "action_redo",
            "重做",
            "重做",
            "やり直し",
            "Redo",
            "action_toolbar",
            "Edit.Redo",
            16,
            "................" +
            "...........###.." +
            "...........####." +
            ".......#########" +
            "....############" +
            "...##......####." +
            "...#.......###.." +
            "..##.......##..." +
            "..#............." +
            "..#............." +
            "..##............" +
            "..##............" +
            "...##..........." +
            "....###........." +
            ".....###########" +
            "................",
            "重做按钮（右上方尖端，逆时针向左下环绕前进箭头）"
        );

        yield return new IconTemplate(
            "action_clear",
            "清空画布",
            "清除",
            "消去",
            "Clear",
            "action_toolbar",
            "Edit.Clear",
            16,
            "................" +
            "...##......##..." +
            "....##....##...." +
            ".....##..##....." +
            "......####......" +
            ".......##......." +
            "......####......" +
            ".....##..##....." +
            "....##....##...." +
            "...##......##..." +
            "..############.." +
            "..############.." +
            "................" +
            "................" +
            "................" +
            "................",
            "清除画布/选区内容（叉号/橡皮擦清空标识）"
        );

        yield return new IconTemplate(
            "action_fill",
            "填充",
            "填色",
            "塗りつぶし",
            "Fill",
            "action_toolbar",
            "Edit.Fill",
            16,
            "................" +
            "......####......" +
            ".....######....." +
            "....##....##...." +
            "...##......##..." +
            "..####....####.." +
            "..############.." +
            "...##########..." +
            "....########...." +
            ".....######....." +
            "......####......" +
            "................" +
            ".......##......." +
            "......####......" +
            ".......##......." +
            "................",
            "填充按钮（倾斜颜料桶倒出墨滴）"
        );

        // 3. 画布与视图控制图标
        yield return new IconTemplate(
            "canvas_flip_h",
            "水平翻转",
            "左右反轉",
            "左右反転",
            "Flip Horizontal",
            "canvas_control",
            "View.FlipHorizontal",
            16,
            "................" +
            ".......##......." +
            "......####......" +
            ".....######....." +
            "....########...." +
            "...##########..." +
            "..############.." +
            ".......##......." +
            ".......##......." +
            "..############.." +
            "...##########..." +
            "....########...." +
            ".....######....." +
            "......####......" +
            ".......##......." +
            "................",
            "画布水平翻转按钮（左右相对镜像三角）"
        );

        yield return new IconTemplate(
            "canvas_flip_v",
            "垂直翻转",
            "上下反轉",
            "上下反転",
            "Flip Vertical",
            "canvas_control",
            "View.FlipVertical",
            16,
            "................" +
            "..#..........#.." +
            "..##........##.." +
            "..###......###.." +
            "..####....####.." +
            "..#####..#####.." +
            "..############.." +
            "..############.." +
            "................" +
            "................" +
            "..############.." +
            "..############.." +
            "..#####..#####.." +
            "..####....####.." +
            "..###......###.." +
            "................",
            "画布垂直翻转按钮（上下镜像梯形）"
        );

        yield return new IconTemplate(
            "canvas_rotate_reset",
            "重置旋转",
            "重設旋轉",
            "回転のリセット",
            "Reset Rotation",
            "canvas_control",
            "View.ResetRotation",
            16,
            "................" +
            ".....######....." +
            "...##########..." +
            "..####....####.." +
            "..###..##..###.." +
            ".###...##...###." +
            ".###...##...###." +
            ".###...##...###." +
            ".###...##...###." +
            ".###...##...###." +
            "..###..##..###.." +
            "..####....####.." +
            "...##########..." +
            ".....######....." +
            "................" +
            "................",
            "重置画布旋转按钮（圆形居中基准指针对齐）"
        );

        yield return new IconTemplate(
            "canvas_rotate_left",
            "向左旋转",
            "向左旋轉",
            "左回転",
            "Rotate Left",
            "canvas_control",
            "View.RotateLeft",
            16,
            "................" +
            "......######...." +
            "....##########.." +
            "...###......###." +
            "..###........##." +
            ".###...##....##." +
            ".###..####...##." +
            ".###.######....." +
            ".##########....." +
            ".###.######....." +
            ".###..####......" +
            ".###...##......." +
            "..###..........." +
            "...###......##.." +
            "....##########.." +
            "......######...." ,
            "向左逆时针旋转画布"
        );

        yield return new IconTemplate(
            "canvas_rotate_right",
            "向右旋转",
            "向右旋轉",
            "右回転",
            "Rotate Right",
            "canvas_control",
            "View.RotateRight",
            16,
            "................" +
            "....######......" +
            "..##########...." +
            ".###......###..." +
            ".##........###.." +
            ".##....##...###." +
            ".##...####..###." +
            ".....######.###." +
            ".....##########." +
            ".....######.###." +
            "......####..###." +
            ".......##...###." +
            "...........###.." +
            "..##......###..." +
            "..##########...." +
            "....######......",
            "向右顺时针旋转画布"
        );

        yield return new IconTemplate(
            "view_zoom_in",
            "放大",
            "放大",
            "ズームイン",
            "Zoom In",
            "canvas_control",
            "View.ZoomIn",
            16,
            "................" +
            "....######......" +
            "...########....." +
            "..##########...." +
            "..###.##.###...." +
            "..###.##.###...." +
            "..##########...." +
            "..##########...." +
            "..###.##.###...." +
            "..###.##.###...." +
            "..##########...." +
            "...########....." +
            "....######.##..." +
            "............##.." +
            ".............##." +
            "..............##",
            "视图放大放大镜（带加号）"
        );

        yield return new IconTemplate(
            "view_zoom_out",
            "缩小",
            "縮小",
            "ズームアウト",
            "Zoom Out",
            "canvas_control",
            "View.ZoomOut",
            16,
            "................" +
            "....######......" +
            "...########....." +
            "..##########...." +
            "..###....###...." +
            "..###....###...." +
            "..##########...." +
            "..##########...." +
            "..###....###...." +
            "..###....###...." +
            "..##########...." +
            "...########....." +
            "....######.##..." +
            "............##.." +
            ".............##." +
            "..............##",
            "视图缩小放大镜（带横杠）"
        );

        yield return new IconTemplate(
            "view_fit_window",
            "适合屏幕",
            "配合視窗大小",
            "全体表示",
            "Fit to Window",
            "canvas_control",
            "View.FitWindow",
            16,
            "................" +
            "..####....####.." +
            "..###......###.." +
            "..##...##...##.." +
            "..#....##....#.." +
            ".......##......." +
            "..###########..." +
            "..###########..." +
            ".......##......." +
            "..#....##....#.." +
            "..##...##...##.." +
            "..###......###.." +
            "..####....####.." +
            "................" +
            "................" +
            "................",
            "适合屏幕大小（对角展开全屏适配）"
        );

        // 4. 选区控制图标
        yield return new IconTemplate(
            "selection_invert",
            "反选",
            "反轉選取範圍",
            "選択範囲を反転",
            "Invert Selection",
            "selection_control",
            "Selection.Invert",
            16,
            "................" +
            "..############.." +
            "..#..........#.." +
            "..#..######..#.." +
            "..#..######..#.." +
            "..#..######..#.." +
            "..#..######..#.." +
            "..#..######..#.." +
            "..#..######..#.." +
            "..#..........#.." +
            "..############.." +
            "................" +
            "................" +
            "................" +
            "................" +
            "................",
            "反选选择范围（反转选区内外嵌套边框）"
        );

        yield return new IconTemplate(
            "selection_deselect",
            "取消选择",
            "取消選取",
            "選択を解除",
            "Deselect",
            "selection_control",
            "Selection.Deselect",
            16,
            "................" +
            "..##..##..##...." +
            "..#............#" +
            "........##......" +
            "...#...####...#." +
            "......######...." +
            "..#..########..#" +
            ".....########..." +
            "........##......" +
            "..#............#" +
            "........##......" +
            "....#........#.." +
            "......##..##...." +
            "................" +
            "................" +
            "................",
            "取消选择选区（虚线方框取消标识）"
        );
    }

    private static class Sqlite
    {
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Open(byte[] name, out IntPtr db, int flags, IntPtr vfs = default);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(IntPtr db);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Prepare(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BindText(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_int", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BindInt(IntPtr statement, int index, int value);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Step(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_reset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Reset(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_clear_bindings", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ClearBindings(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ColumnText(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ColumnInt(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Error(IntPtr db);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
    }
}
