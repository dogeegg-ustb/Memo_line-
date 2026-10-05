# StrokeReplay

Windows x64 memoline 复现器，负责两件事：按历史笔划恢复 CSP 画布视图，以及回放数位板笔点。历史键盘、鼠标、画笔、色彩、图层和侧键操作不回放；运行前请准备好目标文档与画笔。

## 使用

1. 打开 CSP，并让 Recognizer 完成当前画布初始化，保持录制会话运行。
2. 双击 `release/win-x64/StrokeReplay.exe`，选择 Recognizer 生成的 `.memoline` 文件；也可以拖入文件。
3. 复现器启动后持续连接 Recognizer 的实时接口。留空自动寻找当前运行会话，也可填入控制台给出的 `.memoline.live.json` 路径或 `MemoLine.Recognizer.<sessionId>` 管道名，再点击“连接 / 自动寻找”。
4. 确认界面显示当前已识别的视图，选择集中/离散模式并调整回放速度，点击“恢复视图并重放”。
5. 每笔开始前，复现器在当前 Recognizer 返回的精确数字屏幕位置填写历史缩放与旋转，再以空格加鼠标左键拖动恢复画布原点。当前已确认视图符合目标时直接继续；输入过程中收到完整目标匹配结果时也直接继续，否则等待后续有效结果，之后才发送 Windows `PT_PEN` 笔事件。

停止按钮、关闭窗口或命令行 Ctrl+C 均会取消回放并释放合成笔；视图拖动发生取消时也会释放鼠标左键与空格。若 CSP 失去前台焦点、输入区域被其他窗口遮挡、Recognizer 断开或确认超时，回放停止。内置当前会话状态表，保存最近已确认的缩放、旋转、原点、视口和数字位置；接口有效结果替换状态表，中间未确认结果不清空它。状态表符合目标时立即继续，无需新更新；无可用状态或输入后的目标未确认时等待后续有效结果；视图恢复根据下一次已确认状态继续修正，笔内等待时间不计入重放计时。笔内恢复出的已确认视图若与该笔目标不同，停止绘制。监听器会继续尝试重连；正常录制结束后，自动发现模式会等待下一个会话。

## 集中模式与离散模式

- **集中模式**：保留原重放方式，仍是默认模式；笔间立即继续，抬笔保持 `INRANGE | UP`，随后释放该笔的合成设备。
- **离散模式**：每次接触结束发送零压力 `UP`，清除 `INRANGE`，明确结束笔的检测状态；设备保持到空闲等待完成后再释放。下一次按下重新开始笔的生命周期。每笔结束后，根据 memoline 记录的「下一笔开始时间 − 当前笔结束时间」等待，默认最多 **200 ms**：原间隔 80 ms 就等 80 ms，原间隔 2 秒就等 200 ms。界面可将上限调低至 **0–200 ms**；分隔时间不受回放倍速影响，最后一笔不额外等待。

