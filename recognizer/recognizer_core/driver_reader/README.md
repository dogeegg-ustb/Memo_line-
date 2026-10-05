# DriverReader.Core

从原 DriverReader 工具提取的 .NET 8 类库，统一保存在 `recognizer/recognizer_core/driver_reader`。独立 DriverReader CLI 和 Recognizer 均通过 `ProjectReference` 使用这份代码；没有 CLI、窗口、Python 或 HID 设备依赖。程序集仍为 `DriverReader.Core.dll`，命名空间仍为 `DriverReader`。

## 初始化与录制

- 初始化时发现、读取 Gaomon/Huion、Wacom、OpenTabletDriver 的已安装配置；默认不扫描桌面、文档或下载中的备份。
- Recognizer 初始化界面让用户选择配置文件，支持手动选文件和明确关闭驱动映射。不会根据厂商或压力曲线自动应用某一文件。
- 初始化结果保存为 `driverConfiguration` 状态，含配置快照 ID、所选配置、已发现配置、数位板规格、物理屏幕矩形、压力映射状态及正反向仿射矩阵。
- 每个笔点引用配置快照 ID。转换期间只做内存运算，不重新扫描或读取文件。

核心调用示例：

```csharp
using DriverReader;

var session = DriverMappingSession.Initialize(new DriverInitializationOptions
{
    ConfigPath = userSelectedConfigPath,
    Device = new TabletDeviceInfo(deviceId, deviceName, widthMm, heightMm,
        digitizerMaxX, digitizerMaxY, maxPressure),
    Displays = displayTopology,
    ScreenIndexOverride = userSelectedScreenIndex
});
var point = session.Evaluate(deviceId, rawX, rawY, rawPressure, maxPressure);
var snapshot = session.Snapshot;
```

未指定 `ConfigPath` 时只返回候选配置，状态为 `selectionRequired`。调用 `DriverMappingSession.Disabled(...)` 表示用户明确关闭映射。

## 坐标和压力约定

原始笔坐标来自 OTD HID 解析报告。`Counts` 配置直接使用原始计数；`mm` 配置先根据硬件规格 `Width/MaxX`、`Height/MaxY` 换算。屏幕坐标为虚拟桌面的物理像素，允许负原点。

矩阵为行优先 3×3，使用齐次列向量：`screen = PhysicalToScreen × (physicalX, physicalY, 1)`。矩阵表示活动区内的仿射变换；活动区外的输入另做边界饱和，不能把它当作全局可逆变换。正反向矩阵使用同一坐标约定。继承原工具的归一化活动区旋转约定，支持 0/90/180/270 度。

明确的屏幕像素区域直接使用。按屏幕比例保存的配置，在单屏时自动换算；多屏时由用户选择映射屏幕，避免猜测各厂商 `ScreenNum` 的含义。`LockAspectRatio` 保存到快照，转换使用配置中已经保存的有效活动区尺寸。

压力保留原始硬件值；映射结果同时提供原始压力量程下的 `MappedPressure` 和 0–1 的 `NormalizedPressure`。驱动曲线只应用一次，不叠加 Recognizer 默认压力曲线。Recognizer 的接触状态沿用自己的接触阈值，独立于记录的压力映射结果。CSP 更新激活使用 Windows 实际光标位置，驱动映射坐标另外保存为 `screenX/Y`，避免配置差异影响点击和拖拽监听。

相对模式、非直角旋转、缺少单位换算规格、无法重建的 OTD 压力滤镜等返回明确的不可用状态。Recognizer 保留原始数据，并在坐标不可用时使用 Windows 光标位置；不会伪造映射结果。

## 构建

```powershell
dotnet build recognizer/recognizer_core/driver_reader/DriverReader.Core.csproj -c Release
dotnet test recognizer/Recognizer/tests/BehaviorRecognizer.Tests/BehaviorRecognizer.Tests.csproj -c Release
```
