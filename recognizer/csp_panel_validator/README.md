# CSP Panel Validator

一个面向 Windows 的 CLIP STUDIO PAINT 窗口客户区识别验证软件。它自动定位当前 CSP 窗口并直接显示原位冻结框选层，只观察该窗口，不点击、滚动或修改 CSP。

## 设计边界

- 新配置只需一个“工具属性”ROI，框选时应包含当前工具标题和属性区域；程序从属性标题读取工具名称，不再要求单独框选子工具列表。已有双 ROI 配置仍可读取并用高亮行交叉核对；单 ROI 持续识别时，若工具标题和属性标签位置稳定，则沿用名称与标签，只重新读取当前值，最多连续复用 20 帧。
- 初始化截图不保存为字段模板，也不会复用初始化文字框、行号或控件坐标。
- 结果 schema v3 统一放在 `brush` 对象中：`brush.name` 是笔刷名，`brush.properties` 是属性列表。属性先由目录匹配标签，再把同一行标签右侧的数字词段作为值；多个纯色高亮选项输出从 1 开始的选中序号，包裹图案的高亮输出实际裁剪的 PNG（`value.mime_type` 与 `value.png_base64`）。`observed` 保存标签与数值/选项，`evidence` 保存对应图像证据。无法可靠读出的字段标记为 `unknown` 或 `ambiguous`。
- HWND 只在本次运行中使用；配置保存进程名、窗口类、标题、客户区捕获尺寸和 DPI，重启时自动重新发现窗口，不弹出进程/线程/窗口选择列表。
- 窗口仅移动且客户区尺寸/DPI不变时，ROI 仍然有效；客户区尺寸、DPI 或外部布局变化会要求重新框选。
- ROI 坐标统一为目标窗口客户区捕获图像的物理像素坐标，不是整屏坐标。独立浮动面板若属于另一个顶层窗口，不会被静默并入当前捕获。

## 安装与启动

建议使用 Python 3.10-3.14 的 64 位 Windows 环境。本项目固定使用当前维护中的 `rapidocr==3.9.2`，并使用 ONNX Runtime CPU 推理；旧的 `rapidocr_onnxruntime` 包已作为兼容回退，不再作为默认安装依赖。

首次使用时，双击项目目录中的 `start.bat`。它会自动查找兼容的 Python、创建或修复项目内 `.venv`，并在缺少依赖时从 PyPI 安装一次。启动器只为本程序设置 UTF-8 编码，不会修改 Windows 的显示语言、区域格式或系统代码页。后续启动直接复用项目环境。

若提示找不到 Python，需要安装 Python 3.10-3.14 的 64 位运行时；除此之外不需要按 Windows 语言手工配置环境。首次安装依赖需要网络可访问 PyPI。

```powershell
cd csp_panel_validator
py -3.14 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install --index-url https://pypi.org/simple -r requirements.txt
python -m pip install --no-deps -e .
python -m csp_panel_validator.main
```

以上命令用于手动开发环境；日常使用不需要手动激活 venv 或逐条安装依赖，直接运行 `start.bat` 即可。

若本机 pip 被配置为不可访问的镜像，保留 `--index-url https://pypi.org/simple`。构建可分发 wheel：

```powershell
python -m pip wheel . --no-deps --wheel-dir dist
```

首次启动没有配置时，自动隐藏主界面，等待 150 ms 后冻结 CSP 画面；以后点击“初始化 / 重新框选”执行同一流程。框选层是位于 CSP 客户区原位置的无边框窗口，截图不再缩进编辑对话框。程序自动选择前台 CSP，或已存档匹配窗口，再以最大的可见 CSP 窗口为后备，不要求用户选择进程或线程。

### 当前颜色读取器

双击 `start_color_reader.bat` 可单独启动颜色读取窗口。选择“框选屏幕区域”，在任一显示器上拖框，范围应包含 CSP 色轮、方形取色区和左下角的前景/副色/透明色按钮；按 Enter 后每 0.5 秒读取一次当前画面。色块高亮时读取色块像素，透明棋盘格按钮高亮时输出 `RGBA(0, 0, 0, 0)`；找不到这些状态时会尝试从色轮亮条和方形取色点取色。也可用“打开截图”读取静态截图并复制 HEX/RGB/RGBA 结果。

透明色判断依据是棋盘格按钮边框是否比颜色色块的高亮边框更明显。不同 CSP 主题或缩放比例可能改变按钮样式；界面会显示识别置信度，低置信度时应重新框选包含完整色盘的区域。

初始化时只需拖框选择工具属性面板，框内包含面板标题和属性内容；底部“新框”类型保持为“工具属性”，不再切换子工具列表或进行第二次框选。可拖动已有框、拖右下角调整大小、Delete 删除。Enter 保存并识别，Esc 取消保留旧配置，Tab 隐藏/显示底部工具栏。保存后直接识别刚才冻结的同一帧；后续“识别一次 / 开始持续识别”获取新画面，检查工具标题与属性标签后读取当前值。

已有本地虚拟环境时可双击 `start.bat` 从当前源码启动；不要使用旧 dist wheel 判断本次修改。OCR 仅用一个串行后台工作池防止界面卡顿，框选流程没有额外工作线程。

