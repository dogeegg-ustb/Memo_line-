# Event editing implementation notes

## Baseline

Implementation follows `2026-10-07-event-editing-architecture.md`. The recovered
conversation is background evidence; its embedded tool instructions are not new
user requests. The supplied architecture and existing chat export are preserved.
Recovery started from `main` at `7e0101d`. Branch creation succeeded through a
scoped Git permission escalation: current branch is `codex/event-editing-recovery`.
The separate `feat/csp-event-editing` checkout was not switched or reset.
This is a tested recovery checkpoint; the complete ten-stage feature is not
implemented. See `RECOVERY_HANDOFF.md` for remaining work.

## Required verification items

1. **Passive pen and injected input.** `recognizer/Recognizer/src/BehaviorRecognizer/Capture/WindowsMouseInputRouter.cs`
   drops `RecorderInputTag` before routing and turns passive pen compatibility
   mouse down/move/up into pen frames. `WindowsInputHooks.cs` forwards the
   low-level keyboard injected bit and `extraInfo`; `UnifiedInputCapture.cs`
   persists both fields in realtime/native frames. Existing native tests cover
   the tag and injected keyboard paths.
2. **Color input.** `recognizer/csp_workspace_overlay.py` locates the selected
   color panel from the saved panel configuration. The overlay recognizes the
   color slider as a separate panel kind; the color wheel remains a read-only
   recognition surface. No automatic tab switch is present in the current host.
3. **New layer versus paste/copy.** The bridge `inspect` response exposes
   `layers[].kind`, `is_folder`, `uuid`, `canvasId`, `visible`, and opacity, but
   no provenance field distinguishing an empty layer from a pasted/copied layer.
   The host must therefore verify the target by exported pixel identity and
   layer UUID rather than infer provenance from `inspect` alone.
4. **Replay source files.** The replay implementation is split between
   `replayer/packetreplayer/source/PacketReplayRunner.cs` and
   `replayer/strokereplayer/source/PenReplayEngine.cs` plus the model/readers in
   the same directories. Their project files are the authoritative compile
   lists; no wildcard exclusion is currently used for replay source files.
5. **Tool catalog shape.** `core.brushState` publishes `state.name` and a
   `properties[]` array with `key`, `value`, `type`, `unit`, `enabled`, and
   `status`. The subtool name is the child name; the parent group is available
   through the catalog/configuration records, not the brush state name.
6. **Causal keys and whitelist.** Existing catalog reports list language-neutral
   keys such as `ink.color_mixing`, `ink.blending_mode`, and
   `anti_overflow.do_not_cross_lines_of_reference_layer`. Property presence is
   the causal signal, including disabled properties. `correction.*` requires a
   catalog whitelist entry; a same-named non-canvas panel is not sufficient.
7. **Undo count storage.** No recorder state currently publishes a CSP undo
   count setting. The initial implementation must treat the configured count as
   an editing policy value and record the source/absence in the journal rather
   than read a guessed location.
8. **Shortcut command names.** Existing shortcut configuration is read through
   the recognizer shortcut module. Recovery adds a writer for
   `layerdelete`/`layervisiblelayer` on test copies. Actual command semantics and
   menucommand types remain live-check items. It does not assume F13+
   availability; proposals use conflict-checked F9–F12. No real user
   configuration was written.
9. **Recognizer Python runtime.** A no-ignore search of published Recognizer
   outputs found no bundled `python.exe`. `UpdateActivatorBridge` selects
   `MEMOLINE_PYTHON` or `py` (with `-3`); `python_libs` contains dependencies,
   not an interpreter. Publishing the writer requires a verified interpreter
   or the architecture's C# winsqlite3 implementation. The Codex test runtime
   is not a product dependency.
10. **Coverage radius.** `RecognitionEvidence.cs` stores the frozen radius in
    `settings.Radius`; `Organizer/dirty matrix/source/Core/Coverage.cs` expands
    each segment by that radius using the swept-segment distance formula. The
    manifest field is the per-label `radius` value and its coverage segments
    are in canvas pixels.

## Earlier Stage 1 verification

The required failure cases were added to `Organizer/canvas layer watcher/checks/DiffChecks.cs`:
a change outside predicted coverage, a first-window diff after an
explicit recording-start baseline, a broken `after.id` chain, and the
4961x7016 full-resolution performance case. The initial run loaded an older
checks output while a stale `CanvasLayerWatcher.Checks` process held the DLL;
the chain assertion consequently reported the old result. A Release rebuild
completed with 0 warnings and 0 errors. Running the rebuilt checks from a
copied output directory reached the IPC fixture after all stage-1 assertions,
so the diff, baseline, chain-breakpoint and `<400 ms` performance assertions
passed. The copied run then stopped at the existing named-pipe permission
boundary (`UnauthorizedAccessException`), before the remaining integration
checks. This was an earlier partial run, not the final full-suite result.
Recovery adds a literal alpha=1 one-pixel case and runs the entire current
watcher suite successfully (197 checks); see the final checkpoint below.

