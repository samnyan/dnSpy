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

### Store both sides of a method patch

`.ilpatch` v1/v2 method entries store both the normalized baseline body and the patched body. This is intentionally redundant. It enables a future three-way merge:

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

## v2 structural change model

Format v2 keeps the v1 method-body representation intact and adds type-scoped structural change sets. v1 documents remain readable.

`ILPatchTypeChange` is the structural unit. For an existing type (`Kind = Modify`) it can record:

- added / removed fields;
- added / removed methods, including CIL bodies;
- added / removed properties and their getter/setter/other-method relationships;
- added / removed events and their add/remove/invoke/other-method relationships.

Whole types are represented with `Kind = Add` or `Kind = Remove`. A whole-type snapshot contains its identity, namespace/name, attributes, base type, optional declaring type for nested classes, and all replayable fields/methods/properties/events. Nested type changes are emitted as their own type changes and retain their parent identity.

Structural replay is semantic rather than token-based. New `TypeDef`, `FieldDef`, `MethodDef`, `PropertyDef` and `EventDef` objects are created first, references/accessors are rebound to those objects, method bodies are then materialized, and removals are committed last. GUI application updates both the dnlib model and Assembly Explorer tree as one `IUndoCommand`, so one Ctrl+Z restores the complete mixed method/structure batch.

The structural subset intentionally fails closed when metadata cannot yet be round-tripped. Important current exclusions include custom attributes, generic type/method parameter definitions and constraints, type interface lists, explicit class layout/declarative security, field constants/marshal/RVA metadata, P/Invoke/override/security method metadata, property constants/`ExplicitThis`, and event custom attributes. Existing type/member metadata mutations are also not silently rewritten merely because add/remove support exists.
## Integration with dnSpy undo/redo

The assembly editor already routes edits through `IUndoCommandService`, so ILPatch integrates with those existing editing paths instead of polling every loaded module.

The current implementation captures a method's body before its first mutation:

- compiler-based C# / VB edits identify the methods affected by the importer;
- raw IL editing and Replace Body With Stub call `ILPatchWorkspace.EnsureTracked()` through `MethodBodyOptions.CopyTo()`.

An auto-loaded workspace listener observes Add / Undo / Redo events and refreshes only methods that are already tracked. It also subscribes to `IDsDocumentService.CollectionChanged`; when a document is removed or the document list is cleared, tracking, pending mutations and edit-history rows belonging to those `ModuleDef` instances are removed automatically. Imported Exact/Clean-Rebase and v2 structural patches are also applied as one `IUndoCommand`, so one Ctrl+Z reverts the whole imported batch, including Assembly Explorer member/type nodes.

This keeps normal tracking O(number of edited methods), avoids retaining closed assemblies in the singleton workspace, and preserves dnSpy's existing save flow and undo semantics.

## Exact import safety

Import uses a deliberately conservative fail-closed path:

1. Match a method by stable assembly/module/type/name/signature identity.
2. Compare the current normalized body hash with the patch baseline.
3. Report `Exact`, `AlreadyApplied`, `RebasedApplied`, `BaseChanged`, `Missing`, `Ambiguous`, or `Incompatible`.
4. Re-run the preview immediately before Apply so a stale UI state cannot authorize a mutation.
5. Materialize every Exact patched body before changing any method.
6. For v2 structural patches, preflight all type/member operations and every related method body before mutation; structural replay is atomic.
7. Apply the successfully preflighted mixed batch as one dnSpy undo command.

MVID is displayed/provided as source provenance but never used as the primary locator.

Normalized metadata references must be rebound to real dnlib objects before writing a body. The v1 materializer intentionally resolves only references already represented by the target module's metadata / method bodies. Unsupported or unresolved operands fail closed and block the batch instead of guessing a token or silently generating the wrong reference.

`BaseChanged` is never treated as an Exact apply. When three-way analysis proves that all patch hunks are clean and method-body metadata remains compatible, the user can explicitly choose **Apply Clean Rebase**. Structural candidates remain advisory until the user explicitly selects one as a temporary target override.

## CI

The fork's GitHub Actions workflow is enabled and feature-branch pushes validate the implementation across all supported Windows build targets.

