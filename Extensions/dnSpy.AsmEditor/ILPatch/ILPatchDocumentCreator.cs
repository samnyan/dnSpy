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
	sealed class ILPatchCreateReport {
		public int ChangedCount { get; internal set; }
		public int UnchangedCount { get; internal set; }
		public int UnsupportedCount => UnsupportedReasons.Count;
		public List<string> UnsupportedReasons { get; } = new List<string>();
	}

	/// <summary>
	/// Creates a v1 method-body patch by comparing an original module with a separately saved
	/// modified copy of the same module.
	///
	/// v1 intentionally fails closed when the method shape changed. Added/removed/renamed
	/// methods, signature changes, CIL/non-CIL transitions and method flag changes belong to a
	/// future metadata patch format and must not be silently omitted.
	/// </summary>
	static class ILPatchDocumentCreator {
		public static bool TryCreate(ModuleDef original, ModuleDef modified, string name,
			out ILPatchDocument? document, out ILPatchCreateReport report) {
			if (original is null)
				throw new ArgumentNullException(nameof(original));
			if (modified is null)
				throw new ArgumentNullException(nameof(modified));

			report = new ILPatchCreateReport();
			document = null;

			if (!StringComparer.Ordinal.Equals(original.Name?.String ?? string.Empty,
				modified.Name?.String ?? string.Empty)) {
				report.UnsupportedReasons.Add(
					$"Module name changed from '{original.Name}' to '{modified.Name}'.");
			}
			if (!StringComparer.Ordinal.Equals(original.Assembly?.Name?.String ?? string.Empty,
				modified.Assembly?.Name?.String ?? string.Empty)) {
				report.UnsupportedReasons.Add(
					$"Assembly name changed from '{original.Assembly?.Name}' to '{modified.Assembly?.Name}'.");
			}

			var originalIndex = BuildMethodIndex(original);
			var modifiedIndex = BuildMethodIndex(modified);
			var allKeys = new SortedSet<string>(originalIndex.Keys, StringComparer.Ordinal);
			allKeys.UnionWith(modifiedIndex.Keys);

			var created = new ILPatchDocument {
				Name = name ?? string.Empty,
				CreatedUtc = DateTime.UtcNow,
			};

			foreach (string key in allKeys) {
				originalIndex.TryGetValue(key, out var originalMethods);
				modifiedIndex.TryGetValue(key, out var modifiedMethods);

				if (originalMethods is null || originalMethods.Count == 0) {
					report.UnsupportedReasons.Add(
						$"Modified assembly added method '{Describe(modifiedMethods![0])}'.");
					continue;
				}
				if (modifiedMethods is null || modifiedMethods.Count == 0) {
					report.UnsupportedReasons.Add(
						$"Modified assembly removed or renamed method '{Describe(originalMethods[0])}'.");
					continue;
				}
				if (originalMethods.Count != 1 || modifiedMethods.Count != 1) {
					report.UnsupportedReasons.Add(
						$"Method identity '{key}' is ambiguous (original={originalMethods.Count}, modified={modifiedMethods.Count}).");
					continue;
				}

				var beforeMethod = originalMethods[0];
				var afterMethod = modifiedMethods[0];
				if (beforeMethod.Attributes != afterMethod.Attributes ||
					beforeMethod.ImplAttributes != afterMethod.ImplAttributes) {
					report.UnsupportedReasons.Add(
						$"Method metadata flags changed for '{Describe(beforeMethod)}'. v1 only captures CIL body changes.");
					continue;
				}

				bool beforeHasBody = beforeMethod.Body is not null;
				bool afterHasBody = afterMethod.Body is not null;
				if (beforeHasBody != afterHasBody) {
					report.UnsupportedReasons.Add(
						$"Method '{Describe(beforeMethod)}' changed between CIL and non-CIL/no-body form.");
					continue;
				}
				if (!beforeHasBody) {
					report.UnchangedCount++;
					continue;
				}

				var baseline = CilNormalizer.CreateSnapshot(beforeMethod);
				baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);
				var patched = CilNormalizer.CreateSnapshot(afterMethod);
				patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);

				if (StringComparer.Ordinal.Equals(baseline.CanonicalHash, patched.CanonicalHash)) {
					report.UnchangedCount++;
					continue;
				}

				// The exact method identity is the original patch target. Both body snapshots use
				// that identity even if the modified file has a different MVID/provenance.
				var identity = ILPatchMethodIdentity.Create(beforeMethod);
				baseline.Method = identity;
				patched.Method = identity;
				created.Methods.Add(new ILPatchMethodChange {
					Target = identity,
					BaseModuleMvid = original.Mvid,
					BaseBody = baseline,
					PatchedBody = patched,
				});
				report.ChangedCount++;
			}

			if (report.UnsupportedCount != 0)
				return false;

			document = created;
			return true;
		}

		static Dictionary<string, List<MethodDef>> BuildMethodIndex(ModuleDef module) {
			var result = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					string key = ILPatchMethodIdentity.Create(method).ToCanonicalString();
					if (!result.TryGetValue(key, out var methods))
						result.Add(key, methods = new List<MethodDef>());
					methods.Add(method);
				}
			}
			return result;
		}

		static string Describe(MethodDef method) =>
			ILPatchMethodIdentity.Create(method).ToString();
	}
}
