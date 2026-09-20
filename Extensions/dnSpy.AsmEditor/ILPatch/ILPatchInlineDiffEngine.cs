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
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace dnSpy.AsmEditor.ILPatch {
	sealed class ILPatchInlineFragment {
		public string Text { get; }
		public bool IsChanged { get; }

		public ILPatchInlineFragment(string text, bool isChanged) {
			Text = text ?? string.Empty;
			IsChanged = isChanged;
		}
	}

	sealed class ILPatchInlineDiff {
		public IReadOnlyList<ILPatchInlineFragment> Left { get; }
		public IReadOnlyList<ILPatchInlineFragment> Right { get; }

		public ILPatchInlineDiff(IReadOnlyList<ILPatchInlineFragment> left,
			IReadOnlyList<ILPatchInlineFragment> right) {
			Left = left;
			Right = right;
		}
	}

	/// <summary>
	/// Token-level inline diff used inside a line that is already classified as Modified.
	/// Identifiers/numbers, whitespace runs and punctuation are separate tokens. LCS anchors
	/// preserve unchanged pieces while non-anchored tokens are highlighted independently.
	/// </summary>
	static class ILPatchInlineDiffEngine {
		const int MaxTokenCells = 40_000;

		public static ILPatchInlineDiff Compare(string left, string right) {
			left ??= string.Empty;
			right ??= string.Empty;
			if (StringComparer.Ordinal.Equals(left, right)) {
				return new ILPatchInlineDiff(
					new[] { new ILPatchInlineFragment(left, false) },
					new[] { new ILPatchInlineFragment(right, false) });
			}

			var leftTokens = Tokenize(left);
			var rightTokens = Tokenize(right);
			if ((long)(leftTokens.Count + 1) * (rightTokens.Count + 1) > MaxTokenCells) {
				return new ILPatchInlineDiff(
					new[] { new ILPatchInlineFragment(left, true) },
					new[] { new ILPatchInlineFragment(right, true) });
			}

			var lcs = new int[leftTokens.Count + 1, rightTokens.Count + 1];
			for (int i = leftTokens.Count - 1; i >= 0; i--) {
				for (int j = rightTokens.Count - 1; j >= 0; j--) {
					lcs[i, j] = StringComparer.Ordinal.Equals(leftTokens[i], rightTokens[j])
						? lcs[i + 1, j + 1] + 1
						: Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
				}
			}

			var leftFlags = new bool[leftTokens.Count];
			var rightFlags = new bool[rightTokens.Count];
			for (int i = 0; i < leftFlags.Length; i++)
				leftFlags[i] = true;
			for (int i = 0; i < rightFlags.Length; i++)
				rightFlags[i] = true;

			int leftIndex = 0;
			int rightIndex = 0;
			while (leftIndex < leftTokens.Count && rightIndex < rightTokens.Count) {
				if (StringComparer.Ordinal.Equals(leftTokens[leftIndex], rightTokens[rightIndex])) {
					leftFlags[leftIndex] = false;
					rightFlags[rightIndex] = false;
					leftIndex++;
					rightIndex++;
				}
				else if (lcs[leftIndex + 1, rightIndex] >= lcs[leftIndex, rightIndex + 1])
					leftIndex++;
				else
					rightIndex++;
			}

			return new ILPatchInlineDiff(
				BuildFragments(leftTokens, leftFlags),
				BuildFragments(rightTokens, rightFlags));
		}

		static IReadOnlyList<ILPatchInlineFragment> BuildFragments(
			IReadOnlyList<string> tokens, IReadOnlyList<bool> changed) {
			var result = new List<ILPatchInlineFragment>();
			if (tokens.Count == 0)
				return new[] { new ILPatchInlineFragment(string.Empty, false) };

			var builder = new StringBuilder();
			bool currentChanged = changed[0];
			for (int i = 0; i < tokens.Count; i++) {
				if (changed[i] != currentChanged) {
					result.Add(new ILPatchInlineFragment(builder.ToString(), currentChanged));
					builder.Clear();
					currentChanged = changed[i];
				}
				builder.Append(tokens[i]);
			}
			result.Add(new ILPatchInlineFragment(builder.ToString(), currentChanged));
			return result;
		}

		static IReadOnlyList<string> Tokenize(string text) {
			var result = new List<string>();
			int index = 0;
			while (index < text.Length) {
				int start = index;
				char ch = text[index];
				if (char.IsLetterOrDigit(ch) || ch == '_') {
					index++;
					while (index < text.Length) {
						char next = text[index];
						if (!char.IsLetterOrDigit(next) && next != '_')
							break;
						index++;
					}
				}
				else if (char.IsWhiteSpace(ch)) {
					index++;
					while (index < text.Length && char.IsWhiteSpace(text[index]))
						index++;
				}
				else if (IsOperatorChar(ch)) {
					index++;
					while (index < text.Length && IsOperatorChar(text[index]))
						index++;
				}
				else {
					index++;
				}
				result.Add(text.Substring(start, index - start));
			}
			return result;
		}

		static bool IsOperatorChar(char ch) {
			switch (ch) {
			case '!':
			case '%':
			case '&':
			case '*':
			case '+':
			case '-':
			case '/':
			case ':':
			case '<':
			case '=':
			case '>':
			case '?':
			case '^':
			case '|':
			case '~':
				return true;
			default:
				return false;
			}
		}
	}
}