## Blockers

- Live CSP command semantics, color slider OCR behavior after tab switching,
  and exact undo-count storage require the user-owned CLIP copy and are not
  safe to infer or mutate during automated checks. `colorInputState`, undo-count
  acquisition, and product-side shortcut confirmation are not implemented.
- `ReconstructionPlanner`, `ReconstructionEngine`, `PreciseUndoSelector`,
  `CspEditTransaction`, `EditJournal`, v3 containers, Alt+M editing UI and
  controller wiring are absent. Existing offline components are not exposed
  as an executable editing flow.
- Stage 4 index hardening remains required. The final green suites do not
  establish unimplemented planner, transaction, UI or v3 requirements.

## Shared clipboard codec verification

- Initial host build failure: `MemolineDemo.csproj` linked the planned
  `replayer/dirty matrix paster/source/ClipboardImageCodec.cs`, but that file
  did not exist.
- Implemented the codec and refactored `LayerClipboard.DibV5` to use the shared
  straight-RGBA encoder. `EncodePng`, `EncodeDibV5`, and `SetImage` validate
  dimensions; `SetImage` writes PNG and CF_DIBV5 on STA and reads both streams
  back byte-for-byte.
- Passing checks: `dotnet build replayer/dirty matrix paster/source/DirtyMatrixPaster.csproj -c Release --no-restore`
  and `dotnet build Memoline_demo_csponly/source/MemolineDemo.csproj -c Release --no-restore`.

## Recovery slice: replay library, paster codec and shortcut writer

These are related-check results, not a claim that architecture stages 5–8 or the
whole integration are complete. No CSP input, system clipboard write, or user
configuration modification was performed. Existing replay assertions were retained.

### Stage 7: arbitrary event replay

- Red check, before the new core existed:
  `dotnet build replayer/packetreplayer/checks/PacketReplayChecks.csproj -c Release --no-restore -m:1`
  failed with `EventReplayChecks.cs(51,98): error CS0246: IReplayEnvironment`
  (0 warnings, 1 error, 44.20 s).
- `EventReplayRunner` now validates complete contacts, strictly increasing
  penBegin event IDs and native append ordering. It routes every stroke to the
  transaction-confirmed work layer, restores the full brush snapshot per event,
  preserves concentrated/discrete release semantics and computes gaps using the
  next scheduled stroke's original ticks.
- `IReplayEnvironment` provides state/input/view interfaces, a pen injector
  factory, idle function, canvas-point guard and held-input release. `CurrentView`
  supplies the latest live view for per-sample drift guards; `ViewFeed` supplies
  active-request restoration feedback. `WindowsReplayEnvironment` adapts the
  existing standalone components. `PacketReplayRunner.RunAsync` retains its API
  and delegates execution to `RunCoreAsync`; the per-stroke list search was
  replaced by one index dictionary.
- `WindowsViewInput` stamps all SendInput events with `0x4D4C5243`, including
  Unicode text, wheel, keys, buttons and absolute virtual-desktop pointer moves.
  It tracks and releases held keys/buttons. No input was sent during checks.
- Green build: 0 warnings, 0 errors. Full related fake suite, after building:
  `dotnet replayer/packetreplayer/checks/bin/Release/net10.0-windows/PacketReplayChecks.dll`
  ran outside the sandbox because the existing fake IPC fixtures are denied
  inside it. Actual final output:
  `Event replay checks passed: 158 assertions. No CSP input was injected.`
  `Clipboard codec checks passed: 166 assertions. The system clipboard was not modified.`
  `PacketReplay checks passed: 166 assertions. No CSP input was injected.`
  This adds 27 replay and 8 codec assertions to the existing 131.
- One earlier invocation occurred before its build finished and therefore loaded
  the stale check DLL; that run stopped at the existing fake pipe's
  `UnauthorizedAccessException`. It was not counted as a pass. The completed
  build and full run above are the authoritative results.

### Stage 5: existing shared codec review

- No production codec change was necessary. New regression checks passed for
  PNG hidden RGB at alpha=0, alpha=1 and semitransparent RGBA, top-down DIBV5,
  straight BGRA, invalid sizes and MTA rejection before clipboard access.
- Standalone build: `dotnet build "replayer/dirty matrix paster/source/DirtyMatrixPaster.csproj" -c Release --no-restore -m:1`
  succeeded with 0 warnings, 0 errors (0.71 s).
- `ClipboardChecks.ps1` and the standalone program's clipboard formats were
  retained. The actual Windows clipboard smoke workflow was not run.
- Added `backend/checks/alpha-zero.test.mjs`; the existing `checks/*.test.mjs`
  glob includes it. Targeted command
  `node --test --test-isolation=none checks/alpha-zero.test.mjs` passed 1 test,
  fail 0. Simultaneous alpha=0 patches compare all hidden RGB channels and reject
  conflicting data; equal patches and chronological erasure remain exact.

