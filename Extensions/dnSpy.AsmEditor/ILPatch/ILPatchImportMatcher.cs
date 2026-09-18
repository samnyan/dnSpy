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
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.AsmEditor.ILPatch {
	enum ILPatchImportStatus {
		Exact,
		AlreadyApplied,
		RebasedApplied,
		BaseChanged,
		Missing,
		Ambiguous,
		Incompatible,
	}

	sealed class ILPatchImportResult {
		public ILPatchMethodChange Patch { get; }
		public ILPatchImportStatus Status { get; }
		public MethodDef? Target { get; }
		public IReadOnlyList<MethodDef> Candidates { get; }
		public string Message { get; }
		public string? CurrentBodyHash { get; }
		public IReadOnlyList<ILPatchStructuralCandidate> StructuralCandidates { get; }
		public ILPatchRebasePreview? RebasePreview { get; }
		public bool IsManualTarget { get; }

		public ILPatchImportResult(ILPatchMethodChange patch, ILPatchImportStatus status, MethodDef? target,
			IReadOnlyList<MethodDef> candidates, string message, string? currentBodyHash = null,
			IReadOnlyList<ILPatchStructuralCandidate>? structuralCandidates = null,
			ILPatchRebasePreview? rebasePreview = null, bool isManualTarget = false) {
			Patch = patch ?? throw new ArgumentNullException(nameof(patch));
			Status = status;
			Target = target;
			Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
			Message = message ?? string.Empty;
			CurrentBodyHash = currentBodyHash;
			StructuralCandidates = structuralCandidates ?? Array.Empty<ILPatchStructuralCandidate>();
			RebasePreview = rebasePreview;
			IsManualTarget = isManualTarget;
		}
	}

	sealed class ILPatchImportPreview {
		public ILPatchDocument Document { get; }
		public IReadOnlyList<ILPatchImportResult> Results { get; }

		public ILPatchImportPreview(ILPatchDocument document, IReadOnlyList<ILPatchImportResult> results) {
			Document = document ?? throw new ArgumentNullException(nameof(document));
			Results = results ?? throw new ArgumentNullException(nameof(results));
		}

		public int Count(ILPatchImportStatus status) => Results.Count(a => a.Status == status);
	}

	/// <summary>
	/// Resolves v1 ILPatch entries against loaded dnlib modules using stable method identity.
	/// This intentionally performs exact matching only. Structural/fuzzy matching belongs to
	/// the later rebase phase and must never be silently substituted here.
	/// </summary>
	static class ILPatchImportMatcher {
		public static ILPatchImportPreview CreatePreview(ILPatchDocument document, IEnumerable<ModuleDef> modules,
			IReadOnlyDictionary<string, ILPatchMethodIdentity>? targetOverrides = null) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (modules is null)
				throw new ArgumentNullException(nameof(modules));

			var loadedMethods = GetLoadedMethods(modules);
			var methodIndex = BuildMethodIndex(loadedMethods);
			var structuralCatalog = ILPatchStructuralMatcher.CreateCatalog(loadedMethods);
			var results = new List<ILPatchImportResult>(document.Methods.Count);
			foreach (var patch in document.Methods) {
				ILPatchMethodIdentity? targetOverride = null;
				if (targetOverrides is not null)
					targetOverrides.TryGetValue(patch.Id ?? string.Empty, out targetOverride);
				results.Add(Match(patch, methodIndex, structuralCatalog, targetOverride));
			}
			return new ILPatchImportPreview(document, results);
		}

		internal static bool SignaturesCompatible(ILPatchMethodIdentity source, ILPatchMethodIdentity candidate) {
			if (source is null || candidate is null)
				return false;
			if (source.HasThis != candidate.HasThis ||
				source.GenericArity != candidate.GenericArity ||
				!StringComparer.Ordinal.Equals(source.ReturnType, candidate.ReturnType) ||
				source.ParameterTypes.Count != candidate.ParameterTypes.Count)
				return false;

			for (int i = 0; i < source.ParameterTypes.Count; i++) {
				if (!StringComparer.Ordinal.Equals(source.ParameterTypes[i], candidate.ParameterTypes[i]))
					return false;
			}
			return true;
		}

		static MethodDef[] GetLoadedMethods(IEnumerable<ModuleDef> modules) {
			var methods = new List<MethodDef>();
			var visitedModules = new HashSet<ModuleDef>();
			foreach (var module in modules) {
				if (module is null || !visitedModules.Add(module))
					continue;
				foreach (var type in module.GetTypes())
					methods.AddRange(type.Methods);
			}
			return methods.Distinct().ToArray();
		}

		static Dictionary<string, List<MethodDef>> BuildMethodIndex(IEnumerable<MethodDef> loadedMethods) {
			var index = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
			foreach (var method in loadedMethods) {
				string key = ILPatchMethodIdentity.Create(method).ToCanonicalString();
				if (!index.TryGetValue(key, out var methods))
					index.Add(key, methods = new List<MethodDef>());
				methods.Add(method);
			}
			return index;
		}

		static ILPatchImportResult Match(ILPatchMethodChange patch, Dictionary<string, List<MethodDef>> index,
			ILPatchStructuralMatcher.Catalog structuralCatalog, ILPatchMethodIdentity? targetOverride) {
			if (patch is null)
				throw new ArgumentNullException(nameof(patch));
			if (patch.Target is null || patch.BaseBody is null || patch.PatchedBody is null) {
				return new ILPatchImportResult(patch, ILPatchImportStatus.Missing, null, Array.Empty<MethodDef>(),
					"Patch entry is incomplete and cannot be matched.");
			}

			if (targetOverride is not null) {
				var structuralCandidates = structuralCatalog.FindCandidates(patch);
				if (!SignaturesCompatible(patch.Target, targetOverride)) {
					return new ILPatchImportResult(patch, ILPatchImportStatus.Incompatible, null, Array.Empty<MethodDef>(),
						$"The manually selected target '{targetOverride}' does not have a compatible static/instance, generic, return or parameter signature.",
						structuralCandidates: structuralCandidates, isManualTarget: true);
				}

				string overrideKey = targetOverride.ToCanonicalString();
				if (!index.TryGetValue(overrideKey, out var overrideCandidates) || overrideCandidates.Count == 0) {
					return new ILPatchImportResult(patch, ILPatchImportStatus.Missing, null, Array.Empty<MethodDef>(),
						$"The manually selected target '{targetOverride}' is no longer loaded. Clear the override or choose another candidate.",
						structuralCandidates: structuralCandidates, isManualTarget: true);
				}
				if (overrideCandidates.Count != 1) {
					return new ILPatchImportResult(patch, ILPatchImportStatus.Ambiguous, null, overrideCandidates.ToArray(),
						$"{overrideCandidates.Count} loaded methods match the manually selected identity '{targetOverride}'.",
						structuralCandidates: structuralCandidates, isManualTarget: true);
				}

				return EvaluateTarget(patch, overrideCandidates[0], overrideCandidates.ToArray(), structuralCatalog,
					isManualTarget: true, knownStructuralCandidates: structuralCandidates);
			}

			string key = patch.Target.ToCanonicalString();
			if (!index.TryGetValue(key, out var candidates) || candidates.Count == 0) {
				var structuralCandidates = structuralCatalog.FindCandidates(patch);
				return new ILPatchImportResult(patch, ILPatchImportStatus.Missing, null, Array.Empty<MethodDef>(),
					"No loaded method has the exact assembly, module, type, name and signature identity.",
					structuralCandidates: structuralCandidates);
			}

			if (candidates.Count != 1) {
				return new ILPatchImportResult(patch, ILPatchImportStatus.Ambiguous, null, candidates.ToArray(),
					$"{candidates.Count} loaded methods have the same exact identity. Select a target explicitly before applying this patch.");
			}

			return EvaluateTarget(patch, candidates[0], candidates.ToArray(), structuralCatalog, isManualTarget: false);
		}

		static ILPatchImportResult EvaluateTarget(ILPatchMethodChange patch, MethodDef target,
			IReadOnlyList<MethodDef> candidates, ILPatchStructuralMatcher.Catalog structuralCatalog,
			bool isManualTarget, IReadOnlyList<ILPatchStructuralCandidate>? knownStructuralCandidates = null) {
			IReadOnlyList<ILPatchStructuralCandidate> StructuralCandidates() =>
				knownStructuralCandidates ?? structuralCatalog.FindCandidates(patch);

			string targetKind = isManualTarget ? "Manually selected target" : "Exact target";
			if (target.Body is null) {
				var rebasePreview = ILPatchRebaseAnalyzer.Analyze(patch, target);
				return new ILPatchImportResult(patch, ILPatchImportStatus.BaseChanged, target, candidates,
					$"{targetKind} exists but no longer has a CIL body.",
					structuralCandidates: StructuralCandidates(), rebasePreview: rebasePreview, isManualTarget: isManualTarget);
			}

			var snapshot = CilNormalizer.CreateSnapshot(target);
			snapshot.CanonicalHash = ILPatchBodyHasher.Compute(snapshot);
			if (StringComparer.Ordinal.Equals(snapshot.CanonicalHash, patch.PatchedBody.CanonicalHash)) {
				return new ILPatchImportResult(patch, ILPatchImportStatus.AlreadyApplied, target, candidates,
					$"{targetKind} found and its current body already matches the patched body.",
					snapshot.CanonicalHash, knownStructuralCandidates, isManualTarget: isManualTarget);
			}

			bool baselineMatches = StringComparer.Ordinal.Equals(snapshot.CanonicalHash, patch.BaseBody.CanonicalHash);
			if (!baselineMatches &&
				ILPatchRebasedAppliedDetector.TryDetect(patch, target, snapshot, out string rebasedAppliedMessage)) {
				return new ILPatchImportResult(patch, ILPatchImportStatus.RebasedApplied, target, candidates,
					$"{targetKind}: {rebasedAppliedMessage}", snapshot.CanonicalHash,
					knownStructuralCandidates, isManualTarget: isManualTarget);
			}

			if (!baselineMatches) {
				var rebasePreview = ILPatchRebaseAnalyzer.Analyze(patch, target);
				return new ILPatchImportResult(patch, ILPatchImportStatus.BaseChanged, target, candidates,
					$"{targetKind} found, but its current body hash {ShortHash(snapshot.CanonicalHash)} differs from both patch baseline {ShortHash(patch.BaseBody.CanonicalHash)} and patched body {ShortHash(patch.PatchedBody.CanonicalHash)}.",
					snapshot.CanonicalHash, StructuralCandidates(), rebasePreview, isManualTarget);
			}

			string message = isManualTarget
				? "Manually selected target has a compatible signature and its baseline body hash matches exactly."
				: "Exact target and baseline body hash match.";
			Guid currentMvid = target.Module?.Mvid ?? Guid.Empty;
			if (patch.BaseModuleMvid != Guid.Empty && currentMvid != Guid.Empty && patch.BaseModuleMvid != currentMvid)
				message += " Module MVID differs from the source patch, which is allowed because MVID is provenance only.";

			return new ILPatchImportResult(patch, ILPatchImportStatus.Exact, target, candidates, message,
				snapshot.CanonicalHash, knownStructuralCandidates, isManualTarget: isManualTarget);
		}

		static string ShortHash(string hash) {
			if (string.IsNullOrEmpty(hash))
				return "<missing>";
			return hash.Length <= 12 ? hash : hash.Substring(0, 12);
		}
	}
}
