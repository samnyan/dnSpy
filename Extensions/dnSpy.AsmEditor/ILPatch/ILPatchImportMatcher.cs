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
		BaseChanged,
		Missing,
		Ambiguous,
	}

	sealed class ILPatchImportResult {
		public ILPatchMethodChange Patch { get; }
		public ILPatchImportStatus Status { get; }
		public MethodDef? Target { get; }
		public IReadOnlyList<MethodDef> Candidates { get; }
		public string Message { get; }
		public string? CurrentBodyHash { get; }
		public IReadOnlyList<ILPatchStructuralCandidate> StructuralCandidates { get; }

		public ILPatchImportResult(ILPatchMethodChange patch, ILPatchImportStatus status, MethodDef? target,
			IReadOnlyList<MethodDef> candidates, string message, string? currentBodyHash = null,
			IReadOnlyList<ILPatchStructuralCandidate>? structuralCandidates = null) {
			Patch = patch ?? throw new ArgumentNullException(nameof(patch));
			Status = status;
			Target = target;
			Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
			Message = message ?? string.Empty;
			CurrentBodyHash = currentBodyHash;
			StructuralCandidates = structuralCandidates ?? Array.Empty<ILPatchStructuralCandidate>();
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
		public static ILPatchImportPreview CreatePreview(ILPatchDocument document, IEnumerable<ModuleDef> modules) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (modules is null)
				throw new ArgumentNullException(nameof(modules));

			var loadedMethods = GetLoadedMethods(modules);
			var methodIndex = BuildMethodIndex(loadedMethods);
			var structuralCatalog = ILPatchStructuralMatcher.CreateCatalog(loadedMethods);
			var results = new List<ILPatchImportResult>(document.Methods.Count);
			foreach (var patch in document.Methods)
				results.Add(Match(patch, methodIndex, structuralCatalog));
			return new ILPatchImportPreview(document, results);
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
			ILPatchStructuralMatcher.Catalog structuralCatalog) {
			if (patch is null)
				throw new ArgumentNullException(nameof(patch));
			if (patch.Target is null || patch.BaseBody is null || patch.PatchedBody is null) {
				return new ILPatchImportResult(patch, ILPatchImportStatus.Missing, null, Array.Empty<MethodDef>(),
					"Patch entry is incomplete and cannot be matched.");
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

			var target = candidates[0];
			if (target.Body is null) {
				var structuralCandidates = structuralCatalog.FindCandidates(patch);
				return new ILPatchImportResult(patch, ILPatchImportStatus.BaseChanged, target, candidates.ToArray(),
					"The exact target method exists but no longer has a CIL body.",
					structuralCandidates: structuralCandidates);
			}

			var snapshot = CilNormalizer.CreateSnapshot(target);
			snapshot.CanonicalHash = ILPatchBodyHasher.Compute(snapshot);
			if (StringComparer.Ordinal.Equals(snapshot.CanonicalHash, patch.PatchedBody.CanonicalHash)) {
				return new ILPatchImportResult(patch, ILPatchImportStatus.AlreadyApplied, target, candidates.ToArray(),
					"Exact target found and its current body already matches the patched body.",
					snapshot.CanonicalHash);
			}

			if (!StringComparer.Ordinal.Equals(snapshot.CanonicalHash, patch.BaseBody.CanonicalHash)) {
				var structuralCandidates = structuralCatalog.FindCandidates(patch);
				return new ILPatchImportResult(patch, ILPatchImportStatus.BaseChanged, target, candidates.ToArray(),
					$"Exact target found, but its current body hash {ShortHash(snapshot.CanonicalHash)} differs from both patch baseline {ShortHash(patch.BaseBody.CanonicalHash)} and patched body {ShortHash(patch.PatchedBody.CanonicalHash)}.",
					snapshot.CanonicalHash, structuralCandidates);
			}

			string message = "Exact target and baseline body hash match.";
			Guid currentMvid = target.Module?.Mvid ?? Guid.Empty;
			if (patch.BaseModuleMvid != Guid.Empty && currentMvid != Guid.Empty && patch.BaseModuleMvid != currentMvid)
				message += " Module MVID differs from the source patch, which is allowed because MVID is provenance only.";

			return new ILPatchImportResult(patch, ILPatchImportStatus.Exact, target, candidates.ToArray(), message, snapshot.CanonicalHash);
		}

		static string ShortHash(string hash) {
			if (string.IsNullOrEmpty(hash))
				return "<missing>";
			return hash.Length <= 12 ? hash : hash.Substring(0, 12);
		}
	}
}
