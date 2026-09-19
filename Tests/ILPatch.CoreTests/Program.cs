using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using Newtonsoft.Json.Linq;

namespace dnSpy.AsmEditor.ILPatch {
	static class Program {
		static int Main(string[] args) {
			if (args.Length != 0)
				return RunUtility(args);

			var tests = new (string Name, Action Run)[] {
				(nameof(BodyHashIgnoresMaxStack), BodyHashIgnoresMaxStack),
				(nameof(SerializerPreservesDistinctOperands), SerializerPreservesDistinctOperands),
				(nameof(CleanRebasePreservesUpstreamInsertion), CleanRebasePreservesUpstreamInsertion),
				(nameof(UpstreamEditInsidePatchRangeConflicts), UpstreamEditInsidePatchRangeConflicts),
				(nameof(UpstreamAppendedLocalCanRebase), UpstreamAppendedLocalCanRebase),
				(nameof(UpstreamReorderedLocalRemainsUnsupported), UpstreamReorderedLocalRemainsUnsupported),
				(nameof(UpstreamExceptionHandlerChangeCanRebase), UpstreamExceptionHandlerChangeCanRebase),
				(nameof(UpstreamRemovedLocalRemainsUnsupported), UpstreamRemovedLocalRemainsUnsupported),
				(nameof(PreservedBranchIntoPatchedRangeIsUnsupported), PreservedBranchIntoPatchedRangeIsUnsupported),
				(nameof(ExceptionHandlerBoundaryInsidePatchedRangeIsUnsupported), ExceptionHandlerBoundaryInsidePatchedRangeIsUnsupported),
				(nameof(BranchIndexShiftDoesNotCreateFalsePatchHunks), BranchIndexShiftDoesNotCreateFalsePatchHunks),
				(nameof(RebasedAppliedRoundTripIsDetected), RebasedAppliedRoundTripIsDetected),
				(nameof(UnappliedUpstreamBodyIsNotRebasedApplied), UnappliedUpstreamBodyIsNotRebasedApplied),
				(nameof(ManualOverrideCanRetargetRenamedMethod), ManualOverrideCanRetargetRenamedMethod),
				(nameof(ManualOverrideRejectsSignatureChange), ManualOverrideRejectsSignatureChange),
				(nameof(UpdatedDefinitionPersistsManualRenamedTarget), UpdatedDefinitionPersistsManualRenamedTarget),
				(nameof(UpdatedDefinitionRebasesCleanUpstreamChange), UpdatedDefinitionRebasesCleanUpstreamChange),
				(nameof(HeadlessApplyExactMutatesTarget), HeadlessApplyExactMutatesTarget),
				(nameof(HeadlessApplyCleanRebasePreservesUpstream), HeadlessApplyCleanRebasePreservesUpstream),
				(nameof(HeadlessConflictDoesNotMutateTarget), HeadlessConflictDoesNotMutateTarget),
				(nameof(DiskRoundTripWritesPatchedAssembly), DiskRoundTripWritesPatchedAssembly),
				(nameof(BatchComposerCombinesIndependentTargets), BatchComposerCombinesIndependentTargets),
				(nameof(BatchComposerRejectsDuplicatePatchIds), BatchComposerRejectsDuplicatePatchIds),
				(nameof(BatchComposerRejectsOverlappingTargets), BatchComposerRejectsOverlappingTargets),
				(nameof(DocumentCreatorCapturesMethodBodyChange), DocumentCreatorCapturesMethodBodyChange),
				(nameof(DocumentCreatorIgnoresMaxStackNoise), DocumentCreatorIgnoresMaxStackNoise),
				(nameof(DocumentCreatorRejectsAddedMethod), DocumentCreatorRejectsAddedMethod),
				(nameof(DocumentCreatorRejectsMethodFlagChange), DocumentCreatorRejectsMethodFlagChange),
				(nameof(DocumentCreatorDiskRoundTripCapturesBodyChange), DocumentCreatorDiskRoundTripCapturesBodyChange),
				(nameof(DiffEngineAlignsModifiedLines), DiffEngineAlignsModifiedLines),
				(nameof(DiffEngineTracksAddedAndRemovedLines), DiffEngineTracksAddedAndRemovedLines),
				(nameof(DiffEngineKeepsStableAnchors), DiffEngineKeepsStableAnchors),
				(nameof(RepositoryCommitHistoryAndExportRoundTrip), RepositoryCommitHistoryAndExportRoundTrip),
				(nameof(RepositoryRejectsDivergedWorkingTree), RepositoryRejectsDivergedWorkingTree),
				(nameof(RepositoryCommitCanRevertParentChange), RepositoryCommitCanRevertParentChange),
				(nameof(RepositoryCommitDeltaUsesParentState), RepositoryCommitDeltaUsesParentState),
				(nameof(RepositoryDetectsTamperedRoot), RepositoryDetectsTamperedRoot),
			};

			int failed = 0;
			foreach (var test in tests) {
				try {
					test.Run();
					Console.WriteLine($"PASS {test.Name}");
				}
				catch (Exception ex) {
					failed++;
					Console.Error.WriteLine($"FAIL {test.Name}");
					Console.Error.WriteLine(ex);
				}
			}

			Console.WriteLine($"{tests.Length - failed}/{tests.Length} ILPatch core tests passed.");
			return failed == 0 ? 0 : 1;
		}

