# Memoline.Core

.NET 10 读写类库，由 Recognizer 和 MemolineToJson 共同引用。程序集为 `Memoline.Core.dll`，命名空间为 `BehaviorRecognizer.Storage.Memoline`。没有 HID、CSP、窗口或 Python 依赖。

同时提供 `BehaviorRecognizer.Realtime` 下的键盘、鼠标、数位板和 5 个解析核心的实时订阅、快照补发与本机命名管道客户端，详见 [实时状态接口](Realtime/README.md)。

## 即时保存

`AppendHardware` / `AppendState` 将不可变帧入队；后台线程负责压缩和写盘，不在 Windows 输入钩子里压缩或刷盘。空闲批次立即刷新到操作系统，连续录制中默认每 100ms 请求一次硬件刷盘，空闲时也有定时刷盘。实际间隔受写入队列、压缩和磁盘负载影响。

需要确认某批数据已经保存时，等待 `FlushAsync()`。这个写入屏障保证调用前已入队的帧全部写出，并完成 `FileStream.Flush(true)`；它不依赖录制停止。`FlushAsync(durable: false)` 只保证刷新到操作系统缓存，不保证硬件刷盘。

```csharp
using BehaviorRecognizer.Storage.Memoline;

await using var writer = new MemolineWriter(directory, new { purpose = "recording" });
writer.AppendHardware("penBegin", new { x = 100, y = 200, pressure = 300 },
    new HardwareDeviceSource("pen", "myInput", "device-id", "deviceId"));
await writer.FlushAsync();
Console.WriteLine(writer.LiveFilePath); // 录制中：*.memoline.part
// DisposeAsync 写入 footer、刷盘，最后改名为 writer.FilePath（*.memoline）。
```

通过 `MemolineWriterOptions` 可设置 `DurableFlushInterval` 和 `CompressionLevel`。默认 `CompressionLevel.Fastest`；`NoCompression` 可关闭压缩，仍使用 v2 帧格式。进程意外退出时，已有完整帧可从 `.part` 读取；最后一个未完成帧不会被当成已提交数据。

Recognizer 启用 `SuccessfulStatesOnly=true`：核心状态仅持久化 `changed` / `unchanged` 且有确认值的结果。同一包里的成功模块保留，失败、未知、有歧义模块排除；没有成功模块的包和停止时未完成的包不产生文件中的预留槽。预留与结果在成功后一起追加，`reservationOrder` 保留相同输入锚点下的原始预留次序，旧文件仍按原追加 ID 排序。

失败结果、原始 `stateResult` 诊断和初始化错误写入 `DiagnosticFilePath` 指向的同名 `*.diagnostics.jsonl`，首次诊断时创建。成功核心结果仍在 `coreStateUpdated.rawResult` 保存原始输出；输入事件、配置、触发和截图继续保存。`FlushAsync()` 同时刷新诊断日志。录制中跟读日志应以 `FileShare.ReadWrite | FileShare.Delete` 打开。类库默认不启用此选项，兼容原有调用方。

`RecordAppended` 只通知文件中实际追加的帧；`DiagnosticRecorded` 通知只进入日志的观察结果，其 `AppendId=0`。实时 Hub 同时订阅二者，因此仍提供失败状态诊断，但它们没有 memoline 帧引用。

## 读取接口

快照读取，v1 和 v2 自动识别：

```csharp
foreach (var frame in MemolineReader.Read(path))
    Console.WriteLine(frame.GetRawText());
```

增量轮询，在同一个 reader 上只返回新增帧：

```csharp
using var reader = MemolineReader.Open(livePath);
var firstBatch = reader.ReadAvailable();
// 过一段时间再次调用，同一帧不会重复返回。
var nextBatch = reader.ReadAvailable(maxRecords: 256);
Console.WriteLine(reader.Offset);       // 下一个完整帧的边界
Console.WriteLine(reader.FormatVersion);
Console.WriteLine(reader.IsComplete);   // 已读到 footer
```

每批默认最多 256 帧，解压数据达到约 8MiB 后也会结束该批；单帧上限 64MiB。reader 为单消费者对象，不可并发调用。半帧会留在原偏移等待重试；CRC、编码或 JSON 损坏会抛出异常，不跳过错误。读文件的共享模式允许停止时改名；已打开的 reader 可以继续读到 footer。

若传入 `.memoline` 名称而录制尚未结束，会自动打开对应 `.part`；若传入 `.part` 名称而录制已经改名结束，会自动打开对应 `.memoline`。

持续跟读，默认每 50ms 检查新增帧；从第一帧开始，遇 footer 结束，可用取消令牌停止：

```csharp
await foreach (var frame in MemolineReader.FollowAsync(livePath,
    cancellationToken: cancellationToken))
{
    HandleFrame(frame);
}
```

`ExportAsync(input, output, token)` 导出当前完整前缀为 JSONL。`.part` 可能仍有未完成的状态分析包，读取和 footer 完整性不代表解析或回放已全部完成。

## 命令行接口

发布的 Recognizer 无需打开录制窗口即可读取：

```powershell
BehaviorRecognizer.exe --follow "D:\recordings\session.memoline.part"
BehaviorRecognizer.exe --export "D:\recordings\session.memoline.part" "D:\recordings\snapshot.jsonl"
BehaviorRecognizer.exe --compact "D:\recordings\session.memoline" "D:\recordings\session.compressed.memoline"
```

`--follow` 的 stdout 只输出 JSONL，可被其他语言逐行读取；Ctrl+C 停止。独立 MemolineToJson 也提供 `--follow` 和 `--compact`。

`MemolineReader.CompactAsync(input, output, token)` 为旧文件创建 v2 压缩副本，不改原文件，不覆盖已有输出。保留事件、时钟、因果引用、状态和原始 PNG/base64，仅把 header.version 更新为 2。未结束的录制副本必须使用 `.part` 后缀。

## 二进制格式

所有整数使用小端序。文件头为 8 字节 `MEMOLINE` 和 32 位格式版本。

| 格式 | 帧头 | 载荷 |
|---|---|---|
| v1（兼容读取） | int32 载荷长度、uint32 CRC32 | UTF-8 JSON |
| v2（新录制） | int32 存储长度、uint32 flags、int32 原 JSON 长度、uint32 CRC32 | 原始 UTF-8 JSON 或独立 Brotli 数据 |

v2 的 flags 为 0（未压缩）或 1（Brotli），CRC32 校验存储载荷。每帧独立压缩，只有节省至少 32 字节时才存压缩数据。读取接口自动解压，返回与原来一致的 JSON 事件；无需等整个会话结束。未升级的 v1 读取器不能读 v2，须改用本类库或新版 MemolineToJson。
