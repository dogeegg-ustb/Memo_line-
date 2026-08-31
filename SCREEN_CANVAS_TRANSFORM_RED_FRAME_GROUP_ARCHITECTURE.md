# 红框成组与完整边判定：强约束修改架构

> 本文是 `screen_canvas_transform` 的**增量强约束契约**，专门修正  
> `NavigatorThumbnailRoi` 内红框观测 → 完整边判定 → 视口补全 的流水线顺序与消歧规则。  
> 实现 MUST 遵守 `MUST / MUST NOT / SHOULD / MAY`。  
> 本文**不推翻**既有矩阵来源、`WorkspaceCanvasRelation`、`ViewportCompletionPattern`（0.1/0.2/1.0/2.0/2.1/3.0/4.0）与 CaptureId/Generation 体系；  
> 只强制改写：**先成组、再定完整边、再用硬分支选唯一目标组**。

---

## 1. 目标

把当前错误顺序：

```text
红段观测
  → 端点附近任意正交红像素 stub 确认角点
  → CompleteEdge = 两端都有 stub
  → 按 ConfirmedCompleteEdgeCount 补全
```

改为：

```text
红段观测
  → 几何约束 + 空间相近成组（组内边互相平行或垂直，可映射到至多一套 L/T/R/B）
  → 组内邻边直角关系判定完整边
  → 各组按现有 pattern 补全（仅证据足够的组）
  → if-else 硬分支选出唯一目标组
  → 对该组发布 NavigatorViewportFrame
```

成功标准：

1. ROI 内无关红色元素不得仅因局部正交 stub 把某边抬成完整边；
2. 远距、互不相关但方向碰巧正交/平行的红段 MUST NOT 被拼进同一组；
3. 多组并存时 MUST 用确定性 if-else 选出唯一组，MUST NOT 打分排序取“最优”；
4. 无背景粘着时 MUST 用理论导航器红框尺寸做唯一性匹配；匹配不唯一或不存在则失败。

---

## 2. 非目标（明确保留）

以下能力 MUST 保留，不得因本修改删除或改语义：

1. 红像素色度门控、膨胀连续性、投影峰、轴向线段跨度/支撑率观测；
2. `WorkspaceCanvasRelation` 及其中 `visible_canvas_fraction_*`、`canvas_aspect_ratio`、画布像素宽高；
3. 补全模式枚举 `0.1 / 0.2 / 1.0 / 2.0 / 2.1 / 3.0 / 4.0` 的几何含义；
4. 矩阵只由几何证据求解；`ScalePercent` 不得乘进 Screen↔Canvas 矩阵；
5. C ABI、C# 宿主、`CompleteEdge` 导出与覆盖层显示接口的外层形状（字段可扩展，既有字段语义不得悄悄相反）。

本修改 ONLY 约束：红框边如何成组、完整边如何定义、多组如何硬消歧。

---

## 3. 问题陈述（当前违规）

### 3.1 完整边定义过宽

当前实现：

```text
CompleteEdge iff 线段两端 Probe*EndCorner 在膨胀红掩膜上数到足够正交红像素
```

探针 **MUST NOT** 要求该正交 stub 属于同一红框的另一条已识别边。  
因此 ROI 内任意垂直红色元素都可能制造假角点，进而虚增 `ConfirmedCompleteEdgeCount`。

### 3.2 缺少“矩形假设组”

当前把 ROI 内所有轴向红段混进同一池，再按 L/T/R/B 外簇指派。  
多矩形、多干扰段时，边被错误拼进同一视口假设。

### 3.3 消歧手段与契约冲突风险

用软打分在多候选间取 max，必然引入不可审计的并列与阈值漂移。  
本契约禁止该路径。

---

## 4. 核心定义

### 4.1 红段（ObservedEdgeSegment）

与现实现一致：在 `NavigatorThumbnailRoi` 内得到的色稳、轴向、连续、支撑率合格的线段。  
红段本身 **不是** 完整边。

### 4.2 红框边组（RedFrameEdgeGroup）

**组的定义 = 几何约束 + 空间相近。**  
二者 MUST 同时满足；只满足方向正交/平行但空间上不相干的红段，MUST NOT 成组。

一组红段构成一个**轴对齐矩形视口假设**。

#### 4.2.1 几何约束（MUST）

