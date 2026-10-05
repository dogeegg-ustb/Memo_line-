# dirty matrix

Windows 笔触覆盖范围查看器，基于恢复后的 `recognizer/behavior_recognizer` STRO v1 笔迹格式。
画布像素尺寸、笔刷尺寸与缩放由用户输入，屏幕显示可穿透点击的半透明绿色覆盖层。

## 启动

双击 `Start.cmd`，或直接运行 `publish/win-x64/dirty matrix.exe`。发布版自包含，不需要安装 .NET。
源码构建需要 .NET SDK 10：

```powershell
dotnet run --project './Organizer/dirty matrix/source/DirtyMatrix.csproj' -c Release
# 发布自包含 Windows x64 程序
powershell -NoProfile -ExecutionPolicy Bypass -File './Organizer/dirty matrix/Build.ps1'
# 运行计算与文件兼容性检查
dotnet run --project './Organizer/dirty matrix/checks/DirtyMatrix.Checks.csproj' -c Release
```

项目直接链接 BehaviorRecognizer 的 `StrokeModels.cs` 与 `StrokeBinaryReader.cs`，没有复制修改采集器，也不依赖重新编译 OTD。

## 使用

1. 输入画布像素宽高、CSP 笔刷**直径**、单位、画布 DPI 和缩放**百分比**，按“应用参数”。
2. 在“画布位置与可见区域”里填写已知画布点的坐标，再点击它在屏幕上的位置。默认点 `(0,0)` 是完整画布左上角；若它在屏幕外，可用另一个已知点校准。
3. 框选 CSP 可见绘图区，排除面板和工具栏。屏幕原点及映射使用物理像素，支持高 DPI 和负坐标的副屏。
4. 按 `F9` 开始／暂停屏幕输入监视，切回 CSP 绘画。`F8` 清空，`F10` 隐藏／显示覆盖层。热键被占用时可使用按钮。
5. 可导出每笔画布包围盒、接触线段与稀疏 dirty matrix。默认沿笔迹显示笔刷覆盖的并集，也可显示被触及的矩阵单元。

缩放、平移和移动 CSP 窗口后，更新比例／画布原点／可见绘图区。已有范围保存在画布坐标中，会随更新的显示参数重新投影。
改变笔刷／AA／安全参数或覆盖额外扩展只影响后续输入的笔划；改变画布像素宽高会清空旧覆盖。参数保存到程序旁的 `dirty-matrix-settings.json`。
“覆盖额外扩展”默认在轨迹两侧各增加 **12 屏幕 px**，可调大或设为 0；这项余量同时用于覆盖层和矩阵。
默认只按框选绘图区限制显示，不把根据画布宽高推算的屏幕矩形当作硬边界。
矩阵会包含所有记录到的轨迹范围，支持超出标称画布宽高以及负画布坐标。
“按画布宽高裁剪覆盖”可恢复严格的画布边界限制，适用于宽高、缩放及原点均已准确校准的场景。

## CSP 的笔刷尺寸换算

已核对 CSP 官方文档：

