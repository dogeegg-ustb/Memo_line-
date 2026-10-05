# DriverReader (数位板驱动配置解析器)

`DriverReader` 是一个独立的跨厂商数位板驱动配置文件解析与硬件/压感特性提取工具，位于 `recognizer/driver_reader`。

共享解析与映射代码已提取到 [`../recognizer_core/driver_reader`](../recognizer_core/driver_reader/README.md)。本目录保留独立 CLI 和 Python 包装器，Recognizer 直接引用核心类库，初始化界面由用户选择需要应用的驱动配置。

本工具能够深入解析各大数位板厂商存放在系统中的驱动配置文件，提取数位板硬件物理有效尺寸（Physical Width/Height）、起笔接触死区（Activation Threshold），以及核心的**驱动层非线性压感映射曲线**（三次贝塞尔样条 Cubic Bézier、Wacom TipFeel 软硬度档位或 Gamma 指数），并将其标准化导出为结构化 JSON 或供程序实时调用。

---

## 核心特性

1. **多厂商格式支持**:
   - **高漫 / 绘王 (Gaomon / Huion)**: 支持 `EKeySetting.dt`、`Setting.json` 等配置，自动解析设备型号节点、`Ctr0X/Y`、`Ctr1X/Y`、`Ctr2X/Y` 贝塞尔控制点与 `Right`/`Bottom` 物理坐标范围。
   - **Wacom 路径 1 (备份配置/XML)**: 支持 `WacomTabletUserDefaults.xml`、`.wacomprefs`、`.wacomxs`、`.prefs` 等文件及 ZIP 归档/解压目录，精确解析 `DeviceName`、`CoordMaxX/Y`、`ClickThreshold`、`TipFeel` 软硬度（档位 $-3 \sim +3$ 转为 Gamma 指数）与 4 节点 `PressureCurve` 贝塞尔参数。
   - **OpenTabletDriver (OTD)**: 支持 `settings.json`，解析活动 Profile 的设备名、绝对模式有效区域尺寸（Width/Height）、起笔压感阈值（TipActivationThreshold）及启用的压感滤镜。
   - **XP-Pen / 友基**: 支持 `config.xml`。

2. **物理坐标系与屏幕坐标系映射提取**:
   - 提取数位板物理活动范围（`Left`, `Top`, `Right`, `Bottom`，单位支持原始计数 Counts、毫米 mm 等）。
   - 提取目标屏幕映射区域（像素边界或归一化比例 $0.0 \sim 1.0$）、目标显示器编号。
   - 提取旋转角度（0°, 90°, 180°, 270°）、锁定宽高比状态（LockAspectRatio）。
   - 自动推导水平与垂直物理到屏幕的缩放比例（$Scale_X, Scale_Y$）及前向/反向变换公式，提供精确的坐标映射计算器。

3. **高精度压感二分求解器**:
   - 针对三次贝塞尔曲线，采用 20 轮高精度二分搜索算法快速反解参数 $t$ 并计算对应的输出压力 $Y$。
   - 单次计算耗时低于微秒级，可直接集成进实时笔迹回放和高频数据处理中。

4. **双重集成方式**:
   - **独立 CLI 命令行程序 (`DriverReader.exe` / `dotnet run`)**: 支持自动发现、终端报告、坐标映射计算器（`map`）、JSON 导出、单点压感求值（`eval`）与采样阶梯表生成（`table`）。
   - **Python 原生绑定 (`driver_reader.py`)**: 包含纯 Python 实现的高性能贝塞尔求解器与双向坐标系映射，便于在识别器（Recognizer）数据处理和机器学习流水线中直接调用。

---

## 命令行用法 (CLI)

编译后的独立可执行文件位于 `publish/DriverReader.exe`。你也可以直接在当前目录使用 `dotnet run` 执行。

### 1. 自动寻找本机驱动配置 (`auto` / `find` / 无参数启动)
自动检测当前系统中已安装并激活的高漫、绘王、Wacom、OpenTabletDriver、XP-Pen 等数位板驱动配置文件，以及桌面/文档/下载中的备份配置。直接双击运行或不带参数执行，会自动进入交互模式：

```bash
DriverReader.exe
# 或
DriverReader.exe find
# 或
DriverReader.exe auto
```

输出标准 JSON 格式：
```bash
DriverReader.exe find --json
```

### 2. 检查与详细报告 (`inspect`)
输入驱动配置文件路径，输出设备型号、硬件物理边界、起笔死区及 $0\% \sim 100\%$ 压感变换测试采样表：

```bash
DriverReader.exe inspect "C:\Users\<User>\AppData\Roaming\GAOMON\data\EKeySetting.dt"
# 或直接传入文件路径
DriverReader.exe "C:\Users\<User>\AppData\Local\OpenTabletDriver\settings.json"
```

### 3. 坐标映射计算与反向还原 (`map`)
输入数位板物理坐标，计算驱动配置在指定屏幕分辨率下的目标映射像素坐标，并进行反向还原校验：

```bash
# 格式: DriverReader.exe map <配置文件> <phys_x> <phys_y> [screen_w] [screen_h]
DriverReader.exe map "C:\Users\<User>\AppData\Roaming\GAOMON\data\EKeySetting.dt" 25400 15875 1920 1080
```

输出示例：
```text
==================================================
        坐标映射计算 (Physical -> Screen)
==================================================
设备名称    : 高漫数位板 (GM001_T223) (Gaomon / Huion)
物理有效区  : [0, 0] → [50800, 31750] (尺寸: 50800 x 31750) (Counts)
参考屏幕尺寸: 1920 x 1080 px
旋转角度    : 0°
保持宽高比  : 否
输入物理坐标: (25400, 15875)
映射屏幕坐标: (960, 540)
反向还原物理: (25400, 15875)
--------------------------------------------------
映射公式:
  Screen_X = [(Physical_X - 0) / 50800 * 1 + 0] * Screen_Width
  Screen_Y = [(Physical_Y - 0) / 31750 * 1 + 0] * Screen_Height
==================================================
```

