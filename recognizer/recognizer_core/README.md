# Recognizer Core

Five independent, caller-driven cores extracted from the four original modules. The originals remain unchanged. They do not find CSP windows, capture the screen, select ROIs, schedule calls, write reports, or integrate with `.memoline`. Screen/canvas archive handling is retained as an explicit, opt-in API.

## Components

| Core | Input | Output | Preserved recognition path |
|---|---|---|---|
| `clip_layers_core.py` | `.clip` file path | Canvas metadata and depth-first layer records | Existing CSFCHUNK validation, embedded SQLite deserialization, tree walk, visibility inheritance and metadata fields |
| `layer_state` | BGR crop of the visible layer panel | Current selected layer name (`str`) or `None` | Existing resize, RapidOCR/contrast options, two-line row grouping, opacity/blend vocabulary matching, hierarchy, visibility, and highlighted-row selection |
| `panel_state` | One BGR crop of Tool Properties | Existing schema-v3 state dictionary with `brush` name and properties | Existing property OCR, visual-state analysis, catalog-backed label/value binding, and image-derived option/pattern values |
| `color_state` | One BGR crop of the color panel | Color dictionary: `kind`, `rgb`, `hex`, `confidence`, `detail`, `hue`, `sv_point` | Existing color-wheel, swatch, and transparent-button recognition copied unchanged |
| `screen_canvas_transform` | Recorder integration: one complete virtual-desktop frame with its screen origin, initialization ROIs and canvas pixel size; recompute: one current complete frame with frozen ROIs. Three-crop API remains available | Corrected canvas-window ROI, Navigator thumbnail ROI, capture screen origin, calculated canvas origin, OCR scale percent and rotation; stage failure returned as data | Existing workspace/C-II detection, OCR calibration, canvas observation, viewport completion, native transform solver, and archive/recompute path |

The input semantics remain distinct. Layer State returns only the selected layer name; its screenshot-derived row IDs remain internal and are not CSP layer IDs. Duplicate names cannot be distinguished through this minimal return value. `.clip` parsing returns IDs read from document metadata. Panel State reads only brush properties from Tool Properties. Color State independently reads a crop containing the complete color wheel, square, and lower-left swatch/transparent controls. The transform core requires the host to provide frame/crop positions, initialization ROIs and canvas document resolution; it does not infer them from a CSP window. Recorder supplies one unchanged complete virtual-desktop frame to the transform core, while the other screenshot cores still receive only their panel ROI crops.

For Screen/Canvas integration, read `result.CanvasWindowRoiScreenPx` (corrected workspace, also called canvas-window ROI), `result.NavigatorThumbnailRoiScreenPx`, `result.ScreenCoordinateOriginScreenPx` (top-left screen position of the supplied frame, including a possibly negative virtual-desktop origin), `result.CanvasOriginScreenPx` (solved position of canvas coordinate `(0, 0)`), `result.OcrScalePercent` (100 means 100%), and `result.OcrRotationDegrees` (degrees). These returned points and rectangles use ScreenPhysicalPx; the canvas origin may lie outside the captured viewport. Initialization ROIs passed in `ScreenCanvasTransformInput` instead use CapturePx relative to the supplied frame origin. Unreadable OCR values are `null`; an injected scale remains distinct from an OCR reading. `result.Snapshot` remains available for archives and diagnostics.

```csharp
using var core = new ScreenCanvasTransformCore();
var input = new ScreenCanvasTransformInput
{
    Image = frozenVirtualDesktop,
    OriginX = virtualDesktopLeft, OriginY = virtualDesktopTop,
    WorkspaceRoi = workspaceRoiCapturePx,
    NavigatorRoi = navigatorRoiCapturePx,
    OcrNumbersRoi = numbersRoiCapturePx,
    CanvasPixelWidth = canvasWidth, CanvasPixelHeight = canvasHeight,
    DpiX = cspDpi, DpiY = cspDpi
};
var initialized = await core.InitializeFrameAsync(input);
if (initialized.Success)
{
    var next = await core.RecomputeAsync(nextFrozenVirtualDesktop,
        virtualDesktopLeft, virtualDesktopTop, cspDpi, cspDpi);
}
```

