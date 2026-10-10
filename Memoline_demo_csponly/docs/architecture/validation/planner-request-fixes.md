# Request-scoped reconstruction fixes — 2026-10-08

This change owns `EditIndex.cs`, `ReconstructionPlanner.cs`, and their checks.
It does not operate CSP, change shortcuts, or alter the reserved layer-hide module.

## Failure evidence

- `planner-contract-red.log`: the previous planner passed 5 of 11 new checks;
  six failed: three unrelated-layer blockers, editing an image on another
  layer, replacement subtool acceptance, and conservative downstream geometry.
- `planner-undo-red.log`: 46 of 47 index checks passed. Two complete native
  contacts in independent capture groups reproduce an undo between the two
  contacts blocking selection of the later group. Selecting the earlier group
  correctly remained blocked.
- The first conventional checks build encountered another concurrently edited
  check's missing `WindowsCspEditingActions.BuildOverride` API. That compilation
  failure was not counted as behavioral red evidence. An isolated runner under
  `checks/artifacts/planner-contract-runner` avoids other in-progress test files;
  it links the production host and the existing relevant checks unchanged.

## Behavior

- Layer-local chain/capture issues apply only to layers actually affected by
  the final dependency closure. Unscoped global evidence still fails closed.
- Event editing follows selected strokes' layer identities. Precise undo still
  requires the confirmed current layer and rejects foreign-layer strokes.
- A nonempty replacement subtool name is permitted. The shared restorer must
  route and confirm it. Without verified replacement geometry the estimated
  changed region is the whole canvas; downstream trust remains conservative.
- Undo/redo observations become per-layer command evidence with the original
  hardware appendId. Delayed recognizer results retain that original position.
  Missing native references have appendId zero and cannot be treated as harmless.
  The planner filters commands only after the affected-image closure is fixed.

## Existing assertion change

`EditIndexHardeningChecks` previously asserted that a post-contact undo made the
entire index invalid. It now asserts that the equivalent concrete request is
blocked with `UndoRedoInRange`, while the index itself remains valid for later
requests. Earlier commands, delayed results, confirmed other-layer commands,
unconfirmed targets, missing references, and commands beyond the boundary are
still tested through concrete requests. This preserves the range guard while
removing the erroneous session-wide prohibition.

## Intermediate validation

`planner-contract-partial-build.log` and `planner-contract-partial.log` record
the first three fixes: zero warnings/errors; reconstruction 86 (including 11
new request checks), precise undo 30, cross-layer reconstruction 14, index
hardening 45, and footprint contract 19. This checkpoint predates the undo
metadata implementation and does not claim that phase complete.

The final test set contains 18 request contract assertions and four additional
index assertions (two request-scope/index-validity guards and the two-contact
range case). Three cross-layer assertions now require online promotion to reject
undo/redo before injecting or installing the newly affected layer. Root owns
the shared `UndoRedoEvidence` / `LayerIndex.UndoRedo` contract, the engine Queue
guard, and the combined final build/check run. Final green evidence will be
recorded by that integration run; no parallel full-suite run was started here.