### 4. 导出为标准 JSON (`export`)
将解析出的驱动特性（含坐标系映射与压感样点）导出为标准格式的 JSON 文件（若不指定输出路径则输出到终端标准输出）：

```bash
DriverReader.exe export "C:\path\to\EKeySetting.dt" tablet_profile.json
```

### 5. 实时单点压力测试求解 (`eval`)
给定一个硬件原始压力采样值，计算其经过驱动压感曲线映射后的实际输出压感：

```bash
# 格式: DriverReader.exe eval <配置文件> <原始压力> [最大压力上限(可选)]
DriverReader.exe eval "C:\path\to\EKeySetting.dt" 3276 16383
```

输出示例：
```text
输入压力: 3276 / 16383 (20%)
输出压力: 10531.3 / 16383 (64.3%)
```

### 6. 导出曲线采样阶梯表 (`table`)
输出制表符分隔（TSV）格式的采样数据，可直接复制到 Excel、Origin、Python Matplotlib 中绘图或对比：

```bash
# 格式: DriverReader.exe table <配置文件> [采样步数]
DriverReader.exe table "C:\path\to\EKeySetting.dt" 20
```

---

## Python 使用指南 (`driver_reader.py`)

在 `recognizer` 下的任何 Python 脚本中均可直接导入使用：

```python
from driver_reader import DriverReader

# 1. 自动寻找本机最推荐的数位板驱动（零配置）
best_profile = DriverReader.find_best()
if best_profile:
    print(f"自动选用设备: {best_profile.device_name} ({best_profile.vendor})")

# 2. 扫描本机所有数位板驱动列表
profiles = DriverReader.auto()
for p in profiles:
    print(f"检测到设备: {p.device_name} ({p.vendor})")
    print(f"压感特性: {p.curve_summary}")
    print(f"物理尺寸: {p.physical_width} x {p.physical_height}")

# 3. 读取指定配置文件
profile = DriverReader.load(r"C:\Users\username\AppData\Roaming\GAOMON\data\EKeySetting.dt")

# 4. 物理坐标系 ↔ 屏幕坐标系映射计算
if profile.coordinate_mapping:
    cm = profile.coordinate_mapping
    print(f"物理区域: {cm.physical_area} ({cm.physical_unit})")
    print(f"旋转角度: {cm.rotation_degrees}°, 锁定比例: {cm.lock_aspect_ratio}")
    
    # 物理坐标 -> 屏幕像素 (1920x1080)
    screen_x, screen_y = cm.physical_to_screen(25400, 15875, default_screen_width=1920, default_screen_height=1080)
    print(f"映射屏幕坐标: ({screen_x}, {screen_y})")
    
    # 屏幕像素 -> 物理坐标
    phys_x, phys_y = cm.screen_to_physical(screen_x, screen_y, default_screen_width=1920, default_screen_height=1080)
    print(f"反向物理坐标: ({phys_x}, {phys_y})")

# 5. 高精度压感校准计算（纯 Python 原生执行，微秒级开销）
raw_hw_pressure = 4096.0  # 假设硬件原始压力
calibrated_pressure = profile.transform_pressure(raw_hw_pressure)
print(f"校准后压感: {calibrated_pressure:.2f}")
```

---

## 导出的 JSON Schema

导出的 JSON 文件具有如下通用结构：

```json
{
  "Vendor": "Gaomon / Huion",
  "DeviceName": "高漫数位板 (GM001_T223)",
  "ConfigPath": "C:\\Users\\...\\EKeySetting.dt",
  "CurveType": "CubicBezier",
  "CurveSummary": "三次贝塞尔控制点: Ctr0(327,409) → Ctr1(2539,12614) → Ctr2(5652,15891)",
  "RecommendedPressureMax": 16383,
  "PhysicalWidth": 50800,
  "PhysicalHeight": 31750,
  "ThresholdRatio": 0,
  "BezierPoints": {
    "P0": { "X": 327, "Y": 409 },
    "P1": { "X": 2539, "Y": 12614 },
    "P2": { "X": 5652, "Y": 15891 },
    "P3": { "X": 16384, "Y": 16384 }
  },
  "CoordinateMapping": {
    "MappingMode": "Absolute",
    "PhysicalArea": { "Left": 0, "Top": 0, "Right": 50800, "Bottom": 31750, "Width": 50800, "Height": 31750 },
    "PhysicalUnit": "Counts",
    "ScreenMapRatio": { "Left": 0, "Top": 0, "Right": 1, "Bottom": 1, "Width": 1, "Height": 1 },
    "ScreenIndex": 0,
    "RotationDegrees": 0,
    "LockAspectRatio": false,
    "FormulaSummary": "Screen_X = [(Physical_X - 0) / 50800 * 1 + 0] * Screen_Width\nScreen_Y = [(Physical_Y - 0) / 31750 * 1 + 0] * Screen_Height"
  },
  "SampleTable": [
    {
      "InputPressure": 0,
      "InputPercent": 0,
      "OutputPressure": 0,
      "OutputPercent": 0
    },
    ...
  ]
}
```

---

## 编译与发布

本项目使用 .NET 8 SDK 构建：

```bash
# Debug 编译
dotnet build DriverReader.csproj

# 发布独立 win-x64 可执行程序
dotnet publish DriverReader.csproj -c Release -r win-x64 --self-contained false -o publish
```
