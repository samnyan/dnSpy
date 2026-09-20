/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

using System;
using System.Collections.Generic;

namespace dnSpy.AsmEditor.ILPatch {
	enum ILPatchDiffKind {
		Same,
		Added,
		Removed,
		Modified,
	}

	sealed class ILPatchDiffRow {
		public ILPatchDiffKind Kind { get; }
		public int? LeftLineNumber { get; }
		public string LeftText { get; }
		public int? RightLineNumber { get; }
		public string RightText { get; }

		public ILPatchDiffRow(ILPatchDiffKind kind, int? leftLineNumber, string leftText,
			int? rightLineNumber, string rightText) {
			Kind = kind;
			LeftLineNumber = leftLineNumber;
			LeftText = leftText ?? string.Empty;
			RightLineNumber = rightLineNumber;
			RightText = rightText ?? string.Empty;
		}
	}

	/// <summary>
	/// Produces aligned, side-by-side line rows. Change blocks between LCS anchors pair
	/// deletions/additions as Modified rows first, then leave any remainder as Added/Removed.
	/// </summary>
	static class ILPatchDiffEngine {
		const long MaxLcsCells = 4_000_000;

		public static IReadOnlyList<ILPatchDiffRow> Compare(IReadOnlyList<string> left,
			IReadOnlyList<string> right) {
			if (left is null)
				throw new ArgumentNullException(nameof(left));
			if (right is null)
				throw new ArgumentNullException(nameof(right));

			if ((long)(left.Count + 1) * (right.Count + 1) > MaxLcsCells)
				return CompareLinearFallback(left, right);

			var lcs = new int[left.Count + 1, right.Count + 1];
			for (int i = left.Count - 1; i >= 0; i--) {
				for (int j = right.Count - 1; j >= 0; j--) {
					lcs[i, j] = StringComparer.Ordinal.Equals(left[i], right[j])
						? lcs[i + 1, j + 1] + 1
						: Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
				}
			}

			var result = new List<ILPatchDiffRow>();
			var removed = new List<(int Index, string Text)>();
			var added = new List<(int Index, string Text)>();
			int oldIndex = 0;
			int newIndex = 0;
			while (oldIndex < left.Count || newIndex < right.Count) {
				if (oldIndex < left.Count && newIndex < right.Count &&
					StringComparer.Ordinal.Equals(left[oldIndex], right[newIndex])) {
					FlushChangeBlock(result, removed, added);
					result.Add(new ILPatchDiffRow(ILPatchDiffKind.Same,
						oldIndex + 1, left[oldIndex], newIndex + 1, right[newIndex]));
					oldIndex++;
					newIndex++;
					continue;
				}

				if (newIndex < right.Count &&
					(oldIndex == left.Count || lcs[oldIndex, newIndex + 1] >= lcs[oldIndex + 1, newIndex])) {
					added.Add((newIndex, right[newIndex]));
					newIndex++;
				}
				else {
					removed.Add((oldIndex, left[oldIndex]));
					oldIndex++;
				}
			}
			FlushChangeBlock(result, removed, added);
			return result;
		}

		static void FlushChangeBlock(List<ILPatchDiffRow> result,
			List<(int Index, string Text)> removed, List<(int Index, string Text)> added) {
			int paired = Math.Min(removed.Count, added.Count);
			for (int i = 0; i < paired; i++) {
				result.Add(new ILPatchDiffRow(ILPatchDiffKind.Modified,
					removed[i].Index + 1, removed[i].Text,
					added[i].Index + 1, added[i].Text));
			}
			for (int i = paired; i < removed.Count; i++) {
				result.Add(new ILPatchDiffRow(ILPatchDiffKind.Removed,
					removed[i].Index + 1, removed[i].Text, null, string.Empty));
			}
			for (int i = paired; i < added.Count; i++) {
				result.Add(new ILPatchDiffRow(ILPatchDiffKind.Added,
					null, string.Empty, added[i].Index + 1, added[i].Text));
			}
			removed.Clear();
			added.Clear();
		}

		static IReadOnlyList<ILPatchDiffRow> CompareLinearFallback(IReadOnlyList<string> left,
			IReadOnlyList<string> right) {
			var result = new List<ILPatchDiffRow>(Math.Max(left.Count, right.Count));
			int common = Math.Min(left.Count, right.Count);
			for (int i = 0; i < common; i++) {
				bool same = StringComparer.Ordinal.Equals(left[i], right[i]);
				result.Add(new ILPatchDiffRow(same ? ILPatchDiffKind.Same : ILPatchDiffKind.Modified,
					i + 1, left[i], i + 1, right[i]));
			}
			for (int i = common; i < left.Count; i++)
				result.Add(new ILPatchDiffRow(ILPatchDiffKind.Removed, i + 1, left[i], null, string.Empty));
			for (int i = common; i < right.Count; i++)
				result.Add(new ILPatchDiffRow(ILPatchDiffKind.Added, null, string.Empty, i + 1, right[i]));
			return result;
		}
	}
}
