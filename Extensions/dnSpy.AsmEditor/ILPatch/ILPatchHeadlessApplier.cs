/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.AsmEditor.ILPatch {
	enum ILPatchHeadlessAction {
		Exact,
		CleanRebase,
		AlreadyPresent,
		Unresolved,
	}

	sealed class ILPatchHeadlessEntryResult {
		public ILPatchMethodChange Patch { get; }
		public MethodDef? Target { get; }
		public ILPatchHeadlessAction Action { get; }
		public string Message { get; }

		public ILPatchHeadlessEntryResult(ILPatchMethodChange patch, MethodDef? target,
			ILPatchHeadlessAction action, string message) {
			Patch = patch ?? throw new ArgumentNullException(nameof(patch));
			Target = target;
			Action = action;
			Message = message ?? string.Empty;
		}
	}

	sealed class ILPatchHeadlessApplyReport {
		public IReadOnlyList<ILPatchHeadlessEntryResult> Entries { get; }
		public bool Success { get; }
		public int AppliedCount { get; }
		public int AlreadyPresentCount { get; }
		public string StructuralMessage { get; }

		public ILPatchHeadlessApplyReport(IReadOnlyList<ILPatchHeadlessEntryResult> entries,
			bool success, int appliedCount, int alreadyPresentCount, string structuralMessage = "") {
			Entries = entries ?? throw new ArgumentNullException(nameof(entries));
			Success = success;
			AppliedCount = appliedCount;
			AlreadyPresentCount = alreadyPresentCount;
			StructuralMessage = structuralMessage ?? string.Empty;
		}
	}

	/// <summary>
	/// Pure dnlib headless application path used by the CLI and future automation.
	/// One document is preflighted completely before any MethodDef is mutated.
	/// </summary>
	static class ILPatchHeadlessApplier {
		sealed class PlannedBody {
			public MethodDef Target { get; }
			public CilBody Body { get; }

			public PlannedBody(MethodDef target, CilBody body) {
				Target = target;
				Body = body;
			}
		}

		public static ILPatchHeadlessApplyReport Apply(ModuleDef module, ILPatchDocument document) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (!ILPatchStructuralMaterializer.TryPrepare(module, document.TypeChanges, out var structuralPlan, out string structuralError) ||
				structuralPlan is null) {
				return new ILPatchHeadlessApplyReport(Array.Empty<ILPatchHeadlessEntryResult>(), false, 0, 0,
					structuralError);
			}

			structuralPlan.AttachAdditions();
			try {
				var preview = ILPatchImportMatcher.CreatePreview(document, new[] { module });
				var materializer = new ILPatchBodyMaterializer(module);
				var planned = new List<PlannedBody>();
				var seenTargets = new HashSet<MethodDef>();
				var entries = new List<ILPatchHeadlessEntryResult>(preview.Results.Count);
				int alreadyPresent = 0;
				bool unresolved = false;

			foreach (var result in preview.Results) {
				switch (result.Status) {
				case ILPatchImportStatus.AlreadyApplied:
				case ILPatchImportStatus.RebasedApplied:
					entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
						ILPatchHeadlessAction.AlreadyPresent, result.Message));
					alreadyPresent++;
					break;

				case ILPatchImportStatus.Exact:
					if (TryPlanBody(result, result.Patch.PatchedBody, materializer, seenTargets,
						planned, out string exactError)) {
						entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
							ILPatchHeadlessAction.Exact, "Exact target and baseline match."));
					}
					else {
						entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
							ILPatchHeadlessAction.Unresolved, exactError));
						unresolved = true;
					}
					break;

				case ILPatchImportStatus.BaseChanged:
					if (result.RebasePreview?.Status != ILPatchRebaseStatus.Clean) {
						entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
							ILPatchHeadlessAction.Unresolved,
							result.RebasePreview is null
								? result.Message
								: $"{result.Message} Rebase: {result.RebasePreview.Status} - {result.RebasePreview.Message}"));
						unresolved = true;
						break;
					}
					if (!ILPatchRebaseMerger.TryCreateMergedSnapshot(result.Patch, result.RebasePreview,
						out var merged, out string mergeError) || merged is null) {
						entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
							ILPatchHeadlessAction.Unresolved, "Clean rebase could not be materialized: " + mergeError));
						unresolved = true;
						break;
					}
					if (TryPlanBody(result, merged, materializer, seenTargets, planned, out string rebaseError)) {
						entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
							ILPatchHeadlessAction.CleanRebase, "Three-way rebase is clean."));
					}
					else {
						entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
							ILPatchHeadlessAction.Unresolved, rebaseError));
						unresolved = true;
					}
					break;

				default:
					string message = result.Message;
					if (result.StructuralCandidates.Count != 0) {
						var best = result.StructuralCandidates[0];
						message += $" Top candidate: {best.Identity} ({best.Score:P1}).";
					}
					entries.Add(new ILPatchHeadlessEntryResult(result.Patch, result.Target,
						ILPatchHeadlessAction.Unresolved, message));
					unresolved = true;
					break;
				}
			}

				foreach (var added in structuralPlan.AddedMethods) {
					if (added.Body is null)
						continue;
					if (!materializer.TryCreate(added.Method, added.Body, out var body, out string bodyError) || body is null) {
						entries.Add(new ILPatchHeadlessEntryResult(
							new ILPatchMethodChange {
								Target = ILPatchMethodIdentity.Create(added.Method),
								BaseBody = added.Body,
								PatchedBody = added.Body,
							},
							added.Method, ILPatchHeadlessAction.Unresolved,
							"Added method body could not be materialized: " + bodyError));
						unresolved = true;
						continue;
					}
					planned.Add(new PlannedBody(added.Method, body));
				}

				if (unresolved) {
					structuralPlan.RollbackAdditions();
					return new ILPatchHeadlessApplyReport(entries, false, 0, alreadyPresent,
						"At least one method or structural entry could not be materialized.");
				}

				foreach (var entry in planned)
					entry.Target.MethodBody = entry.Body;
				structuralPlan.CommitRemovals();

				int existingBodyApplications = entries.Count(a =>
					a.Action == ILPatchHeadlessAction.Exact ||
					a.Action == ILPatchHeadlessAction.CleanRebase);
				return new ILPatchHeadlessApplyReport(entries, true,
					existingBodyApplications + structuralPlan.OperationCount, alreadyPresent, string.Empty);
			}
			catch {
				structuralPlan.RollbackAdditions();
				throw;
			}
		}

		static bool TryPlanBody(ILPatchImportResult result, ILPatchMethodBodySnapshot snapshot,
			ILPatchBodyMaterializer materializer, HashSet<MethodDef> seenTargets,
			List<PlannedBody> planned, out string error) {
			error = string.Empty;
			if (result.Target is null) {
				error = "Resolved patch entry has no target MethodDef.";
				return false;
			}
			if (!seenTargets.Add(result.Target)) {
				error = "Multiple actionable entries in the same patch document target this MethodDef.";
				return false;
			}
			if (!materializer.TryCreate(result.Target, snapshot, out var body, out error) || body is null)
				return false;
			planned.Add(new PlannedBody(result.Target, body));
			return true;
		}
	}
}