1. 组内任意两边的方向关系只能是**平行或垂直**（禁止斜交、禁止非正交夹角）；
2. 同向边至多 2 条（一对平行边）；正交方向同理；
3. 边可映射到至多一套 `L/T/R/B`，同一 workspace_edge 位不得被两条边占用；
4. 正交邻接边若在端点容差内相交或共端，MUST 视为该组的角点候选；
5. 组可以缺边（1～4 条边），但 MUST NOT 包含无法纳入同一矩形假设的多余边；
6. 平行对边的间距、正交边的跨度 MUST 能共同解释同一个矩形宽高（允许缺边时由后续补全恢复，但不得与已观测边自相矛盾）。

#### 4.2.2 空间相近（MUST）

“相近”不是软相似度，而是硬几何邻域：

1. **正交邻接**：两边垂直时，至少有一对端点在固定像素容差内相交、共端或可沿轴向投影落到对方线段的延长邻域内；
2. **平行对边**：两边平行时，在对边方向上的投影 MUST 有足够重叠（固定占比或固定像素门槛），且间距落在合理视口尺度内（不得超过导航器画布对应边长，也不得小于实现固定的最小视口边长）；
3. **缺边传递**：若组内只有不相邻的边（例如仅 L 与 R，尚无 T/B），仍可靠平行对边的投影重叠与间距约束成组；不得把画面对角两端互不相关的平行短线拼成一对；
4. **拒绝远距拼装**：仅方向合法、但端点/投影均不落入上述容差的红段集合，MUST NOT 成为候选组。

容差与重叠门槛 MUST 写成实现常量并进入契约测试，MUST NOT 运行时自适应放宽到“总能成组”。

#### 4.2.3 成员资格一句话

```text
红段集合 G 是合法 RedFrameEdgeGroup
  iff G 满足 §4.2.1 几何约束
  AND G 满足 §4.2.2 空间相近
  AND G 解释至多一个轴对齐矩形视口假设
```

无法同时满足的红段集合 MUST NOT 成为候选组。  
一条红段 MAY 参与多个组的枚举尝试，但最终发布前 MUST 只属于被选中的唯一目标组（或随失败丢弃）。

### 4.3 组内直角（GroupRightAngle）

在同一 `RedFrameEdgeGroup` 内：

```text
边 A 的端点 P 存在组内直角
  iff 存在组内边 B，使：
      A 与 B 方向垂直
      且 A 在 P 附近与 B 在容差内相交或端点贴合
```

MUST NOT 再用“端点邻域任意正交红像素 stub”作为完整边的充分条件。  
旧 `Probe*EndCorner` 掩膜计数 MAY 仅作调试诊断，MUST NOT 决定 `complete`。

### 4.4 完整边（CompleteEdge）

```text
CompleteEdge iff 该边属于某一 RedFrameEdgeGroup
              且该边的两个端点都存在 GroupRightAngle
```

部分边：仅一端有组内直角。  
无锚段：两端都无组内直角。

`ConfirmedCompleteEdgeCount`、导出覆盖层、pattern 分派所使用的完整边，MUST 全部是**目标组内**的完整边，不得混入其它组。

### 4.5 背景粘着（TouchesNavigatorBackground）

导航器定义：

```text
NavigatorThumbnailRoi
  = NavigatorCanvas ∪ NavigatorBackground（及其中 UI 残差）
NavigatorCanvas
  = ThumbnailRoi 内扣掉导航器背景色后的画布预览区域
```

实现 MUST 使用已有/等价的导航器背景色模型（与导航器画布观测同源），不得另起无法审计的第二套背景色。

对组内一条边：

```text
EdgeTouchesBackground
  iff 该边外侧法向邻域内，大量像素与导航器背景色一致
```

“外侧”MUST 是朝向该组矩形假设的**框外**方向，不得混用框内像素。  
“大量”MUST 用固定阈值（像素计数或占比门槛），写入实现常量并在契约测试中固定，不得运行时自适应放宽。

对组：

```text
GroupTouchesBackground
  iff 组内至少一条边满足 EdgeTouchesBackground
```

### 4.6 理论红框尺寸（TheoreticalNavigatorViewportSize）

MUST 由 `WorkspaceCanvasRelation` 与 `NavigatorCanvas` 几何反推，不得用 OCR `ScalePercent`：

```text
W_nav = navigator_canvas_width  * visible_canvas_fraction_x
H_nav = navigator_canvas_height * visible_canvas_fraction_y
```

其中：

