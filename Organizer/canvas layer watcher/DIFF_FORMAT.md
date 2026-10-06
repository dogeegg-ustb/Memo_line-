# dirty-matrix-image-diff/v1

输出根目录是程序旁的 `layer-diffs/`。`current.json.layerStacks` 按 CLIP 图层编号保存独立的 after/now 快照栈，每层最多保留两个完整源 PNG。顶层 `after/now` 仍指向当前选中的栈，兼容旧读取方式。`packets/<id>/` 的差异补丁和 JSON 独立保留，不受完整快照轮换影响。

切换到另一个编号仅切换快照栈，A → B → A 返回时与 A 自己上次快照比较；不会跨图层比较。首次捕获一个编号、同编号 UUID 改变或源快照缺失时为该层建立新的基准。同一次运行、Recognizer 会话、连接代次和画布尺寸内保留各层历史；这些范围改变时重建整组基准。每层最近快照中最早的 triggerTicks 是事件缓存保留界限，避免 B 的封包清掉 A 尚需关联的笔画。

## manifest.json

| 字段 | 含义 |
| --- | --- |
| `schema / kind / id` | 版本、`baseline` 或 `diff`、差异包 ID |
| `after / now` | 较早和较新图层描述；首张基准只有 after，now 为 null |
| `recognizer` | 会话、Stopwatch 频率、输入时间窗口、状态和原始输入 |
| `capture` | 视口触发时间、被冻结的选中图层及属性核心身份、视口值、触发操作和实际保存控制响应 |
| `search` | 单元大小、范围外降采样倍数／尺寸、检查方式、缺失证据说明、比较像素数和检测到的变化像素数 |
| `predictedMatrix` | 输入与笔刷预测影响范围的并集 |
| `labels[]` | 每个影响范围标签、对应状态与差异图像 |
| `images[]` | 实际检测到的图像差异，及关联标签 |
| `canvasPreviewImage` | 可选，完整当前图层的缩略 PNG，用于完整画布位置总览；路径相对于 manifest 所在目录，长边最多 1600 像素 |

图层描述包含 `id/sessionId/generation/triggerTicks/saveDispatchedTicks/savedTicks/width/height/layer/rasterMetadata/recognizerStates`。`triggerTicks` 是快照、比较窗口和文件 ID 使用的时间，文件 ID 为 `trigger-<20 位 triggerTicks>-<随机后缀>`；`saveDispatchedTicks` 仅诊断实际保存派发，`savedTicks` 保留旧字段兼容且与它相同。`layer` 是文件解析确认的 ID、名称、UUID；`rasterMetadata` 保留完整画布尺寸、原 raster 尺寸及偏移。历次包中省略完整源 PNG 路径，因为旧源图像会删除。`current.json` 的 `imageFile` 路径相对于输出根目录。

## 时间和状态

`triggerTicks`、保存派发和输入／状态时间都是从 Recognizer 录制开始计的 Stopwatch ticks；秒数为 `ticks / recognizer.frequency`，不是 Unix 时间。前后两轮的分界使用 API 的 triggerTicks，完成、发布时间和保存派发时间不参与分界。CSP 没有提供精确的文件内容冻结时刻，读取的像素来自实际保存后的文件，不代表可回溯得到过去时刻的像素。

`recognizer.fromTicks/toTicks` 描述 `(该图层前次 triggerTicks, 本次 triggerTicks]`；基准包从 0 开始。触发保存请求时就固定取证缓存，不在图层解析完成后重新读取实时缓存。`capture.triggerKind=evidenceCaptured` 且 `viewPending=true` 表示保存由存证事件提前发起，`capture.view` 是当时最近的已确认视口，尚未声称新视口已识别；后续解析结果仍独立发送。`capture.timeSource` 明确记录 triggerTicks 或旧协议回退来源。`complete` 只表示该输入窗口没有已知连接／缓存缺口，不代表异步核心已全部完成。首张图像前若程序尚未连接，complete 为 false；缓存上限是 50000 个输入点和 2048 条状态，丢弃导致的缺口也明确记录。

