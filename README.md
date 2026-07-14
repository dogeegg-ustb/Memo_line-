# Wintab Background Collector

Windows 后台数位笔原始数据采集器。

## 依赖

- Windows 10/11 x64
- Visual Studio 2022（MSVC）
- CMake 3.24+
- 已安装数位板厂商官方驱动（提供系统自带的 `Wintab32.dll`）

本项目**不**捆绑任何来源不明的 `Wintab32.dll`。

## 构建

```powershell
cd WintabCollector
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

## 录制

默认输出 `.wtlog` 二进制会话文件：

```powershell
.\build\Release\wintab_collector.exe --output .\sessions
```

常用参数：

```powershell
# 指定录制 30 秒后自动停止
.\build\Release\wintab_collector.exe --output .\sessions --duration 30

# 调试时直接写 JSONL
.\build\Release\wintab_collector.exe --output .\sessions --format jsonl
```

录制中先写 `*.wtlog.part`，正常退出后自动重命名为 `*.wtlog`。

## 导出

将 `.wtlog` 转为 `.jsonl`：

```powershell
.\build\Release\wintab_collector.exe --export .\sessions\session-abc.wtlog .\sessions\session-abc.jsonl
```

## 恢复遗留 `.part` 文件

```powershell
.\build\Release\wintab_collector.exe --recover .\sessions
```

## 测试

```powershell
ctest --test-dir build -C Release --output-on-failure
```

## 格式说明

见 [docs/session_format.md](docs/session_format.md)。

## 尚未完成的实机验证

- 多厂商数位板兼容性
- 绘画软件前台、本程序后台共存
- 多显示器与非 100% DPI
- 30 分钟长时稳定性
- 设备热插拔与磁盘空间不足
