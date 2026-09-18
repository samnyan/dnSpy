# ILPatch workspace design

## Goal

ILPatch turns dnSpy assembly editing into a replayable patch workflow.

The primary workflow is:

1. Open an original managed assembly in dnSpy.
2. Edit methods normally using **Edit Method (C#)** or **Edit IL Instructions**.
3. dnSpy keeps the edits in memory as it does today.
4. ILPatch automatically tracks the first method body as the baseline and the current method body as the effective change.
5. A Patch Workspace shows edit history and the effective baseline -> current IL diff.
6. Export the effective changes to a versioned `.ilpatch` file.
7. Open a newer build of the assembly and import the old `.ilpatch`.
8. Apply exact matches directly, or review structural candidates and clean three-way rebases when the newer build changed the method.

This is an offline assembly editing feature. It does not inject code or hook the process at runtime.

## Design principles

### Patch semantic CIL, not file bytes

A managed assembly rebuild can change method RVAs, instruction offsets and metadata tokens while preserving the same program logic. ILPatch therefore normalizes method bodies before comparing or matching them.

The canonical representation currently normalizes:

- short/long branch encodings (`br.s` / `br`, `leave.s` / `leave`, etc.);
- implicit argument/local opcodes (`ldarg.0`, `ldloc.2`, `stloc.1`);
- integer constant shorthand (`ldc.i4.0` ... `ldc.i4.8`, `ldc.i4.s`);
- branch and switch operands to instruction indices instead of byte offsets;
- method/type/field operands to stable full names instead of metadata tokens.

The representation intentionally keeps semantic distinctions such as `call` vs `callvirt`.

### Baseline and edit history are different things

Undo history records every operation. A portable patch normally wants only the final effective change.

For a method edited three times:

```
original -> edit 1 -> edit 2 -> edit 3
```

Patch Workspace keeps all three events for review, but exports:

```
original -> edit 3
```

Undoing back to the original body therefore makes the effective patch disappear without deleting the audit history.

### Store both sides of a patch

`.ilpatch` v1 stores both the normalized baseline body and the patched body. This is intentionally redundant. It enables a future three-way merge:

```
base (old original)
ours (old patched)
theirs (new original)
```

That allows an old patch to be rebased onto a new game build instead of replacing an entire method blindly.

### Refuse ambiguous changes

Future fuzzy matching must fail closed. If multiple target methods score similarly, or the upstream build changed the same IL region that the patch changed, the GUI should report a conflict instead of silently guessing.

## v1 data model

`ILPatchDocument`

- format version
- patch name
- creation time
- method changes

`ILPatchMethodChange`

- stable method identity
- source module MVID (provenance only, never the sole locator)
- normalized baseline body
- normalized patched body

`ILPatchMethodIdentity`

- assembly/module name
- declaring type
- method name
- generic arity
- return type
- parameter types
- instance/static (`HasThis`)

`ILPatchMethodBodySnapshot`

- InitLocals / MaxStack
- locals
- normalized instructions
- normalized exception handlers
- deterministic SHA-256 canonical hash

## Integration with dnSpy undo/redo

The assembly editor already routes edits through `IUndoCommandService`, so ILPatch integrates with those existing editing paths instead of polling every loaded module.

The current implementation captures a method's body before its first mutation:

- compiler-based C# / VB edits identify the methods affected by the importer;
- raw IL editing and Replace Body With Stub call `ILPatchWorkspace.EnsureTracked()` through `MethodBodyOptions.CopyTo()`.

An auto-loaded undo listener observes Add / Undo / Redo events and refreshes only methods that are already tracked. Imported Exact patches are also applied as one `IUndoCommand`, so one Ctrl+Z reverts the whole imported batch.

This keeps normal tracking O(number of edited methods), while preserving dnSpy's existing save flow and undo semantics.

## Exact import safety

Import currently has a deliberately conservative exact-only path:

1. Match a method by stable assembly/module/type/name/signature identity.
2. Compare the current normalized body hash with the patch baseline.
3. Report `Exact`, `AlreadyApplied`, `RebasedApplied`, `BaseChanged`, `Missing`, `Ambiguous`, or `Incompatible`.
4. Re-run the preview immediately before Apply so a stale UI state cannot authorize a mutation.
5. Materialize every Exact patched body before changing any method.
6. Apply all successfully preflighted Exact entries as one dnSpy undo command.

MVID is displayed/provided as source provenance but never used as the primary locator.

Normalized metadata references must be rebound to real dnlib objects before writing a body. The v1 materializer intentionally resolves only references already represented by the target module's metadata / method bodies. Unsupported or unresolved operands fail closed and block the batch instead of guessing a token or silently generating the wrong reference.

`BaseChanged` is never treated as an Exact apply. When three-way analysis proves that all patch hunks are clean and method-body metadata remains compatible, the user can explicitly choose **Apply Clean Rebase**. Structural candidates remain advisory until the user explicitly selects one as a temporary target override.

## CI

The fork's GitHub Actions workflow is enabled and feature-branch pushes validate the implementation across all supported Windows build targets.

Before the Windows build matrix starts, a lightweight `ILPatch.CoreTests` console project runs regression tests directly against the linked core source files. This keeps rebase/matching regressions independent from the WPF/MEF application startup path.

## Planned milestones

### Phase 1 - tracking foundation

- [x] Versioned in-memory patch model.
- [x] CIL normalizer and deterministic method-body hash.
- [x] Workspace model with baseline/current separation and edit history.
- [x] Hook method-affecting undo commands into the workspace.
- [x] Add core regression tests for normalization/hash stability and rebase behavior.

### Phase 2 - Patch Workspace UI

- [x] Tool window listing modified methods.
- [x] Per-method edit history.
- [x] Effective normalized IL diff.
- [x] Revert selected method to baseline through dnSpy undo/redo.
- [x] Export all effective changes.

**Revert Selected** restores the MethodBodyOptions snapshot captured immediately before the method's first tracked edit. The revert itself is one dnSpy undo command, so Ctrl+Z restores the edited body. Returning to the baseline removes that method from the effective patch list while retaining edit history.

The first UI can show normalized IL. A decompiled C# diff can be added as a convenience view later; it must not become the authoritative patch representation because decompiler output is not stable enough for matching.

### Phase 3 - `.ilpatch` import/export

- [x] JSON serialization with explicit format versioning.
- [x] Exact method identity + baseline hash validation.
- [x] Preview before applying with Exact / AlreadyApplied / RebasedApplied / BaseChanged / Missing / Ambiguous / Incompatible states.
- [x] Apply Exact entries through dnSpy's undo command service so imported patches are undoable.
- [x] Never write the assembly automatically; saving remains an explicit dnSpy action.

### Phase 4 - cross-version rebase
Manual candidate selection is session-local until the user explicitly exports an updated definition. Choosing **Use Candidate** stores an in-memory override and re-evaluates Exact/Clean-Rebase safety checks against that method; it never mutates the imported source file in place.

**Export Rebased .ilpatch...** creates a new patch document. Entries proven safe by Exact, Already/RebasedApplied round-trip recovery, or Clean three-way rebase are rewritten onto the current target/baseline. Conflicting or unsupported entries are preserved unchanged and reported to the user.



- [x] Structural method fingerprints (calls, fields, strings, constants, types, locals, opcode n-grams and EH shape).
- [x] Advisory candidate scoring with score/margin display.
- [x] Instruction alignment between old baseline, old patched and new current bodies.
- [x] Three-way clean-hunk materialization and undoable application.
- [x] Conflict preview and explicit manual candidate target selection.
- [x] Detect `RebasedApplied` through a reversible normalized round-trip.
- [x] Persist accepted target overrides and clean rebases into a newly exported `.ilpatch` while preserving unresolved entries.
- [ ] Extend safe rebasing across local-layout / exception-handler metadata changes.

### Phase 5 - headless application

The headless path uses the same normalized CIL matcher, three-way rebase logic and dnlib body materializer as the GUI.

```powershell
# Apply one or more patches in order.
ilpatch apply Assembly-CSharp.dll patches/001.ilpatch patches/002.ilpatch -o Assembly-CSharp.patched.dll

# Perform the complete matching/rebase/materialization pass without writing a DLL.
ilpatch apply --dry-run Assembly-CSharp.dll patches/001.ilpatch patches/002.ilpatch
```

The MVP is deliberately fail-closed:

- `Exact` -> apply;
- `AlreadyApplied` / `RebasedApplied` -> report present and continue;
- `BaseChanged + Clean` -> three-way rebase and apply;
- `Missing`, `Ambiguous`, `Incompatible`, rebase conflict or unsupported materialization -> fail the command;
- a failed command never writes the output assembly;
- the source DLL is never overwritten in place.

Exit codes are `0` for success, `1` for usage/I/O/unexpected failures and `2` for unresolved patch entries.

The CLI currently accepts already-resolved patch definitions. Structural candidates are printed for diagnosis but never auto-selected; use the dnSpy Patch Workspace to confirm a manual candidate and **Export Rebased .ilpatch...** before headless deployment.

- [x] Extract method-body materialization into pure dnlib core code.
- [x] Add a pure headless apply engine shared by automation code.
- [x] Add `Tools/ILPatch.Cli` with sequential multi-patch and `--dry-run` support.
- [x] Add headless Exact / Clean-Rebase / fail-closed regression tests.
- [ ] Package the CLI in release artifacts.
- [ ] Add optional machine-readable JSON report output.
- [ ] Add explicit partial-apply mode only if a real workflow needs it.

## Non-goals for the first version

The first version intentionally supports CIL method-body changes only. Later versions can extend the change-set model for:

- adding/removing methods, fields and types;
- metadata/custom attribute changes;
- compiler-generated async/iterator state-machine members;
- resources;
- native/mixed-mode method bodies.

Keeping these out of v1 lets the method-body workflow become reliable before the patch format grows into a general assembly merge format.