### Stage 8: standard-library shortcut writer

- Red check using the Codex bundled Python executable: importing
  `test_csp_shortcut_writer` failed with
  `ModuleNotFoundError: No module named 'csp_shortcut_writer'`.
- Added `csp_shortcut_writer.py` with JSON `plan/apply/verify/rollback` commands.
  Plan and verify are read-only. Apply requires CSP to be absent and an exact
  confirmation token bound to the complete menu/tool snapshot. Only unassigned
  `layerdelete` and `layervisiblelayer` rows can be provisioned; F9–F12 candidates
  are checked against menu and tool keys. Existing nonempty rows remain intact.
- Existing command rows supply menucommandtype. When the command has no row,
  plan reports `CommandTypeUnconfirmed` unless the caller supplies an explicitly
  verified `confirmedCommandTypes` mapping in the requirements object. No actual
  user's command semantics or command type was assumed or changed.
- Backups include default.khc and existing WAL/journal files with readback hashes.
  The durable provision manifest records original/new rows and backup paths.
  Readback uses the existing `read_menu_shortcuts`; failure compensates the write.
  Rollback rejects altered bindings, tampered original values and bad backup hashes.
- Green command, from `CSP_Shortcut_Manager`:
  `C:/Users/dogeegg/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe -m unittest test_csp_shortcut_writer test_csp_shortcuts -v`
  returned `Ran 11 tests in 0.384s`, `OK` (9 writer tests, 2 existing reader tests).
  Fixtures are temporary copies under `CSP_Shortcut_Manager/tests/artifacts`.
- The prior note claiming `Recognizer/integration/python.exe` was bundled is
  not verified: a no-ignore search of published recognizer outputs found no
  python.exe. The Codex Python used for checks is a development dependency.
  Publishing the writer still requires a verified runtime or the specified C#
  winsqlite3 adapter; neither is claimed complete here.

### Remaining integration limits

- Color slider OCR, wheel freshness after tab switching and main-slot location
  have not been verified on a user-owned CLIP copy. Per §5.2.3, `ColorRestorer`
  only verifies equal confirmed colors and throws `ColorUnrestorable` when a
  fresh color request does not match. No speculative RGB writes are implemented.
- Host transaction/selection/journal/bundle wiring is owned by the host phase.
  The replay environment and writer CLI are reusable components, not a claim
  that those host flows or live acceptance are complete.

## Recovery checkpoint: native, watcher, geometry and host core

2026-10-07. The user allowed a context-limited partial handoff in the recovered
conversation. This checkpoint preserves the original requirements and records
unfinished phases rather than marking green partial components as the whole
feature. No real CSP input, clipboard write or user configuration edit occurred.

### Stages 1–2: exact capture and native protocol

- `LayerDiff` scans all full-resolution RGBA pixels, regardless of predicted
  search bounds. Checks cover a literal alpha=1 one-pixel change outside
  prediction, first-window diff, corrupt/foreign snapshot chains and a strict
  `<400 ms` Release assertion on 4961×7016 images. No runtime pixel result is
  inferred from the preview or predicted bounds.
- Required assertion update: the old optimization assertion
  `FullResolutionComparedPixels == 8192 && CoarseComparedPixels > 0` became
  `FullResolutionComparedPixels == 256*256 && CoarseComparedPixels == 0`.
  §5.3.1 explicitly removes the coarse scan, so retaining its old counter
  expectation would assert forbidden behavior. Existing detected-image and
  pixel-count checks remain, with additional single-pixel checks.
- `RecordingBaselineInitialization` requests native flushed boundaries before
  and after one stable save/export of every non-folder raster layer. It reads
  the appendId prefix and rejects any intervening penBegin, cleans exported
  temporaries and retries at most three times. Same-tick penBegin is detected
  by appendId. Exhaustion records failure rather than installing a baseline.
- `SaveCapture.CaptureBoundaryAsync` saves once and exports each required layer
  UUID; `WatchState.ReserveEditBoundary` includes previously visited penBegin
  layers and the current layer. `CaptureCoordinator` serializes background
  capture, recording-start baselines and the future edit transaction owner.
- Native red output: `validation/stage2-native-red.log` reports missing
  `PrepareEditBoundaryAsync`, `BeginEditTransactionAsync` and
  `EndEditTransactionAsync`. The old recovered draft disposed its recording
  before reading and assumed missing APIs; it was replaced by seven meaningful
  fixture tests in `EditingControlTests.cs`. Existing 110 native tests remain.
- Additional red output: `validation/stage2-recovery-red.log` contains two
  failures: end marker was written on restoration failure, and a direct guard
  configuration bypassed transaction ownership. Both are fixed. Queued commands
  now drain before capture disposal; restoring a failed transaction is retryable.