- [Preferences → Ruler/Unit](https://help.clip-studio.com/en-us/manual_en/720_preferences/Preferences.htm)：长度单位可以设为 **px 或 mm**，笔刷尺寸使用这个单位。
- [Brush size](https://help.clip-studio.com/en-us/manual_en/810_subtools/B.htm)：笔刷大小可受压力／速度动态影响；**Specify by size on screen** 开启时，笔刷在不同画布缩放下保持与 100% 视图相同的屏幕尺寸。
- [官方性能说明中的画布单位解释](https://support.clip-studio.com/en-us/faq/articles/20210101)：固定画布像素宽高时，单独改变 DPI 不改变像素数量；使用物理单位时像素数量与 DPI 相关。
- [Spraying effect / Stroke](https://help.clip-studio.com/en-us/manual_en/810_subtools/S.htm)：散布粒子及后续笔划设置也会影响实际绘制形状。

因此不能仅因为画布宽高较大就给所有笔刷乘一个画布尺寸系数。本程序按单位作以下几何换算（基于 DPI 定义推导）：

```text
D_canvas = brushSize                         # CSP 单位 px
D_canvas = brushSize_mm × canvasDPI / 25.4    # CSP 单位 mm
z = zoomPercent / 100

R_brush = D_canvas × envelopeFactor / 2
# 若开启“按屏幕尺寸指定”：
R_brush = D_canvas × envelopeFactor / (2 × z)

R_padding = extraCoverage_screenPx / z       # 默认额外扩展 12 屏幕 px
R = R_brush + R_AA + R_safety + R_padding     # 所有 R 均为画布 px
```

例如 300 DPI、1 mm 的普通笔刷，标称直径约 `11.811` 画布 px；50% 缩放时标称屏幕直径约 `5.906` px。
将 AA=2、安全=4 画布 px 和额外扩展=12 屏幕 px 加入后，总扩展半径约 `35.906` 画布 px，屏幕扩展半径约 `17.953` px。
开启“按屏幕尺寸指定”时会先按 `1/z` 调整画布笔刷半径。

覆盖沿相邻接触点的连线生成：每条线段以半径 `R` 扩展为两端为圆形的带状范围，取所有范围的并集。
单点生成圆形范围，悬浮点断开连线，避免跨越抬笔空白。矩阵逐行计算该范围触及的单元，不填满整笔包围盒。
实时输入在抬笔前补入抬笔事件的位置，避免最后一次移动消息与抬笔位置不同时遗漏末段。

导出的每笔总包围盒用于索引，接触点极值为 `[xmin,ymin,xmax,ymax]` 时，采用：

```text
left   = floor(xmin - R)
top    = floor(ymin - R)
right  = floor(xmax + R) + 1
bottom = floor(ymax + R) + 1
```

范围使用左／上包含、右／下不包含的整数像素边界，向外取整覆盖恰好位于整数边界上的末端像素。
屏幕覆盖以 `screen = canvasOrigin + canvas × z` 投影线段，按屏幕像素向外覆盖，再与框选的可见绘图区相交。
矩阵范围默认自动扩展，避免画布参数不准确时在固定横向／纵向位置漏记轨迹。
开启“按画布宽高裁剪覆盖”后，矩阵限制在 `[0,width) × [0,height)`，显示也限制在推算的画布屏幕矩形内。
重叠绿色范围只填充一次，透明度相同。
接触开始后即使轨迹离开可见区域，也继续记录到抬笔，确保跨边界的末段完整。

`R_AA=2`、`R_safety=4` 是**本程序可调的估计初值**，不是 CSP 公布的固定内部参数。
为了保守估计，程序使用完整笔刷半径，不按压力缩小。复杂笔尖、散布、双笔刷、稳定器、后校正、标尺和对称笔迹
无法仅凭原始轨迹与一个尺寸精确恢复，须增加包络系数／余量或提供实际校正后的轨迹。
绿色区域表示沿输入轨迹扩展的笔刷范围估计，不表示所有绿色像素都实际被涂色。
框选的可见区域是显示边界，应涵盖需要观察的完整绘图区。画布宽高、缩放与原点用于坐标映射，仍应与 CSP 当前画布保持一致。

## 笔迹输入

### 直接监视 CSP

单独的消息线程使用 Windows `WH_MOUSE_LL` 接收屏幕笔／鼠标左键轨迹，不占用数位板 HID，不改变 CSP 或驱动。
仅记录 CSP 为前台时、在可见绘图区内开始的接触；按住 Space／Ctrl／Alt 时结束当前采集笔划，避免常见平移／尺寸调节被计入。
鼠标与系统转发的笔消息都支持。某些 Wintab 模式不转发左键消息，此时使用 Recognizer 目录监视。
无法自动识别当前 CSP 工具、撤销、选择区、标尺或画布视图变化；覆盖按输入累计，需要用户清空和校准。
Windows 接口依据：[LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)、[MSLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct)。

### BehaviorRecognizer 文件

“导入笔迹文件”接受 `.strokebin`、`.strokebin.part` 和 Recognizer 导出 JSON。
STRO 的 `x/y` 是 **PreTransform 原始数位板坐标**，文件未包含完整画布映射参数。
选择“数位板原始坐标”，输入录制时的实际有效原始范围及驱动映射屏幕，再勾选确认。默认 32767 仅为占位值。
不使用单笔轨迹极值自动适配画布，那会改变原始笔迹的绝对位置和大小。

```text
screenX = mappedLeft + (rawX - rawLeft) / rawWidth × mappedWidth
screenY = mappedTop  + (rawY - rawTop) / rawHeight × mappedHeight
canvasX = (screenX - canvasOriginX) / z
canvasY = (screenY - canvasOriginY) / z
```

此输入映射支持轴对齐的绝对模式。保留长宽比及裁剪需反映在有效原始范围／映射范围中。
相对模式、驱动旋转或其他复杂变换需要先转换成屏幕／画布坐标。历史文件应使用录制时的画布视图参数。

“监视 Recognizer 输出目录”选择运行中的 `procedure/stroke` 目录，每 500 ms 检查最近修改的文件。
只接受开启监视之后**开始**的笔划，跳过历史笔迹。读取共享 `.part` 快照并恢复完整帧，正在写入的截断帧留待下次读取。
相同文件／strokeId 更新去重，`.part` 完成改名后也不会重复；每笔第一次接收时的坐标映射与半径被冻结。
Recognizer 的分段提交通常在抬笔 500 ms 后发生，文件流缓冲还可能增加可见延迟，因此此模式并非逐点实时，长笔划需等待提交。
屏幕监视和目录监视不能同时开启，避免重复计算。清空时停止目录监视，重新开始可避免旧文件重现。

### 简单 JSON

`examples/canvas-strokes.json` 可直接导入。`coordinateSpace` 可取 `canvas`、`screen` 或 `tablet`；声明优先于窗口的文件坐标类型。
简化格式省略 `inContact` 时默认是接触点。给定 `strokeId` 时同一文件内必须唯一；省略时按数组顺序生成。

```json
{
  "coordinateSpace": "canvas",
  "strokes": [
    { "strokeId": 1, "points": [
      { "x": 120, "y": 180, "inContact": true },
      { "x": 240, "y": 260, "inContact": true }
    ] }
  ]
}
```

## dirty matrix 输出

导出 schema 为 `dirty-matrix/v2`，`coverageMode` 为 `swept-polyline`，包含当前设置、每笔半径／画布包围盒／接触线段 `Segments`、
矩阵宽高、单元尺寸、脏单元数，以及 `rowRuns`。包围盒仅用于索引；行区间由线段扩展范围计算。
每个行区间的 `startColumn` 包含自身，`endColumnExclusive` 不包含自身。
矩阵包含画布坐标中的 `originX`、`originY` 和半开边界 `bounds`；行列索引相对于矩阵原点，保持非负。
原点按单元尺寸对齐，可为负数。单元左上角为 `(originX + column × tileSize, originY + row × tileSize)`。
自动扩展时，矩阵行列数可能超过按标称画布宽高算出的尺寸；严格裁剪时原点为 `(0,0)`，范围为标称画布。
矩阵采用稀疏行区间并集，避免按整张画布分配像素或单元数组；矩阵单元覆盖比沿线扩展范围略大。
最多保留 10000 笔，单个输入文件上限 256 MB。

## 验证范围

`checks` 覆盖 px／mm／DPI 与缩放、屏幕固定尺寸模式、R 叠加、向外取整、边缘裁剪、单点／悬浮、负坐标副屏映射、
稀疏矩阵随机并集及大画布、沿线覆盖与独立逐单元距离计算的随机对照、折线空白区、圆形单点、悬浮断线、
跨画布边界长线与小缩放，以及原 Recognizer 编码器生成的 STRO、打开的共享 `.part`、截断尾帧和实时文件目录读取。
`--smoke <目录>` 还验证抬笔位置补全、跨推算右边界、边界外起笔、负坐标、覆盖加宽、可选画布裁剪、
可见绘图区裁剪和两种显示方式的像素，并输出窗口及覆盖预览。
这些检查验证几何估算与文件兼容性；实际 CSP 笔刷效果仍需按上述余量参数校准。

## 接入画布图层差异流

`../canvas layer watcher` 直接复用本项目的 `source/Core/Coverage.cs`，通过 Recognizer 实时接口取得接触点、视口、当前图层、图层属性、笔刷、颜色和驱动状态。视图切换触发保存、解析完整画布图层后，维护 `after/now` 两个全尺寸快照，以脏矩阵为精细 diff 搜索范围，范围外做低分辨率补查。

每个差异包包含实际变化的 after/now 像素、擦除 mask、差异可视化，以及关联影响矩阵／Recognizer 状态的标签。这是 `dirty-matrix-image-diff/v1` 输出；本项目独立范围预览工具的 `dirty-matrix/v2` 输出仍可使用。快照轮换和文件结构见 [图层监听器说明](../canvas%20layer%20watcher/README.md) 与 [差异包格式](../canvas%20layer%20watcher/DIFF_FORMAT.md)。
