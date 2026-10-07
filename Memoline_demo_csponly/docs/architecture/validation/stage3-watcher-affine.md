# Stage 3 watcher affine consumption

The added fixture uses the actual transform-host shape:
`rawResult.snapshot.matrices.screenToCanvas` / `canvasToScreen`, each a six-number
array with `sct-affine-row-major/v1` and normalized canvas coordinates. Its
512x256 canvas has screen origin (-300,-200), 50% zoom, and 90-degree clockwise
rotation. The expected point (32,64) at screen (-332,-184) is a fixed basis-point
fixture, not calculated with the implementation's mapping formula. Native
geometry rotation is absent in the primary fixture, so the old implementation
cannot pass through its fallback.

## Red

`stage3-watcher-affine-red-build.log` records a successful build with the new
tests and the old mapper. `stage3-watcher-affine-red.log` records the first
assertion failure:

```text
Unhandled exception. System.Exception: Published raw matrices map negative screen origin and rotation to exact non-square canvas pixels
```

## Green

`stage3-watcher-affine-green-build.log` records the Release build with zero
warnings and zero errors. `stage3-watcher-affine-green.log` records:

```text
AFFINE CHECKS: 15 passed
```

The checks cover negative origin, rotation, width/height normalization, semantic
matrix publication, legacy affine objects, native geometry fallback, canvas-size
agreement, convention and canvas-space rejection, array length, non-finite
coefficients and output, singular matrices, and inconsistent inverse pairs.
Invalid published matrix contracts do not silently fall back to geometry.

Commands, from the repository root:

```powershell
dotnet build 'Organizer/canvas layer watcher/checks/CanvasLayerWatcher.Checks.csproj' -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nr:false
dotnet 'Organizer/canvas layer watcher/checks/bin/Release/net10.0-windows/CanvasLayerWatcher.Checks.dll' --affine-checks
```

The focused checks use synthetic data and send no CSP input. The root agent
runs the full shared validation suites after this handoff.