- Failure-order detail: end restores interception before writing its marker, on
  the same input consumer. This keeps the native exclusion interval open if
  restoration fails. §5.1.2 lists end/restore in the other order; this explicit
  failure-safe ordering is recorded here and must be reconciled during the
  transaction phase before declaring full conformance. No physical input can
  interleave on that consumer between successful restoration and the end marker.
- Watcher failure evidence during recovery: a first-capture baseline was
  incorrectly accepted as recordingStart (`Stage12Checks: NoBaseline`), and
  baseline initialization APIs were missing when new checks were first built.
  Current Stage12Checks/BaselineBarrierChecks exercise explicit baseline trust,
  per-layer exports, save failures, same-tick barrier races and three-attempt
  exhaustion. The entire watcher suite now passes 197 checks.
- Initial green native result was 117 passed; final input-reference hardening
  adds two more tests and the full suite now has 119 passed, 0 failed, 0 skipped
  (`native-full.log`).
  Editing system notifications do not cache as connection snapshots; tag input
  is ignored and other injected mouse/key metadata is retained.
- Exact input-reference red: `native-input-reference-red.log` proves a
  synchronous HardwareAppended subscriber's state-package append stole the
  reported ID (expected input 3, actual 4), and unrecorded mouse motion reused
  an unrelated ID. Writer APIs now return the actual created frame; capture
  records this ID directly per input, independent of reentrant state appends.
  Alerts retain appendId (0 when no direct frame), add inputRecorded and a
  separately named latestAppendId watermark. Existing filtering and normal
  recording kinds are preserved. Green evidence is the full 119-test run.

### Stage 3: partial geometry and same-frame locator

- `transform_host/Program.cs:64–83` serializes Raw ScreenToCanvas and
  CanvasToScreen arrays without recomputing them. The convention is affine
  row-major, canvas units normalized; `state_timeline.py` includes matrices in
  semantic comparison so a matrix-only update is not discarded.
- Red: `stage3-matrices-red.log` contains two `KeyError: 'matrices'` failures.
  Green: two synthetic rotation/origin/semantic tests pass in
  `stage3-matrices-green.log`. This verifies fixture geometry, not real native
  screenshot serialization or user CLIP behavior.
- `LayerStateCore.process` still returns the legacy selected-name string.
  `process_with_locator` performs one OCR read and projects row/eye rectangles
  from the prepared image back to the captured panel screen ROI. The pipeline
  publishes locator and selectedName in the same raw result/capture identity.
- Red: missing `process_with_locator` and legacy semantic mismatch in
  `stage3-locator-red.log`. Current green: three locator tests pass in
  `stage3-locator-green.log`; complete Python suite passes 133 tests.
- Consumer red: `stage3-watcher-affine-red.log` shows watcher ignored the new
  published matrix arrays. `RecognitionEvidence` now accepts both raw snapshot
  and semantic transform matrices, validates the convention, normalized canvas
  space, dimensions, finite coefficients, invertibility and matching inverse.
  Missing new arrays retain legacy affine/rotation support; malformed published
  arrays reject attribution instead of falling back. Fifteen actual-structure
  checks cover a fixed negative-origin, 90-degree, non-square-canvas point and
  invalid contracts. The final full watcher suite passes 212 checks.
- `colorInputState` now has an independent RGB slider observation module,
  same-frame ROI/value-region evidence, initial/fresh-request capture and
  realtime publication. Main/sub/transparent capsule roles and selected-slot
  markers have no real-frame calibration, so `activeColorSlot` remains
  `unknown`, RGB observations remain `ambiguous`, and no confirmed write
  locations are exposed. Manual tab selection and real visual acceptance are
  pending. Stage 3 is not complete; see `validation/stage3-color-input.md`.

### Stages 4–5: partial offline editing core

- Host now compiles reusable replay sources excluding their standalone entry
  programs; the shared clipboard codec remains independently buildable.
- `ToolCatalog` consumes actual flat node/parentId/toolId records.
  `BrushClassifier` checks causal property presence including disabled terms
  and verified canvas categories, with versioned causal/probe resources. The
  catalog's 158 definition_json records supply real keys; blur is
  `ink.intensity_of_blur`. No copy-stamp key was found: resource metadata names
  that unresolved category rather than inventing a property key. Probe wildcard
  `correction.*` was removed in favor of a narrow explicit whitelist.
- Red host logs show absent `ImageCompositor`, `EditIndexBuilder` and
  `LiveSessionReader` APIs (`host-composition-before.log`,
  `host-index-before.log`, `host-reader-before.log`). Green: 26 editing checks
  and five live-reader checks, plus all pre-existing host checks.
- `LiveSessionReader` reads a CRC-checked, continuous appendId prefix with no
  footer requirement. It validates package paths, descriptor sizes, mask counts
  and bidirectional label/image references. It stops at the native boundary;
  it does not silently hide corrupt complete frames.
