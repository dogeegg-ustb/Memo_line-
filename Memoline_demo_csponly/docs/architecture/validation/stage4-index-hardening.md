# Stage 4 index and footprint validation — 2026-10-07

The checks use synthetic native frames, frozen watcher evidence and in-memory pixel masks. No CSP input, clipboard mutation or user configuration change was performed.

## Red before implementation

- `stage4-index-hardening-red-build.log`: clean build, 0 warnings/errors.
- `stage4-index-hardening-red.log`: runtime exit 1; 22/40 checks passed. The 18 failures cover transactions that start during a contact, ambiguous and invalidated history, undo/redo, native sample probes and independently published history.
- `stage4-index-hardening-second-red.log`: runtime exit 1; 42/44 checks passed. A confirmation preceding a later invalidation at the same hardware anchor was incorrectly reused, and a pending native state package incorrectly inherited an older confirmation.
- `stage4-mixed-image-red.log`: runtime exit 1; 44/45 index checks passed. An unknown-layer contact was incorrectly ignored when the same image also contained an eligible contact.
- `stage4-footprint-contract-red.log`: runtime exit 1; 1/8 contract checks passed. Native probes ignored actual published matrices, accepted invalid matrix contracts, and the watcher did not serialize its radius recipe.

## Green after implementation

- `stage4-index-hardening-green-build.log`: 0 warnings/errors.
- `stage4-index-hardening-green.log`: index hardening 45/45, footprint contract 19/19; runtime exit 0.
- `stage4-watcher-affine-green.log`: all existing 15 affine checks passed.
- `stage4-packet-replay-build.log`: 0 warnings/errors.
- `stage4-packet-replay-green.log`: retained sandbox attempt, which stopped at local fake named-pipe access denial. This is not a successful suite result.
- `stage4-packet-replay-unrestricted.log`: existing PacketReplay checks 166/166 passed after automatic approval of the local fake-pipe test command. Its output confirms no CSP input and no clipboard mutation.

The isolated harness is `checks/artifacts/stage4-index-hardening/IndexHardeningHarness.csproj`, built with `dotnet build <project> -m:1 -nr:false -p:UseSharedCompilation=false`. The harness is under the existing excluded artifacts directory. The durable entry points for root integration are `EditIndexHardeningChecks.Run()` and `FootprintContractChecks.Run()`; the subagent did not edit `checks/Program.cs`.

## Rules and actual source contracts

- Each complete contact uses native `penBegin.appendId` through its matching `penEnd.operationId`, or `penInterrupted.relatedEventIds`. Intersection with any `editTransactionBegan`/`editTransactionEnded` interval excludes the whole contact. Unclosed intervals extend through `BoundaryAppendId`, and independent transaction IDs do not close one another.
- Native `shortcutResolved.data.matches[].command` equal to `undo` or `redo` is resolved against `relatedEventIds` and the referenced hardware frames. Its append publication time does not replace the hardware time. Commands overlapping the layer's recorded contact range produce `UndoRedoInRange`; unknown target identity is conservative. Confirmed commands on another UUID do not poison this layer.
- History uses `HistorySlot.Anchor` then `Order`, with the strict `Anchor < beginEventId` rule. `changed`/`unchanged` alone is insufficient when `evidence.causalAmbiguous` is true. The latest invalid value stays invalid; no older confirmed value is substituted.
- Actual `panelUpdateRequested.data.phase="invalidate"`, `modules[]` and `relatedEventIds` invalidate earlier values. A same-anchor restoration needs a later real native confirmation. An unresolved `statePackageReserved` cannot inherit earlier values. Brush, current-layer, saved clip UUID mapping and historical view all require usable evidence.
- `StateHistory.Resolve` now keeps each no-`packageId` `coreStateUpdated` observation as its own slot. Only provisional updates in the same explicit package can collapse into that package's final result. Existing reservation ordering assertions remain unchanged.
- Guaranteed probes map only `drawing.Samples.Where(InContact)` through the same validated `RecognitionEvidence.TryCanvasPoint` consumed by the watcher, then rasterize a 0.75 px centerline. Coarse label segments and pen-up coordinates cannot substitute for the native probe. Missing view or invalid published matrix contracts exclude the contact.
- The shared mapper accepts actual `rawResult.snapshot.matrices` or semantic `state.transform.matrices`, requires row-major normalized convention, finite six-element arrays, invertibility, inverse agreement and matching dimensions. Old affine objects and native geometry fallback remain supported.
- `DirtyLabel.CoverageSettings` carries the exact recipe used by the watcher into `ImageDirtyLabel` and camelCase manifest `labels[].coverageSettings`. `LiveSessionReader` requires all radius calculation inputs when a recipe is declared, validates it, checks the saved radius and packet dimensions, and retains it in `LabelEvidence.CoverageSettings`. Legacy labels with no recipe remain readable with `null`; no settings are guessed or assigned to `FrozenCoverageSettings`.

This validation establishes indexing and evidence contracts. It does not confirm the complete editing UI, real CSP transaction execution or visual color restoration.
