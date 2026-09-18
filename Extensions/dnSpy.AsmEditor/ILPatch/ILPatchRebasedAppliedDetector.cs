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
using dnlib.DotNet;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Detects that a patch has already been clean-rebased into a newer method body.
	///
	/// The detector intentionally uses a round trip instead of similarity:
	/// current --(reverse patch)--> candidate upstream --(original patch)--> current
	///
	/// Only if both rebases are Clean and the final normalized body hash exactly equals the
	/// original current body do we report RebasedApplied.
	/// </summary>
	static class ILPatchRebasedAppliedDetector {
		public static bool TryDetect(ILPatchMethodChange patch, MethodDef target,
			ILPatchMethodBodySnapshot current, out string message) {
			message = string.Empty;
			if (patch is null)
				throw new ArgumentNullException(nameof(patch));
			if (target is null)
				throw new ArgumentNullException(nameof(target));
			if (current is null)
				throw new ArgumentNullException(nameof(current));
			if (patch.BaseBody is null || patch.PatchedBody is null)
				return false;

			var reverse = new ILPatchMethodChange {
				Id = patch.Id + "-reverse-detect",
				Target = patch.Target,
				BaseModuleMvid = patch.BaseModuleMvid,
				BaseBody = patch.PatchedBody,
				PatchedBody = patch.BaseBody,
			};

			var reversePreview = ILPatchRebaseAnalyzer.Analyze(reverse, target, current);
			if (reversePreview.Status != ILPatchRebaseStatus.Clean || reversePreview.Hunks.Count == 0)
				return false;

			if (!ILPatchRebaseMerger.TryCreateMergedSnapshot(reverse, reversePreview,
				out var candidateUpstream, out _ ) || candidateUpstream is null)
				return false;

			var forwardPreview = ILPatchRebaseAnalyzer.Analyze(patch, target, candidateUpstream);
			if (forwardPreview.Status != ILPatchRebaseStatus.Clean || forwardPreview.Hunks.Count == 0)
				return false;

			if (!ILPatchRebaseMerger.TryCreateMergedSnapshot(patch, forwardPreview,
				out var roundTrip, out _ ) || roundTrip is null)
				return false;

			if (!StringComparer.Ordinal.Equals(roundTrip.CanonicalHash, current.CanonicalHash))
				return false;

			message =
				"The current method is not byte-for-byte the old patched body, but the patch can be " +
				"cleanly removed and reapplied with an exact normalized round-trip. It is already " +
				"present on top of newer upstream IL.";
			return true;
		}
	}
}
