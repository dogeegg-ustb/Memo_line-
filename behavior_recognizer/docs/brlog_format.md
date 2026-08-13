# BRLOG 会话容器（版本 1）

可扩展分块容器，对应架构规范 §16。

## 文件头（16 字节）

| 字段 | 类型 | 说明 |
|------|------|------|
| magic | char[8] | `BRLOG\0\0\0` |
| formatVersion | uint32 LE | `1` |
| reserved | uint32 LE | `0` |

## 块头（24 字节）

| 字段 | 类型 | 说明 |
|------|------|------|
| blockType | uint32 LE | `1=Header` `2=Manifest` `3=StreamEvents` `4=Chunk` `5=Footer` |
| formatVersion | uint32 LE | 块格式版本 |
| itemCount | uint32 LE | 条目数 |
| payloadBytes | uint32 LE | 负载长度 |
| crc32 | uint32 LE | 负载 CRC32 |
| reserved | uint32 LE | 保留 |

## 约定

- 录制中写 `*.brlog.part`，正常结束重命名为 `*.brlog`
- 未知 `blockType` 可按 `payloadBytes` 跳过，保证向前兼容
- 键盘 / 图层 / 笔刷等扩展通过 `Chunk` 或新 Stream 类型追加，不改旧字段布局
