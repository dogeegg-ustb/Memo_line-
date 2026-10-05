# dirty matrix paster

Windows x64 程序，支持用户选择多个 canvas layer watcher 的 `dirty-matrix-image-diff/v1` 脏矩阵包，在内部按发生时间执行 RGBA 像素替换合成，再将一个最终结果写入 Windows 持久剪贴板。默认流程直接生成 RGBA/PNG，不经过 PSD。

双击 `Start.cmd`。默认列出并勾选本次提供的 7 个差异包。可以多选添加 manifest、扫描 packets 目录、拖入多个包目录，再通过复选框选择参与合成的包。「全选」「清空选择」只改变勾选状态。「合成预览」展示结果；「合成并写入剪贴板」将所有勾选包合成后一次写入。首个 baseline 没有 image，自动跳过。

程序仅使用 `images[].image`（旧包缺少该字段时使用 nowImage）。每个补丁按 bounds 的完整画布像素坐标放置，不缩放、不读取 canvasPreviewImage。一个包内多个 image 先合并成该时刻的更新，再跨包按 `now.triggerTicks` 升序合成；缺少该值时依次回退到 capture.triggerTicks、recognizer.toTicks。选择或文件添加的顺序不影响结果，文件修改时间不参与排序。

合成从透明 RGBA 状态开始。只有 maskImage 标记的变化像素更新状态：后发生的 R、G、B、A 四个字节直接替换该位置的先前值，不做 alpha 混合。半透明像素也直接替换；alpha=0 的擦除同样写入，后续绘制可以再替换它。未参与更新的像素保持透明；本程序不会从缺少完整 image 的 baseline 推断未选范围的原内容。

`labels[].impactRange` 的行区间按 `origin + index × tileSize` 展开，右端不包含自身。程序校验每个变化像素位于该 image 关联的矩阵并集内，包含范围外低分辨率补查标签。矩阵是预测范围，不能全部白色化：实际蒙版使用 maskImage 的不透明白色像素，确保未变化像素不被清除。透明擦除保留为白色蒙版 + alpha=0 原始 RGBA；半透明像素及透明像素的 RGB 不经画布绘制、预乘或重采样。

不同录制会话、代次、时间频率、画布尺寸或目标图层的包拒绝混合。相同时间且重叠像素不同的包拒绝合成，避免凭选择顺序猜测先后；相同时间的同值或不相交更新可以合成。同一路径重复选择会去重。结果保留发生顺序、来源包及脏矩阵，界面列出实际采用的顺序和覆盖替换次数。

## 剪贴板结果

替换合成已经在程序内部完成。剪贴板保存一个最终 RGBA 状态及其累计更新范围，二进制协议为 `MemoLine.DirtyMatrix.Layer.v1`。默认同时提供完整画布尺寸的 PNG、CF_DIBV5 和 PNG 文件路径，方便其他程序粘贴图像；程序退出后剪贴板仍然保留。取消「同时提供普通图像格式」时只提供 RGBA、更新范围与元数据。

普通 CSP 粘贴接收的是最终合成图像，CSP 接收后的定位及透明度兼容性尚未实机验证。本版本不操作已有 CSP 图层，也不声称普通粘贴会清除目标图层旧像素。

## 构建与命令行

需要 .NET 10 SDK、Node.js 和 npm。构建后自带 .NET 与 Node 运行时，不需要再次安装；依赖锁定在 package-lock.json。

```powershell
./Build.ps1 -Check
./publish/win-x64-v2/DirtyMatrixPaster.exe --compose "包A" "包B" --output "输出目录"
./publish/win-x64-v2/DirtyMatrixPaster.exe --copy "包A" "包B" --output "输出目录"
./publish/win-x64-v2/DirtyMatrixPaster.exe --copy "包A" "包B" --layer-only
./publish/win-x64-v2/DirtyMatrixPaster.exe --copy --selection "选择列表.json" --output "输出目录"
./publish/win-x64-v2/DirtyMatrixPaster.exe --inspect-clipboard
```

选择列表 JSON 是包目录或 manifest 路径组成的字符串数组。CLI 可用 Start-Process 重定向 stdout/stderr 读取 JSON 结果。`--compose` 不改剪贴板；`--copy` 覆盖当前剪贴板并读回验证；`--layer-only` 不提供普通图像回退。全部是 baseline/无变化包时返回 `status=skipped`，保留已有剪贴板。错误返回非零退出码。

生成目录默认位于程序旁的 `artifacts/<时间-随机ID>/`，包含合成后的 `layer.rgba` / `mask.gray`、坐标及时间顺序元数据 `layer.json`、局部 `layer.png`、完整画布 `clipboard.png`、累计更新范围 `mask.png` 和本次选择列表 `composition-request.json`；复制时还包含二进制 `layer.dmlayer`。文件保留供剪贴板文件引用和检查使用，请在不需要它们时手动删除。

旧版单包 PSD 导出仍可通过 `--export "包目录" "PSD输出目录"` 使用；它不参与新版合成或剪贴板流程。

`backend/checks/composition.test.mjs` 检查乱序输入、半透明替换、擦除后重绘、更新范围、时间与身份校验、重复选择和相同时间冲突；对本次 7 个真实包，还使用原始 PNG 独立重放并逐字节核对整个合成结果。`packet.test.mjs` 保留包读取和旧版 PSD 导出的检查。

`checks/ClipboardChecks.ps1` 在复制进程退出后，直接通过 Win32 读取剪贴板，验证合成二进制、PNG 字节以及预定义 CF_DIBV5 的坐标和 RGBA。检查前使用 `--copy ... --output artifacts/clipboard-smoke`。