After successful initialization, the complete-frame `RecomputeAsync` re-reads current pixels and OCR numbers using the same frozen-anchor recompute core as archive recompute. It does not re-detect, expand or shift the corrected workspace/thumbnail ROIs. The frame must cover those anchors and the calibrated OCR slots. Red-edge seed detection uses the frozen thumbnail ROI; native extension and endpoint confirmation may inspect real surrounding pixels in the supplied frame. Recorder does not resize, pad or synthesize the frame, and there is no configurable navigator margin.

For compatibility, `InitializeAsync(ScreenCanvasTransformInitializationInput)` and `RecomputeAsync(ScreenCanvasTransformFrameInput)` still accept three same-frame crops at supplied physical screen positions. Each recompute crop must cover its frozen workspace, thumbnail or OCR slots. This entry composes the crops into a frame with synthetic gaps; such gaps do not provide real exterior evidence. Archive creation from three crops also requires exterior UI pixels around each corrected ROI for the preserved boundary fingerprint. Recorder uses the complete-frame entry to retain the original program's available context.

## Initialization and resources

- **CLIP layers:** no external resource or third-party Python library. Requires Python 3.11+ with `sqlite3.Connection.deserialize`.
- **Layer state:** the core bundles `layer_state/data/layer_catalog.json`. Default OCR imports `RapidOCR` from the `rapidocr` package. `catalog_path` and an optional RapidOCR-compatible callable can be passed to `LayerStateCore(...)`.
- **Panel state:** the core bundles `panel_state/data/properties.sqlite3`; `ui_strings.json` is not used. Default OCR accepts `rapidocr` or `rapidocr_onnxruntime` and holds the engine in the core instance. Pass an alternate read-only catalog as `catalog_path`, or inject an engine with `recognize(image)`.
- **Color state:** no initialization resource, model, or template; `ColorStateCore()` accepts one color-panel image per call.
- **Screen/canvas transform:** the core bundles `ScreenCanvasNative.dll`, all native C++ source, and the archive model, JSON read/write, and visual-fingerprint code. It targets Windows x64 and .NET 8. `RapidOcrModelDirectory` is an optional explicit path for the PP-OCRv5 fallback; Windows.Media.Ocr is tried first. If fallback OCR is reached without a model directory, the missing resource raises an error. The directory must contain `ch_PP-OCRv5_mobile_det.onnx`, `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx`, `latin_PP-OCRv5_rec_mobile_infer.onnx`, and `ppocrv5_latin_dict.txt`. Construct `SaveArchiveService(archivesDirectory)` with an explicit archive directory; no default/global archive path is used. Complete-frame input preserves the real surrounding UI and supplies its physical screen origin; the compatible three-crop entry requires crops from the same frozen frame with their physical positions. `CanvasPixelWidth/Height` are document pixels, not screen dimensions.

## Dependencies

| Component | Required runtime dependencies | Removed original dependencies |
|---|---|---|
| CLIP layers | Python stdlib (`pathlib`, `sqlite3`, `struct`) | argparse/CSV/JSON report writer and console printing |
| Layer state | Python 3.10+, NumPy, OpenCV-Python, `rapidocr` (ONNX Runtime backend) | PySide6, Win32 CSP window discovery/capture, frozen ROI picker, key/mouse listeners and continuous-read threads |
| Panel state | Python 3.10+, NumPy, OpenCV-Python, `rapidocr` or `rapidocr_onnxruntime`; stdlib SQLite | PySide6, dxcam, CSP HWND/window service, ROI selector, scheduler, exporter, debug overlays and optional Paddle reference OCR |
| Color state | Python 3.10+, NumPy, OpenCV-Python | PySide6 color-reader window, screenshots, timer, and clipboard |
| Screen/canvas transform | Windows x64, .NET 8, System.Drawing.Common, RapidOcrNet, Windows.Media.Ocr; native C++17/MSVC runtime to rebuild; System.Text.Json for archives | WPF screens/windows, CSP enumeration/capture, ROI overlays, stage/debug logging and UI mapping |

