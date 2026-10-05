# Integration Interface Summary

Six independent cores; callers own capture, ROIs, scheduling, driver selection and Recorder integration.

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

Recognizer's [TransformHost project](../recorder_integration/transform_host/TransformHost.csproj) references this folder's `screen_canvas_transform/ScreenCanvasTransform.Core.csproj` directly. Recorder uses the complete-frame entry:

The 2026-10-03 extracted core preserves this API and can build/test with its own `build.ps1`. To rebuild and replace the host used by Recognizer, run `powershell -NoProfile -ExecutionPolicy Bypass -File recognizer/recorder_integration/update-transform-core.ps1` from the repository root. It updates both published and Release-build `integration/transform_host` directories and checks the deployed native, managed-core and host hashes. See [core build and deployment details](screen_canvas_transform/README.md).

```csharp
using ScreenCanvasTransform.Core;

using var core = new ScreenCanvasTransformCore(config);
var result = await core.InitializeFrameAsync(new ScreenCanvasTransformInput
{
    Image = completeVirtualDesktopFrame,
    OriginX = virtualDesktopLeft, OriginY = virtualDesktopTop,
    WorkspaceRoi = workspaceCapturePx, NavigatorRoi = navigatorCapturePx,
    OcrNumbersRoi = numbersCapturePx,
    CanvasPixelWidth = documentWidth, CanvasPixelHeight = documentHeight,
    DpiX = dpi, DpiY = dpi
});
if (result.Success)
    result = await core.RecomputeAsync(nextCompleteFrame, virtualDesktopLeft, virtualDesktopTop, dpi, dpi);
```

The input ROIs are relative to the supplied frame. Returned points and rectangles are physical screen pixels. Subsequent recompute calls keep the successfully initialized anchors. The compatible three-crop entry remains available:

`using var core = new ScreenCanvasTransformCore(config);`
`var result = await core.InitializeAsync(init);` // three same-frame crops, their ScreenPhysicalPx positions, canvas resolution
`var canvasWindow = result.CanvasWindowRoiScreenPx;` // corrected workspace ROI
`var thumbnail = result.NavigatorThumbnailRoiScreenPx;` // detected thumbnail ROI
`var screenOrigin = result.ScreenCoordinateOriginScreenPx;` // supplied capture's top-left screen position
`var origin = result.CanvasOriginScreenPx;` // top-left canvas `(0,0)` in ScreenPhysicalPx
`var scalePercent = result.OcrScalePercent;` // OCR reading; `null` if unavailable
`var angleDegrees = result.OcrRotationDegrees;` // OCR reading; `null` if unavailable
`init` supplies Workspace, Navigator, OcrNumbers crops (`Image`, `ScreenX`, `ScreenY`) plus `CanvasPixelWidth/Height`. Every subsequent call supplies three new crops in `ScreenCanvasTransformFrameInput`: `await core.RecomputeAsync(nextCrops)`. Stage failures set `Success=false`; an injected scale is never reported as OCR.

## Digit positions in recorder output

TransformHost forwards `result.PipelineState?.OcrLayoutUsed` as `rawResult.ocrLayout` in the recorder's `coreStateUpdated` canvas payload and recorded state-package result. `scaleDigitsScreen` and `rotationDigitsScreen` are nullable rectangles with `left`, `top`, `right`, `bottom` in physical virtual-desktop screen pixels. They are the calibrated digit anchors reused during recompute; reinitialize after moving the Navigator or changing the window layout. Do not add capture-origin or DPI offsets to these coordinates. `scaleSlotScreen` and `rotationSlotScreen` describe larger OCR slots and are not a substitute for missing digit positions.

## Preserved archive API

The archive APIs below belong to the screen/canvas transform core.

Use `new SaveArchiveService(archivesDirectory)` with an explicit path. `TryCreateArchive(input, initResult, service)` saves the original same-frame fingerprint; `TryLoad(id)` validates an archive; `RecomputeFromArchiveAsync(nextCrops, archive)` reuses its frozen anchors. `ArchiveVisualFingerprintService.Match(...)` remains available to check the current frame before recompute. Update archive metadata with `TryUpdateLastSuccessfulRecompute(...)` after success.

## Driver configuration, pressure and coordinates

Both Recognizer and the standalone DriverReader reference `driver_reader/DriverReader.Core.csproj`. The assembly is `DriverReader.Core.dll`, namespace `DriverReader`.

```csharp
using DriverReader;

var driver = DriverMappingSession.Initialize(new DriverInitializationOptions
{
    ConfigPath = userSelectedConfigPath,
    Device = tabletSpecifications,
    Displays = displayTopology,
    ScreenIndexOverride = userSelectedScreenIndex
});
var configuration = driver.Snapshot;
var point = driver.Evaluate(deviceId, rawX, rawY, rawPressure, maxPressure);
```

`Snapshot` returns the selected/discovered profiles, tablet/display specifications, configuration snapshot ID, pressure and coordinate availability, target screen area, `PhysicalToScreen` / `ScreenToPhysical` affine matrices, and warnings. `Evaluate` returns `DriverSnapshotId`, `PressureStatus`, `CoordinateStatus`, `MappedPressure`, `NormalizedPressure`, `PhysicalX/Y`, and `ScreenX/Y`.

Initialization reads the selected file once. No selected path produces `selectionRequired`; explicit opt-out uses `DriverMappingSession.Disabled(...)`. Configuration discovery is optional through `DriverProfileParser.AutoDetectCandidatePaths(...)`. Mapping performs only in-memory calculations. Matrices are row-major 3×3 and act on homogeneous column vectors; screen coordinates are virtual-desktop physical pixels and can be negative. See [coordinate/pressure conventions](driver_reader/README.md) for units, clamping and unsupported modes.