- `visible_canvas_fraction_*` = 工作区可见画布边长 / 完整画布模型边长；
- 其几何含义：红框占导航器画布的比例 ≈ 当前视口占完整画布的比例；
- `canvas_aspect_ratio`（画布像素宽/高）只用于缺边补全时的形状约束，MUST NOT 单独充当多组消歧判据；
- `canvas_to_workspace_scale_*` 是工作区显示像素相对画布像素的尺度，是关系链的上游证据，但导航器理论红框尺寸 MUST 落到 `W_nav/H_nav`，不得拿工作区像素尺寸直接当导航器匹配目标。

容差 MUST 为硬阈值，例如：

```text
|w - W_nav| ≤ max(AbsPx, Rel * W_nav)
|h - H_nav| ≤ max(AbsPx, Rel * H_nav)
```

AbsPx / Rel 实现时固定；MUST NOT 为凑唯一解而放大容差。

---

## 5. 强制流水线

实现 MUST 按以下顺序执行，不得调换：

```text
A. 在 NavigatorThumbnailRoi 内观测轴向红段
B. 按 §4.2（几何约束 + 空间相近）枚举合法 RedFrameEdgeGroup
C. 对每个组：用 GroupRightAngle 标注完整边 / 部分边 / 无锚段
D. 对每个证据足够的组：按 ViewportCompletionPattern 补全得到候选矩形
E. 多组硬消歧（§6）选出唯一目标组
F. 仅发布目标组的 NavigatorViewportFrame 与 CompleteEdge 导出
```

强制约束：

- B 之前 MUST NOT 宣布全局 `ConfirmedCompleteEdgeCount`；
- B MUST 同时执行几何约束与空间相近，缺一不可；
- C 的完整边定义 MUST 是组内直角，不得回退到掩膜 stub；
- D 中证据不足、无法落入任何合法 pattern 的组 MUST 淘汰，不得“硬编”一个矩形参赛；
- E MUST 是 if-else，MUST NOT 是加权打分、距离排序取 top-1、或“最接近理论尺寸”的软选择；
- 候选不唯一或不存在时 MUST 返回 `AmbiguousViewportGeometry`（或既有等价失败状态），MUST NOT 用上一帧、默认方向或任意一组兜底。

---

## 6. 多组硬消歧（唯一允许的 if-else）

前提：经过 §5.D 后，存活候选组集合为 `G`。

```text
若 |G| == 0:
  → 失败（无合法红框组）

若 |G| == 1:
  → 该组为唯一目标组

若 |G| >= 2:
  令 B = { g ∈ G | GroupTouchesBackground(g) }

  若 |B| == 1:
    → B 中唯一组为目标组
    → MUST NOT 再进入尺寸分支

  若 |B| >= 2:
    → 失败（AmbiguousViewportGeometry）
    → 本契约断言：真实场景不应出现多组同时粘导航器背景；
      若出现，视为观测/成组错误或场景超出本版本假设，禁止猜选

  若 |B| == 0:
    → 进入理论尺寸分支（§6.1）
```

### 6.1 无背景粘着时的尺寸分支

```text
计算 (W_nav, H_nav)
令 M = { g ∈ G | 补全后宽高均在容差内匹配 (W_nav, H_nav) }

若 |M| == 1:
  → M 中唯一组为目标组
若 |M| == 0 或 |M| >= 2:
  → 失败（AmbiguousViewportGeometry）
```

硬禁止：

- MUST NOT 在 `|M| >= 2` 时取“误差更小”的一组；
- MUST NOT 在 `|M| == 0` 时放宽容差重试到出现唯一解；
- MUST NOT 用背景粘着条数、完整边条数、支撑率等做二次排序打破并列。

### 6.2 关于“不可能多组都粘背景”

产品假设（写入契约）：

```text
多组并存时，至多一组 GroupTouchesBackground 为真。
```

因此：

- `|B| == 1` 是正常的高优先级硬分支；
- `|B| == 0` 是正常的放大/框内视口情形，进入尺寸分支；
- `|B| >= 2` MUST 失败，不得设计成可恢复路径。

实现 MUST NOT 把 `|B| >= 2` 静默降级为尺寸分支。

---

## 7. 组内补全与 pattern

目标组（或尺寸分支参赛组）的补全 MUST 继续使用既有模式语义：

```text
0.1  无完整边，平行段
0.2  无完整边，相交有向段
1.0  一条完整边
2.0  两条相交完整边
2.1  两条平行完整边（套用 1.0 + 第二边验证）
3.0  三条完整边
4.0  四条完整边
```

约束：

