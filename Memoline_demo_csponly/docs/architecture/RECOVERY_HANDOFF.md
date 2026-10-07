# 事件编辑恢复交接（2026-10-07）

本轮沿恢复对话推进，按用户允许的上下文不足时分段交接留下已验证检查点。完整目标仍是 [原架构](2026-10-07-event-editing-architecture.md) 的阶段 1–10；本轮没有降低目标或将未实现阶段标为完成。

工作分支：`codex/event-editing-recovery`，起点 `7e0101d`。不推送、不改写已有历史。已有聊天导出、构建日志及临时检查目录保留，不混入功能提交。当前根目录 Memoline.exe 未替换，发布与启动 smoke 待最终阶段。

## 现有入口与缺口

| 阶段 | 可继续使用的入口 | 必须补齐 |
| --- | --- | --- |
| 1 | watcher LayerDiff、SnapshotHistory、RecordingBaselineInitialization | 实机开始基线/性能验收；复现 baseline 尚未由事务生成 |
| 2 | native EditingInputControl、RecorderInputControlServer；watcher CaptureCoordinator、CaptureBoundaryAsync | 宿主边界 flush/聚集发布接线、事务告警取消；复核 notes 中 end 失败时的顺序 |
| 3 | transform_host Raw matrices，watcher 已消费/校验新数组；LayerStateCore.process_with_locator | colorInputState、滑块 ROI 与 RGB/freshness 核实、真实 native 矩阵序列化集成测试 |
| 4 | source/Editing 的 LiveSessionReader（严格 RGBA8 IHDR）、ToolCatalog、BrushClassifier、EditIndexBuilder | 接触全区间排除事务、undo/redo 区间、状态新鲜度/causalAmbiguous、链策略 |
| 5 | ImageCompositor、LayerTimeline；shared ClipboardImageCodec；实际 Node CLI 对拍 54 项 | 真实 CSP 粘贴自检及事务接线 |
| 6 | PixelMask 与 Footprints 几何基础 | Probe 取原生接触样本；ReconstructionPlanner、ReconstructionEngine 及 §9.12 全部 20 场景 |
| 7 | EventReplayRunner、IReplayEnvironment、WindowsReplayEnvironment、MLRC tagged WindowsViewInput | 宿主调度接线、工作层事务配合；颜色更改仍明确 ColorUnrestorable |
| 8 | shortcut writer CLI 及临时数据库测试 | 设置页明确确认、发布解释器或 winsqlite3、EditJournal、v3 Seal/Verify/读取与资产校验 |
| 9 | 串行 CaptureCoordinator、native transaction API | CspEditTransaction、LayerCapture，fake CSP 严格动作顺序/取消/失败日志 |
| 10 | Build.ps1 扩展检查与 transform host 重编译 | 精确撤销 50 笔/闭包、Alt+M/托盘/选择/白线 UI、DemoController 接线、发布 smoke |

宿主 core 与 replay 的主要 DTO 和可注入接口已经存在。继续工作时应先阅读实际签名，避免照旧对话中的草稿写重复定义。当前分类资源使用实际属性库键；复制图章词条仍未找到可信映射，应按架构要求记录未知来源，不猜名字。

## 建议继续顺序

1. 完成阶段 3 颜色定位契约及缺失集成测试；需要实机确认的点保持显式失败，色环只读、滑块手动切换。
2. 补齐阶段 4 的严格拒绝条件和阶段 5 验收。先写 red 测试再改实现，不用现有绿色断言替代新验收；严格 RGBA8 IHDR 检查已完成。
3. 完成阶段 6。Planner 保持纯函数；Engine 使用实际重放捕获更新精确 D、动态闭包及批次合成，覆盖架构 §9.12 二十个场景。不要把 change mask 当 W/Probe/I。
4. 完成阶段 8 日志/v3/快捷键确认，再完成阶段 9 的 fake CSP 原子流程。必须先 checkpoint 刷盘、确认同一工作层 UUID，再删除；故障保留未入库图层。
5. 接阶段 10 精确撤销与 UI；最后执行 Build.ps1、依赖 hash 和发布物 smoke。仅由用户在 CLIP 副本执行 [实机清单](LIVE_CHECKLIST.md)。

每阶段结束仍按架构 §9.0 跑全部六套及快捷键检查，再提交。不要把本轮一次完整组合检查当作后来修改的验收证据。禁向真实 CSP 注入输入、禁写真实用户配置；开发测试继续只用 fake/副本。

## 验证证据

最终组合检查全部通过：host（editing 42，含 reader 21；实际 Node 对拍 54）；native 119；watcher 212；replay 166 项累计断言；Python 133；Node 24；快捷键 11。日志在 [validation](validation)，各阶段 red/green 和实测边界见 [IMPLEMENTATION_NOTES](IMPLEMENTATION_NOTES.md)。部分历史日志是权限拒绝、旧 DLL 或修复前失败，不能替代最终 `*-full.log` / `python-current.log`。

Git 写入需要针对具体 add/commit 命令升级权限，因为 .git 在此工作区只读。Python 全套检查使用本机 `py -3`（需 cv2）；Codex 依赖解释器没有 cv2。Fake named-pipe 的 native/watcher/host/replay 检查在沙箱内会遇到权限限制，已获批的限定范围运行成功。

完整编辑目前没有可执行产品入口，不能向用户报告 PixelVerified 或完成精确撤销。下一段对话从此表继续即可，无需重做本轮已通过的纯文档整理。
