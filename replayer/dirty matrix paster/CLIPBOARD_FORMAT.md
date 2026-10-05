# Windows 剪贴板图层格式

`MemoLine.DirtyMatrix.Layer.v1` 为原始二进制 MemoryStream。Windows 持久剪贴板保留该流，程序退出后仍可读取。它是本项目公开的接入协议，CSP 不会自动识别。

| 顺序 | 编码 | 内容 |
| --- | --- | --- |
| 1 | 8 ASCII 字节 | `DMLAYER1` |
| 2 | uint32 little-endian | UTF-8 JSON 字节长度 |
| 3 | uint64 little-endian | RGBA 字节长度 |
| 4 | uint64 little-endian | 蒙版字节长度 |
| 5 | UTF-8，无 BOM | layer.json 元数据 |
| 6 | 原始字节 | width × height × 4，行优先、从左上到右下，straight RGBA8 |
| 7 | 原始字节 | width × height，行优先 Gray8，0=保持、255=替换 |

层尺寸为所选补丁并集的包围盒，完整画布尺寸为 `canvas.width/height`；放置位置为 `bounds.left/top`。JSON 保留每个 image 的 bounds、原始文件名、关联标签、脏矩阵原点与 rowRuns，以及目标图层身份。RGBA 来自程序内部按发生时间进行像素替换的最终状态；蒙版是所选包更新范围的并集，不以最终 alpha 推断。

`composition.order` 按 triggerTicks 升序记录来源包，包含 id、manifestPath、triggerTicks、changedPixels 和 erasedPixels。`composition` 另含 selectedCount、appliedCount、skippedPackets、replacementWrites 和 overwrittenPixels（后续包重复写入已知像素的次数）。每个 image 与 dirtyMatrix 增加 packetId/triggerTicks，使不同包的相同局部 ID 可区分。

对于每个蒙版为 255 的像素，直接将对应源 RGBA 写到目标 `(bounds.left+x,bounds.top+y)`。为 0 的像素保持目标原值。alpha=0 的源像素同样必须写入，否则擦除会失效。应用端必须核对 canvas 和 targetLayer 身份。

其他格式：`MemoLine.DirtyMatrix.Metadata.v1` 是 JSON 字符串。默认输出 `PNG`、Windows 预定义 `CF_DIBV5`（编号 17）和 `FileDrop`（完整画布 PNG 路径）。PNG/DIBV5 中各包之间的像素替换已经完成。DIBV5 是顶部起始 BGRA、BI_BITFIELDS、32 位 RGBA 掩码、sRGB。普通图像格式不表达对接收程序已有图层的原位替换命令。旧版单包 PSD 导出可保留 PSD 数据和路径，新版合成流程不产生 PSD。