Before the Windows build matrix starts, a lightweight `ILPatch.CoreTests` console project runs regression tests directly against the linked core source files. This keeps rebase/matching/batch-composition regressions independent from the WPF/MEF application startup path.

CI then builds the CLI, runs a real child-process integration flow (`input.dll + .ilpatch -> ilpatch apply -> output.dll -> reload/verify`), and publishes both a framework-dependent Windows x64 package and a portable `dotnet ilpatch.dll` package as short-lived workflow artifacts. On `release` events, a separate write-scoped job waits for the CLI gate and Windows build matrix, downloads those two artifacts, archives them, and attaches them to the GitHub Release.

## Planned milestones

### Phase 1 - tracking foundation

- [x] Versioned in-memory patch model.
- [x] CIL normalizer and deterministic method-body hash.
- [x] Workspace model with baseline/current separation and edit history.
- [x] Hook method-affecting undo commands into the workspace.
- [x] Remove tracked methods/history automatically when dnSpy documents close.
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
- [x] Multi-file GUI import for independent method targets, with source-file provenance shown per row.
- [x] Apply Exact + Clean-Rebase entries together as one preflighted, undoable **Apply Safe** batch.
- [x] Reject duplicate patch ids or overlapping target methods in one GUI batch so order-dependent patches must be applied/rebased sequentially.
- [x] Never write the assembly automatically; saving remains an explicit dnSpy action.

The import dialog supports selecting multiple `.ilpatch` files. Independent entries are combined into one preview and one undoable application batch, while the **Source** column keeps each method traceable to its original patch file. The batch composer intentionally refuses two selected files that target the same method identity: those patches may depend on application order, so silently flattening them against one pre-mutation baseline would be unsafe.

### .dnspy change repository (foundation)

ILPatch repository mode is intentionally closer to a small source-control repository than to a folder of DLL backups. A repository lives beside the tracked module:

```text
Game/
├─ Assembly-CSharp.dll
└─ .dnspy/
   ├─ repo.json
   ├─ base/
   │  └─ Assembly-CSharp.dll
   ├─ commits/
   │  └─ <commit-id>.json
   └─ patches/
      └─ <commit-id>.ilpatch
```

The immutable `base/` DLL is the repository ROOT. Each commit stores a full **ROOT -> commit state** semantic patch plus parent/message/time/per-method summary metadata. This is deliberately snapshot-oriented in v1: every node can be exported independently from ROOT, while the parent chain still provides Git-like history and future room for staging/branching/deduplicated object storage.

Before a commit is accepted, repository mode validates that the current working baseline is actually based on HEAD. Methods already changed in HEAD must appear with the HEAD patched hash; untouched methods must also match HEAD. Opening an older/root DLL and accidentally committing on top of it therefore fails closed instead of silently rewriting history.

Repository format v2 keeps legacy method-state hashes for backward compatibility and adds a full structure-state fingerprint. Repositories can commit/review/export replayable v2 field/method/property/event/type topology changes. Structural working changes are currently staged atomically with all method-body edits in that working tree, rather than allowing a half-staged class topology.

For history review, stored ROOT-to-commit snapshots are converted on demand into a **parent -> selected commit** semantic delta. This lets the same side-by-side diff viewer show what one commit actually changed without weakening the independent-export property of stored commits.

Repository integrity is verified fail-closed. The immutable ROOT DLL is protected by both its original file SHA-256 and semantic module-state hash. Commit export recomputes the materialized module-state hash and must match the commit metadata before a DLL is written; a modified ROOT or corrupted patch/commit state therefore cannot silently produce a trusted export.

The GUI now exposes this repository through **Change Repository...**. The selected loaded module can be initialized as a repository, committed repeatedly, and reviewed as a normal working-tree/history flow:

```text
Initialize ROOT
   ↓
edit methods in dnSpy
   ↓
review Working Changes / Compare
   ↓
stage the methods that belong to one logical feature
   ↓
Commit Staged Changes
   ↓
staged methods become clean; unstaged methods remain
   ↓
continue editing / stage next logical commit
```