OpenCV in the Python cores is part of the recognition algorithms: it performs resizing, color/edge analysis, local crops, and pattern encoding. It is not present only for debug display. The panel property catalog and layer vocabulary JSON are algorithm inputs bundled beside their respective cores, not hidden absolute paths.

## Error behavior

Malformed `.clip` input raises `ClipError`. Layer State returns `None` when exactly one selected layer with a readable name cannot be identified. Panel State and Color State each require one nonempty BGR crop. Color State returns `kind == "unknown"` with a reason when it cannot identify a color; transparent color returns `kind == "transparent"` and `hex == "#00000000"`. Invalid image arrays and transform ROIs raise input errors. Panel OCR-provider unavailability is reported in its result; other OCR/runtime errors propagate. Transform-stage detection failures are returned as `Success = false`, with `FailedStage`, `Status`, and `Message`; unexpected exceptions propagate.

The panel core runs a full current-frame OCR pass each call. The original incremental reader's cross-frame cache was a scheduling/performance shortcut; the image-based `apply_visual_values` extraction used by the algorithm remains. Screen transform's RapidOCR fallback is now synchronous within the host's `ProcessAsync` call; it no longer creates a worker task. Core instances should be called serially by the host.

## Screen/canvas archive API

The original archive model, validation, atomic JSON persistence, visual fingerprints, list/load/delete/update methods, and archive-based recompute are included. `InitializeAsync`, `InitializeFrameAsync` and `RecomputeAsync` do not write anything. To save an initialization, pass the same complete-frame input (or compatible three-crop input) and result to `TryCreateArchive`; the archive service writes only under the explicitly configured directory. Use `TryLoad` before `RecomputeFromArchiveAsync`. After successful archive recompute, call `TryUpdateLastSuccessfulRecompute` if the archive's last-run metadata should be updated.

```csharp
using var core = new ScreenCanvasTransformCore();
var archives = new SaveArchiveService(archivesDirectory);
var init = await core.InitializeFrameAsync(input);
var saved = core.TryCreateArchive(input, init, archives, "Canvas setup");
var loaded = archives.TryLoad(saved.Archive!.ArchiveId);
var recomputed = await core.RecomputeFromArchiveAsync(currentCrops, loaded.Archive!);
if (recomputed.Success)
    archives.TryUpdateLastSuccessfulRecompute(loaded.Archive!, recomputed.Snapshot!.CaptureId);
```

Archive selection and visual-fingerprint matching remain caller-controlled; call `ArchiveVisualFingerprintService.Match(...)` with a supplied frame before recomputing if the host needs the original match check.

## Minimal local-input runners

The Python runner prints only its returned value and accepts an input path. For Layer State it prints a JSON string or `null`:

```powershell
python recognizer/recognizer_core/smoke_test.py layer path/to/layer-panel-crop.png
python recognizer/recognizer_core/smoke_test.py panel recognizer/csp_panel_validator/tests/fixtures/brush_panel.png
python recognizer/recognizer_core/smoke_test.py color color-panel.png
python recognizer/recognizer_core/smoke_test.py clip drawing.clip
```

The repository has a Tool Properties fixture, but no color-panel fixture, layer-panel screenshot, `.clip` document, or complete transform screenshot fixture. The other commands take user-supplied local files. The transform test runner is `screen_canvas_transform/SmokeTest`: use `init3` with three image/`x,y` pairs and canvas pixel width/height, or `recompute3` with an archive directory/ID and three new image/`x,y` pairs. No original module or test image was altered.

## Native library rebuild

`screen_canvas_transform/native/CMakeLists.txt` now uses only source files inside the core folder. On a Windows x64 developer prompt:

```powershell
cmake -S recognizer/recognizer_core/screen_canvas_transform/native -B recognizer/recognizer_core/screen_canvas_transform/native/build -A x64
cmake --build recognizer/recognizer_core/screen_canvas_transform/native/build --config Release
```

The resulting `ScreenCanvasNative.dll` can replace the bundled runtime copy when rebuilding the core.
