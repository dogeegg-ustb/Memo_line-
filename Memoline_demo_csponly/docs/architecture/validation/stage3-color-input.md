# Stage 3: partial colorInputState observation

This slice implements RGB slider observation and native publication, not color
restoration or real CSP acceptance. Architecture §5.2.3 defines the module;
§5.3 and §5.5.4 require fresh evidence and guarded restoration.

## Source contract

`recognizer_core/color_input_state/core.py` consumes one supplied uint8 BGR image,
its exact `[x,y,width,height]` screen ROI and its capture identity. It requires
unique vertical RGB/HSV/CMYK labels and unique channel rows. For RGB it requires
unique integer OCR values in 0..255 and a closed editor border observed in those
same pixels; screen value boxes stay inside the image ROI and exclude the
separate spin-button region. Values without editor pixels are not promoted.
The color-wheel footer has no input targets. Visible HSV/CMYK return `tabNotRgb`;
no slider signature returns `panelNotVisible`; partial RGB returns `ambiguous`.

The saved color booth ROI has a `色彩滑块` alias. Its presence is only capture
geometry, never proof of the visible palette, active tab or active color slot.
Initial capture expects `colorInputState`. Color activations capture both
readers, and explicit `request_states` calls request fresh registered slider
pixels for the supplied package. Native request lists and realtime topics
include `colorInputState` / `core.colorInputState` independently of `colorState`.
No `initial_capture.py` exists in this implementation: initial wiring is
`Runtime.refresh` -> `capture_panels` plus `Pipeline.configure` expectation.

Equal booth ROIs use one actual acquisition. Persisted slider jobs carry their
own capture span rather than the batch completion time; color-wheel jobs keep
the slider companion and suppress wheel state when slider pixels are detected.
The existing color-wheel recognition core is unchanged.

The timeline requires raw `captureFrameId == evidence.captureId`, identical
raw/evidence ROI, valid capture start/end, three distinct valid channel regions
and bounded integer RGB/editor coordinates before allowing semantic state.
`panelNotVisible`, `tabNotRgb`, `ambiguous`, malformed regions and inconsistent
frame/ROI/time become `unknown`. Raw/observed diagnostics remain recorded;
confirmed realtime `valueRegions`, `tabRegions` and `colorSlotRegions` are empty
while the state is unknown. Existing per-panel analysis tokens prevent an older
in-flight analysis from publishing after a new activation.

## Unverified fields and manual acceptance

No real CSP frame proves main/sub/transparent capsule roles or selection
markers. The reader therefore always emits `activeColorSlot=unknown` and never
claims `status=ok`. Even synthetic Main/Sub/Transparent text and a bright border
do not count as selection proof. RGB values and boxes may appear in raw partial
diagnostics, but no usable confirmed write locations are published. Tests that
validate the future confirmed timeline/publication path explicitly construct a
synthetic verified state; they are not proof of real UI compatibility.

An operator must manually select the RGB tab and provide real observations of
editor layout, spin buttons, selected capsule role, commit behavior, readback
freshness and visual color equality at the applicable DPI/theme. Unknown roles
must remain unavailable until that independent evidence is implemented. There
is no invented hotkey, automatic tab change or color-wheel footer click.
`ColorRestorer` remains verification-only; this slice does not write colors.

## Red and green evidence

All checks use synthetic OCR/images, fake APIs, saved source inspection or mock
native request state. No live desktop capture, CSP input, clipboard operation,
configuration change, process termination or package installation was performed.

| Log | Evidence |
| --- | --- |
| `stage3-color-input-red.log` | 11 tests: 3 behavior failures (native module/explicit request/unconfirmed status) and 8 absent-core API errors; honest missing API red |
| `stage3-color-input-failclosed-red.log` | 2 tests: decorative selected-slot guess failed and malformed region raised instead of returning unknown |
| `stage3-color-input-persist-red.log` | 1 test, 5 subcase failures: missing wheel companion plus slider capture start/end/latency/duration inherited from batch end |
| `stage3-color-input-green.log` | All 19 targeted Python tests pass |
| `stage3-color-input-python-all.log` | All 152 Python integration tests pass; the original 133 are retained |
| `stage3-color-input-native-green.log` | Sandbox diagnostic only: vstest could not connect to testhost within 90 seconds; aborted, not green evidence |
| `stage3-color-input-native-unrestricted.log` | Same compiled native mock tests rerun with scoped execution: 2 passed, 0 failed |

Python commands, from `recognizer`:

```powershell
& 'C:/Users/dogeegg/AppData/Local/Python/pythoncore-3.14-64/python.exe' -m unittest recorder_integration.tests.test_color_input_state -v
& 'C:/Users/dogeegg/AppData/Local/Python/pythoncore-3.14-64/python.exe' -m unittest discover -s recorder_integration/tests -v
```

Native targeted build/run used `dotnet test` on
`recognizer/Recognizer/tests/BehaviorRecognizer.Tests/BehaviorRecognizer.Tests.csproj`
with `--filter FullyQualifiedName~ColorInputStateTests -m:1 -nr:false
-p:UseSharedCompilation=false`. The approved rerun added `--no-build --no-restore`
and ran only those two mock tests. NU1900 in the first run reports unavailable
NuGet advisory metadata; compilation succeeded. Root owns the combined suite
and final checkpoint. No all-stage completion is claimed here.