1. pattern 分派所用的完整边计数 MUST 来自**该组**组内直角结果；
2. `0.1/0.2/1.0` 等需 `WorkspaceCanvasRelation` 的路径保持原契约：用关系约束语义轴与缺边，禁止“长边对长边”机械交换；
3. 尺寸分支中，只有成功完成补全并得到有限 `width/height` 的组才能进入 `M`；
4. 补全是为恢复 `OriginTopLeftDisplayed / AxisXDisplayed / AxisYDisplayed`，不是为了在屏幕上画缺失红线。

---

## 8. 坐标与数据流硬约束

1. 红段、成组、直角、完整边、补全结果均在 CapturePx（或与现实现一致的缩略图局部再映射回 CapturePx）中表达；发布前 MUST 与现有 `NavigatorViewportFrame` 坐标约定一致。
2. 背景粘着检测 MUST 使用与当前帧相同的冻结截图与同一 `NavigatorThumbnailRoi`。
3. `(W_nav, H_nav)` MUST 与当前 `WorkspaceCanvasRelation`、当前导航器画布边界同源；CaptureId / SourceRevision 不一致时 MUST 失败，不得混帧。
4. C# 侧覆盖层显示的完整边 MUST 只来自最终目标组导出，不得把淘汰组的边画上去。

---

## 9. 失败状态

以下情形 MUST 显式失败，禁止静默成功：

| 条件 | 要求 |
|------|------|
| 无合法红段 | 既有不足几何失败 |
| 无法形成任何合法组 | `AmbiguousViewportGeometry` 或不足几何 |
| `|G|>=2` 且 `|B|>=2` | `AmbiguousViewportGeometry` |
| `|B|==0` 且 `|M|!=1` | `AmbiguousViewportGeometry` |
| 目标组 pattern 拓扑不受支持 | 既有 `AmbiguousViewportGeometry` |
| 关系/画布像素缺失导致无法算 `W_nav/H_nav` 且又需要尺寸分支 | 失败，不得改用 aspect 单独猜组 |

失败时 MUST NOT：

- 回退到掩膜 stub 完整边路径；
- 选用“完整边最多”的组；
- 沿用上一帧视口。

---

## 10. 测试强约束

契约测试 MUST 至少覆盖：

1. **干扰正交红**：单框四边 + ROI 内额外垂直红短线 → 不得把无邻边支撑的边标成完整边；
2. **成组**：两套可分离平行/垂直结构 → 产生两组而非一团；
3. **背景唯一**：两组中仅一组外侧粘导航器背景 → 不进入尺寸分支即选中该组；
4. **多组粘背景**：构造 `|B|>=2` → 必须失败；
5. **无粘背景 + 尺寸唯一**：`|B|==0`，仅一组匹配 `(W_nav,H_nav)` → 选中；
6. **无粘背景 + 尺寸并列/全不匹配**：必须失败，不得取较近者；
7. **回归**：原 4 完整边 / 3 边 / 2.0 / 2.1 / 0.1 / 0.2 在单组无干扰场景下行为与模式语义保持一致。

---

## 11. 允许修改的范围

实现本契约时，优先修改：

```text
screen_canvas_transform/native/src/viewport_frame.cpp
screen_canvas_transform/native/include/sct/viewport_frame.hpp
screen_canvas_transform/native/include/sct/types.hpp   （仅当需导出组成组/诊断字段）
screen_canvas_transform/native/tests/contract_tests.cpp
以及为接通背景色/导航器画布边界所必需的最少调用点
```

MUST NOT 借机重写：

- 工作区检测主路径；
- 矩阵求解核心；
- 重算锚点语义（另见重算架构文）；
- 无关 UI。

---

## 12. 验收清单

- [ ] 完整边定义已改为组内双端 `GroupRightAngle`
- [ ] 掩膜 stub 不再作为 `complete` 充分条件
- [ ] 存在 `RedFrameEdgeGroup` 枚举与合法性约束
- [ ] 多组消歧仅为 §6 if-else，无打分
- [ ] `|B|==1` 直接唯一；`|B|>=2` 失败；`|B|==0` 走 `(W_nav,H_nav)` 恰好唯一匹配
- [ ] 理论尺寸使用 `visible_canvas_fraction_* × navigator_canvas_*`
- [ ] 单组回归 pattern 仍可用
- [ ] 契约测试 §10 全部通过

---

## 13. 一句话根因与修正

**根因：** 完整边被定义成“端点旁有任意正交红”，且缺少“几何约束 + 空间相近”的矩形组成组与硬消歧。  
**修正：** 先按几何与空间相近成组，用组内直角定义完整边；多组时先背景粘着唯一性，否则用理论导航器红框尺寸唯一性；并列即失败。
