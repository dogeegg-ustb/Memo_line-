# 记艺线 · 独立字形实验台

一个不依赖录制器、无需 OCR 模型的本机实验程序。打开截图，框选**单行文字**，查看识别结果、候选、拒识原因和耗时。目标是完整识别流程达到 **1 ms/字**。

目前支持两条路径：

- **词表文字**：匹配词表内的完整标签。先按宽高和笔画方向初筛，再做灰度相关和局部笔画校验。支持自定义工具、笔刷名词表。
- **数字**：用 `0123456789.,-+%°` 的单字符模板拼读未枚举的数值。用共同基线、完整笔画覆盖和数字语法区分小数点、逗号、符号与单位。仍属实验功能。

匹配不可靠时返回拒识，不自动调用 OCR。字体渲染只在建库时发生；热识别路径不渲染，也不缓存输入图片的识别答案。程序尚未接入录制器。

## 启动

需要 Python 3.10 或以上，首次安装依赖需要联网。

- macOS：双击本目录的 `start-macos.command`。
- Windows：双击 `start-windows.cmd`；需要可用的 `py -3`。

启动器在 `glyph_lab/.venv` 建立独立环境，并打开 `http://127.0.0.1:8766`。关闭运行它的终端，或按 Ctrl+C，退出服务。若端口已占用，可给脚本加 `--port 8767`。

也可以在仓库根目录手动运行：

```bash
python3 -m venv glyph_lab/.venv
glyph_lab/.venv/bin/python -m pip install -r glyph_lab/requirements.txt
glyph_lab/.venv/bin/python -m glyph_lab serve --open
```

Windows 的虚拟环境解释器路径为 `glyph_lab\.venv\Scripts\python.exe`。

工作台在本机回环地址提供页面，截图在本机处理，不上传到外部服务。可以先点“载入合成示例”，再点“识别选中区域”。识别真实截图时，选择字体和字号，框选一行标签，并留少量空白。输入“已知真值”后，可按真值长度计算每字耗时。

## 字体、字号与词表

**字体必须与截图实际使用的字体对应。** 语言、软件和操作系统是不同的配置维度，程序不会根据界面语言自动推断字体。

- Mac 默认预设为 `/System/Library/Fonts/Supplemental/Songti.ttc` 的第 6 个字体（Songti SC Regular），18 px；这是本机实验预设。
- Windows 默认预设为 GDI `SimSun`；只适合作为中文 Windows CSP 的起点。其他系统、软件需按实测配置。
- Mac 宋体不能替代 Windows 宋体做准确率验收。Windows GDI 路径已实现，但尚未在 Windows 上实测。
- DPI 已知时可先按 `12 × DPI/96` 试设 CSP Windows 面板字号，如 150% 为 18 px、175% 为 21 px。这是此前实测的特定规则，不是所有软件的通用规则。标题和其他面板可能另有字号。
- 缺字检查会跳过字体不支持的词，防止把相同方框当作可辨别的文字。

默认使用本目录 `data/ui_words_zh_cn.txt` 的 1350 词简体词表，来源是仓库内 `recognizer/csp_panel_validator/csp_panel_validator/data/ui_strings.json`，内容去重后保持一致。程序目录可整体复制使用，不需要录制器目录。**这不是尚未在本机重建的 11092 词完整简体字典。**

可用 `--words` 指定 UTF-8 逐行 TXT、字符串 JSON 数组、现有 `ui_strings.json`、`text_words.json` 或 CSP 字符串 SQLite 数据库；用 `--language` 选择语言。去重后最长保留 32 字的单行词。当前不支持把整张面板自动分成多行，也不支持用整词路径拼出任意未收录的文字。PS 可以使用相同接口和自定义字体、词表，目前没有 PS 的实测配置。

字形库按字体内容、字体序号、字号、后端、词表和缓存版本区分，缓存在 `.cache/`。改字体或词表会重建；坏缓存会重建。首次建库、加载缓存、读取图片和界面通信不计入每字识别耗时。

## 命令行

以下命令在仓库根目录、已激活虚拟环境后执行。

