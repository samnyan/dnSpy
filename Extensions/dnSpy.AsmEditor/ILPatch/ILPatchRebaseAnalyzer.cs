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
	enum ILPatchRebaseStatus {
		Clean,
		Conflict,
		Unsupported,
	}

	enum ILPatchRebaseHunkStatus {
		Clean,
		Conflict,
	}

	sealed class ILPatchRebaseHunk {
		public int BaseStart { get; }
		public int BaseLength { get; }
		public int PatchedStart { get; }
		public int PatchedLength { get; }
		public int NewStart { get; }
		public int NewLength { get; }
		public ILPatchRebaseHunkStatus Status { get; }
		public string Message { get; }

		public ILPatchRebaseHunk(int baseStart, int baseLength, int patchedStart, int patchedLength,
			int newStart, int newLength, ILPatchRebaseHunkStatus status, string message) {
			BaseStart = baseStart;
			BaseLength = baseLength;
			PatchedStart = patchedStart;
			PatchedLength = patchedLength;
			NewStart = newStart;
			NewLength = newLength;
			Status = status;
			Message = message ?? string.Empty;
		}
	}

	sealed class ILPatchRebasePreview {
		public ILPatchRebaseStatus Status { get; }
		public MethodDef Target { get; }
		public string CurrentBodyHash { get; }
		public IReadOnlyList<ILPatchRebaseHunk> Hunks { get; }
		public string Message { get; }
		public bool LocalsChangedByPatch { get; }
		public bool ExceptionHandlersChangedByPatch { get; }
		public bool InitLocalsChangedByPatch { get; }
		public bool LocalsChangedUpstream { get; }
		public bool UpstreamLocalsCompatible { get; }
		public bool ExceptionHandlersChangedUpstream { get; }
		internal ILPatchMethodBodySnapshot? CurrentBody { get; }
		internal int?[]? BaseToPatched { get; }
		internal int?[]? BaseToCurrent { get; }

		public ILPatchRebasePreview(ILPatchRebaseStatus status, MethodDef target, string currentBodyHash,
			IReadOnlyList<ILPatchRebaseHunk> hunks, string message, bool localsChangedByPatch = false,
			bool exceptionHandlersChangedByPatch = false, bool initLocalsChangedByPatch = false,
			bool localsChangedUpstream = false, bool upstreamLocalsCompatible = true,
			bool exceptionHandlersChangedUpstream = false,
			ILPatchMethodBodySnapshot? currentBody = null, int?[]? baseToPatched = null, int?[]? baseToCurrent = null) {
			Status = status;
			Target = target ?? throw new ArgumentNullException(nameof(target));
			CurrentBodyHash = currentBodyHash ?? string.Empty;
			Hunks = hunks ?? throw new ArgumentNullException(nameof(hunks));
			Message = message ?? string.Empty;
			LocalsChangedByPatch = localsChangedByPatch;
			ExceptionHandlersChangedByPatch = exceptionHandlersChangedByPatch;
			InitLocalsChangedByPatch = initLocalsChangedByPatch;
			LocalsChangedUpstream = localsChangedUpstream;
			UpstreamLocalsCompatible = upstreamLocalsCompatible;
			ExceptionHandlersChangedUpstream = exceptionHandlersChangedUpstream;
			CurrentBody = currentBody;
			BaseToPatched = baseToPatched;
			BaseToCurrent = baseToCurrent;
		}
	}

	/// <summary>
	/// Performs a conservative three-way overlap analysis for an exact-identity method whose
	/// current body no longer equals the stored baseline.
	///
	/// This class does not materialize or mutate a merged body. It answers the preceding
	/// question: can each old base -> old patched instruction hunk still be located in the new
	/// body without overlapping an upstream edit?
	/// </summary>
	static class ILPatchRebaseAnalyzer {
		const long MaxLcsCells = 1_500_000;

		readonly struct MatchPair {
			public int Left { get; }
			public int Right { get; }

			public MatchPair(int left, int right) {
				Left = left;
				Right = right;
			}
		}

		readonly struct DiffHunk {
			public int BaseStart { get; }
			public int BaseLength { get; }
			public int PatchedStart { get; }
			public int PatchedLength { get; }

			public DiffHunk(int baseStart, int baseLength, int patchedStart, int patchedLength) {
				BaseStart = baseStart;
				BaseLength = baseLength;
				PatchedStart = patchedStart;
				PatchedLength = patchedLength;
			}
		}

		public static ILPatchRebasePreview Analyze(ILPatchMethodChange patch, MethodDef target) {
			if (patch is null)
				throw new ArgumentNullException(nameof(patch));
			if (target is null)
				throw new ArgumentNullException(nameof(target));
			if (patch.BaseBody is null || patch.PatchedBody is null)
				return Unsupported(target, string.Empty, "Patch entry does not contain complete baseline and patched bodies.");
			if (target.Body is null)
				return Unsupported(target, string.Empty, "The target method no longer has a CIL body.");

			var current = CilNormalizer.CreateSnapshot(target);
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);
			return Analyze(patch, target, current);
		}

		internal static ILPatchRebasePreview Analyze(ILPatchMethodChange patch, MethodDef target,
			ILPatchMethodBodySnapshot current) {
			if (patch is null)
				throw new ArgumentNullException(nameof(patch));
			if (target is null)
				throw new ArgumentNullException(nameof(target));
			if (current is null)
				throw new ArgumentNullException(nameof(current));
			if (patch.BaseBody is null || patch.PatchedBody is null)
				return Unsupported(target, current.CanonicalHash, "Patch entry does not contain complete baseline and patched bodies.");

			bool localsChanged = !patch.BaseBody.Locals.SequenceEqual(patch.PatchedBody.Locals, StringComparer.Ordinal);
			bool handlersChanged = false;
			bool initLocalsChanged = patch.BaseBody.InitLocals != patch.PatchedBody.InitLocals;

			// First align base -> patched using branch-insensitive keys. A simple insertion can
			// shift every later branch target instruction index, which must not turn otherwise
			// unchanged branch instructions into fake patch hunks.
			var basePatchAlignment = patch.BaseBody.Instructions.Select(CreateAlignmentKey).ToArray();
			var patchedAlignment = patch.PatchedBody.Instructions.Select(CreateAlignmentKey).ToArray();
			if (!TryGetMatches(basePatchAlignment, patchedAlignment, out var relaxedPatchMatches, out string error))
				return Unsupported(target, current.CanonicalHash, error, localsChanged, handlersChanged, initLocalsChanged);

			var baseToPatched = CreateIndexMap(patch.BaseBody.Instructions.Count, relaxedPatchMatches);
			handlersChanged = !ExceptionHandlersEquivalentAfterMapping(
				patch.BaseBody.ExceptionHandlers,
				patch.PatchedBody.ExceptionHandlers,
				baseToPatched);
			var patchMatches = relaxedPatchMatches
				.Where(pair => InstructionMatchesMappedBaseline(
					patch.BaseBody.Instructions[pair.Left],
					patch.PatchedBody.Instructions[pair.Right],
					baseToPatched))
				.ToArray();

			var diffHunks = CreateDiffHunks(patch.BaseBody.Instructions.Count, patch.PatchedBody.Instructions.Count, patchMatches);
			if (diffHunks.Count == 0) {
				if (localsChanged || handlersChanged || initLocalsChanged) {
					return Unsupported(target, current.CanonicalHash,
						"The patch changes method-body metadata (locals, exception handlers or InitLocals) but has no instruction hunk. Metadata rebasing is not implemented yet.",
						localsChanged, handlersChanged, initLocalsChanged);
				}
				return new ILPatchRebasePreview(ILPatchRebaseStatus.Clean, target, current.CanonicalHash,
					Array.Empty<ILPatchRebaseHunk>(), "The stored patch contains no effective instruction hunk.",
					currentBody: current);
			}

			var baseAlignment = patch.BaseBody.Instructions.Select(CreateAlignmentKey).ToArray();
			var newAlignment = current.Instructions.Select(CreateAlignmentKey).ToArray();
			if (!TryGetMatches(baseAlignment, newAlignment, out var baseToNewMatches, out error))
				return Unsupported(target, current.CanonicalHash, error, localsChanged, handlersChanged, initLocalsChanged);

			var baseToNew = CreateIndexMap(patch.BaseBody.Instructions.Count, baseToNewMatches);
			bool localsChangedUpstream = !patch.BaseBody.Locals.SequenceEqual(current.Locals, StringComparer.Ordinal);
			bool upstreamLocalsCompatible = !localsChangedUpstream ||
				LocalsArePrefix(patch.BaseBody.Locals, current.Locals);
			bool handlersChangedUpstream = !ExceptionHandlersEquivalentAfterMapping(
				patch.BaseBody.ExceptionHandlers,
				current.ExceptionHandlers,
				baseToNew);

			var hunks = new List<ILPatchRebaseHunk>(diffHunks.Count);
			foreach (var diff in diffHunks)
				hunks.Add(AnalyzeHunk(diff, patch.BaseBody, current, baseToNew));

			bool hasConflict = hunks.Any(a => a.Status == ILPatchRebaseHunkStatus.Conflict);
			if (hasConflict) {
				return new ILPatchRebasePreview(ILPatchRebaseStatus.Conflict, target, current.CanonicalHash, hunks,
					"At least one patch hunk overlaps or cannot be unambiguously aligned with changes in the current method.",
					localsChanged, handlersChanged, initLocalsChanged, localsChangedUpstream, upstreamLocalsCompatible,
					handlersChangedUpstream, current, baseToPatched, baseToNew);
			}

			if (localsChanged || handlersChanged || initLocalsChanged) {
				return new ILPatchRebasePreview(ILPatchRebaseStatus.Unsupported, target, current.CanonicalHash, hunks,
					"Instruction hunks do not overlap upstream changes, but the patch also changes locals, exception handlers or InitLocals. Extended body metadata rebasing is still required.",
					localsChanged, handlersChanged, initLocalsChanged, localsChangedUpstream, upstreamLocalsCompatible,
					handlersChangedUpstream, current, baseToPatched, baseToNew);
			}

			if (localsChangedUpstream && !upstreamLocalsCompatible) {
				return new ILPatchRebasePreview(ILPatchRebaseStatus.Unsupported, target, current.CanonicalHash, hunks,
					"Instruction hunks do not overlap, but the current version changed existing local-variable slots. Only append-only upstream local changes are safe for automatic rebasing.",
					localsChanged, handlersChanged, initLocalsChanged, localsChangedUpstream, upstreamLocalsCompatible,
					handlersChangedUpstream, current, baseToPatched, baseToNew);
			}

			if (!PreservedCurrentReferencesRemainMappable(current, hunks, out string referenceError)) {
				return new ILPatchRebasePreview(ILPatchRebaseStatus.Unsupported, target, current.CanonicalHash, hunks,
					referenceError,
					localsChanged, handlersChanged, initLocalsChanged, localsChangedUpstream, upstreamLocalsCompatible,
					handlersChangedUpstream, current, baseToPatched, baseToNew);
			}

			return new ILPatchRebasePreview(ILPatchRebaseStatus.Clean, target, current.CanonicalHash, hunks,
				"All instruction hunks can be located without overlapping current-version changes. Upstream append-only locals and exception-handler changes are preserved.",
				localsChanged, handlersChanged, initLocalsChanged, localsChangedUpstream, upstreamLocalsCompatible,
				handlersChangedUpstream, current, baseToPatched, baseToNew);
		}

		static bool PreservedCurrentReferencesRemainMappable(ILPatchMethodBodySnapshot current,
			IReadOnlyList<ILPatchRebaseHunk> hunks, out string error) {
			error = string.Empty;
			var removed = new bool[current.Instructions.Count];
			foreach (var hunk in hunks) {
				if (hunk.Status != ILPatchRebaseHunkStatus.Clean || hunk.NewLength <= 0)
					continue;
				int end = hunk.NewStart + hunk.NewLength;
				if (hunk.NewStart < 0 || end > removed.Length) {
					error = "A clean rebase hunk points outside the current method while validating preserved control-flow references.";
					return false;
				}
				for (int i = hunk.NewStart; i < end; i++)
					removed[i] = true;
			}

			for (int index = 0; index < current.Instructions.Count; index++) {
				if (removed[index])
					continue;
				var operand = current.Instructions[index].Operand ?? ILPatchOperand.None;
				switch (operand.Kind) {
				case ILPatchOperandKind.BranchTarget:
					if (TargetsRemovedInstruction(operand.Index, removed)) {
						error = $"Current instruction {index} branches to instruction {operand.Index}, which this patch hunk replaces. The target cannot be mapped conservatively.";
						return false;
					}
					break;
				case ILPatchOperandKind.SwitchTargets:
					foreach (int target in operand.Indices ?? Array.Empty<int>()) {
						if (TargetsRemovedInstruction(target, removed)) {
							error = $"Current instruction {index} has a switch target {target} that this patch hunk replaces. The target cannot be mapped conservatively.";
							return false;
						}
					}
					break;
				}
			}

			foreach (var handler in current.ExceptionHandlers) {
				if (TargetsRemovedInstruction(handler.TryStart, removed) ||
					TargetsRemovedInstruction(handler.TryEnd, removed) ||
					TargetsRemovedInstruction(handler.HandlerStart, removed) ||
					TargetsRemovedInstruction(handler.HandlerEnd, removed) ||
					TargetsRemovedInstruction(handler.FilterStart, removed)) {
					error = "A current exception-handler boundary points to an instruction replaced by this patch hunk. Automatic rebasing cannot choose an equivalent boundary safely.";
					return false;
				}
			}
			return true;
		}

		static bool TargetsRemovedInstruction(int index, bool[] removed) =>
			index >= 0 && (uint)index < (uint)removed.Length && removed[index];

		static bool LocalsArePrefix(IReadOnlyList<string> baseline, IReadOnlyList<string> current) {
			if (baseline.Count > current.Count)
				return false;
			for (int i = 0; i < baseline.Count; i++) {
				if (!StringComparer.Ordinal.Equals(baseline[i], current[i]))
					return false;
			}
			return true;
		}

		static ILPatchRebasePreview Unsupported(MethodDef target, string hash, string message,
			bool localsChanged = false, bool handlersChanged = false, bool initLocalsChanged = false) =>
			new ILPatchRebasePreview(ILPatchRebaseStatus.Unsupported, target, hash,
				Array.Empty<ILPatchRebaseHunk>(), message, localsChanged, handlersChanged, initLocalsChanged);

		static ILPatchRebaseHunk AnalyzeHunk(DiffHunk hunk, ILPatchMethodBodySnapshot baseline,
			ILPatchMethodBodySnapshot current, int?[] baseToNew) {
			if (hunk.BaseLength == 0)
				return AnalyzeInsertion(hunk, baseline.Instructions.Count, current.Instructions.Count, baseToNew);

			int? first = baseToNew[hunk.BaseStart];
			if (!first.HasValue) {
				return Conflict(hunk, "The first baseline instruction touched by this patch hunk no longer aligns to the current method.");
			}

			for (int offset = 0; offset < hunk.BaseLength; offset++) {
				int baseIndex = hunk.BaseStart + offset;
				int? newIndex = baseToNew[baseIndex];
				if (!newIndex.HasValue)
					return Conflict(hunk, $"Baseline instruction {baseIndex} touched by this patch no longer aligns to the current method.");
				if (newIndex.Value != first.Value + offset)
					return Conflict(hunk, "The current version inserted or removed instructions inside the baseline range touched by this patch.");
				if (!InstructionMatchesMappedBaseline(baseline.Instructions[baseIndex], current.Instructions[newIndex.Value], baseToNew))
					return Conflict(hunk, $"Current instruction {newIndex.Value} changed semantically inside the range touched by this patch.");
			}

			return new ILPatchRebaseHunk(hunk.BaseStart, hunk.BaseLength, hunk.PatchedStart, hunk.PatchedLength,
				first.Value, hunk.BaseLength, ILPatchRebaseHunkStatus.Clean,
				$"Baseline [{hunk.BaseStart}, {hunk.BaseStart + hunk.BaseLength}) maps contiguously to current [{first.Value}, {first.Value + hunk.BaseLength}).");
		}

		static ILPatchRebaseHunk AnalyzeInsertion(DiffHunk hunk, int baseCount, int newCount, int?[] baseToNew) {
			int position = hunk.BaseStart;
			if (baseCount == 0) {
				if (newCount != 0)
					return Conflict(hunk, "The patch inserts into an empty baseline method, but the current method is no longer empty.");
				return CleanInsertion(hunk, 0);
			}

			if (position == 0) {
				int? next = baseToNew[0];
				if (!next.HasValue || next.Value != 0)
					return Conflict(hunk, "Both the patch and the current version insert/change instructions before the first baseline instruction.");
				return CleanInsertion(hunk, 0);
			}

			if (position == baseCount) {
				int? previous = baseToNew[baseCount - 1];
				if (!previous.HasValue || previous.Value != newCount - 1)
					return Conflict(hunk, "Both the patch and the current version insert/change instructions after the last baseline instruction.");
				return CleanInsertion(hunk, newCount);
			}

			int? before = baseToNew[position - 1];
			int? after = baseToNew[position];
			if (!before.HasValue || !after.HasValue)
				return Conflict(hunk, "The insertion boundary cannot be anchored on both neighboring baseline instructions.");
			if (after.Value != before.Value + 1)
				return Conflict(hunk, "The current version also inserted or changed instructions at this patch insertion boundary.");
			return CleanInsertion(hunk, after.Value);
		}

		static ILPatchRebaseHunk CleanInsertion(DiffHunk hunk, int newStart) =>
			new ILPatchRebaseHunk(hunk.BaseStart, 0, hunk.PatchedStart, hunk.PatchedLength,
				newStart, 0, ILPatchRebaseHunkStatus.Clean,
				$"Insertion boundary maps cleanly to current instruction position {newStart}.");

		static ILPatchRebaseHunk Conflict(DiffHunk hunk, string message) =>
			new ILPatchRebaseHunk(hunk.BaseStart, hunk.BaseLength, hunk.PatchedStart, hunk.PatchedLength,
				-1, 0, ILPatchRebaseHunkStatus.Conflict, message);

		static bool ExceptionHandlersEquivalentAfterMapping(
			IReadOnlyList<ILPatchExceptionHandler> baseline,
			IReadOnlyList<ILPatchExceptionHandler> patched,
			int?[] baseToPatched) {
			if (baseline.Count != patched.Count)
				return false;

			for (int i = 0; i < baseline.Count; i++) {
				var left = baseline[i];
				var right = patched[i];
				if (!StringComparer.Ordinal.Equals(left.HandlerType, right.HandlerType) ||
					!StringComparer.Ordinal.Equals(left.CatchType, right.CatchType))
					return false;
				if (!BoundaryMatches(left.TryStart, right.TryStart, baseToPatched) ||
					!BoundaryMatches(left.TryEnd, right.TryEnd, baseToPatched) ||
					!BoundaryMatches(left.HandlerStart, right.HandlerStart, baseToPatched) ||
					!BoundaryMatches(left.HandlerEnd, right.HandlerEnd, baseToPatched) ||
					!BoundaryMatches(left.FilterStart, right.FilterStart, baseToPatched))
					return false;
			}
			return true;
		}

		static bool BoundaryMatches(int baselineIndex, int patchedIndex, int?[] baseToPatched) {
			if (baselineIndex < 0 || patchedIndex < 0)
				return baselineIndex == patchedIndex;
			return TryMapIndex(baselineIndex, baseToPatched, out int mapped) && mapped == patchedIndex;
		}

		static bool InstructionMatchesMappedBaseline(ILPatchInstruction baseline, ILPatchInstruction current, int?[] baseToNew) {
			if (!StringComparer.Ordinal.Equals(baseline.OpCode, current.OpCode))
				return false;

			var left = baseline.Operand ?? ILPatchOperand.None;
			var right = current.Operand ?? ILPatchOperand.None;
			if (left.Kind != right.Kind)
				return false;

			switch (left.Kind) {
			case ILPatchOperandKind.BranchTarget:
				return TryMapIndex(left.Index, baseToNew, out int mapped) && right.Index == mapped;

			case ILPatchOperandKind.SwitchTargets:
				var leftTargets = left.Indices ?? Array.Empty<int>();
				var rightTargets = right.Indices ?? Array.Empty<int>();
				if (leftTargets.Length != rightTargets.Length)
					return false;
				for (int i = 0; i < leftTargets.Length; i++) {
					if (!TryMapIndex(leftTargets[i], baseToNew, out mapped) || rightTargets[i] != mapped)
						return false;
				}
				return true;

			default:
				return StringComparer.Ordinal.Equals(left.ToCanonicalString(), right.ToCanonicalString());
			}
		}

		static bool TryMapIndex(int baseIndex, int?[] baseToNew, out int mapped) {
			if ((uint)baseIndex < (uint)baseToNew.Length) {
				int? value = baseToNew[baseIndex];
				if (value.HasValue) {
					mapped = value.GetValueOrDefault();
					return true;
				}
			}
			mapped = -1;
			return false;
		}

		static string CreateAlignmentKey(ILPatchInstruction instruction) {
			var operand = instruction.Operand ?? ILPatchOperand.None;
			switch (operand.Kind) {
			case ILPatchOperandKind.BranchTarget:
				return $"{instruction.OpCode}|branch";
			case ILPatchOperandKind.SwitchTargets:
				return $"{instruction.OpCode}|switch:{(operand.Indices ?? Array.Empty<int>()).Length}";
			default:
				return instruction.ToCanonicalString();
			}
		}

		static int?[] CreateIndexMap(int sourceCount, IReadOnlyList<MatchPair> matches) {
			var map = new int?[sourceCount];
			foreach (var pair in matches) {
				if ((uint)pair.Left < (uint)map.Length)
					map[pair.Left] = pair.Right;
			}
			return map;
		}

		static List<DiffHunk> CreateDiffHunks(int baseCount, int patchedCount, IReadOnlyList<MatchPair> matches) {
			var result = new List<DiffHunk>();
			int baseCursor = 0;
			int patchedCursor = 0;
			foreach (var match in matches) {
				if (match.Left > baseCursor || match.Right > patchedCursor) {
					result.Add(new DiffHunk(baseCursor, match.Left - baseCursor,
						patchedCursor, match.Right - patchedCursor));
				}
				baseCursor = match.Left + 1;
				patchedCursor = match.Right + 1;
			}
			if (baseCursor < baseCount || patchedCursor < patchedCount)
				result.Add(new DiffHunk(baseCursor, baseCount - baseCursor, patchedCursor, patchedCount - patchedCursor));
			return result;
		}

		static bool TryGetMatches(string[] left, string[] right, out IReadOnlyList<MatchPair> matches, out string error) {
			matches = Array.Empty<MatchPair>();
			error = string.Empty;
			long cells = (long)(left.Length + 1) * (right.Length + 1);
			if (cells > MaxLcsCells) {
				error = $"Method is too large for the current rebase LCS analyzer ({left.Length} x {right.Length} instructions).";
				return false;
			}

			var lcs = new int[left.Length + 1, right.Length + 1];
			for (int i = left.Length - 1; i >= 0; i--) {
				for (int j = right.Length - 1; j >= 0; j--)
					lcs[i, j] = StringComparer.Ordinal.Equals(left[i], right[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
			}

			var result = new List<MatchPair>();
			int leftIndex = 0;
			int rightIndex = 0;
			while (leftIndex < left.Length && rightIndex < right.Length) {
				if (StringComparer.Ordinal.Equals(left[leftIndex], right[rightIndex])) {
					result.Add(new MatchPair(leftIndex, rightIndex));
					leftIndex++;
					rightIndex++;
				}
				else if (lcs[leftIndex + 1, rightIndex] >= lcs[leftIndex, rightIndex + 1])
					leftIndex++;
				else
					rightIndex++;
			}

			matches = result;
			return true;
		}
	}
}