A successful repository commit accepts only that module's current tracked state as the next clean Workspace baseline; other loaded modules are untouched. History shows `HEAD` explicitly. Selecting a commit reconstructs its **parent → commit** semantic delta, and double-clicking a changed method opens the same Normalized IL / Decompiled C# diff viewer used by the working tree. **Export Selected Commit DLL...** materializes any commit (or ROOT) from the immutable repository base and verifies its recorded semantic state hash before writing. Export deliberately refuses to overwrite the currently tracked working DLL.

**Restore Selected to Working Tree** is the safe historical-checkout operation. It materializes the selected commit/ROOT, computes a current-working-tree → selected-state semantic patch, preflights every changed method, then applies the whole restore as one dnSpy undo command. Repository HEAD is not moved and the disk DLL is not written automatically. Any pre-existing uncommitted working changes are replaced, but Ctrl+Z restores them. After restore, all resulting working changes are intentionally unstaged; review/stage them and commit if you want the historical state to become a new linear commit.

Restore is enabled only when the loaded working tree is already based on repository HEAD. If the loaded DLL is an older/root/diverged state, first export/open HEAD (or otherwise synchronize the loaded module to HEAD). This keeps the workspace baseline, undo behavior and subsequent commit parent unambiguous. Repository format v2 still avoids detached-HEAD/branch semantics while making arbitrary historical states editable. Legacy v1 repositories remain readable.

Repository commits support **method-level staging** when the working tree contains method-body edits only. The Working Changes table has a Stage checkbox plus **Stage All / Unstage All** controls. A commit validates the complete working tree against HEAD but advances HEAD using only staged method changes; unstaged methods remain in the in-memory working tree and can be committed later. The commit state hash is materialized from immutable ROOT plus the newly selected full-state patch, rather than hashing the current in-memory module (which may still contain unstaged edits). After commit, only staged methods are accepted as new Workspace baselines. Their tracking entries remain alive with the committed body as the new baseline, so a later dnSpy Undo/Redo or further edit immediately becomes a new working-tree change relative to HEAD. When structural type/member changes are present, the current implementation requires the complete structural working state (and all method-body rows) to be staged together so a commit cannot contain half of a class topology. Hunk-level staging inside one CIL method remains a separate future feature.

Repository mode also distinguishes the in-memory working tree from the DLL currently on disk. The window reports whether the disk file matches HEAD, is still at ROOT/behind HEAD, or differs from both. After committing in-memory edits, use dnSpy's normal **Save Module** before closing if you want the next session to reopen directly on HEAD; repository commits themselves never silently overwrite the working DLL.

### Side-by-side patch diff

Tracked workspace changes and imported patch entries can be opened in a dedicated **Compare** window (or by double-clicking the row). The viewer keeps both sides vertically aligned in one grid and classifies each line as unchanged, added, removed, or modified.

Two review projections are available:

- **Normalized IL** — authoritative patch-oriented representation using stable instruction indices and normalized operands, plus locals/init-locals/exception-handler metadata.
- **Decompiled C#** — review-only projection. The Base and Patched snapshots are materialized against the loaded target method and passed through dnSpy's C# decompiler, then line-diffed side-by-side. If a compatible target method is unavailable or an operand cannot be rebound safely, this mode reports the reason and leaves Normalized IL available.

The C# diff deliberately never becomes the source of truth for apply/rebase decisions; decompiler output can change across decompiler versions even when IL semantics do not.

The viewer is optimized for code-review rather than raw text dumping: unchanged regions are collapsed to three context lines around each change by default, **Show all unchanged** expands the complete method, and **Previous change / Next change** jumps between added/removed/modified rows. A compact summary shows the number of changed, added, removed and modified aligned rows. Because both sides are rendered as one aligned table, scrolling is inherently synchronized between Base and Patched.

Modified rows also receive a token-level inline diff. Identifiers/numbers, whitespace runs and punctuation are compared independently with a small LCS, so C# review can highlight the exact changed tokens inside a line instead of only tinting the entire row. Large pathological lines fall back to whole-line highlighting to keep review responsive.

### Recommended GUI workflow

The Patch Workspace is split conceptually into two workflows:

1. **Create a patch from edits** — load the original assembly, edit CIL using dnSpy as usual, review the tracked normalized IL changes in the upper grid, then choose **Export .ilpatch...**. **Revert Selected** restores one tracked method to the captured pre-edit baseline through dnSpy undo/redo.
   For long-running projects, choose **Change Repository...** instead of repeatedly saving ad-hoc DLL copies: initialize once, commit each completed feature/fix, then review or export any historical commit later.
   If you already have an old original DLL and a separately saved dnSpy-modified DLL, choose **Recover from DLL Pair...** instead. The recovery path writes a v2 `.ilpatch` when it finds replayable structural changes and groups them by declaring type. Unsupported metadata changes still abort recovery instead of being silently omitted.
2. **Replay a patch** — load the target/newer assembly, choose **Import .ilpatch...**, inspect each result and its normalized IL diff, resolve renamed/moved methods with **Use Candidate** only when appropriate, then use **Apply Safe**. If you load/replace/close assemblies after importing, choose **Refresh Preview** to rerun matching against the modules currently loaded in dnSpy without reselecting the patch file; manual target overrides are preserved. Apply Safe combines all current `Exact` and `BaseChanged + Clean` entries into one preflighted dnSpy undo command. Finally, save the modified module using dnSpy's normal save command.
3. **Move the patch baseline forward** — after resolving a newer assembly, choose **Export Rebased...** to write a new definition for future versions without overwriting the source patch.

The replay toolbar shows live counts such as `Apply Safe (5)`, and the status hint explains whether entries are ready, already present, or still need review. **Clear Import** only clears the imported preview, source labels, and manual target overrides; it never reverts changes that were already applied to the loaded assembly.



**Apply Safe** is the batch convenience action. It revalidates the whole preview, materializes every `Exact` entry and every `BaseChanged + Clean` entry first, then submits all of them as one dnSpy undo command. `AlreadyApplied` / `RebasedApplied` entries are skipped and unresolved entries remain untouched in the preview. The separate **Apply Exact** and **Apply Clean Rebase** actions remain available when the user wants finer control.

### Phase 4 - cross-version rebase
Manual candidate selection is session-local until the user explicitly exports an updated definition. Choosing **Use Candidate** stores an in-memory override and re-evaluates Exact/Clean-Rebase safety checks against that method; it never mutates the imported source file in place.

**Export Rebased .ilpatch...** creates a new patch document. Entries proven safe by Exact, Already/RebasedApplied round-trip recovery, or Clean three-way rebase are rewritten onto the current target/baseline. Conflicting or unsupported method entries are preserved unchanged and reported to the user, and v2 structural type records are preserved rather than being stripped.

For instruction-only patches, clean rebase also preserves a limited set of upstream method-body metadata changes. Existing local slots must remain an exact prefix of the current local list, so locals appended by the newer build are safe while removals/reorders/retypes remain blocked. Upstream exception handlers are taken from the current method and translated through the merged instruction map. The analyzer rejects a rebase up front if a preserved current branch/switch target or EH boundary points into a current instruction range that the patch will replace; this avoids presenting a false Clean preview that would only fail later during materialization. Patch-side local/EH/InitLocals edits remain unsupported.



- [x] Structural method fingerprints (calls, fields, strings, constants, types, locals, opcode n-grams and EH shape).
- [x] Advisory candidate scoring with score/margin display.
- [x] Instruction alignment between old baseline, old patched and new current bodies.
- [x] Three-way clean-hunk materialization and undoable application.
- [x] Conflict preview and explicit manual candidate target selection.
- [x] Detect `RebasedApplied` through a reversible normalized round-trip.
- [x] Persist accepted target overrides and clean rebases into a newly exported `.ilpatch` while preserving unresolved entries.
- [x] Preserve append-only upstream local-variable additions when existing slot indexes remain unchanged.
- [x] Preserve upstream exception-handler changes when all current EH boundaries can be translated into the merged instruction body.
- [ ] Rebase patch-side local/EH/InitLocals edits and incompatible upstream local-layout changes.

### Phase 5 - headless application

The headless path uses the same normalized CIL matcher, three-way rebase logic and dnlib body materializer as the GUI.

