# Integration Interface Summary

Five independent cores; callers own capture, ROIs, scheduling and Recorder integration.

## Python cores

```python
from recognizer_core.clip_layers_core import read_clip_layers
layers = read_clip_layers(clip_path)  # canvas metadata + depth-first layers
from recognizer_core.layer_state import LayerStateCore
current_layer_name = LayerStateCore(catalog_path=None, ocr_engine=None).process(layer_panel_bgr)
from recognizer_core.panel_state import PanelStateCore
panel_state = PanelStateCore(ocr_engine=None, catalog_path=None).process(properties_bgr)
from recognizer_core.color_state import ColorStateCore
color_state = ColorStateCore().process(color_panel_bgr)
```

Images are `uint8` BGR arrays. Layer State returns `str | None`; Panel State returns schema-v3 `brush` (name and properties); Color State independently returns `kind`, `rgb`, `hex`, `confidence`, `detail`, `hue`, `sv_point`. Color `kind` may be `color`, `transparent`, or `unknown`. CLIP parsing needs Python 3.11+.

## Screen/canvas transform

`using var core = new ScreenCanvasTransformCore(config);`
`var result = await core.InitializeAsync(init);` // three same-frame crops, their ScreenPhysicalPx positions, canvas resolution
`var canvasWindow = result.CanvasWindowRoiScreenPx;` // corrected workspace ROI
`var thumbnail = result.NavigatorThumbnailRoiScreenPx;` // detected thumbnail ROI
`var screenOrigin = result.ScreenCoordinateOriginScreenPx;` // supplied capture's top-left screen position
`var origin = result.CanvasOriginScreenPx;` // top-left canvas `(0,0)` in ScreenPhysicalPx
`var scalePercent = result.OcrScalePercent;` // OCR reading; `null` if unavailable
`var angleDegrees = result.OcrRotationDegrees;` // OCR reading; `null` if unavailable
`init` supplies Workspace, Navigator, OcrNumbers crops (`Image`, `ScreenX`, `ScreenY`) plus `CanvasPixelWidth/Height`. Every subsequent call supplies three new crops in `ScreenCanvasTransformFrameInput`: `await core.RecomputeAsync(nextCrops)`. Stage failures set `Success=false`; an injected scale is never reported as OCR.

## Preserved archive API

Use `new SaveArchiveService(archivesDirectory)` with an explicit path. `TryCreateArchive(input, initResult, service)` saves the original same-frame fingerprint; `TryLoad(id)` validates an archive; `RecomputeFromArchiveAsync(nextCrops, archive)` reuses its frozen anchors. `ArchiveVisualFingerprintService.Match(...)` remains available to check the current frame before recompute. Update archive metadata with `TryUpdateLastSuccessfulRecompute(...)` after success.
