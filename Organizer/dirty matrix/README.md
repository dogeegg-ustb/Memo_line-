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
5. 可导出每笔画布包围盒与稀疏 dirty matrix。默认显示笔划包围盒的并集，也可显示被触及的矩阵单元。

缩放、平移和移动 CSP 窗口后，更新比例／画布原点／可见绘图区。已有范围保存在画布坐标中，会随更新的显示参数重新投影。
改变笔刷／AA／安全参数只影响后续输入的笔划；改变画布像素宽高会清空旧覆盖。参数保存到程序旁的 `dirty-matrix-settings.json`。

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

R = R_brush + R_AA + R_safety                 # 所有 R 均为画布 px
```

例如 300 DPI、1 mm 的普通笔刷，标称直径约 `11.811` 画布 px；50% 缩放时标称屏幕直径约 `5.906` px。
将 AA=2、安全=4 画布 px 加入后，总扩展半径约 `11.906` 画布 px，屏幕扩展半径约 `5.953` px。
开启“按屏幕尺寸指定”时会先按 `1/z` 调整画布笔刷半径。

接触点轨迹包围盒为 `[xmin,ymin,xmax,ymax]`，扩展范围采用：

```text
left   = floor(xmin - R)
top    = floor(ymin - R)
right  = floor(xmax + R) + 1
bottom = floor(ymax + R) + 1
```

范围使用左／上包含、右／下不包含的整数像素边界，向外取整覆盖恰好位于整数边界上的末端像素。
先与画布 `[0,width) × [0,height)` 相交，再以 `screen = canvasOrigin + canvas × z` 投影，最后与可见绘图区相交。
悬浮点不贡献范围；单点笔划仍有半径；重叠绿色范围只填充一次，透明度相同。

`R_AA=2`、`R_safety=4` 是**本程序可调的估计初值**，不是 CSP 公布的固定内部参数。
为了保守估计，程序使用完整笔刷半径，不按压力缩小。复杂笔尖、散布、双笔刷、稳定器、后校正、标尺和对称笔迹
无法仅凭原始轨迹与一个尺寸精确恢复，须增加包络系数／余量或提供实际校正后的轨迹。
绿色区域表示扩展的轴对齐包围盒，不表示所有绿色像素都实际被涂色。

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

导出 schema 为 `dirty-matrix/v1`，包含当前设置、每笔半径／画布包围盒、矩阵宽高、单元尺寸、脏单元数，以及 `rowRuns`。
每个行区间的 `startColumn` 包含自身，`endColumnExclusive` 不包含自身。
矩阵采用稀疏行区间并集，避免按整张画布分配像素或单元数组；矩阵单元覆盖比精确包围盒略大。
最多保留 10000 笔，单个输入文件上限 256 MB。

## 验证范围

`checks` 覆盖 px／mm／DPI 与缩放、屏幕固定尺寸模式、R 叠加、向外取整、边缘裁剪、单点／悬浮、负坐标副屏映射、
稀疏矩阵随机并集及大画布，以及原 Recognizer 编码器生成的 STRO、打开的共享 `.part`、截断尾帧和实时文件目录读取。
这些检查验证几何估算与文件兼容性；实际 CSP 笔刷效果仍需按上述余量参数校准。
