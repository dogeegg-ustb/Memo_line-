# OtdStrokePlugin

OpenTabletDriver 外部笔划异步记录插件（C++ 核心库）。

依据仓库根目录 `WINTAB_BACKGROUND_COLLECTOR_SPEC.md` 中的 OTD 外部插件架构实现：

- 异步落盘（采集线程不写磁盘）
- 抬笔 ≥ 500ms 或笔划数 ≥ 100 时 flush
- 二进制事件流格式 `.strokebin`（魔数 `STRO`）
- 输出目录：`<OTD根目录>/stroke/`
- 文件名：`yyyyMMdd_HHmmss.strokebin`
- 支持导出 JSON

本仓库是**独立工程**，不与 `WintabCollector` / `OpenTabletDriver` 源码耦合。OpenTabletDriver 侧可通过 C ABI（`otd_stroke/c_api.h`）做薄封装接入。

## 目录

```text
OtdStrokePlugin/
  include/otd_stroke/   公共头文件与 C API
  src/
    plugin/             C ABI 入口
    recorder/           stroke 聚合与 flush 策略
    model/              编解码辅助
    io/                 异步队列与二进制写入
    reader/             二进制读取
    exporter/           JSON 导出
    tools/              demo / export 工具
  tests/
  stroke/               运行时可生成的输出目录（默认在 otdRoot 下）
```

## 构建

```powershell
cd OtdStrokePlugin
cmake -S . -B build -DOTD_STROKE_BUILD_SHARED=ON
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
```

产物：

- `otd_stroke.dll` / `otd_stroke.lib` — 插件核心
- `otd_stroke_demo.exe` — 模拟笔事件并写盘
- `otd_stroke_export.exe` — `.strokebin` → JSON

## 安装到 OpenTabletDriver（重要）

**不能把 `otd_stroke.dll`（C++ 原生库）直接丢进 OTD 插件管理器。**  
OTD 只接受托管 .NET 程序集（`.dll` / `.zip`）。直接安装原生 DLL 会报错 / 安装失败。

正确产物是 zip，里面同时包含：

- `OtdStrokeRecorder.Plugin.dll`（C# Filter 插件）
- `otd_stroke.dll`（C++ 记录核心）
- `metadata.json`

### 打包

先编译 C++，再装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，然后：

```powershell
cd OtdStrokePlugin
.\scripts\pack_otd_plugin.ps1
```

得到：`dist\ARTStrokeRecorder.zip`

### 安装

1. 打开 OpenTabletDriver → Plugins → Install Plugin
2. 选择 `ARTStrokeRecorder.zip`（不要选单独的 `otd_stroke.dll`）
3. 在 Filters 里勾选 **ART Stroke Recorder**
4. Apply / Save

记录默认写到：

`%LocalAppData%\OpenTabletDriver\stroke\`

### 接入原理

C# 过滤器实现 `IPositionedPipelineElement<IDeviceReport>`，在管线中旁路记录，经 C ABI 调用原生库：

1. `otd_stroke_create` / `otd_stroke_start`
2. `otd_stroke_pen_down` / `otd_stroke_on_point` / `otd_stroke_pen_up`
3. 定时 `otd_stroke_tick`（500ms 抬笔超时）
4. 退出 `otd_stroke_stop` / `otd_stroke_destroy`
