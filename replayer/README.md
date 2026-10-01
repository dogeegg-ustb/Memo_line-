# StrokeReplay

Windows x64 图形界面笔迹复现器。双击 `release/win-x64/StrokeReplay.exe` 打开主窗口，不会启动终端。`source/` 包含完整源码和笔迹格式依赖。

## 使用

1. 在主窗口选择笔迹 JSON，也可以把 JSON 文件拖入窗口。
2. 首次启动时，程序会检测是否已配置数位板驱动配置文件：
   - 若未配置，会自动弹出对话框引导选择。若检测到本机已有高漫配置（`%APPDATA%\GAOMON\data\EKeySetting.dt`），支持一键直接关联。
   - 也支持选择 Wacom 导出的备份文件（`.wacomprefs` / `.wacomxs`）或 XML 配置文件。
   - 配置有效后会自动持久化保存到 `%APPDATA%\StrokeReplay\settings.json`，后续运行将一直沿用。主界面可随时点击“更换配置…”或“清除”。
3. 查看设备、笔划数量、坐标范围及驱动压感曲线状态（支持一键勾选/取消“启用驱动层压感曲线”）；需要时调整压力上限和回放速度。
4. 点击“定位并重放”。CSP 当前 UI 线程窗口会显示为置顶冻结画面。
5. 拖动绿色矩形到目标画布位置，按 Enter 确认重放；按 Esc 取消定位。

矩形按原笔迹宽高比缩放，拖动只改变位置。确认后关闭冻结层，再向 CSP 发送 Windows `PT_PEN` 合成笔输入（若启用了驱动曲线，会自动套用三次贝塞尔或 Wacom TipFeel 软硬度变换）。压力上限默认 16383，速度默认 1 倍；回放过程中可从任务栏恢复主窗口并点击“停止回放”。

JSON 保存的是原始数位板坐标，复现器将接触点包围盒映射到所选矩形。矩形初始最长边约占 CSP 客户区的 60%。JSON 不含画笔实际笔刷宽度，因此最终落笔范围可能略超出矩形边缘。冻结画面是置顶截图覆盖层，不会暂停 CSP 线程。

多显示器下，界面使用 Windows 屏幕坐标；发送给 `InjectSyntheticPointerInput` 前会减去虚拟桌面的左上角坐标。左侧或上方副屏导致的负屏幕坐标也可正确换算。

## 命令行用法

除了图形界面外，也可以通过命令行测试驱动配置或执行回放：

```powershell
# 测试解析驱动配置（高漫/绘王或 Wacom）并输出压感映射表
StrokeReplay test-driver "C:\Users\<用户>\AppData\Roaming\GAOMON\data\EKeySetting.dt"
StrokeReplay test-driver "C:\path\to\backup.wacomprefs"

# 检查笔迹文件
StrokeReplay inspect stroke/20260714_081038.json

# 命令行回放（支持指定 --driver-config 和 --no-driver-curve）
StrokeReplay replay stroke/20260714_081038.strokebin --source 0,0,32767,32767 --target 200,100,1600,900 --pressure-max 16383 --driver-config "C:\path\to\config.dt"
```

## 从源码发布

在本目录运行：

```powershell
dotnet publish .\source\StrokeReplay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\release\win-x64
```

发布文件：`release/win-x64/StrokeReplay.exe`。该版本是自包含 Windows x64 单文件程序。