		static int RunUtility(string[] args) {
			try {
				if (args.Length == 2 && StringComparer.Ordinal.Equals(args[0], "create-cli-fixture")) {
					CreateCliFixture(args[1]);
					return 0;
				}
				if (args.Length == 2 && StringComparer.Ordinal.Equals(args[0], "verify-cli-output")) {
					VerifyCliOutput(args[1]);
					return 0;
				}
				if (args.Length == 4 && StringComparer.Ordinal.Equals(args[0], "verify-cli-report")) {
					VerifyCliReport(args[1], bool.Parse(args[2]), int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
					return 0;
				}
				if (args.Length == 2 && StringComparer.Ordinal.Equals(args[0], "verify-cli-rebased-output")) {
					VerifyCliRebasedOutput(args[1]);
					return 0;
				}
				if (args.Length == 4 && StringComparer.Ordinal.Equals(args[0], "verify-cli-rebase-report")) {
					VerifyCliRebaseReport(args[1], bool.Parse(args[2]), int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
					return 0;
				}
				Console.Error.WriteLine("Unknown ILPatch.CoreTests utility command.");
				return 2;
			}
			catch (Exception ex) {
				Console.Error.WriteLine(ex);
				return 1;
			}
		}

		static void CreateCliFixture(string directory) {
			directory = Path.GetFullPath(directory);
			Directory.CreateDirectory(directory);

			string inputPath = Path.Combine(directory, "input.dll");
			string conflictInputPath = Path.Combine(directory, "conflict-input.dll");
			string rebaseInputPath = Path.Combine(directory, "rebase-input.dll");
			string patchDirectory = Path.Combine(directory, "patches");
			Directory.CreateDirectory(patchDirectory);
			string patchPath = Path.Combine(patchDirectory, "001-change.ilpatch");

			var patchSource = CreateNamedIntMethod("Run", 1);
			var patch = CreateRealBodyConstantPatch(patchSource, 2);
			var document = new ILPatchDocument { Name = "cli-process-integration" };
			document.Methods.Add(patch);
			ILPatchSerializer.Save(patchPath, document);

			var inputMethod = CreateNamedIntMethod("Run", 1);
			inputMethod.Module.Write(inputPath);
			var conflictMethod = CreateNamedIntMethod("Run", 3);
			conflictMethod.Module.Write(conflictInputPath);

			var rebaseMethod = CreateNamedIntMethod("Run", 1);
			rebaseMethod.Body!.Instructions.Insert(0,
				new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Nop));
			rebaseMethod.Module.Write(rebaseInputPath);

			Console.WriteLine(inputPath);
			Console.WriteLine(conflictInputPath);
			Console.WriteLine(rebaseInputPath);
			Console.WriteLine(patchPath);
		}

		static void VerifyCliOutput(string outputPath) {
			outputPath = Path.GetFullPath(outputPath);
			using var module = ModuleDefMD.Load(outputPath);
			var method = module.GetTypes()
				.SelectMany(a => a.Methods)
				.Single(a => StringComparer.Ordinal.Equals(a.Name?.String, "Run"));
			var snapshot = CilNormalizer.CreateSnapshot(method);
			Equal(2L, snapshot.Instructions[0].Operand.IntegerValue,
				"CLI process output assembly must contain the patched IL.");
			Console.WriteLine("CLI process output verified.");
		}

		static void VerifyCliRebasedOutput(string outputPath) {
			outputPath = Path.GetFullPath(outputPath);
			using var module = ModuleDefMD.Load(outputPath);
			var method = module.GetTypes()
				.SelectMany(a => a.Methods)
				.Single(a => StringComparer.Ordinal.Equals(a.Name?.String, "Run"));
			var snapshot = CilNormalizer.CreateSnapshot(method);
			Equal(3, snapshot.Instructions.Count,
				"Rebased CLI output must preserve the newer-build instruction count.");
			Equal("nop", snapshot.Instructions[0].OpCode,
				"Rebased CLI output must preserve the upstream nop.");
			Equal(2L, snapshot.Instructions[1].Operand.IntegerValue,
				"Rebased CLI output must apply the stored constant edit.");
			Console.WriteLine("CLI rebased output verified.");
		}

		static void VerifyCliRebaseReport(string reportPath, bool expectedSuccess, int expectedExitCode) {
			reportPath = Path.GetFullPath(reportPath);
			var json = JObject.Parse(File.ReadAllText(reportPath));
			Equal(1, (int?)json["formatVersion"] ?? -1, "CLI rebase JSON report format version mismatch.");
			Equal(expectedSuccess, (bool?)json["success"] ?? !expectedSuccess,
				"CLI rebase JSON report success flag mismatch.");
			Equal(expectedExitCode, (int?)json["exitCode"] ?? -1,
				"CLI rebase JSON report exit code mismatch.");
			Equal(expectedSuccess, (bool?)json["outputWritten"] ?? !expectedSuccess,
				"CLI rebase JSON report outputWritten mismatch.");
			Equal(expectedSuccess ? 1 : 0, (int?)json["updatedCount"] ?? -1,
				"CLI rebase JSON report updatedCount mismatch.");
			Equal(expectedSuccess ? 0 : 1, (int?)json["unresolvedCount"] ?? -1,
				"CLI rebase JSON report unresolvedCount mismatch.");
			var entries = json["entries"] as JArray;
			True(entries is not null && entries.Count == 1,
				"CLI rebase JSON report must contain exactly one fixture entry.");
			Equal(expectedSuccess, (bool?)entries![0]?["updated"] ?? !expectedSuccess,
				"CLI rebase entry updated flag mismatch.");
			if (expectedSuccess) {
				Equal("BaseChanged", (string?)entries[0]?["status"],
					"Successful rebase fixture should report BaseChanged.");
				Equal("Clean", (string?)entries[0]?["rebaseStatus"],
					"Successful rebase fixture should report a Clean three-way rebase.");
			}
			Console.WriteLine("CLI rebase JSON report verified.");
		}

		static void VerifyCliReport(string reportPath, bool expectedSuccess, int expectedExitCode) {
			reportPath = Path.GetFullPath(reportPath);
			var json = JObject.Parse(File.ReadAllText(reportPath));
			Equal(1, (int?)json["formatVersion"] ?? -1, "CLI JSON report format version mismatch.");
			Equal(expectedSuccess, (bool?)json["success"] ?? !expectedSuccess,
				"CLI JSON report success flag mismatch.");
			Equal(expectedExitCode, (int?)json["exitCode"] ?? -1,
				"CLI JSON report exit code mismatch.");
			var patches = json["patches"] as JArray;
			True(patches is not null && patches.Count != 0, "CLI JSON report must contain patch results.");
			var entries = patches![0]?["entries"] as JArray;
			True(entries is not null && entries.Count != 0, "CLI JSON report must contain entry results.");
			string? action = (string?)entries![0]?["action"];
			if (expectedSuccess)
				Equal("Exact", action, "Successful fixture should report an Exact action.");
			else
				Equal("Unresolved", action, "Conflicting fixture should report an Unresolved action.");
			Console.WriteLine("CLI JSON report verified.");
		}

		static void BodyHashIgnoresMaxStack() {
			var target = CreateTarget();
			var first = Snapshot(target, Ldc(1), Op("ret"));
			first.MaxStack = 1;
			first.CanonicalHash = ILPatchBodyHasher.Compute(first);

			var second = Snapshot(target, Ldc(1), Op("ret"));
			second.MaxStack = 99;
			second.CanonicalHash = ILPatchBodyHasher.Compute(second);

			Equal(first.CanonicalHash, second.CanonicalHash,
				"Portable body hash must not change when only MaxStack changes.");
		}

		static void SerializerPreservesDistinctOperands() {
			var target = CreateTarget();
			var change = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(2), Op("ret")));
			var document = new ILPatchDocument { Name = "serializer-operands" };
			document.Methods.Add(change);

			var roundTrip = ILPatchSerializer.Deserialize(ILPatchSerializer.Serialize(document));
			var instructions = roundTrip.Methods[0].PatchedBody.Instructions;
			Equal(ILPatchOperandKind.Integer, instructions[0].Operand.Kind,
				"Integer operand kind must survive JSON round-trip.");
			Equal(2L, instructions[0].Operand.IntegerValue,
				"Integer operand value must survive JSON round-trip.");
			Equal(ILPatchOperandKind.None, instructions[1].Operand.Kind,
				"A later operand-less instruction must not mutate the earlier operand instance.");
			False(ReferenceEquals(instructions[0].Operand, instructions[1].Operand),
				"Deserialized instruction operands must not share a mutable None singleton.");
		}

