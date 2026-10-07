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
- `colorInputState`, confirmed RGB slider write locations and tab-switch
  freshness remain absent. Stage 3 is not complete.

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
