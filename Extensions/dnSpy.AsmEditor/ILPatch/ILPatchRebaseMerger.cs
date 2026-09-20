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

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Materializes a normalized merged body for a rebase preview that the analyzer classified
	/// as Clean. The current-version body remains the base; only stored patch hunks are spliced
	/// into it. All branch/switch/EH instruction indices are then translated to the merged body.
	/// </summary>
	static class ILPatchRebaseMerger {
		enum InstructionOrigin {
			Current,
			Patched,
		}

		readonly struct InstructionSource {
			public InstructionOrigin Origin { get; }
			public int Index { get; }

			public InstructionSource(InstructionOrigin origin, int index) {
				Origin = origin;
				Index = index;
			}
		}

		public static bool TryCreateMergedSnapshot(ILPatchMethodChange patch, ILPatchRebasePreview preview,
			out ILPatchMethodBodySnapshot? merged, out string error) {
			merged = null;
			error = string.Empty;

			if (patch is null) {
				error = "Patch is missing.";
				return false;
			}
			if (preview is null) {
				error = "Rebase preview is missing.";
				return false;
			}
			if (preview.Status != ILPatchRebaseStatus.Clean) {
				error = $"Rebase preview status is {preview.Status}, not Clean.";
				return false;
			}
			if (patch.PatchedBody is null || patch.BaseBody is null) {
				error = "Patch entry does not contain complete bodies.";
				return false;
			}
			if (preview.CurrentBody is null || preview.BaseToCurrent is null || preview.BaseToPatched is null) {
				error = "Rebase preview does not contain the alignment data required to materialize a merge.";
				return false;
			}
			if (preview.Hunks.Count == 0) {
				error = "There are no instruction hunks to merge.";
				return false;
			}
			if (preview.LocalsChangedByPatch || preview.ExceptionHandlersChangedByPatch ||
				preview.InitLocalsChangedByPatch) {
				error = "Patch-side method-body metadata changes are not supported by automatic rebasing.";
				return false;
			}
			if (preview.LocalsChangedUpstream && !preview.UpstreamLocalsCompatible) {
				error = "The current version changed existing local-variable slots; only append-only upstream local changes are safe.";
				return false;
			}

			var current = preview.CurrentBody;
			var sources = new List<InstructionSource>(current.Instructions.Count);
			for (int i = 0; i < current.Instructions.Count; i++)
				sources.Add(new InstructionSource(InstructionOrigin.Current, i));

			// NewStart coordinates refer to the untouched current body. Applying in descending
			// order keeps every earlier coordinate stable while we splice later regions.
			foreach (var hunk in preview.Hunks.OrderByDescending(a => a.NewStart)) {
				if (hunk.Status != ILPatchRebaseHunkStatus.Clean || hunk.NewStart < 0) {
					error = "Rebase preview contains a non-clean hunk.";
					return false;
				}
				if (hunk.NewStart > sources.Count || hunk.NewLength < 0 ||
					hunk.NewStart + hunk.NewLength > sources.Count) {
					error = "A rebase hunk points outside the current method body.";
					return false;
				}
				if (hunk.PatchedStart < 0 || hunk.PatchedLength < 0 ||
					hunk.PatchedStart + hunk.PatchedLength > patch.PatchedBody.Instructions.Count) {
					error = "A rebase hunk points outside the stored patched body.";
					return false;
				}

				sources.RemoveRange(hunk.NewStart, hunk.NewLength);
				for (int i = hunk.PatchedLength - 1; i >= 0; i--)
					sources.Insert(hunk.NewStart, new InstructionSource(InstructionOrigin.Patched, hunk.PatchedStart + i));
			}

			var currentToMerged = CreateOriginMap(current.Instructions.Count, sources, InstructionOrigin.Current);
			var patchedToMerged = CreateOriginMap(patch.PatchedBody.Instructions.Count, sources, InstructionOrigin.Patched);

			// Fill patched indices for unchanged instructions via base -> patched/current -> merged.
			for (int baseIndex = 0; baseIndex < patch.BaseBody.Instructions.Count; baseIndex++) {
				int? patchedIndex = preview.BaseToPatched[baseIndex];
				int? currentIndex = preview.BaseToCurrent[baseIndex];
				if (!patchedIndex.HasValue || !currentIndex.HasValue)
					continue;
				if ((uint)patchedIndex.Value >= (uint)patchedToMerged.Length ||
					(uint)currentIndex.Value >= (uint)currentToMerged.Length)
					continue;
				if (!patchedToMerged[patchedIndex.Value].HasValue && currentToMerged[currentIndex.Value].HasValue)
					patchedToMerged[patchedIndex.Value] = currentToMerged[currentIndex.Value];
			}

			var result = new ILPatchMethodBodySnapshot {
				Method = ILPatchMethodIdentity.Create(preview.Target),
				InitLocals = current.InitLocals,
				MaxStack = Math.Max(current.MaxStack, patch.PatchedBody.MaxStack),
			};
			result.Locals.AddRange(current.Locals);

			foreach (var source in sources) {
				ILPatchInstruction original;
				int?[] indexMap;
				if (source.Origin == InstructionOrigin.Current) {
					original = current.Instructions[source.Index];
					indexMap = currentToMerged;
				}
				else {
					original = patch.PatchedBody.Instructions[source.Index];
					indexMap = patchedToMerged;
				}

				if (!TryTranslateInstruction(original, indexMap, out var translated, out error))
					return false;
				result.Instructions.Add(translated!);
			}

			foreach (var handler in current.ExceptionHandlers) {
				if (!TryTranslateExceptionHandler(handler, currentToMerged, out var translated, out error))
					return false;
				result.ExceptionHandlers.Add(translated!);
			}

			result.CanonicalHash = ILPatchBodyHasher.Compute(result);
			merged = result;
			return true;
		}

		static int?[] CreateOriginMap(int sourceCount, IReadOnlyList<InstructionSource> sources, InstructionOrigin origin) {
			var result = new int?[sourceCount];
			for (int mergedIndex = 0; mergedIndex < sources.Count; mergedIndex++) {
				var source = sources[mergedIndex];
				if (source.Origin == origin && (uint)source.Index < (uint)result.Length)
					result[source.Index] = mergedIndex;
			}
			return result;
		}

		static bool TryTranslateInstruction(ILPatchInstruction source, int?[] indexMap,
			out ILPatchInstruction? translated, out string error) {
			translated = null;
			error = string.Empty;
			var operand = source.Operand ?? ILPatchOperand.None;
			if (!TryTranslateOperand(operand, indexMap, out var translatedOperand, out error))
				return false;

			translated = new ILPatchInstruction {
				OpCode = source.OpCode ?? string.Empty,
				Operand = translatedOperand!,
			};
			return true;
		}

		static bool TryTranslateOperand(ILPatchOperand source, int?[] indexMap,
			out ILPatchOperand? translated, out string error) {
			translated = null;
			error = string.Empty;

			switch (source.Kind) {
			case ILPatchOperandKind.BranchTarget:
				if (!TryTranslateIndex(source.Index, indexMap, out int branchTarget)) {
					error = $"Could not translate branch target instruction {source.Index} into the merged body.";
					return false;
				}
				translated = new ILPatchOperand {
					Kind = source.Kind,
					Index = branchTarget,
				};
				return true;

			case ILPatchOperandKind.SwitchTargets:
				var sourceTargets = source.Indices ?? Array.Empty<int>();
				var targets = new int[sourceTargets.Length];
				for (int i = 0; i < sourceTargets.Length; i++) {
					if (!TryTranslateIndex(sourceTargets[i], indexMap, out targets[i])) {
						error = $"Could not translate switch target instruction {sourceTargets[i]} into the merged body.";
						return false;
					}
				}
				translated = new ILPatchOperand {
					Kind = source.Kind,
					Indices = targets,
				};
				return true;

			default:
				translated = CloneOperand(source);
				return true;
			}
		}

		static ILPatchOperand CloneOperand(ILPatchOperand source) => new ILPatchOperand {
			Kind = source.Kind,
			Text = source.Text,
			IntegerValue = source.IntegerValue,
			FloatingPointValue = source.FloatingPointValue,
			Index = source.Index,
			Indices = source.Indices?.ToArray(),
		};

		static bool TryTranslateExceptionHandler(ILPatchExceptionHandler source, int?[] currentToMerged,
			out ILPatchExceptionHandler? translated, out string error) {
			translated = null;
			error = string.Empty;

			if (!TryTranslateBoundary(source.TryStart, currentToMerged, out int tryStart) ||
				!TryTranslateBoundary(source.TryEnd, currentToMerged, out int tryEnd) ||
				!TryTranslateBoundary(source.HandlerStart, currentToMerged, out int handlerStart) ||
				!TryTranslateBoundary(source.HandlerEnd, currentToMerged, out int handlerEnd) ||
				!TryTranslateBoundary(source.FilterStart, currentToMerged, out int filterStart)) {
				error = "Could not translate an exception-handler boundary into the merged body.";
				return false;
			}

			translated = new ILPatchExceptionHandler {
				HandlerType = source.HandlerType ?? string.Empty,
				CatchType = source.CatchType,
				TryStart = tryStart,
				TryEnd = tryEnd,
				HandlerStart = handlerStart,
				HandlerEnd = handlerEnd,
				FilterStart = filterStart,
			};
			return true;
		}

		static bool TryTranslateBoundary(int sourceIndex, int?[] currentToMerged, out int translated) {
			if (sourceIndex < 0) {
				translated = sourceIndex;
				return true;
			}
			return TryTranslateIndex(sourceIndex, currentToMerged, out translated);
		}

		static bool TryTranslateIndex(int sourceIndex, int?[] indexMap, out int translated) {
			if ((uint)sourceIndex < (uint)indexMap.Length) {
				int? value = indexMap[sourceIndex];
				if (value.HasValue) {
					translated = value.GetValueOrDefault();
					return true;
				}
			}
			translated = -1;
			return false;
		}
	}
}
