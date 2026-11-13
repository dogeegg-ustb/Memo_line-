# 图标字典：构建、手册释义与接手状态

2026-11-13：本轮基于 `claude/glyph-icon-dictionary`，在 `codex/glyph-icon-dictionary` 开发。当前阶段仍是建字典和验证；运行时识别接入按交接安排最后处理。

## 本轮改动

- `manual_icon_meanings.py`：黑色、橙色编号与面板边框粘连时也能补检，不再依赖先找到独立黑圆；限制合理半径，修正细笔画数字 1 的筛选。
- 手册图标模板尺寸从原来的 18–32 px 扩至默认 12–64 px，每 2 px 一档。可用 `--min-icon-px`、`--max-icon-px`、`--icon-step` 调整；奇数尺寸需设 `--icon-step 1`。这是离线建库搜索范围，尚未验证全手册的耗时。
- 默认读取真实编号。阅读顺序推断仅能通过 `--allow-reading-order` 开启，并明确标为未验证候选。同一截图里重复读到的编号暂时跳过，减少自动关联错误，代价是漏掉这些编号的结果。
- 每条释义保存图集文件、格号、像素摘要、编号读取方式、分数、图标框及手册来源；不再依赖重建时可能变化的顺序 ID。
- `build_icon_dictionary.py` 自动合并输出目录里的 `manual_meanings.json`，也可通过 `--manual-meanings` 指定其他文件。人工 `labels.json` 最后应用，优先级最高。只有单一名称、图标相关度至少 0.9 且编号可靠的结果才补充缺失名称；冲突和低置信结果保存在 `manual_meanings`、`manual_names`，并标记 `manual_review_required`。
- 旧的纯 ID 释义无法确认所属构建，跳过并计入 `skipped`，应重新跑手册。带稳定位置但像素摘要不符的结果也跳过。
- 局部 `--only` 重跑仅替换实际读取成功的截图结果，保留其他截图的已有结果；写文件使用临时文件替换。缺少图集时构建会报错，不会用空字典覆盖原字典。
- 旧手画模板默认不再参与命名，符合交接中的实验结论。保留 `--legacy-template-names` 供复现实验。

## 验证结果与边界

16 项离线回归检查通过，涵盖粘连编号、橙色标记、40 px 图标匹配、NumPy OCR 返回值、人工命名优先、来源副本、重建 ID 变化、像素不符、歧义候选、局部重跑和缺素材保护。

另在官网图层、导航器两页的 5 张截图上，人工核对了 47 处编号标记，当前都能检出，仍有 4 个多余候选。这只是标记位置检测结果，不能解释为数字识别或图标释义准确率；后两步尚无完整实测。参考坐标、官网地址和图片 SHA-256 存在 `tests/fixtures/manual_callouts.json`，截图不提交。

| 官网截图 | 标记数 | 检出 | 多余候选 |
| --- | ---: | ---: | ---: |
| `180_layers_0003.png` | 4 | 4 | 2 |
| `180_layers_0004.png` | 11 | 11 | 1 |
| `180_layers_0005.png` | 11 | 11 | 0 |
| `180_layers_0006.png` | 9 | 9 | 0 |
| `270_canvas_0023.png` | 12 | 12 | 1 |

`180_layers_0004.png` 有 11 处标记，但只对应 9 个编号条目，其中 4、5 重复；这说明不能根据候选数量和排列顺序直接推出编号。插图、字母标记等也可能通过几何筛选，必须继续验证数字读取。

本次已确认官网目录列出 260 页，但没有完成 260 页的图标匹配。本机没有 CSP、`extracted_icons/master_sheets`、`masks.npz` 或另一个会话的 `text_reader.py`。`icons.json` 目前仍是上游提交的数据，本轮没有用模拟素材替换正式字典。

## 运行方法

在仓库根目录执行。以下脚本使用 Python 3.10+；基础依赖为 NumPy、OpenCV、Pillow。下载手册并自举编号模板还需要 RapidOCR、ONNX Runtime 及对应模型。

```sh
python -m pip install numpy opencv-python-headless pillow
python -m unittest discover -s recognizer/CSPevent/tests -p test_icon_dictionary.py -v
python recognizer/CSPevent/check_manual_callouts.py
```

最后一条命令只下载 5 张官网截图并检查编号检测，不需要 CSP、图标像素或 OCR。再次运行可加 `--offline` 使用缓存；JSON 输出单独列出多余候选数。

取得原机器的图集母版后，放到默认 `recognizer/CSPevent/extracted_icons/master_sheets`，或通过 `--sheets` 指定：

```sh
python recognizer/CSPevent/build_icon_dictionary.py --sheets /path/to/master_sheets
python -m pip install rapidocr onnxruntime
python recognizer/CSPevent/manual_icon_meanings.py --only 180_layers
python recognizer/CSPevent/manual_icon_meanings.py --only 270_canvas
```

先复核上述两章的 `manual_meanings.json`：数字是否读对、是否确实指向目标图标、名称是否只是区域标题。确认效果后再跑全目录，并重建以合并释义：

```sh
python recognizer/CSPevent/manual_icon_meanings.py
python recognizer/CSPevent/build_icon_dictionary.py --sheets /path/to/master_sheets
python recognizer/CSPevent/check_icon_dictionary.py
python recognizer/CSPevent/build_icon_dictionary.py --sheets /path/to/master_sheets --sightings recognizer/CSPevent/icon_dictionary/sightings.json
```

`check_icon_dictionary.py` 的录制样本路径仍需按机器调整。`sightings.json` 仍按当前构建的顺序 ID 关联，母版集合变化后必须重新生成，不能直接套用旧 sightings。

如果没有原始母版，也可以在装有 CSP 的机器上，使用仓库已有的 `extract_all_csp_icons.py` 重新提取。该脚本仍需先修改顶部的 `SCAN_DIRS`、`OUTPUT_DIR` 及派生输出路径。不同 CSP 版本不保证生成相同图集，命名与人工标签必须复核。构建脚本现支持 `--scan` 指定本机安装资源位置；已有 `sources.json` 是旧路径缓存，换安装位置后需要删除该缓存再生成。

## 后续顺序

1. 取得母版或同一构建的 `icons.json`、`masks.npz`，完成数字读取及图标配对的真实验证，再跑全手册。
2. 核实 `NodeIcon` 与图集位置的对应。现有 `resource_index` 仅为实验推算，不能视为已证实的映射。
3. 补韩文、其他 Windows 系统语言及标签页字号实测；现有中文 Windows 字体结论不能直接推广到 macOS。
4. 单独构建数值字符匹配，覆盖小数点、负号、百分号等实际格式。
5. 等 `text_reader.py` 合入后接入 glyph reader，迁移期间保留 OCR 回退，再逐步达到全部 OCR 替换。PS 和 macOS 的规则及采样验证仍需单独完成。
