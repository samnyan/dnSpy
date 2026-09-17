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

The assembly editor already routes edits through `IUndoCommandService`. ILPatch should integrate at this boundary instead of polling every loaded module.

The proposed integration is:

```
IUndoCommandService
  Before Execute/Undo
      -> identify affected MethodDef targets
      -> PatchWorkspace.BeginMutation(method)

  After Execute/Undo
      -> PatchWorkspace.EndMutation(method, description)
```

Commands that directly know their method should expose it through a small internal target-provider interface. Initial targets:

- `EditMethodBodyCodeCommand` (C# / VB method editing)
- `EditMethodBodyILCommand` (raw IL editor)
- `ReplaceILMethodBodyWithStub`

This keeps tracking O(number of edited methods) and avoids rescanning a large assembly after every undoable command.

## Planned milestones

### Phase 1 - tracking foundation

- [x] Versioned in-memory patch model.
- [x] CIL normalizer and deterministic method-body hash.
- [x] Workspace model with baseline/current separation and edit history.
- [ ] Hook method-affecting undo commands into the workspace.
- [ ] Add tests for normalization stability.

### Phase 2 - Patch Workspace UI

- [ ] Tool window listing modified methods.
- [ ] Per-method edit history.
- [ ] Effective normalized IL diff.
- [ ] Revert selected method to baseline.
- [ ] Export selected/all changes.

The first UI can show normalized IL. A decompiled C# diff can be added as a convenience view later; it must not become the authoritative patch representation because decompiler output is not stable enough for matching.

### Phase 3 - `.ilpatch` import/export

- [ ] JSON serialization with explicit format versioning.
- [ ] Exact method identity + baseline hash validation.
- [ ] Preview before applying.
- [ ] Apply through dnSpy's undo command service so imported patches are undoable.
- [ ] Never write the assembly automatically; saving remains an explicit dnSpy action.

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
