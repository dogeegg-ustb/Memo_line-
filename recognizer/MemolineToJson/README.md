# MemolineToJson

独立的 `.memoline` 转 JSON 命令行程序，与记录器共同引用 [Memoline.Core](../core/memoline/README.md)。自动读取 v1 和 v2 压缩格式，检查格式版本、长度及 CRC32；完整文件还检查结尾标记。解析器另外检查硬件事件 ID、追加 ID、时间顺序和结尾计数。

```powershell
dotnet build .\MemolineToJson\MemolineToJson.csproj -c Release
.\MemolineToJson\bin\Release\net10.0\MemolineToJson.exe .\Recognizer\publish\win-x64\procedure\stroke\example.memoline

# 录制期间持续输出新增 JSONL；Ctrl+C 停止，读到 footer 自动结束
MemolineToJson --follow example.memoline.part

# 创建 v2 压缩副本；不改原文件，不覆盖已有输出
MemolineToJson --compact example.memoline example.compressed.memoline
```

输出默认在输入文件旁，扩展名为 `.json`。可传第二个参数指定位置。JSON 包含 `header`、按追加顺序排列的 `events`、`complete` 和 `footer`。对 `.memoline.part`，最后一帧不完整时会保留此前完整记录并输出 `complete: false`；CRC 错误和格式错误会拒绝转换。失败时不会覆盖已有 JSON。

新会话另含 `timeline`：按 `statePackageReserved.afterEventId` 放置迟到的 `statePackageResult`，初始包在输入 1 前，输入 N 的状态包在输入 N 后、输入 N+1 前。`events` 保留物理追加证据。没有实际变化的包从逻辑时间线省略；未解析的包保留 pending，分析失败、归属不明确或未知状态保留相应状态。`pendingStatePackages` 统计未完成的包；`replayableThroughEventId` 表示第一个阻断状态之前可安全推进到的输入编号。`complete` 仅代表二进制会话结尾完整，不代表所有状态都可识别或复现。

Recognizer 新录制启用 `statePersistence=successfulOnly`，只保存成功状态；失败和未完成的包保存在会话同名 `*.diagnostics.jsonl` 中。同一输入锚点的延迟预留槽优先按 `reservationOrder` 排序；旧文件仍按 appendId 排序，原有失败缺口也继续兼容读取。

旧文件无状态包预留时 `timelineAvailable=false`，不能从旧文件凭空恢复缺失的状态因果关系。v2 文件需要使用新版解析器或 Memoline.Core；复现消费者应使用 `timeline`，并遵守 `replayBlocked`。

新录制的硬件事件带 `deviceSource`：`deviceType` 是输入类别，`captureApi` 是采集接口，`deviceId` 是能从接口取得的设备 ID，`identification` 表示识别精度。旧文件没有此字段，解析器不会根据事件类别补造物理设备 ID。
