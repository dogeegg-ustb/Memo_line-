# 阶段 6：规划、重建与精确撤销选择

`ReconstructionPlanner.Build` 是纯函数。删除估计只增长旧 W 与关联 dirty mask 的交集；数值覆盖再增长新 W；普通未修改笔的重放不增长估计。共享接触闭包重算到不动点，同捕获组的可信合成排在重放之后。尺寸覆盖逐标签使用实际冻结的 CoverageSettings，旧包缺少 recipe 时返回 UnsupportedOverride。

`ReconstructionEngine.RunAsync` 同步推进实际 A、历史 H 和精确 D。每次 Flush 只使用实际捕获更新 D；原生 begin eventId 决定笔顺序，appendId 决定样本顺序，共享接触全程只重放一次。运行时提升同时关闭后续共享图片，依赖指向已合成图片（含历史 seed 之前）时返回 RuntimeClosureViolation。

跨层参考读取按捕获组时间倒推未编辑层，不能使用其边界未来图像。静态未受影响的潜在 reference 层仍进入在线扫描；实际捕获偏差可以使其提升。含 reference 读取的批次在该组结束时 Flush，避免跨过其他层的历史变化。潜在层只有实际受影响时才安装复现层。

安装读回与期望 RGBA 不符返回 PasteNotExact；尺寸不符返回 CaptureMismatch。精确撤销的安装器先验证粘贴，再离散重放，返回最终实际捕获；该捕获不能错误地与删除种子 A 比较。发生过任何重放或执行精确撤销时只返回 Executed。纯重建且读回完全一致才可返回 PixelVerified。

新增检查：ReconstructionChecks 75、ReconstructionCrossLayerChecks 14、PreciseUndoChecks 30。覆盖 §9.12 的 20 个场景；17/18 在选择器检查中，其余在重建检查中。实际 undo/redo、lossy/baseline gates 另由原 EditingChecks 和新增 index checks 验证，重建器使用这些 issues 阻止执行。

真实 red 证据：stage6-reconstruction-red.log 为缺失 Planner/Engine API；stage6-status-seed-red.log 为缺失 Executed 状态；stage6-seed-red.log 为 seed 前接触漏记的行为失败；stage6-precise-final-red.log 为错误比较离散最终捕获的行为失败。跨层检查在独立审查发现缺陷后新增，root 并发修复先于检查运行，因此 host-cross-layer-current.log 是新增用例 green，不能称为缺陷版本的行为 red。测试没有删除已有断言。

所有 CSP 行为经接口注入。本阶段只有 fake executor；没有向 CSP 注入输入，没有操作真实剪贴板，也没有写真实 CSP 配置。真实动作适配器、事务、undo 次数确认和 UI 接线仍由后续阶段完成。
