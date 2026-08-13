# BehaviorRecognizer

个人数位板持续采集软件。内嵌 OpenTabletDriver 采集核心，**用户无需单独安装 OpenTabletDriver**。

## 能力

- 启动时自动探测环境（Windows Ink / vMulti / 权限 / 数位板）
- 自动加载内置笔配置
- 持续采集笔输入并写入可扩展容器格式 `.brlog`
- 统一事件总线 + 可插拔记录器（键盘 / 笔刷 / 图层接口已预留）
- vMulti 缺失时仅引导安装，**不阻塞基础采集**

## 构建

需要 .NET SDK 10：

```powershell
cd behavior_recognizer
dotnet build .\src\BehaviorRecognizer\BehaviorRecognizer.csproj -c Release
```

## 运行

推荐直接运行**自包含发布版**（无需安装 .NET 10）：

```powershell
.\publish\win-x64\BehaviorRecognizer.exe
```

重新发布：

```powershell
dotnet publish .\src\BehaviorRecognizer\BehaviorRecognizer.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

开发调试：

```powershell
dotnet run --project .\src\BehaviorRecognizer\BehaviorRecognizer.csproj -c Release
```

常用命令：

```powershell
# 导出会话为 JSONL
BehaviorRecognizer --export .\procedure\sessions\session-xxx.brlog

# 恢复未完成的 .brlog.part
BehaviorRecognizer --recover
```

录制中输入 `V` + Enter 可打开 vMulti 安装引导。

## 目录布局（自动创建）

运行后写入程序所在目录下的 `procedure\`：

- `config/`
- `cache/`
- `sessions/`
- `exports/`
- `logs/`
- `drivers/`
- `bootstrap/`

## 架构分层

| 层 | 目录 | 职责 |
|---|---|---|
| 启动编排 | `Bootstrap/` | 环境探测、配置装载、管道组装 |
| 采集核心 | `Capture/` | OTD 内嵌设备发现与报告读取 |
| 归一化 / 总线 | `Capture/` | `InputEventNormalizer` + `InputEventBus` |
| 会话 | `Session/` | 会话状态机与应用目录 |
| 记录器 | `Recording/` | 记录器总线与默认 / 扩展记录器 |
| 存储 | `Storage/` | BRLOG 容器、JSON 导出、恢复 |
| 契约 | `Abstractions/` | 规范要求的全部可替换接口 |

## 许可证

本项目链接 OpenTabletDriver（LGPL-3.0-or-later）。详见 [NOTICE.md](NOTICE.md)。