```powershell
# Apply one or more patches in order.
ilpatch apply Assembly-CSharp.dll patches/001.ilpatch patches/002.ilpatch -o Assembly-CSharp.patched.dll

# A directory expands to top-level *.ilpatch files in deterministic filename order.
ilpatch apply Assembly-CSharp.dll patches/ -o Assembly-CSharp.patched.dll

# Perform the complete matching/rebase/materialization pass without writing a DLL.
ilpatch apply --dry-run Assembly-CSharp.dll patches/001.ilpatch patches/002.ilpatch

# Write a machine-readable report for CI/MCP/batch tooling.
ilpatch apply Assembly-CSharp.dll patches/001.ilpatch -o Assembly-CSharp.patched.dll --json ilpatch-report.json

# Move one resolved patch definition onto a newer assembly baseline without modifying the DLL.
ilpatch rebase Assembly-CSharp.new.dll patches/001.ilpatch -o patches/001.rebased.ilpatch --json rebase-report.json
```

The MVP is deliberately fail-closed:

- `Exact` -> apply;
- `AlreadyApplied` / `RebasedApplied` -> report present and continue;
- `BaseChanged + Clean` -> three-way rebase and apply;
- `Missing`, `Ambiguous`, `Incompatible`, rebase conflict or unsupported materialization -> fail the command;
- a failed command never writes the output assembly;
- the source DLL is never overwritten in place;
- output/report paths are rejected if they would overwrite the input assembly or any source `.ilpatch` file, and output/report paths may not alias each other.

Exit codes are `0` for success, `1` for usage/I/O/unexpected failures and `2` for unresolved patch entries.

`--json <report.json>` writes a camelCase report without changing the normal console output. The report includes the input/output paths, dry-run state, success/exit code, whether an output assembly was written, aggregate applicable/already-present counts, and per-patch/per-entry actions (`Exact`, `CleanRebase`, `AlreadyPresent`, or `Unresolved`). Conflict reports are written before exiting with code 2 and explicitly report `outputWritten: false`.

The CLI replays v2 structural records with the same fail-closed materializer as the GUI, including whole replayable type additions/removals. Structural method *candidates* for renamed/signature-changed existing methods remain advisory and are never auto-selected; use the dnSpy Patch Workspace to confirm a manual candidate and **Export Rebased .ilpatch...** before headless deployment.

`ilpatch rebase` is the headless equivalent of exporting an updated definition for one patch file. It never edits the target assembly and never overwrites the source patch. Every entry must be safely updateable through Exact, Already/RebasedApplied recovery, or a Clean three-way rebase; otherwise the command exits with code 2 and writes no rebased patch. Its optional JSON report records per-entry import/rebase status and whether the output patch was written.

- [x] Extract method-body materialization into pure dnlib core code.
- [x] Add a pure headless apply engine shared by automation code.
- [x] Add `Tools/ILPatch.Cli` with sequential multi-patch and `--dry-run` support.
- [x] Accept patch directories and expand top-level `*.ilpatch` files in deterministic filename order.
- [x] Add headless Exact / Clean-Rebase / fail-closed regression tests.
- [x] Add real CLI child-process / on-disk assembly integration tests for both method-body and whole-type structural replay.
- [x] Publish portable and Windows x64 CLI packages as CI artifacts.
- [x] Wire release events to attach portable and Windows x64 CLI archives to GitHub Releases.
- [x] Add machine-readable JSON report output for success and conflict paths.
- [x] Add fail-closed `ilpatch rebase` for moving a resolved patch definition onto a newer assembly baseline.
- [ ] Add explicit partial-apply mode only if a real workflow needs it.

## Remaining structural non-goals

v2 deliberately does not claim to be a general-purpose metadata merger. Unsupported metadata continues to fail closed, notably:

- arbitrary custom-attribute edits;
- generic parameter/constraint definition changes;
- interface-list and explicit layout changes;
- declarative security, marshal/RVA/P/Invoke metadata not represented by the structural snapshots;
- resources and native/mixed-mode method bodies;
- automatic semantic merging of incompatible structural edits made independently on both versions.

The design keeps method CIL as the authoritative mergeable unit and grows structural support only where the metadata can be represented and replayed deterministically.
