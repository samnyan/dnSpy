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

namespace dnSpy.AsmEditor.ILPatch {
	sealed class ILPatchBatchSource {
		public string Name { get; }
		public ILPatchDocument Document { get; }

		public ILPatchBatchSource(string name, ILPatchDocument document) {
			Name = name ?? string.Empty;
			Document = document ?? throw new ArgumentNullException(nameof(document));
		}
	}

	/// <summary>
	/// Combines independent patch documents for one GUI preview/apply batch.
	///
	/// Multiple entries targeting the same original method are intentionally rejected. Such
	/// patches may depend on application order and therefore need sequential preview/apply
	/// instead of one shared pre-mutation snapshot.
	/// </summary>
	static class ILPatchBatchComposer {
		public static bool TryCombine(IReadOnlyList<ILPatchBatchSource> sources,
			out ILPatchDocument? combined, out string error) {
			combined = null;
			error = string.Empty;
			if (sources is null)
				throw new ArgumentNullException(nameof(sources));
			if (sources.Count == 0) {
				error = "No patch documents were selected.";
				return false;
			}

			var patchIds = new HashSet<string>(StringComparer.Ordinal);
			var targets = new Dictionary<string, string>(StringComparer.Ordinal);
			var result = new ILPatchDocument {
				Name = sources.Count == 1 ? sources[0].Document.Name : $"Batch import ({sources.Count} files)",
				CreatedUtc = DateTime.UtcNow,
			};

			foreach (var source in sources) {
				if (source.Document.TypeChanges.Count != 0 && sources.Count > 1) {
					error = $"'{source.Name}' contains type/member structural changes. Multi-file structural composition is not implemented yet; import it separately.";
					return false;
				}
				foreach (var patch in source.Document.Methods) {
					if (patch is null || patch.Target is null) {
						error = $"'{source.Name}' contains an incomplete patch entry without a target identity.";
						return false;
					}
					if (string.IsNullOrWhiteSpace(patch.Id)) {
						error = $"'{source.Name}' contains a patch entry without a stable id. Stable ids are required for multi-file import.";
						return false;
					}
					if (!patchIds.Add(patch.Id)) {
						error = $"Patch id '{patch.Id}' appears more than once across the selected files. Multi-file import requires unique patch ids.";
						return false;
					}

					string targetKey = patch.Target.ToCanonicalString();
					if (targets.TryGetValue(targetKey, out string? firstSource)) {
						error =
							$"Method '{patch.Target}' is patched by both '{firstSource}' and '{source.Name}'. " +
							"These files may depend on application order, so apply/rebase them sequentially instead of as one batch.";
						return false;
					}

					targets.Add(targetKey, source.Name);
					result.Methods.Add(patch);
				}
				if (sources.Count == 1)
					result.TypeChanges.AddRange(source.Document.TypeChanges);
			}

			combined = result;
			return true;
		}
	}
}