		static void CleanRebasePreservesUpstreamInsertion() {
			var target = CreateTarget();
			var patch = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(2), Op("ret")));
			var upstream = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, upstream);
			Equal(ILPatchRebaseStatus.Clean, preview.Status, preview.Message);
			True(ILPatchRebaseMerger.TryCreateMergedSnapshot(patch, preview, out var merged, out string error), error);
			NotNull(merged, "Merged snapshot was null.");

			Equal(3, merged!.Instructions.Count, "Merged body should preserve the upstream insertion.");
			Equal("nop", merged.Instructions[0].OpCode, "Upstream nop should remain.");
			Equal(2L, merged.Instructions[1].Operand.IntegerValue, "Patch replacement should be applied.");
			Equal("ret", merged.Instructions[2].OpCode, "Return should remain.");
		}

		static void UpstreamEditInsidePatchRangeConflicts() {
			var target = CreateTarget();
			var patch = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(2), Op("ret")));
			var upstream = Snapshot(target, Op("nop"), Ldc(3), Op("ret"));

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, upstream);
			Equal(ILPatchRebaseStatus.Conflict, preview.Status,
				"Changing the same instruction upstream must not be auto-rebased.");
		}

		static void UpstreamAppendedLocalCanRebase() {
			var target = CreateTarget();
			var baseline = Snapshot(target, Ldc(1), Op("ret"));
			baseline.Locals.Add("System.Int32");
			baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);
			var patched = Snapshot(target, Ldc(2), Op("ret"));
			patched.Locals.Add("System.Int32");
			patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);
			var patch = Change(target, baseline, patched);

			var current = Snapshot(target, Ldc(1), Op("ret"));
			current.Locals.Add("System.Int32");
			current.Locals.Add("System.String");
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, current);
			Equal(ILPatchRebaseStatus.Clean, preview.Status, preview.Message);
			True(preview.LocalsChangedUpstream, "Upstream local append should be detected.");
			True(preview.UpstreamLocalsCompatible, "Append-only locals should preserve existing slot indexes.");
			True(ILPatchRebaseMerger.TryCreateMergedSnapshot(patch, preview, out var merged, out string error), error);
			NotNull(merged, "Merged snapshot was null.");
			Equal(2, merged!.Locals.Count, "Merged body must preserve the appended upstream local.");
			Equal("System.Int32", merged.Locals[0], "Existing local slot 0 must remain stable.");
			Equal("System.String", merged.Locals[1], "Appended upstream local must remain.");
			Equal(2L, merged.Instructions[0].Operand.IntegerValue, "Patch instruction edit must still apply.");
		}

		static void UpstreamReorderedLocalRemainsUnsupported() {
			var target = CreateTarget();
			var baseline = Snapshot(target, Ldc(1), Op("ret"));
			baseline.Locals.Add("System.Int32");
			baseline.Locals.Add("System.String");
			baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);
			var patched = Snapshot(target, Ldc(2), Op("ret"));
			patched.Locals.Add("System.Int32");
			patched.Locals.Add("System.String");
			patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);
			var patch = Change(target, baseline, patched);

			var current = Snapshot(target, Ldc(1), Op("ret"));
			current.Locals.Add("System.String");
			current.Locals.Add("System.Int32");
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, current);
			Equal(ILPatchRebaseStatus.Unsupported, preview.Status,
				"Changing existing local slots must remain fail-closed.");
			True(preview.LocalsChangedUpstream, "Upstream local reorder should be detected.");
			False(preview.UpstreamLocalsCompatible, "Reordered locals must not be treated as compatible.");
		}

		static void UpstreamExceptionHandlerChangeCanRebase() {
			var target = CreateTarget();
			var baseline = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));
			var patched = Snapshot(target, Op("nop"), Ldc(2), Op("ret"));
			var patch = Change(target, baseline, patched);

			var current = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));
			current.ExceptionHandlers.Add(new ILPatchExceptionHandler {
				HandlerType = "Finally",
				TryStart = 0,
				TryEnd = 2,
				HandlerStart = 2,
				HandlerEnd = -1,
				FilterStart = -1,
			});
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, current);
			Equal(ILPatchRebaseStatus.Clean, preview.Status, preview.Message);
			True(preview.ExceptionHandlersChangedUpstream, "Upstream EH change should be detected.");
			True(ILPatchRebaseMerger.TryCreateMergedSnapshot(patch, preview, out var merged, out string error), error);
			NotNull(merged, "Merged snapshot was null.");
			Equal(1, merged!.ExceptionHandlers.Count, "Merged body must preserve the upstream EH.");
			Equal(0, merged.ExceptionHandlers[0].TryStart, "EH try start must remain mapped.");
			Equal(2, merged.ExceptionHandlers[0].TryEnd, "EH try end must remain mapped.");
			Equal(2, merged.ExceptionHandlers[0].HandlerStart, "EH handler start must remain mapped.");
			Equal(2L, merged.Instructions[1].Operand.IntegerValue, "Patch edit must apply under preserved EH metadata.");
		}

		static void UpstreamRemovedLocalRemainsUnsupported() {
			var target = CreateTarget();
			var baseline = Snapshot(target, Ldc(1), Op("ret"));
			baseline.Locals.Add("System.Int32");
			baseline.Locals.Add("System.String");
			baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);
			var patched = Snapshot(target, Ldc(2), Op("ret"));
			patched.Locals.Add("System.Int32");
			patched.Locals.Add("System.String");
			patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);
			var patch = Change(target, baseline, patched);

			var current = Snapshot(target, Ldc(1), Op("ret"));
			current.Locals.Add("System.Int32");
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, current);
			Equal(ILPatchRebaseStatus.Unsupported, preview.Status,
				"Removing an existing local slot must remain fail-closed.");
			True(preview.LocalsChangedUpstream, "Upstream local removal should be detected.");
			False(preview.UpstreamLocalsCompatible, "Removed locals must not be treated as a compatible prefix.");
		}

		static void PreservedBranchIntoPatchedRangeIsUnsupported() {
			var target = CreateTarget();
			var baseline = Snapshot(target,
				Op("nop"),
				Ldc(1),
				Op("ret"));
			var patched = Snapshot(target,
				Op("nop"),
				Ldc(2),
				Op("ret"));
			var patch = Change(target, baseline, patched);

			// The newer build inserted a branch that is not part of the stored patch. It targets
			// the current instruction corresponding to the old ldc.i4 that the patch will replace.
			var current = Snapshot(target,
				Branch("br", 2),
				Op("nop"),
				Ldc(1),
				Op("ret"));

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, current);
			Equal(ILPatchRebaseStatus.Unsupported, preview.Status,
				"A preserved current branch into an instruction replaced by the patch must be rejected during preview.");
			True(preview.Message.Contains("branches to instruction", StringComparison.Ordinal),
				"Preview should explain that a preserved branch target cannot be mapped safely.");
		}

		static void ExceptionHandlerBoundaryInsidePatchedRangeIsUnsupported() {
			var target = CreateTarget();
			var baseline = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));
			var patched = Snapshot(target, Op("nop"), Ldc(2), Op("ret"));
			var patch = Change(target, baseline, patched);

			var current = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));
			current.ExceptionHandlers.Add(new ILPatchExceptionHandler {
				HandlerType = "Finally",
				TryStart = 1,
				TryEnd = 2,
				HandlerStart = 2,
				HandlerEnd = -1,
				FilterStart = -1,
			});
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, current);
			Equal(ILPatchRebaseStatus.Unsupported, preview.Status,
				"An EH boundary on an instruction replaced by the patch must be rejected during preview.");
			True(preview.Message.Contains("exception-handler boundary", StringComparison.Ordinal),
				"Preview should explain that the EH boundary cannot be remapped safely.");
		}

		static void BranchIndexShiftDoesNotCreateFalsePatchHunks() {
			var target = CreateTarget();
			var baseline = Snapshot(target,
				Branch("br", 2),
				Ldc(0),
				Op("ret"));
			var patched = Snapshot(target,
				Op("nop"),
				Branch("br", 3),
				Ldc(0),
				Op("ret"));
			var patch = Change(target, baseline, patched);

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, baseline);
			Equal(ILPatchRebaseStatus.Clean, preview.Status, preview.Message);
			Equal(1, preview.Hunks.Count,
				"A leading insertion should remain one hunk even though branch target indices shift.");
			Equal(0, preview.Hunks[0].BaseLength, "The hunk should be an insertion.");
			Equal(1, preview.Hunks[0].PatchedLength, "Only the inserted nop belongs to the patch hunk.");

			True(ILPatchRebaseMerger.TryCreateMergedSnapshot(patch, preview, out var merged, out string error), error);
			NotNull(merged, "Merged branch snapshot was null.");
			Equal(3, merged!.Instructions[1].Operand.Index,
				"The unchanged branch target must be translated to the merged instruction index.");
		}

		static void RebasedAppliedRoundTripIsDetected() {
			var target = CreateTarget();
			var patch = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(2), Op("ret")));
			var upstream = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));

			var preview = ILPatchRebaseAnalyzer.Analyze(patch, target, upstream);
			Equal(ILPatchRebaseStatus.Clean, preview.Status, preview.Message);
			True(ILPatchRebaseMerger.TryCreateMergedSnapshot(patch, preview, out var merged, out string error), error);
			NotNull(merged, "Merged snapshot was null.");

			True(ILPatchRebasedAppliedDetector.TryDetect(patch, target, merged!, out string message),
				"Clean-rebased body should be detected as already applied. " + message);
		}

		static void UnappliedUpstreamBodyIsNotRebasedApplied() {
			var target = CreateTarget();
			var patch = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(2), Op("ret")));
			var upstream = Snapshot(target, Op("nop"), Ldc(1), Op("ret"));

			False(ILPatchRebasedAppliedDetector.TryDetect(patch, target, upstream, out _),
				"An upstream-only body must not be mistaken for an already-rebased patch.");
		}

		static void ManualOverrideCanRetargetRenamedMethod() {
			var oldTarget = CreateNamedIntMethod("OldName", 1);
			var newTarget = CreateNamedIntMethod("NewName", 1);
			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var document = new ILPatchDocument();
			document.Methods.Add(patch);

			var automatic = ILPatchImportMatcher.CreatePreview(document, new[] { newTarget.Module });
			Equal(ILPatchImportStatus.Missing, automatic.Results[0].Status,
				"A renamed method should not be silently selected by structural matching.");

			var overrides = new Dictionary<string, ILPatchMethodIdentity>(StringComparer.Ordinal) {
				[patch.Id] = ILPatchMethodIdentity.Create(newTarget),
			};
			var manual = ILPatchImportMatcher.CreatePreview(document, new[] { newTarget.Module }, overrides);
			Equal(ILPatchImportStatus.Exact, manual.Results[0].Status,
				"A manually selected renamed method with the same signature and baseline should become Exact.");
			True(manual.Results[0].IsManualTarget, "Manual target flag should be preserved in preview.");
			True(ReferenceEquals(newTarget, manual.Results[0].Target), "Preview should resolve to the selected MethodDef.");
		}

		static void ManualOverrideRejectsSignatureChange() {
			var oldTarget = CreateNamedIntMethod("OldName", 1);
			var module = CreateModule();
			var type = new TypeDefUser("Tests", "Fixture", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			var incompatible = new MethodDefUser("NewName",
				MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32));
			incompatible.Body = new dnlib.DotNet.Emit.CilBody();
			incompatible.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ldarg_0));
			incompatible.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ret));
			type.Methods.Add(incompatible);

			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var document = new ILPatchDocument();
			document.Methods.Add(patch);
			var overrides = new Dictionary<string, ILPatchMethodIdentity>(StringComparer.Ordinal) {
				[patch.Id] = ILPatchMethodIdentity.Create(incompatible),
			};

			var preview = ILPatchImportMatcher.CreatePreview(document, new[] { module }, overrides);
			Equal(ILPatchImportStatus.Incompatible, preview.Results[0].Status,
				"A manual target with a different parameter signature must be rejected before apply/rebase.");
			True(preview.Results[0].IsManualTarget, "Rejected override should still be marked as manual.");
		}

		static void UpdatedDefinitionPersistsManualRenamedTarget() {
			var oldTarget = CreateNamedIntMethod("OldName", 1);
			var newTarget = CreateNamedIntMethod("NewName", 1);
			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var document = new ILPatchDocument();
			document.Methods.Add(patch);
			var overrides = new Dictionary<string, ILPatchMethodIdentity>(StringComparer.Ordinal) {
				[patch.Id] = ILPatchMethodIdentity.Create(newTarget),
			};

			var preview = ILPatchImportMatcher.CreatePreview(document, new[] { newTarget.Module }, overrides);
			Equal(ILPatchImportStatus.Exact, preview.Results[0].Status, "Manual renamed target must be Exact before definition update.");

			var updatedDocument = ILPatchDefinitionRebaser.CreateUpdatedDocument(preview, out var report);
			Equal(1, report.UpdatedCount, "Manual target should produce one updated definition.");
			Equal(0, report.PreservedCount, "No entry should remain unresolved.");
			Equal("NewName", updatedDocument.Methods[0].Target.MethodName,
				"Updated definition must persist the manually selected target identity.");
			Equal(1L, updatedDocument.Methods[0].BaseBody.Instructions[0].Operand.IntegerValue,
				"Updated baseline must remain the current unpatched body.");
			Equal(2L, updatedDocument.Methods[0].PatchedBody.Instructions[0].Operand.IntegerValue,
				"Updated patched body must preserve the patch edit.");
		}

		static void UpdatedDefinitionRebasesCleanUpstreamChange() {
			var oldTarget = CreateNamedIntMethod("Run", 1);
			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var current = CreateNamedIntMethod("Run", 1);
			current.Body!.Instructions.Insert(0, new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Nop));

			var document = new ILPatchDocument();
			document.Methods.Add(patch);
			var preview = ILPatchImportMatcher.CreatePreview(document, new[] { current.Module });
			Equal(ILPatchImportStatus.BaseChanged, preview.Results[0].Status,
				"Upstream insertion should produce BaseChanged.");
			Equal(ILPatchRebaseStatus.Clean, preview.Results[0].RebasePreview?.Status,
				"Independent upstream insertion should be cleanly rebaseable.");

			var updatedDocument = ILPatchDefinitionRebaser.CreateUpdatedDocument(preview, out var report);
			Equal(1, report.UpdatedCount, "Clean rebase should update the patch definition.");
			var updated = updatedDocument.Methods[0];
			Equal(3, updated.BaseBody.Instructions.Count, "Updated baseline must include the upstream nop.");
			Equal("nop", updated.BaseBody.Instructions[0].OpCode, "Updated baseline must preserve upstream IL.");
			Equal(3, updated.PatchedBody.Instructions.Count, "Updated patched body must preserve upstream method shape.");
			Equal("nop", updated.PatchedBody.Instructions[0].OpCode, "Rebased patch must preserve upstream nop.");
			Equal(2L, updated.PatchedBody.Instructions[1].Operand.IntegerValue,
				"Rebased patch must apply the original constant edit on top of upstream IL.");
		}

		static void HeadlessApplyExactMutatesTarget() {
			var oldTarget = CreateNamedIntMethod("Run", 1);
			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var current = CreateNamedIntMethod("Run", 1);
			var document = new ILPatchDocument();
			document.Methods.Add(patch);

			var report = ILPatchHeadlessApplier.Apply(current.Module, document);
			True(report.Success, "Exact headless patch should succeed.");
			Equal(1, report.AppliedCount, "Exactly one method should be applied.");
			Equal(ILPatchHeadlessAction.Exact, report.Entries[0].Action,
				"Exact patch should report AppliedExact.");
			var snapshot = CilNormalizer.CreateSnapshot(current);
			Equal(2L, snapshot.Instructions[0].Operand.IntegerValue,
				"Headless exact apply should replace the constant.");
		}

		static void HeadlessApplyCleanRebasePreservesUpstream() {
			var oldTarget = CreateNamedIntMethod("Run", 1);
			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var current = CreateNamedIntMethod("Run", 1);
			current.Body!.Instructions.Insert(0,
				new dnlib.DotNet.Emit.Instruction(dnlib.DotNet.Emit.OpCodes.Nop));
			var document = new ILPatchDocument();
			document.Methods.Add(patch);

			var report = ILPatchHeadlessApplier.Apply(current.Module, document);
			True(report.Success, "Independent upstream insertion should headlessly rebase.");
			Equal(ILPatchHeadlessAction.CleanRebase, report.Entries[0].Action,
				"Headless action should report a clean rebase.");
			var snapshot = CilNormalizer.CreateSnapshot(current);
			Equal(3, snapshot.Instructions.Count, "Upstream insertion must remain.");
			Equal("nop", snapshot.Instructions[0].OpCode, "Upstream nop must remain.");
			Equal(2L, snapshot.Instructions[1].Operand.IntegerValue,
				"Original patch edit must be applied after the upstream nop.");
		}

		static void HeadlessConflictDoesNotMutateTarget() {
			var oldTarget = CreateNamedIntMethod("Run", 1);
			var patch = CreateRealBodyConstantPatch(oldTarget, 2);
			var current = CreateNamedIntMethod("Run", 3);
			var before = CilNormalizer.CreateSnapshot(current);
			before.CanonicalHash = ILPatchBodyHasher.Compute(before);
			var document = new ILPatchDocument();
			document.Methods.Add(patch);

			var report = ILPatchHeadlessApplier.Apply(current.Module, document);
			False(report.Success, "Conflicting upstream edit must fail closed.");
			Equal(0, report.AppliedCount, "Failure must not report applied methods.");
			Equal(ILPatchHeadlessAction.Unresolved, report.Entries[0].Action,
				"Conflict must be reported as unresolved.");
			var after = CilNormalizer.CreateSnapshot(current);
			after.CanonicalHash = ILPatchBodyHasher.Compute(after);
			Equal(before.CanonicalHash, after.CanonicalHash,
				"Failed headless preflight must not mutate the target MethodDef.");
		}

		static void DiskRoundTripWritesPatchedAssembly() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-core-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string inputPath = Path.Combine(directory, "input.dll");
				string patchPath = Path.Combine(directory, "change.ilpatch");
				string outputPath = Path.Combine(directory, "output.dll");

				var patchSource = CreateNamedIntMethod("Run", 1);
				var patch = CreateRealBodyConstantPatch(patchSource, 2);
				var document = new ILPatchDocument { Name = "disk-round-trip" };
				document.Methods.Add(patch);
				ILPatchSerializer.Save(patchPath, document);

				var inputMethod = CreateNamedIntMethod("Run", 1);
				inputMethod.Module.Write(inputPath);

				using (var loaded = ModuleDefMD.Load(inputPath)) {
					var loadedDocument = ILPatchSerializer.Load(patchPath);
					var loadedMethod = loaded.GetTypes()
						.SelectMany(a => a.Methods)
						.Single(a => StringComparer.Ordinal.Equals(a.Name?.String, "Run"));
					var loadedSnapshot = CilNormalizer.CreateSnapshot(loadedMethod);
					loadedSnapshot.CanonicalHash = ILPatchBodyHasher.Compute(loadedSnapshot);
					var report = ILPatchHeadlessApplier.Apply(loaded, loadedDocument);
					var entry = report.Entries[0];
					True(report.Success,
						"On-disk input should accept the Exact patch. " +
						$"Action={entry.Action}; message={entry.Message}; " +
						$"patchTarget={loadedDocument.Methods[0].Target.ToCanonicalString()}; " +
						$"loadedTarget={ILPatchMethodIdentity.Create(loadedMethod).ToCanonicalString()}; " +
						$"baseHash={loadedDocument.Methods[0].BaseBody.CanonicalHash}; " +
						$"loadedHash={loadedSnapshot.CanonicalHash}.");
					loaded.Write(outputPath);
				}

				using (var verified = ModuleDefMD.Load(outputPath)) {
					var method = verified.GetTypes()
						.SelectMany(a => a.Methods)
						.Single(a => StringComparer.Ordinal.Equals(a.Name?.String, "Run"));
					var snapshot = CilNormalizer.CreateSnapshot(method);
					Equal(2L, snapshot.Instructions[0].Operand.IntegerValue,
						"Reloaded output assembly must contain the patched IL.");
				}
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void BatchComposerCombinesIndependentTargets() {
			var first = CreateNamedIntMethod("First", 1);
			var second = CreateNamedIntMethod("Second", 10);
			var firstPatch = CreateRealBodyConstantPatch(first, 2);
			var secondPatch = CreateRealBodyConstantPatch(second, 20);
			var firstDocument = new ILPatchDocument { Name = "first" };
			var secondDocument = new ILPatchDocument { Name = "second" };
			firstDocument.Methods.Add(firstPatch);
			secondDocument.Methods.Add(secondPatch);

			var sources = new[] {
				new ILPatchBatchSource("001-first.ilpatch", firstDocument),
				new ILPatchBatchSource("002-second.ilpatch", secondDocument),
			};
			True(ILPatchBatchComposer.TryCombine(sources, out var combined, out string error), error);
			NotNull(combined, "Combined batch document was null.");
			Equal(2, combined!.Methods.Count, "Independent patch targets should be combined.");
			Equal("Batch import (2 files)", combined.Name, "Batch name should describe the source count.");
		}

		static void BatchComposerRejectsDuplicatePatchIds() {
			var first = CreateNamedIntMethod("First", 1);
			var second = CreateNamedIntMethod("Second", 10);
			var firstPatch = CreateRealBodyConstantPatch(first, 2);
			var secondPatch = CreateRealBodyConstantPatch(second, 20);
			secondPatch.Id = firstPatch.Id;
			var firstDocument = new ILPatchDocument();
			var secondDocument = new ILPatchDocument();
			firstDocument.Methods.Add(firstPatch);
			secondDocument.Methods.Add(secondPatch);

			False(ILPatchBatchComposer.TryCombine(new[] {
				new ILPatchBatchSource("a.ilpatch", firstDocument),
				new ILPatchBatchSource("b.ilpatch", secondDocument),
			}, out _, out string error),
				"Duplicate patch ids must block multi-file import.");
			True(error.Contains("appears more than once", StringComparison.Ordinal),
				"Duplicate-id error should explain the collision.");
		}

		static void BatchComposerRejectsOverlappingTargets() {
			var target = CreateTarget();
			var firstPatch = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(2), Op("ret")));
			var secondPatch = Change(target,
				Snapshot(target, Ldc(1), Op("ret")),
				Snapshot(target, Ldc(3), Op("ret")));
			var firstDocument = new ILPatchDocument();
			var secondDocument = new ILPatchDocument();
			firstDocument.Methods.Add(firstPatch);
			secondDocument.Methods.Add(secondPatch);

			False(ILPatchBatchComposer.TryCombine(new[] {
				new ILPatchBatchSource("first.ilpatch", firstDocument),
				new ILPatchBatchSource("second.ilpatch", secondDocument),
			}, out _, out string error),
				"Multiple selected files targeting the same method must not be combined.");
			True(error.Contains("apply/rebase them sequentially", StringComparison.Ordinal),
				"Overlapping-target error should direct the user to sequential application.");
		}

		static void DocumentCreatorCapturesMethodBodyChange() {
			var original = CreateNamedIntMethod("Run", 1);
			var modified = CreateNamedIntMethod("Run", 2);

			True(ILPatchDocumentCreator.TryCreate(original.Module, modified.Module, "created",
				out var document, out var report), string.Join(" ", report.UnsupportedReasons));
			NotNull(document, "Created patch document was null.");
			Equal(1, report.ChangedCount, "One changed CIL method should be captured.");
			Equal(0, report.UnsupportedCount, "Simple body edit should be supported.");
			Equal(1, document!.Methods.Count, "Created document should contain one method patch.");
			Equal(1L, document.Methods[0].BaseBody.Instructions[0].Operand.IntegerValue,
				"Created baseline should come from the original assembly.");
			Equal(2L, document.Methods[0].PatchedBody.Instructions[0].Operand.IntegerValue,
				"Created patched body should come from the modified assembly.");
		}

		static void DocumentCreatorIgnoresMaxStackNoise() {
			var original = CreateNamedIntMethod("Run", 1);
			var modified = CreateNamedIntMethod("Run", 1);
			original.Body!.MaxStack = 1;
			modified.Body!.MaxStack = 32;

			True(ILPatchDocumentCreator.TryCreate(original.Module, modified.Module, "noise",
				out var document, out var report), string.Join(" ", report.UnsupportedReasons));
			NotNull(document, "Created patch document was null.");
			Equal(0, report.ChangedCount, "MaxStack-only differences must not create a portable patch.");
			Equal(1, report.UnchangedCount, "Method should be counted as semantically unchanged.");
			Equal(0, document!.Methods.Count, "No patch entry should be emitted for MaxStack-only noise.");
		}

		static void DocumentCreatorRejectsAddedMethod() {
			var original = CreateNamedIntMethod("Run", 1);
			var modified = CreateNamedIntMethod("Run", 1);
			var type = modified.DeclaringType!;
			var added = new MethodDefUser("Added", MethodSig.CreateStatic(modified.Module.CorLibTypes.Void)) {
				Body = new dnlib.DotNet.Emit.CilBody(),
			};
			added.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ret));
			type.Methods.Add(added);

			False(ILPatchDocumentCreator.TryCreate(original.Module, modified.Module, "unsupported",
				out var document, out var report),
				"Added methods are outside the v1 method-body patch format and must fail closed.");
			True(document is null, "Failed creation must not return a partial patch document.");
			True(report.UnsupportedReasons.Any(a => a.Contains("added method", StringComparison.OrdinalIgnoreCase)),
				"Create report should identify the added method.");
		}

		static void RepositoryCommitHistoryAndExportRoundTrip() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-repo-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string workingPath = Path.Combine(directory, "Assembly-CSharp.dll");
				CreateRepositoryFixtureModule(1, 10).Write(workingPath);
				var repository = ILPatchRepository.Initialize(workingPath);
				True(File.Exists(Path.Combine(directory, ".dnspy", "repo.json")),
					"Repository initialization should create .dnspy/repo.json.");
				True(File.Exists(repository.RootModulePath),
					"Repository initialization should preserve an immutable root DLL.");

				using (var working = ModuleDefMD.Load(workingPath)) {
					var first = FindFixtureMethod(working, "First");
					var change = CreateWorkingConstantChange(first, 2);
					var commit = repository.Commit(working, new[] { change }, "change first");
					Equal(1, commit.Changes.Count, "First repository commit should report one changed method.");
					Equal(ILPatchRepositoryMethodChangeKind.AddedChange, commit.Changes[0].Kind,
						"First change from root should be AddedChange.");
				}

				string firstExport = Path.Combine(directory, "first-export.dll");
				repository.Export(repository.Metadata.HeadCommitId, firstExport);
				VerifyFixtureConstants(firstExport, 2, 10);
				// Use the exported HEAD as the next working DLL, just like Save/Export Commit in the GUI.
				File.Copy(firstExport, workingPath, true);

				var reopened = ILPatchRepository.OpenForModule(workingPath);
				string firstCommitId = reopened.Metadata.HeadCommitId!;
				using (var working = ModuleDefMD.Load(workingPath)) {
					var second = FindFixtureMethod(working, "Second");
					var change = CreateWorkingConstantChange(second, 20);
					var commit = reopened.Commit(working, new[] { change }, "change second");
					Equal(firstCommitId, commit.ParentId, "Second commit should reference the previous HEAD.");
				}

				var history = reopened.GetHistory();
				Equal(2, history.Count, "Repository history should survive reopening and contain both commits.");
				Equal("change second", history[0].Message, "History should be newest-first.");
				Equal("change first", history[1].Message, "Older commit should remain reachable.");

				string rootExport = Path.Combine(directory, "root-export.dll");
				string oldExport = Path.Combine(directory, "old-export.dll");
				string headExport = Path.Combine(directory, "head-export.dll");
				reopened.Export(null, rootExport);
				reopened.Export(history[1].Id, oldExport);
				reopened.Export(history[0].Id, headExport);
				VerifyFixtureConstants(rootExport, 1, 10);
				VerifyFixtureConstants(oldExport, 2, 10);
				VerifyFixtureConstants(headExport, 2, 20);
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void RepositoryRejectsDivergedWorkingTree() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-repo-diverged-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string workingPath = Path.Combine(directory, "Assembly-CSharp.dll");
				CreateRepositoryFixtureModule(1, 10).Write(workingPath);
				var repository = ILPatchRepository.Initialize(workingPath);
				using (var working = ModuleDefMD.Load(workingPath)) {
					var change = CreateWorkingConstantChange(FindFixtureMethod(working, "First"), 2);
					repository.Commit(working, new[] { change }, "head changes first");
				}

				// Deliberately reopen the immutable root instead of HEAD, then edit a different method.
				File.Copy(repository.RootModulePath, workingPath, true);
				using var diverged = ModuleDefMD.Load(workingPath);
				var secondChange = CreateWorkingConstantChange(FindFixtureMethod(diverged, "Second"), 20);
				False(repository.TryValidateWorkingBase(diverged, new[] { secondChange }, out string error),
					"A working tree based on ROOT must not be accepted when HEAD already changed another method.");
				True(error.Contains("not based on repository HEAD", StringComparison.Ordinal),
					"Divergence error should explain that the loaded DLL is not based on HEAD.");
				bool threw = false;
				try {
					repository.Commit(diverged, new[] { secondChange }, "should fail");
				}
				catch (InvalidOperationException) {
					threw = true;
				}
				True(threw, "Commit itself must fail closed on a diverged working tree.");
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void RepositoryDetectsTamperedRoot() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-repo-tamper-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string workingPath = Path.Combine(directory, "Assembly-CSharp.dll");
				CreateRepositoryFixtureModule(1, 10).Write(workingPath);
				var repository = ILPatchRepository.Initialize(workingPath);
				CreateRepositoryFixtureModule(99, 10).Write(repository.RootModulePath);

				bool threw = false;
				try {
					ILPatchRepository.OpenForModule(workingPath);
				}
				catch (InvalidDataException) {
					threw = true;
				}
				True(threw, "Opening a repository must detect a modified immutable ROOT assembly.");
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void RepositoryCommitDeltaUsesParentState() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-repo-delta-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string workingPath = Path.Combine(directory, "Assembly-CSharp.dll");
				CreateRepositoryFixtureModule(1, 10).Write(workingPath);
				var repository = ILPatchRepository.Initialize(workingPath);
				using (var working = ModuleDefMD.Load(workingPath)) {
					var change = CreateWorkingConstantChange(FindFixtureMethod(working, "First"), 2);
					repository.Commit(working, new[] { change }, "first");
				}
				string firstHead = Path.Combine(directory, "first-head.dll");
				repository.Export(repository.Metadata.HeadCommitId, firstHead);
				File.Copy(firstHead, workingPath, true);

				string secondId;
				using (var working = ModuleDefMD.Load(workingPath)) {
					var change = CreateWorkingConstantChange(FindFixtureMethod(working, "First"), 3);
					secondId = repository.Commit(working, new[] { change }, "second").Id;
				}

				var delta = repository.CreateCommitDelta(secondId);
				Equal(1, delta.Methods.Count, "Second commit delta should contain the parent-relative changed method.");
				Equal(2L, delta.Methods[0].BaseBody.Instructions[0].Operand.IntegerValue,
					"History diff must use the parent commit state as its base, not ROOT.");
				Equal(3L, delta.Methods[0].PatchedBody.Instructions[0].Operand.IntegerValue,
					"History diff patched side must use the selected commit state.");
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void RepositoryCommitCanRevertParentChange() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-repo-revert-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string workingPath = Path.Combine(directory, "Assembly-CSharp.dll");
				CreateRepositoryFixtureModule(1, 10).Write(workingPath);
				var repository = ILPatchRepository.Initialize(workingPath);
				using (var working = ModuleDefMD.Load(workingPath)) {
					var change = CreateWorkingConstantChange(FindFixtureMethod(working, "First"), 2);
					repository.Commit(working, new[] { change }, "change first");
				}
				string headPath = Path.Combine(directory, "head.dll");
				repository.Export(repository.Metadata.HeadCommitId, headPath);
				File.Copy(headPath, workingPath, true);

				using (var working = ModuleDefMD.Load(workingPath)) {
					var revert = CreateWorkingConstantChange(FindFixtureMethod(working, "First"), 1);
					var commit = repository.Commit(working, new[] { revert }, "revert first");
					Equal(1, commit.Changes.Count, "Revert commit should contain one method summary.");
					Equal(ILPatchRepositoryMethodChangeKind.Reverted, commit.Changes[0].Kind,
						"Returning a parent change to ROOT should be classified as Reverted.");
				}

				string exported = Path.Combine(directory, "reverted.dll");
				repository.Export(repository.Metadata.HeadCommitId, exported);
				VerifyFixtureConstants(exported, 1, 10);
				Equal(0, repository.LoadCommitPatch(repository.Metadata.HeadCommitId!).Methods.Count,
					"Full-state patch after reverting the only change should be empty.");
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void DiffEngineAlignsModifiedLines() {
			var rows = ILPatchDiffEngine.Compare(
				new[] { "a", "old", "z" },
				new[] { "a", "new", "z" });
			Equal(3, rows.Count, "Simple line replacement should stay aligned.");
			Equal(ILPatchDiffKind.Same, rows[0].Kind, "First anchor should be unchanged.");
			Equal(ILPatchDiffKind.Modified, rows[1].Kind, "Replacement should be represented as one Modified row.");
			Equal("old", rows[1].LeftText, "Modified row must preserve old text.");
			Equal("new", rows[1].RightText, "Modified row must preserve new text.");
			Equal(ILPatchDiffKind.Same, rows[2].Kind, "Last anchor should be unchanged.");
		}

		static void DiffEngineTracksAddedAndRemovedLines() {
			var rows = ILPatchDiffEngine.Compare(
				new[] { "a", "remove-1", "remove-2", "z" },
				new[] { "a", "add-1", "z", "tail" });
			True(rows.Any(a => a.Kind == ILPatchDiffKind.Modified && a.LeftText == "remove-1" && a.RightText == "add-1"),
				"First remove/add pair should be a Modified row.");
			True(rows.Any(a => a.Kind == ILPatchDiffKind.Removed && a.LeftText == "remove-2"),
				"Unpaired deletion should remain Removed.");
			True(rows.Any(a => a.Kind == ILPatchDiffKind.Added && a.RightText == "tail"),
				"Unpaired insertion should remain Added.");
		}

		static void DiffEngineKeepsStableAnchors() {
			var rows = ILPatchDiffEngine.Compare(
				new[] { "start", "same", "old", "end" },
				new[] { "start", "inserted", "same", "new", "end" });
			var same = rows.Where(a => a.Kind == ILPatchDiffKind.Same).Select(a => a.LeftText).ToArray();
			True(same.SequenceEqual(new[] { "start", "same", "end" }),
				"LCS anchors should keep surrounding unchanged lines aligned.");
			True(rows.Any(a => a.Kind == ILPatchDiffKind.Added && a.RightText == "inserted"),
				"Inserted line before an anchor should be visible as Added.");
		}

		static void DocumentCreatorDiskRoundTripCapturesBodyChange() {
			string directory = Path.Combine(Path.GetTempPath(), "ilpatch-create-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try {
				string originalPath = Path.Combine(directory, "original.dll");
				string modifiedPath = Path.Combine(directory, "modified.dll");

				var originalMethod = CreateNamedIntMethod("Run", 1);
				originalMethod.Module.Write(originalPath);
				var modifiedMethod = CreateNamedIntMethod("Run", 2);
				modifiedMethod.Module.Write(modifiedPath);

				using var original = ModuleDefMD.Load(originalPath);
				using var modified = ModuleDefMD.Load(modifiedPath);
				True(ILPatchDocumentCreator.TryCreate(original, modified, "disk-recovery",
					out var document, out var report), string.Join(" ", report.UnsupportedReasons));
				NotNull(document, "Disk recovery should produce a patch document.");
				Equal(1, report.ChangedCount,
					"Disk recovery should capture the one semantic method-body change despite independent module metadata.");
				Equal(0, report.UnsupportedCount,
					"Independent on-disk MVID/token/RID allocation must not be treated as unsupported.");
				Equal(1, document!.Methods.Count,
					"Disk recovery should emit exactly one method patch.");
				Equal(1L, document.Methods[0].BaseBody.Instructions[0].Operand.IntegerValue,
					"Recovered disk baseline must contain the original IL.");
				Equal(2L, document.Methods[0].PatchedBody.Instructions[0].Operand.IntegerValue,
					"Recovered disk patched body must contain the modified IL.");
			}
			finally {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
		}

		static void DocumentCreatorRejectsMethodFlagChange() {
			var original = CreateNamedIntMethod("Run", 1);
			var modified = CreateNamedIntMethod("Run", 1);
			modified.Attributes |= MethodAttributes.Public;

			False(ILPatchDocumentCreator.TryCreate(original.Module, modified.Module, "unsupported",
				out _, out var report),
				"Method metadata flag changes must not be silently omitted.");
			True(report.UnsupportedReasons.Any(a => a.Contains("metadata flags changed", StringComparison.Ordinal)),
				"Create report should identify the method flag change.");
		}

		static ILPatchMethodChange CreateRealBodyConstantPatch(MethodDef target, int patchedValue) {
			var baseline = CilNormalizer.CreateSnapshot(target);
			baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);

			target.Body!.Instructions[0] = dnlib.DotNet.Emit.Instruction.CreateLdcI4(patchedValue);
			var patched = CilNormalizer.CreateSnapshot(target);
			patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);

			return Change(target, baseline, patched);
		}

		static ModuleDef CreateModule() {
			var module = new ModuleDefUser("Assembly-CSharp.dll");
			var assembly = new AssemblyDefUser("Assembly-CSharp", new Version(1, 0, 0, 0));
			assembly.Modules.Add(module);
			return module;
		}

		static ModuleDef CreateRepositoryFixtureModule(int firstValue, int secondValue) {
			var module = CreateModule();
			var type = new TypeDefUser("Tests", "RepositoryFixture", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			AddConstantMethod(type, "First", firstValue);
			AddConstantMethod(type, "Second", secondValue);
			return module;
		}

		static void AddConstantMethod(TypeDef type, string name, int value) {
			var method = new MethodDefUser(name, MethodSig.CreateStatic(type.Module.CorLibTypes.Int32)) {
				Body = new dnlib.DotNet.Emit.CilBody(),
			};
			method.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.CreateLdcI4(value));
			method.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ret));
			type.Methods.Add(method);
		}

		static MethodDef FindFixtureMethod(ModuleDef module, string name) =>
			module.GetTypes().SelectMany(a => a.Methods)
				.Single(a => StringComparer.Ordinal.Equals(a.Name?.String, name));

		static ILPatchMethodChange CreateWorkingConstantChange(MethodDef method, int newValue) {
			var baseline = CilNormalizer.CreateSnapshot(method);
			baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);
			method.Body!.Instructions[0] = dnlib.DotNet.Emit.Instruction.CreateLdcI4(newValue);
			var patched = CilNormalizer.CreateSnapshot(method);
			patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);
			return new ILPatchMethodChange {
				Target = ILPatchMethodIdentity.Create(method),
				BaseModuleMvid = method.Module?.Mvid ?? Guid.Empty,
				BaseBody = baseline,
				PatchedBody = patched,
			};
		}

		static void VerifyFixtureConstants(string path, int first, int second) {
			using var module = ModuleDefMD.Load(path);
			var firstMethod = FindFixtureMethod(module, "First");
			var secondMethod = FindFixtureMethod(module, "Second");
			Equal((long)first, CilNormalizer.CreateSnapshot(firstMethod).Instructions[0].Operand.IntegerValue,
				"Unexpected First() constant.");
			Equal((long)second, CilNormalizer.CreateSnapshot(secondMethod).Instructions[0].Operand.IntegerValue,
				"Unexpected Second() constant.");
		}

		static MethodDef CreateNamedIntMethod(string name, int constant) {
			var module = CreateModule();
			var type = new TypeDefUser("Tests", "Fixture", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32));
			method.Body = new dnlib.DotNet.Emit.CilBody();
			method.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.CreateLdcI4(constant));
			method.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ret));
			type.Methods.Add(method);
			return method;
		}

		static MethodDef CreateTarget() {
			var module = new ModuleDefUser("Assembly-CSharp.dll");
			var assembly = new AssemblyDefUser("Assembly-CSharp", new Version(1, 0, 0, 0));
			assembly.Modules.Add(module);
			var type = new TypeDefUser("Tests", "Fixture", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			var method = new MethodDefUser("Run", MethodSig.CreateStatic(module.CorLibTypes.Void));
			type.Methods.Add(method);
			return method;
		}

		static ILPatchMethodChange Change(MethodDef target, ILPatchMethodBodySnapshot baseline,
			ILPatchMethodBodySnapshot patched) => new ILPatchMethodChange {
				Target = ILPatchMethodIdentity.Create(target),
				BaseBody = baseline,
				PatchedBody = patched,
			};

		static ILPatchMethodBodySnapshot Snapshot(MethodDef target, params ILPatchInstruction[] instructions) {
			var body = new ILPatchMethodBodySnapshot {
				Method = ILPatchMethodIdentity.Create(target),
				InitLocals = false,
				MaxStack = 8,
			};
			body.Instructions.AddRange(instructions);
			body.CanonicalHash = ILPatchBodyHasher.Compute(body);
			return body;
		}

		static ILPatchInstruction Op(string opCode) => new ILPatchInstruction {
			OpCode = opCode,
			Operand = ILPatchOperand.None,
		};

		static ILPatchInstruction Ldc(long value) => new ILPatchInstruction {
			OpCode = "ldc.i4",
			Operand = new ILPatchOperand {
				Kind = ILPatchOperandKind.Integer,
				IntegerValue = value,
			},
		};

		static ILPatchInstruction Branch(string opCode, int target) => new ILPatchInstruction {
			OpCode = opCode,
			Operand = new ILPatchOperand {
				Kind = ILPatchOperandKind.BranchTarget,
				Index = target,
			},
		};

		static void True(bool value, string message) {
			if (!value)
				throw new InvalidOperationException(message);
		}

		static void False(bool value, string message) => True(!value, message);

		static void NotNull(object? value, string message) {
			if (value is null)
				throw new InvalidOperationException(message);
		}

		static void Equal<T>(T expected, T actual, string message) {
			if (!EqualityComparer<T>.Default.Equals(expected, actual))
				throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
		}
	}
}