- PNG hardening red: `validation/host-png-red.log` shows valid RGB, grayscale
  and palette PNGs were silently accepted through GDI+ format conversion.
  The reader now checks the first IHDR, exact dimensions, RGBA color type 6,
  bit depth 8 and compression/filter/interlace methods before decoding. The
  positive checks preserve hidden RGB in RGBA and alpha=0 mask pixels; valid
  Adam7 is accepted. Current reader checks pass 21 assertions, and the editing
  subtotal is 42 (includes those 21); see `host-png-green.log`.
- `EditIndexBuilder` uses (sessionId, beginAppendId) keys, complete pen contacts,
  historical layer/tool evidence, same-layer image links and cross-packet
  deduplication. `LayerTimeline` reverses after patches from the current capture,
  then forward-composes to check byte equality. Masked replacement preserves
  alpha=0 and hidden RGB; conflicting same-group overlaps are rejected.
- Remaining index gaps: full-contact transaction intersection, undo/redo range
  rejection, causal ambiguity/state freshness checks and broader strict baseline
  policy. Footprints Probe is currently based on label centerlines; the required
  native contact sample mapping/penUp exclusion is not wired. Node/C# production
  CLI parity now passes 54 assertions across seven scenarios (see below). Stage 4 is not
  complete, and real CSP clipboard acceptance is still pending for stage 5.
- Fixture-only changes add real raster `kind=1/is_folder=false` metadata to old
  layer tests; old ambiguity assertions remain. BundleChecks exposes its existing
  fixture for new reader tests without changing its assertions. No existing
  behavior check was deleted or weakened.

### Final validation and build limits

All six mandatory suites and the shortcut suite were run against the combined
recovery slice. This is one complete checkpoint, not evidence of all ten phases
or six suites after each unfinished partial component.

| Suite | Authoritative output | Result |
| --- | --- | --- |
| Host | `validation/host-full.log` | integration passed; editing 42 (includes live reader 21), Node/C# parity 54; bundle 65, aggregate 47, lifecycle 14 |
| Native | `validation/native-full.log` | 119 passed, failed 0, skipped 0 |
| Watcher | `validation/watcher-full.log` | 212 checks; no CSP input |
| Replay | `validation/replay-full.log` | 166 cumulative assertions; no CSP input or clipboard modification |
| Python | `validation/python-current.log` | 133 tests, OK |
| Node | `validation/node-full.log` | 24 tests, fail 0 |
| Shortcut reader/writer | `validation/shortcuts-full.log` | 11 tests, OK |

Native/watcher fake pipe tests needed scoped execution outside the filesystem
sandbox; sandbox failure/timeout logs are diagnostic history, not product test
failures or green evidence. A delegated host invocation was initially rejected
by automatic approval review because its scope instruction was misread as
prohibiting that single suite (`host-full-approval-rejection.txt`). Root clarified
the single fake-suite scope and the later scoped invocation was approved and
passed. There is no outstanding approval blocker.

NU1900 reports unavailable NuGet advisory metadata on the restricted network;
the native tests and transform build still succeeded. `Build.ps1` parses and now
runs every required suite plus shortcut checks, and rebuilds the transform host
before copying it. Product publishing and startup smoke were not run. The
existing Memoline.exe was not replaced with an incomplete feature build.

Stage 6, stage 8 v3/journal, stage 9 and stage 10 remain unimplemented. Follow
`RECOVERY_HANDOFF.md`; live acceptance items are in `LIVE_CHECKLIST.md` and are
pending until the corresponding product controls exist.

### Stage 5 follow-up: production C#/Node CLI parity

- Added `checks/CompositorParityChecks.cs` and the transport-only
  `replayer/dirty matrix paster/backend/checks/compositor-parity-runner.mjs`.
  The runner deliberately has no `.test.mjs` suffix; the host check drives it.
  `checks/Program.cs` invokes parity once in the full suite and supports a
  targeted `--compositor-parity-only` run.
- Red evidence is `validation/compositor-parity-red.log`: after the C# harness
  compiled, the targeted run failed with `FileNotFoundException` because its
  Node companion did not exist. This is an honest missing-harness API red; the
  pre-existing C# alpha and overlap semantics were not shown to be incorrect.
- Final green evidence is `validation/compositor-parity-green.log`:
  `54 assertions across 7 scenarios`, exit 0. Release build used `-m:1` and
  returned 0 warnings, 0 errors. No production implementation was changed.
- Each deterministic 8x5 scenario writes real watcher-format PNG packets.
  C# calls production `ImageCompositor.ValidateGroup`, `Replace` and
  `LayerTimeline`; the Node adapter runs the actual
  `node engine.mjs --compose <request.json>` CLI, which uses production
  `composition.mjs` and `packet.mjs`. Successful comparison reads the reported
  `layer.rgba` and `mask.gray` bytes directly. The only output conversion is
  placing Node's cropped rows back at their full-canvas offsets; no alternate
  replacement/blend algorithm is used in the test.