```bash
# 生成/加载字形库
python -m glyph_lab build

# 识别截图中的一行；ROI 顺序为 x、y、宽、高，坐标来自原图
python -m glyph_lab read screenshot.png --roi 30 90 100 26

# 数字单字符拼读
python -m glyph_lab read number.png --numbers

# Windows 使用实际 GDI 字体
python -m glyph_lab serve --font SimSun --backend gdi --px 21

# 字体文件及 TTC 字体序号
python -m glyph_lab serve --font /path/to/font.ttc --face-index 0 --px 18

# 更换词表、语言
python -m glyph_lab build --words recognizer/CSPevent/csp_strings.sqlite3 --language chinese_tc
```

`read` 输出 JSON，含 `accepted`、`text`、`score`、`margin`、`reason`、候选和 `elapsed_ms`。得分是相似度，不是正确概率。默认阈值 0.84、候选分差 0.04、初筛保留 16 个模板候选。数字另有语法约束；逗号只按小数分隔符处理，不支持千位分隔符、科学计数法或多组数字。

## 复现基准

```bash
python -m glyph_lab benchmark --samples 2000 --rounds 5 --output glyph_lab/runs/validated-18px
python -m glyph_lab benchmark --px 21 --samples 400 --rounds 3 --output glyph_lab/runs/validated-21px
python -m glyph_lab benchmark --words recognizer/CSPevent/csp_strings.sqlite3 --language chinese_tc --samples 800 --rounds 3 --output glyph_lab/runs/validated-large
python -m unittest glyph_lab.tests.test_matching -v
```

三个基准请依次运行，避免争抢 CPU。输出 `report.json`、逐条结果 `cases.json` 和抽样图 `samples.png`。默认样本固定随机种子；数字使用独立随机种子，不随词表抽样变化。更改 `--samples` 可能改变文字抽样及栅格化分组。

复现发布时的依赖版本，可在 Python 3.12 环境执行 `python -m pip install -r glyph_lab/requirements-benchmark.txt`。普通启动器使用较宽的兼容版本范围；依赖与字体版本变化可能影响结果。

计时包围预热后的完整 `recognize()` 调用，包括预处理、初筛、精匹配和结果构造；不含读图、渲染、初始化、UI 或进程通信。每轮混合不同输入，没有答案缓存。分母为**真值字符数**，包含拒识样本；ASCII 数字和标点每个计一字。报告加权均值、P95 和不超过 1 ms/字的调用比例。这是按单行长度折算的吞吐指标，不是每个字符或每次调用的最坏延迟保证。

合成测试包含明暗背景、噪声、渐变、局部高亮和边线；三分之二用原字号渲染后扰动，三分之一用 3 倍字号渲染后下采样。模板来自原字号和 2 倍渲染。后者比直接重放模板更严格，**仍使用相同字体文件与渲染库，不是真实截图或独立渲染引擎的验证**。词表外负例包括追加文字、追加点号、删字和替换前缀；数字负例包括字母、多组数字和非法符号。

本次结果见 [BENCHMARK.md](BENCHMARK.md)。详细开发数据在被 Git 忽略的 `runs/`，可复核的汇总在 `results/`。

新增的 [CSP 官网真实 UI 测试](official/README.md) 已完成：合成测试的高识别率没有迁移到官网截图，并发现数字拆分误识。判断可用性时请优先参考这份真实截图报告。

## 真实截图标注

`--manifest` 可追加真实样本测试。路径相对标注文件；所有行用本次命令指定的同一字体与字号。不同字号分开运行。

```json
[
  {"image": "screenshots/panel.png", "text": "不透明度", "roi": [33, 97, 80, 23]}
]
```

```bash
python -m glyph_lab benchmark --font /path/to/actual-font.ttf --px 18 --manifest labels.json
```

本机已有旧 CSP 面板截图，但没有与之对应的 Windows 宋体；不能用 Mac 宋体上的合成成绩宣称真实 CSP 准确率。下一步需要按软件、系统、DPI、主题建立留出的真实标注集，再决定是否接入录制器。当前字典缓存只缓存模板，真实测试也执行完整识别。

## 代码入口

`core.py` 是文字和数字匹配器；`fonts.py` 负责 Pillow / Windows GDI 字体渲染及缺字检查；`dictionary.py` 读取词表；`benchmark.py` 测准确率与延迟；`server.py`、`ui.html` 是本地工作台；`tests/` 保存括号缺失、小数点/逗号、近似词、未知词、缓存等回归检查。

直接调用时给 `GlyphMatcher(words, FontSpec(...))` 传词表与字体，对选好的灰度/BGR/BGRA `uint8` 单行图像调用 `recognize(image)` 即可，不需要导入项目的录制器或 OCR 模块。
