using System;
using System.Collections.Generic;
using dnlib.DotNet;

namespace dnSpy.AsmEditor.ILPatch {
	static class Program {
		static int Main() {
			var tests = new (string Name, Action Run)[] {
				(nameof(BodyHashIgnoresMaxStack), BodyHashIgnoresMaxStack),
				(nameof(CleanRebasePreservesUpstreamInsertion), CleanRebasePreservesUpstreamInsertion),
				(nameof(UpstreamEditInsidePatchRangeConflicts), UpstreamEditInsidePatchRangeConflicts),
				(nameof(BranchIndexShiftDoesNotCreateFalsePatchHunks), BranchIndexShiftDoesNotCreateFalsePatchHunks),
				(nameof(RebasedAppliedRoundTripIsDetected), RebasedAppliedRoundTripIsDetected),
				(nameof(UnappliedUpstreamBodyIsNotRebasedApplied), UnappliedUpstreamBodyIsNotRebasedApplied),
				(nameof(ManualOverrideCanRetargetRenamedMethod), ManualOverrideCanRetargetRenamedMethod),
				(nameof(ManualOverrideRejectsSignatureChange), ManualOverrideRejectsSignatureChange),
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
			var patch = Change(oldTarget,
				Snapshot(oldTarget, Ldc(1), Op("ret")),
				Snapshot(oldTarget, Ldc(2), Op("ret")));
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

			var patch = Change(oldTarget,
				Snapshot(oldTarget, Ldc(1), Op("ret")),
				Snapshot(oldTarget, Ldc(2), Op("ret")));
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