- Scenarios cover sparse mask=0/255 patches (including nonzero RGB under
  transparent mask pixels), nonoverlapping crops, multiple time groups with
  reversed input order, opaque overwrite, alpha=1, semitransparency, alpha=0
  erasure with hidden RGB, same-group equal overlaps and conflicting visible
  or hidden RGB overlaps. Rejected cases must fail in C#, in LayerTimeline and
  in the actual Node CLI with a nonzero exit and a conflict reason.
- Accepted cases compare the entire canvas RGBA and mask byte for byte,
  including untouched pixels, then reconstruct each historical boundary from
  Node's final canvas and verify LayerTimeline reverse/forward round trips.
  No clipboard or CSP action is invoked. Root reran the combined host suite
  after this check was connected (`host-full.log`); targeted success alone is not claimed
  as completion of all stage-5 acceptance or all ten architecture stages.

### Stage 3 follow-up: partial RGB input observation

- Added `recognizer_core/color_input_state` alongside the unchanged read-only
  color-wheel core. It accepts caller-supplied pixels only. Vertical
  RGB/HSV/CMYK tab OCR, unique channel rows and observed closed numeric editor
  borders locate RGB values and their screen boxes. Duplicate/out-of-range
  numbers, missing editor pixels or uncertain layouts stay unresolved.
  The color-wheel footer is not exported as an input; visible HSV/CMYK report
  `tabNotRgb`, and absent slider evidence reports `panelNotVisible`.
- The saved color booth ROI supplies a stable `色彩滑块` alias. It does not prove
  the visible palette or selected tab. Color activation and initial capture
  include the independent `colorInputState` module; explicit requests capture
  its registered ROI under the request package identity. No code changes the
  selected tab, sends a color hotkey or writes CSP configuration.
- Slider OCR, border checks, ROI projection and `captureFrameId` use one image.
  Duplicate color-booth ROIs share one actual acquisition. Persisted jobs use
  the slider ROI's own capture start/end, and the color-wheel job carries its
  slider companion so visible slider pixels do not become a color-wheel
  observation. Timeline validation rejects mismatched frame/ROI, bad capture
  span, malformed value regions, invalid RGB values and out-of-ROI locations.
  Unconfirmed observations keep `observedState`/`rawResult` diagnostics while
  publishing empty confirmed `valueRegions`/`tabRegions`/`colorSlotRegions`.
- `activeColorSlot` is deliberately always `unknown`: no actual CSP frame
  verifies main/sub/transparent capsule roles or the selection marker. A
  decorative label, position, bright border or sampled color is insufficient.
  Valid RGB values can therefore appear in the partial raw observation, but
  `status=ambiguous` prevents semantic confirmation and automated writing.
  The tests that exercise a future confirmed semantic path explicitly
  construct a synthetic verified state; they do not establish UI acceptance.
- Manual acceptance still requires evidence of the RGB tab, each editor and
  spin-button exclusion at the user's DPI/theme, active capsule roles and
  selection, text commit behavior, fresh readback, and visual color equality.
  No automatic real UI test was performed. `ColorRestorer` still verifies color
  state; it does not write these sliders. No main/sub/transparent hotkey has
  been inferred or added.
- Red evidence includes 11 original missing-module/behavior checks, an
  unverified selected-slot guess and malformed-region crash, and five
  production persist/companion failures. Final green: 19 targeted Python
  tests, all 152 recorder integration tests (the original 133 retained), and
  two native mock request/topic tests. The sandbox native testhost connection
  timeout is retained separately; the same compiled two tests passed under
  scoped execution outside the sandbox. See `validation/stage3-color-input.md`
  for exact commands and logs. These checks do not complete Stage 3.

### Continued stage 8 slice: durable EditJournal and v3 editing entries

- This continuation supersedes the earlier checkpoint statement that the
  journal/v3 components are absent. It does not claim that product startup,
  recording-stop integration, live CSP acceptance or every stage is complete.
- `source/Editing/EditJournal.cs` owns only `edit-journal.jsonl` and
  `edits/assets/<sha256>.png` under its working directory. A process holds one
  exclusive writer; concurrent producers serialize through its lock. Each
  JSONL append ends in LF and calls `FileStream.Flush(true)`. Records bind a
  contiguous sequence, previous record hash and canonical payload hash.
- APIs explicitly record `Start`, `FreezePlan`, `StartSegment`, `Compose`,
  `Replay`, `Capture`, `CheckpointFlushed`, `LayerHidden`, `WorkLayerDeleted`,
  `ReproductionInstalled`, `Fail` and `Finish`. Start accepts all involved
  `LayerKey`s and retains its old single-layer overload; recovery accepts both
  earlier `targetLayer` rows and new `targetLayers` rows. Every participating
  original UUID is protected from being used as a work/reproduction UUID.