`states[]` 的每项包含 `id/channel/ticks/publishedTicks/data`。核心 data 原样保留 status、state、lastConfirmedState、changedFields、rawResult、evidence 和 error 等实际字段。状态优先按 `evidence.triggerTicks` 排序，缺少时依次回退到 capturedTicks、消息 ticks。还包含初始化配置、驱动／设备元数据、键盘状态；存证消息以 `core.<module>.evidence` 单独保存，避免替换确认状态。窗口之前每个频道的最近状态作为上下文保留；`captureStateIds` 指向截止本次 triggerTicks 的最近观察，不强制它们已确认。

`inputs[]` 保留 `ticks/channel/kind/operationId/data/deviceSource/eventId/relatedEventIds`。data 不改写压力、倾斜、驱动配置 ID、坐标状态或 heldKeys。`continuity=true` 的点在窗口开始之前，仅用来衔接同一笔，不重复绘制它之前的段。没有 API 操作 ID／事件 ID 时使用负数作为本客户端的临时操作标识，不据此声称设备身份。

## 矩阵标签

`labels[]` 的每项包含：

- `id/source/operationId/fromTicks/toTicks`：标签和输入／低分辨率补查来源。
- `stateIds`：该输入发生时的 Recognizer 上下文；范围外补查标签关联本次截止时间的状态，不能据此断言是哪一笔造成变化。
- `coverage`：完整画布像素中的接触线段及扩展半径；范围外补查为 null。
- `impactRange`：标签的稀疏影响范围矩阵。
- `warnings`：笔刷／单位回退等说明。
- `imageIds`：由该标签覆盖的差异图像 ID，可为空（有输入而没有实际像素变化）。

矩阵包含 `canvasWidth/canvasHeight/tileSize/originX/originY/rowRuns`。本集成严格裁剪至完整画布，原点为 `(0,0)`；`rowRuns` 包含 `row/startColumn/endColumnExclusive`，右端不包含自身。单元左上角为 `(originX + column * tileSize, originY + row * tileSize)`。预测影响范围包含余量，不等于精确变化像素。

## 图像差异

`images[]` 的每项包含 `id/bounds/changedPixels/image/afterImage/nowImage/maskImage/differenceImage/labelIds`。`image` 是一张主更改图像，等于 `nowImage` 路径：只保留 after/now 比较后发生更改的原始 now RGBA，其余透明。旧包没有 `image` 时可直接使用 `nowImage`。文件路径相对于 manifest 所在目录；bounds 为半开区间 `[left,right) × [top,bottom)`，坐标属于完整画布，PNG 宽高等于 bounds 宽高，不缩放。

`afterImage` 与 `nowImage` 只保留被 mask 标为变化的原始 RGBA；其余像素透明。mask 的变化像素为不透明白色，其余透明。擦除时 now 对应像素为透明，mask 仍为白色；因此不能通过 now 的非透明区域推断变化范围。`differenceImage` 是便于查看的绝对通道差异可视化，使用不透明 alpha，不是原始图层像素。

窗口默认将主更改图像放在左侧放大，右侧独立显示 `canvasPreviewImage` 并使用完整画布坐标框出该补丁。总览是额外显示用缩略图，完整源快照与主更改图像保持原分辨率。总览随差异包保留，不依赖会被轮换删除的源 PNG。旧包优先使用仍存在且 ID 匹配的完整源快照，找不到时以完整画布尺寸放置更改补丁定位。

将补丁放到 bounds 左上角即可对齐。每个 mask 白色像素表示可以用 now 的 RGBA 替换 after 的对应像素，包括 alpha=0 的擦除。范围外低分辨率筛选可能漏掉很小的变化，因此补丁只表达本次检测到的差异，不能保证单靠补丁重建完整 now；当前完整 now 可从 current.json 读取。
