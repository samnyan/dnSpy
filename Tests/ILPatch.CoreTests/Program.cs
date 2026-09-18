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

			Console.WriteLine(inputPath);
			Console.WriteLine(conflictInputPath);
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
				Branch("br", 2),
				Op("nop"),
				Ldc(1),
				Op("ret"));
			var patched = Snapshot(target,
				Branch("br", 2),
				Op("nop"),
				Ldc(2),
				Op("ret"));
			var patch = Change(target, baseline, patched);

			var current = Snapshot(target,
				Op("nop"),
				Branch("br", 3),
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