- PNGs are stored as exact original bytes: temporary file, flush, rename,
  hash/length/dimensions/PNG validity readback, then the referencing record.
  Identical PNG bytes reuse one asset. Existing corrupt assets are rejected;
  they are not replaced. Path traversal and reparse paths are rejected.
- `CheckpointFlushed` returns a receipt only after append/flush and full
  ledger/asset readback. Deletion records require the latest receipt and its
  exact work UUID/hash/sequence. Failure records bind the last checkpoint and
  prohibit subsequent replay, hiding, installation and deletion records.
  Escaped multiline failure details remain one physical JSONL row.
- `Recover` is read-only and reports an active transaction and any final
  unterminated row. `Open` truncates only that uncommitted final row; committed
  invalid JSON/UTF-8, blank rows, unknown outer fields, bad hashes, reordered
  records and missing/tampered PNGs fail closed. No recovery automatically
  cleans CSP layers or redraws unfinished strokes. Outcomes are only
  `Executed`, `Failed` and `Cancelled`; the journal never claims PixelVerified.
- `BundleArchive.SealWithEdits(mechanical, aggregate, workDirectory,
  destination)` adds v3 while the existing `Seal` remains v2. The optional
  `edits` index describes `edits/journal.jsonl(.br)` with decoded SHA-256,
  decoded size, codec and deduplicated asset count. Exact PNG entries retain
  their `edits/assets/<sha256>.png` names. An active writer or residual journal
  tail cannot be sealed; missing/tampered/orphan assets and unsupported codecs
  are rejected. All target sessions and replay/checkpoint keys resolve against
  the native recording; editable keys must point to native hardware penBegin.
- `ReadEditJournal` returns null for v1/v2 without edits and uses the same
  `EditJournal.Read(Stream, assetReader)` validator for v3. `Compact` and
  `UpgradeDimensions` preserve the editing ledger, every referenced PNG,
  original mechanical/aggregation bytes and immutable digests. Packet replay
  shares these sources/resources through explicit project references.
- Red evidence: `validation/edit-journal-red.log` contains 53 missing-journal
  API compiler errors; `edit-journal-multilayer-red.log` contains three missing
  multilayer-overload errors. `edit-journal-multiline-failure-red.log` records
  the actual runtime rejection of newline-containing failure details, corrected
  by accepting JSON string messages. `bundle-edits-red.log` contains 24 missing
  v3 API errors plus one fixture EventStamp.AppendId error; that fixture was
  corrected to AppendHardwareRecord. These API reds are not claims that older
  production byte composition was incorrect.
- Final targeted evidence: `validation/edit-journal-green.log` reports **57**
  checks; `bundle-edits-green.log` reports the existing **65** bundle checks
  plus **38** new v3 checks. Builds use `-m:1` and report zero warnings/errors.
  `bundle-edits-replay-green.log` reports **166** fake replay assertions after
  the shared-source project change. Its scoped fake-pipe run required execution
  outside the filesystem sandbox; the earlier access-denied log is retained as
  `bundle-edits-replay-sandbox-denied.log`, not green evidence. No CSP input,
  system clipboard write or real user shortcut configuration change was made.
- Shortcut publication still lacks a verified embedded Python executable or
  completed C# winsqlite writer path. Root owns the later recording-stop/UI
  integration and combined full-suite evidence; this component slice alone
  does not claim those remaining acceptance items.

### Continued stage 9 slice: host UI and live editing boundary wiring

- `DemoApplicationContext` now owns the recorder window, tray menu, editing
  panel and system `Alt+M` registration. Closing the recorder window hides it
  while recording continues; `ExitThreadCore` releases the hotkey, tray and
  panel, and `Program` performs the single controller disposal in its outer+  `finally`.
- `DemoEditingHost.BeginEditSessionAsync` requires an active recording, asks+  `MainWindow.CaptureEditingBoundaryAsync` to freeze and capture the boundary,
  then reads the mechanical prefix and dirty-matrix packets into `EditIndex`.
  Planning is available only after that boundary exists.
- `ExecuteEditAsync` currently returns `ShortcutsMissing` before constructing
  or invoking a CSP transaction because no verified CSP layer/shortcut action
  adapter is connected. It sends no input and does not claim execution or
  pixel verification. `EditingPanel` exposes the boundary result and index+  issues without creating a simulated success path.
- Host build evidence: `dotnet build source/MemolineDemo.csproj -c Release
  --no-restore -m:1 -p:UseSharedCompilation=false -nr:false` completed with
  zero warnings and zero errors. The three runtime check suites also build+  cleanly; their named-pipe integration runs remain environment-limited by+  `UnauthorizedAccessException` and are retained as non-green evidence.

### 2026-10-07: RGB, active editing and project continuation

