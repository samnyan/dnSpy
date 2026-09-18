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
8. Apply exact matches automatically; later versions will support structural matching, three-way rebasing and conflict resolution.

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
3. Report `Exact`, `AlreadyApplied`, `BaseChanged`, `Missing`, or `Ambiguous`.
4. Re-run the preview immediately before Apply so a stale UI state cannot authorize a mutation.
5. Materialize every Exact patched body before changing any method.
6. Apply all successfully preflighted Exact entries as one dnSpy undo command.

MVID is displayed/provided as source provenance but never used as the primary locator.

Normalized metadata references must be rebound to real dnlib objects before writing a body. The v1 materializer intentionally resolves only references already represented by the target module's metadata / method bodies. Unsupported or unresolved operands fail closed and block the batch instead of guessing a token or silently generating the wrong reference.

`BaseChanged` is not auto-applied. Structural matching and three-way rebasing are Phase 4 work.

## Planned milestones

### Phase 1 - tracking foundation

- [x] Versioned in-memory patch model.
- [x] CIL normalizer and deterministic method-body hash.
- [x] Workspace model with baseline/current separation and edit history.
- [x] Hook method-affecting undo commands into the workspace.
- [ ] Add tests for normalization stability.

### Phase 2 - Patch Workspace UI

- [x] Tool window listing modified methods.
- [x] Per-method edit history.
- [x] Effective normalized IL diff.
- [ ] Revert selected method to baseline.
- [x] Export all effective changes.

The first UI can show normalized IL. A decompiled C# diff can be added as a convenience view later; it must not become the authoritative patch representation because decompiler output is not stable enough for matching.

### Phase 3 - `.ilpatch` import/export

- [x] JSON serialization with explicit format versioning.
- [x] Exact method identity + baseline hash validation.
- [x] Preview before applying with Exact / AlreadyApplied / BaseChanged / Missing / Ambiguous states.
- [x] Apply Exact entries through dnSpy's undo command service so imported patches are undoable.
- [x] Never write the assembly automatically; saving remains an explicit dnSpy action.

### Phase 4 - cross-version rebase

- [ ] Structural method fingerprints (calls, fields, strings, opcode n-grams, CFG).
- [ ] Candidate scoring with Exact / Strong / Ambiguous states.
- [ ] Instruction alignment between old and new normalized bodies.
- [ ] Three-way hunk application.
- [ ] Conflict UI and manual target selection.
- [ ] Rebase/update an `.ilpatch` after a conflict is resolved.

### Phase 5 - headless application

Move format/normalization/matching/apply code into a reusable core library and add a CLI using the same engine:

```powershell
ilpatch apply Assembly-CSharp.dll patches/*.ilpatch -o Assembly-CSharp.patched.dll
```

The CLI should fail without writing output when any patch conflicts unless an explicit partial-apply option is supplied.

## Non-goals for the first version

The first version intentionally supports CIL method-body changes only. Later versions can extend the change-set model for:

- adding/removing methods, fields and types;
- metadata/custom attribute changes;
- compiler-generated async/iterator state-machine members;
- resources;
- native/mixed-mode method bodies.

Keeping these out of v1 lets the method-body workflow become reliable before the patch format grows into a general assembly merge format.