RapidOCR 3.9.2 wheel 已包含默认 small 模型资源，初始化后可离线运行。窗口捕获首选 Win32 `PrintWindow` 客户区后端；如果 CSP 不响应该 API，才尝试 DXcam 的可见窗口客户区裁剪，并在结果中记录 `capture_backend=dxcam_visible_client_crop`。该降级后端不能读取被其他窗口遮挡的 CSP 内容；窗口最小化、关闭或捕获失败会输出 `unavailable`，不会继续显示旧结果。

## 离线回放

实时识别和回放使用同一个 `RecognitionPipeline`。最基本的回放：

```powershell
python scripts/replay.py --profile profiles/example.json --image samples/panel.png --out replay.json
```

没有模型或希望固定 OCR 输入时，可提供 sidecar：

```powershell
python scripts/replay.py --profile profiles/example.json --image samples/panel.png --ocr-json samples/panel.ocr.json --out replay.json
```

sidecar 格式为 `{ "roi_id": [{"box": [[x,y],...], "text": "...", "score": 0.98}] }`，坐标为对应 ROI 内物理像素坐标。它只用于可重复验证解析器，不会替代实时默认 OCR。

## 目录

```text
csp_panel_validator/
  csp_panel_validator/
    models.py              数据模型与结果 schema
    profile_store.py       配置保存、版本及边界/DPI 检查
    capture_service.py     HWND 客户区捕获、PrintWindow、DXcam 可见区域降级与 ROI 裁剪
    window_service.py      CSP 窗口枚举、匹配、DPI 和客户区定位
    window_selector.py     旧窗口选择器（新流程不再调用）
    roi_selector.py        冻结截图框选和 Qt/物理像素转换
    ocr_engine.py          RapidOCR、sidecar 与可选 Paddle 适配
    visual_state.py        行高亮、复选框和启用状态证据
    panel_parser.py        基于当前帧几何关系重建字段
    incremental_reader.py  当前工具与标签校验、只读值和高亮图案提取
    state_assembler.py     同帧结果组装与状态计算
    recognition_pipeline.py纯实时/回放共用流水线
    recognition_scheduler.py后台线程、变化检测和最新帧队列
    exporter.py            JSON、JSONL、证据叠加图
    ui.py                  PySide6 主界面
    main.py                启动入口
  scripts/replay.py        离线回放入口
  tests/                   不依赖桌面环境的解析/配置测试
  examples/                示例结果 JSON 与离线 OCR sidecar
```

## 依赖版本

版本固定在 `requirements.txt`。本验证版默认使用 RapidOCR + ONNX Runtime，不把云端 OCR 或大语言模型作为依赖。PaddleOCR 仅作为可选对照安装包，不进入默认实时链路。

## 验证与已知限制

运行自动测试：

```powershell
python -m unittest discover -s tests -v
```

测试覆盖同帧字段重建、消失字段不残留、ROI 边界和冲突状态、窗口身份匹配及不持久化 HWND。真实 CSP 场景的名称准确率、参数绑定准确率、换笔刷重排和视觉状态准确率需要在用户 CSP 窗口上按 JSONL 结果统计；本仓库没有把测试窗口冒充真实 CSP 验收，也不虚构延迟。每次结果的 `timings_ms` 会记录实际捕获、OCR、解析和总耗时。

输出 JSON 中的 `evidence` 保存 ROI 内物理像素 bounding box、证据类型、OCR 原始分数和绑定依据；界面点击参数行可高亮相应证据。

## 属性库与双面板读取（2026-09-24）

- 一次窗口截图读取工具属性区域。当前工具名称从属性面板标题取得；旧双面板配置仍会额外读取子工具列表的高亮行并交叉核对。
- 高亮行先动态检测，再拼成带空白边距的 OCR 图片，避免 RapidOCR 默认按短边放大窄条造成性能倒退。OCR 坐标还原到原 ROI，JSON 的 `panels` 记录每个面板的区域、耗时和状态。
- 多个高亮候选通过当前工具属性标题核对；无唯一证据则输出 ambiguous。灰色高亮、平铺图标模式仍未验证；不会回退到读取整份列表。
- `data/properties.sqlite3` 包含 150 项文档属性定义；`ui_strings` 表另存本机 CSP 2.0.0 资源包中 547 条相关 UI 词条、11 种语言。82 项文档属性补充了安装资源译名。资源编号只是字符串编号，不是笔刷参数 ID。
- 属性库在启动时读入索引，识别后用标签匹配。库外或歧义标签保留原文；不凭 OCR 数字随意生成参数。图标、曲线、隐藏字段以及未支持控件仍返回 unknown。
- 属性库页面可查看标签、分类、支持范围和译名。属性资料来自公开手册及当前安装版本，不声称是 CSP 全部内部属性数据库；内部 ID、未验证范围保留空值。11 语言是匹配词条覆盖，不代表当前中文 OCR 模型已验证全部语言。
- 匹配索引减少解析工作；目前测得的 OCR 加速来自高亮区域裁剪和图片布局，不能把字典匹配说成直接加速 OCR 模型。

重建文档库和追加安装译名（在项目目录运行）：

```powershell
.venv/Scripts/python.exe -m scripts.fetch_property_docs
.venv/Scripts/python.exe -m scripts.build_property_catalog
.venv/Scripts/python.exe -m scripts.import_csp_resources 'D:/CLIP STUDIO 1.5/CLIP STUDIO PAINT' --version 2.0.0
.venv/Scripts/python.exe -m scripts.verify_both_panels
```

资源读取只读操作，不会修改 CSP。文档覆盖与每项来源在 JSON/SQLite 中保留，原始手册缓存位于 `.tmp/docs`。