The later implementation supersedes the UI adapter limitation above. The host
now runs guarded CSP transactions using shared replay, clipboard and capture
components. RGB OCR reuses the calibrated color ROI, including borderless
digits and selected capsule evidence. The supplied RGB screenshot was read by
the packaged RapidOCR engine; all three values and the main slot were confirmed.

Replay inputs and observations are excluded from mechanical history during the
native transaction, while realtime diagnostics remain available. Successful
transactions record one semantic editing action with changes. Physical user
Ctrl+Z after the transaction is recorded normally. Stop and exit wait for editing
cleanup before ending or sealing the recorder.

Project continuation uses a v4 container at the same project path, keeping all
prior native sessions and their original bytes/clocks as flat session entries.
Current editing indexes only the active session. Historical reference-layer
replay is guarded and is not yet connected to the real adapter.

Final offline runs passed: host integration (including 13 continuation, 5 replay
persistence, 9 boundary, 69 transaction and 15 lifecycle checks), 181 replay
assertions, 212 watcher checks, 123 native tests, 153 Python integration tests,
11 shortcut tests, 24 Node backend tests and 2 Rust bridge tests. Host builds
reported zero warnings/errors. Packaged transform restore reported NU1900 because
the advisory feed could not be reached; compilation and publication succeeded.
Root Memoline.exe and its private OCR/native/bridge package were rebuilt and
hash matched against current source/staging. Published editor rendering was
checked at its minimum size without recording or sending CSP input.

No real CSP acceptance run was performed. See RGB_LIVE_EDITING_CONTINUATION.md
and LIVE_CHECKLIST.md for the current scope and remaining acceptance work.

### 2026-10-07：当前架构一致性审查与阻塞

本轮用户说明尚未开始实机复现，要求主要检查实现是否符合主架构，
并明确交互应冻结 CSP 画面后直接在画布位置选择范围。详细结论见
[一致性审查](2026-10-07-architecture-consistency-audit.md)。本轮没有修改产品源码、
重新发布程序或向真实 CSP 输入。

阶段 10 不能视为完成：当前使用独立 PictureBox 预览和当前层笔画复选列表，
未实现架构 §7.2 的原生矩阵屏幕选区、跨层单张图片、附近白线和矢量橡皮扫选。
Alt+M 未自动建立边界，计划详情不足，执行时取消入口随面板隐藏。
上一轮 26 项选择检查验证的是这一偏离设计的预览流程，不能作为阶段 10 合规证据。

本次核实的其他阻塞/差距包括：无关层问题被全局提升导致有效请求被拒绝，
undo/redo 拒绝窗口过宽，更换子工具被计划器拒绝，撤销容量没有按选笔总数+5校验，
真实参考层复现仍预先拒绝，边界到事务开始间隙没有输入连续性证明，
日志 planFrozen 只包含修改请求而非完整调度计划。逐项源码位置和验证范围见审查报告。
事务物理输入排除及滑块生成 colorState 与主架构文字不同。后续整合说明已公开前者，
仍需要统一设计与实现的契约，不能凭已有离线绿测声称符合主架构。

用户最新指定隐藏原图层由其独立开发模块接入。原层隐藏、眼睛定位和隐藏快捷键相关
实现不列入本轮整改范围；接入该模块与画布交互、索引及重放调度应保持独立职责。
集中/离散重放、最终层保留、实际捕获入库后删除中间层的路径已接通；
实机像素和真实撤销栈仍未验收，不将本轮代码审查写成实机通过。

### 2026-10-08：按用户最新流程整改

Alt+M 现在先显示精确撤销和事件编辑入口，选功能后再冻结 CSP。精确撤销直接在当前层选区并显示路径、基础/闭包笔数；事件编辑经过跨层脏矩阵图片、单张图片和整笔扫选。笔刷修改通过用户框选的属性面板截图取得实时完整快照，保留所选笔画，使用现有 packetreplayer 路由及数字读回。最小窗口的控件重叠已修复并检查。

计划器现在保留图层局部问题，命令拒绝区间从闭包的最早受影响接触开始。新边界捕获之前已记录的层；在线新增依赖仍校验链和 undo/redo。撤销容量统一要求实际所选笔数＋5。完整选区和计划调度写入 planFrozen。

最新宿主完整检查、packetreplayer 193 断言、watcher 219 检查、原生 128 测试、Python 155 测试通过；原先已通过的快捷键 11 和 Node 24 项仍有效。新面板选择检查为 32 项，取代偏离设计的旧复选 UI 检查。保留实际失败和环境失败日志，详见 [整改说明](2026-10-08-canvas-editing-corrections.md)。这些都是离线证据，没有真实 CSP 输入或用户配置修改。

活动历史参考层重放仍未完成。历史像素和逐笔场景已接到事务，但指定参考层角色、读回和不污染撤销栈的清理尚无可靠实现；真实适配器提前拒绝。明确关闭的参考复选框可用普通场景，落笔前再次读回，因果分类不降级。隐藏原图层仍等待用户模块，本轮未改其实现。