CSP 官方手册的 [性能 → 撤销](https://help.clip-studio.com/en-us/manual_en/720_preferences/Preferences.htm) 说明，同一工具连续快速操作时，CSP 根据无操作持续时间与“撤销间隔”划分撤销记录。当前程序原本每笔已经发送 DOWN/UP，因此“一次撤销很多笔”与 CSP 的快速操作合并规则相符，但尚未在当前 CSP 实例中实测确认原因。离散模式结合明确离开检测范围和空闲等待进行分笔；200 ms 是笔间等待上限，不是 CSP 默认撤销阈值。Windows [指针标志文档](https://learn.microsoft.com/en-us/windows/win32/inputmsg/pointer-flags-contants) 规定 `UP` 表示结束接触，未设置 `INRANGE` 表示离开检测范围。

如果 CSP 仍合并多笔撤销，可以把 CSP「文件 → 环境设置 → 性能 → 撤销间隔」设为 0。程序不自动修改 CSP 的全局偏好。视图恢复产生的操作仍由 CSP 自己决定是否进入撤销历史。

模式和间隔上限保存到原设置文件；旧设置中的 1 秒自动限制到 200 ms。停止或视图检查失败时立即释放接触与设备，不为撤销分隔继续等待。模拟检查验证了事件顺序、空闲期间设备存活、录制时钟换算、短间隔保持、长间隔截断、最终笔无等待、缺失抬笔与取消清理；CSP 实际撤销栈仍需实机验证。

## 数据与接口

源码直接引用 `recognizer/core/memoline/Memoline.Core.csproj`，复用 Recognizer 的 `MemolineReader` 和 `RecorderRealtimeClient`。实时订阅 `core.canvasViewState` 与 `tablet.metadata`（旧版 Recognizer 自动回退到 `tablet`），同时保存当前驱动与设备快照。复现器不运行额外的 OCR、HID 监听或 Recognizer 进程。

- 文件支持 MEMOLINE v1/v2；压缩、CRC 和完整帧验证由共享读取器完成。`.memoline.part` 可读取完整且已经结束的笔操作，尚未抬笔的操作会报错。
- 历史状态按 `statePackageReserved.afterEventId` 的因果位置解析，匹配 `statePackageResult` 中的 `canvasViewState`。异步追加的结果不会被当作录制时刻的新状态。其他解析核心的错误不影响仅需画布视图的重放。
- 当前缩放/旋转输入区域来自 Recognizer 的 OCR 布局；使用实时 `rawResult.ocrLayout.scaleDigitsScreen` 与 `rotationDigitsScreen` 的矩形中心，点击后 Ctrl+A、输入数值、回车；坐标已经是物理屏幕像素，不再加截图偏移或 DPI 缩放。需要修正的数字位置缺失时停止，不从 `numbersRoi` 或 OCR slot 推算。画布尺寸来自当前初始化与历史状态，两者须一致。改变窗口布局后需重新初始化 Recognizer。
- 为排除修改前已在队列中的 OCR，输入开始前使用接口提供的 `originTicks` 与系统 Stopwatch 时钟建立屏障，保留输入过程中已经返回的确认结果；旧接口从录制文件读取时钟。实时状态中的 `causalAmbiguous` 只表示与触发操作的时序归属不确定，不拒绝核心已确认的当前视图。界面显示消息序号、取证到结果耗时和接口接收耗时，便于区分解析和传输延迟。数值提交后在导航器数字区域触发一次新的解析，即使 Enter 没有快捷键映射也会刷新。
- 笔点使用记录的 Windows `x/y`，缺失时使用 `screenX/screenY`；原始 `tabletX/tabletY` 不能直接作为屏幕像素。保留采样时间与倾斜。压力优先使用 `normalizedPressure`；缺失时使用 `mappedPressure / maxPressure`，再回退到原始 `pressure / maxPressure`。`mappedPressure` 仍是设备压力量程中的值，即使小于 1 也不能当成归一化值；不重复套用驱动曲线。
- 按住空格或 R 的数位板操作视为画布导航并跳过，结合笔点 `heldKeys` 和独立 `keyboardStateChanged` 键盘状态判定；笔内任意时刻进入导航状态都会跳过整笔，释放导航键后的绘画正常保留；历史画布视口外开始的笔操作也跳过。中断的笔操作在最后测得位置抬笔。历史画布视图无效、未知、歧义或尚未确认时，整笔操作直接跳过，直到后续出现有效的已确认视图；界面显示跳过数量。有效视图下的笔操作缺少屏幕坐标或足够压力数据时仍拒绝回放。所有操作都被跳过时提示没有可重放笔操作。
- 实时视图仅在 Recognizer 原有触发规则完成识别后更新，其速度受 OCR 和截图稳定检查影响。回放恢复视图期间暂停笔点计时；笔内采样间隔按指定速度缩放；集中模式忽略原录制笔间时间，一笔结束后立即恢复下一笔视图并重放；离散模式保留不超过 200 ms 的录制笔间时间，再继续下一笔。离散模式中同一操作内若出现多次接触，也按录制的抬笔到下一次接触间隔处理，不重复等待原始悬停时长。视图恢复与 Recognizer 确认所需的时间独立于笔间等待上限。

屏幕坐标支持负坐标副屏，笔点须落在当前画布视口和虚拟桌面内。Windows 被动笔采集缺少压力时不能用于该数位板重放流程。

## 命令行

```powershell
# 检查历史文件，不连接 CSP 或注入输入
.\release\win-x64\StrokeReplay.exe inspect 'D:\notes\session.memoline'

# 持续监听当前 Recognizer（留空自动寻找）
.\release\win-x64\StrokeReplay.exe listen --endpoint 'D:\recorder\procedure\stroke\current.memoline.live.json'

# 只解析预览
.\release\win-x64\StrokeReplay.exe replay 'D:\notes\session.memoline' --speed 2

# 恢复视图并回放（默认集中模式）
.\release\win-x64\StrokeReplay.exe replay 'D:\notes\session.memoline' --pipe 'MemoLine.Recognizer.<sessionId>' --speed 1 --inject

# 离散模式，参考录制笔间隔，上限 200 ms
.\release\win-x64\StrokeReplay.exe replay 'D:\notes\session.memoline' --mode discrete --stroke-gap 0.2 --inject
```

当前接口可以来自新的录制会话；历史 memoline 的 `.live.json` 在对应 Recognizer 会话结束后不会提供实时状态。界面将接口选择、速度、模式和离散笔间隔上限保存到 `%APPDATA%\StrokeReplay\settings.json`。

## 构建、验证与发布

需要 .NET 10 SDK。发布目录包含自包含单文件程序，使用者无需单独安装 .NET。

```powershell
dotnet build .\source\StrokeReplay.csproj -c Release -m:1 -nr:false
dotnet build .\tests\ReplayChecks.csproj -c Release -m:1 -nr:false
dotnet .\tests\bin\Release\net10.0-windows\ReplayChecks.dll
dotnet publish .\source\StrokeReplay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\release\win-x64 -m:1 -nr:false
```

检查使用模拟输入与本机测试管道，不向 CSP 发送鼠标、键盘或笔输入；覆盖因果状态、压力与屏幕坐标、截断文件、视图恢复顺序、平移反馈、取消、实时截图屏障、状态失效与会话切换，以及两种笔注入模式的生命周期和取消清理。