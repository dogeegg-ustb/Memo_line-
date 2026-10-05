# 集成 memoline 格式

容器 schema 为 `memoline-csponly/v1`。文件扩展名为 `.memoline`，外层是 ZIP；里面的原生机械记录仍保持 Recognizer 的原始 `MEMOLINE` 帧格式。

## 三部分与两份运行时流

| 内容 | 运行时位置 | 封盘后位置 | 后续升级规则 |
|---|---|---|---|
| 原生机械事件 | Recognizer 的 `.memoline.part` | `mechanical/recording.memoline` | 原始字节不能改 |
| 维度随时间的数值 | 聚集 JSONL 的 `dimensionSample` | 原始聚集流或 `dimensions/<版本>.jsonl` | 可新增活动算法版本 |
| 固定聚集包、脏矩阵、图像与事件指针 | 聚集 JSONL 的 `packet`、`asset` | `aggregation/events.jsonl` | 已记录的内容不能改 |

最初的容器有三个 ZIP entry：

```text
mechanical/recording.memoline
aggregation/events.jsonl
index.json
```

图像由 `asset` 记录的 base64 分块保存，不依赖额外 ZIP entry 或外部 PNG。维度升级只新增 `dimensions/<安全版本名>.jsonl`，并更新 `index.json` 的 `activeDimensionsEntry`。原始机械 entry、原始聚集 entry 和已有维度版本全部保留。

## 时钟与机械指针

所有事件时间均属于同一 Recognizer 会话。`sessionId`、`frequency`、`originTicks` 来自原生 header/实时 hello；`ticks` 和包的 `fromTicks`、`toTicks` 使用该会话的相对 Stopwatch ticks。相对秒数为 `ticks / frequency`。墙上时钟、解析完成时间和保存派发时间不作为封包边界。

原生机械文件按照原有追加顺序保存。延迟解析的状态可以在较晚追加时保留较早的发生 ticks，这种原生因果顺序不被重排。硬件事件的发生 ticks 必须非降序，原生记录必须有完整 footer 才能封盘。

一个指针的典型结构为：

```json
{
  "sessionId": "原生会话ID",
  "appendId": 123,
  "eventId": 97,
  "operationId": 82,
  "ticks": 4567890,
  "context": false
}
```

`sessionId + appendId` 唯一定位原生持久化帧。`eventId` 指向原生硬件事件，状态帧的该字段为 `0`；`operationId` 是操作分组。实时接口的运输 `sequence` 不作为文件指针；诊断消息的 `appendId=0` 也不能引用为原生帧。

包和矩阵的 `eventPointers` 只包含硬件帧。矩阵可另外带 `statePointers`，指向实际存在的原生状态帧。窗前连续笔画的连接点及状态上下文带 `context: true`，允许其 ticks 早于包的 fromTicks，避免将它们重复算作新发生的动作。每个指针的原生 ticks、eventId 和 operationId 必须与被引用帧一致。

## 聚集 JSONL

每行是一个 UTF-8 JSON 对象。首行必须是 header，末行必须是 footer；不允许尾部残帧、空行、未知记录种类或 footer 后追加数据。当前支持 header、dimensionSample、packet、asset、footer。

header 至少包含 kind、sessionId 和 frequency；如果包含 originTicks，也必须与原生时钟一致。footer 表明聚集写入已经完成。

### 维度样本

```json
{
  "kind": "dimensionSample",
  "sessionId": "原生会话ID",
  "ticks": 4567890,
  "canvasViewportChanged": 1,
  "actionPatternContinuity": null,
  "toolContinuity": null,
  "layerContinuity": null,
  "regionContinuity": null,
  "timeContinuity": null,
  "algorithmVersion": "csponly-dimensions/v1"
}
```

`canvasViewportChanged` 只允许 `0` 和 `1`，表示这个确认观察相对于上一个确认视口是否变化；初始基线为 `0`。其余五个连续性字段目前必须留出位置，值为 `null`；升级算法后可使用有限数值。空值表示尚未计算，并不等于数值 0。

ticks 必须位于原生会话范围内。`ReadDimensions` 只读取 `activeDimensionsEntry` 中的样本，并按 ticks 排序；不会混入旧算法的同名维度。

### 固定聚集包

packet 至少包含：

- kind 为 `packet`、原生 sessionId、fromTicks 和 toTicks。
- `eventPointers`：这个时间窗中实际硬件事件的原生指针。
- `dirtyMatrices`：脏矩阵、范围标签及对应图像关联。每个矩阵都有自己的 eventPointers，可以有 statePointers。

当前边界是确认的画布视口变化。通常窗口为 `(fromTicks, toTicks]`；窗前上下文另行标识。捕获任务未产生图像时仍记录固定空包，dirtyMatrices 和图像列表为空，并记录 status/reason。

矩阵保留完整画布坐标系的 impactRange 和标签数据；包内 manifest 保留现有 watcher 的 after/now 描述、搜索信息、差异图像和 Recognizer 状态。主差异图是 now 与 after 不同位置上的原始 now 像素，图像局部由完整画布 bounds 定位。无法可靠关联操作的低分辨率补充差异标记为 unresolved，不伪造机械事件归属。

### 图像资产

asset 包含 assetId、base64、sha256，可包含 chunkIndex、chunkCount、totalSha256。当前 PNG 按 512 KiB 分块：

- chunkIndex 从 0 开始，同一资产按顺序递增。
- sha256 校验该行 base64 解码所得的分块字节。
- totalSha256 校验所有分块按顺序连接所得的完整 PNG。

封盘校验重复、缺失、乱序分块及不一致的块数或哈希。资产的 sessionId 如果提供，必须与原生会话一致。

## 索引与封盘

index.json 记录 schema、sessionId、frequency、originTicks、机械和聚集 entry 的路径、SHA256 与字节数、fixedSha256、activeDimensionsEntry，以及已有维度版本的 entry 元数据。

`fixedSha256` 只覆盖 packet/asset 的原始 UTF-8 JSON 行；每行后用 LF 分隔后计算 SHA256。原始 JSON 表达、字段和图像资产都参与固定哈希，维度行不参与。完整聚集 entry 另有逐字节哈希，原始 CRLF/LF 等文件字节也不能在封盘或升级时改写。

封盘先校验原生 footer、会话时钟、硬件时间轴和所有事件指针；再写同目录临时容器、刷盘、重新校验；最后原子改名到尚不存在的目标路径。`BundleArchive.Seal` 不删除输入。宿主只有在封盘校验成功后才清理自己拥有的两份主数据中间文件，失败时保留输入。

## 维度升级接口

```powershell
.\MemolineDemo.exe --upgrade-dimensions input.memoline dimensions.jsonl continuity-v2 output.memoline | Out-Host
```

新的维度 JSONL 必须独立完整，仅含 header、dimensionSample、footer，使用同一原生 sessionId 和 frequency。每个样本的 algorithmVersion 必须等于命令给出的版本名。版本名符合 `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`；不允许路径字符或覆盖已有版本。

升级会再次验证原容器与新维度，复制所有原 entry 的原始内容，新增版本并切换活动入口。原机械哈希、原聚集哈希和 fixedSha256 保持相同。升级输出也必须是新路径，输入不会删除或覆盖。

公共核心接口为 `BundleArchive.Seal`、`Verify`、`ReadMechanical`、`ReadDimensions` 和 `UpgradeDimensions`。ReadMechanical 通过受控临时文件调用原生 reader，逐帧返回原记录；原生 reader 自身不识别这个 ZIP 外层，独立工具请先使用 `--extract-mechanical`。
