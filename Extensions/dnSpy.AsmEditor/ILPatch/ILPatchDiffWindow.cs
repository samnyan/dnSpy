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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Settings.AppearanceCategory;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.AsmEditor.ILPatch {
	enum ILPatchDiffViewMode {
		NormalizedIL,
		DecompiledCSharp,
	}

	static class ILPatchDiffTextProvider {
		public static string[] GetNormalizedILLines(ILPatchMethodBodySnapshot snapshot) {
			if (snapshot is null)
				throw new ArgumentNullException(nameof(snapshot));

			var lines = new List<string> {
				$".initlocals {snapshot.InitLocals.ToString().ToLowerInvariant()}",
				$".maxstack {snapshot.MaxStack}",
			};
			for (int i = 0; i < snapshot.Locals.Count; i++)
				lines.Add($".local {i:D3} {snapshot.Locals[i]}");
			for (int i = 0; i < snapshot.Instructions.Count; i++)
				lines.Add($"{i:D4}: {snapshot.Instructions[i]}");
			foreach (var handler in snapshot.ExceptionHandlers)
				lines.Add(".eh " + handler.ToCanonicalString());
			return lines.ToArray();
		}

		public static bool TryGetDecompiledCSharp(ILPatchMethodChange change, MethodDef? target,
			IDecompilerService decompilerService, out string[] before, out string[] after, out string error) {
			before = Array.Empty<string>();
			after = Array.Empty<string>();
			error = string.Empty;
			if (change is null)
				throw new ArgumentNullException(nameof(change));
			if (decompilerService is null)
				throw new ArgumentNullException(nameof(decompilerService));
			if (target is null || target.Module is null || target.Body is null) {
				error = "A compatible loaded target method is required for Decompiled C# mode. Normalized IL mode is still available.";
				return false;
			}

			var materializer = new ILPatchBodyMaterializer(target.Module);
			if (!materializer.TryCreate(target, change.BaseBody, out var baselineBody, out string baselineError) ||
				baselineBody is null) {
				error = "Could not materialize the baseline body for decompilation: " + baselineError;
				return false;
			}
			if (!materializer.TryCreate(target, change.PatchedBody, out var patchedBody, out string patchedError) ||
				patchedBody is null) {
				error = "Could not materialize the patched body for decompilation: " + patchedError;
				return false;
			}

			var decompiler = decompilerService.Find(DecompilerConstants.LANGUAGE_CSHARP_ILSPY) ??
				decompilerService.FindOrDefault(DecompilerConstants.LANGUAGE_CSHARP);
			var originalBody = target.Body;
			try {
				target.Body = baselineBody;
				before = DecompileMethod(target, decompiler);
				target.Body = patchedBody;
				after = DecompileMethod(target, decompiler);
				return true;
			}
			catch (Exception ex) {
				error = "C# decompilation failed: " + ex.Message;
				before = Array.Empty<string>();
				after = Array.Empty<string>();
				return false;
			}
			finally {
				target.Body = originalBody;
			}
		}

		static string[] DecompileMethod(MethodDef method, IDecompiler decompiler) {
			var output = new StringBuilderDecompilerOutput();
			var context = new DecompilationContext {
				AsyncMethodBodyDecompilation = false,
				IsBodyModified = candidate => ReferenceEquals(candidate, method),
			};
			decompiler.Decompile(method, output, context);
			return SplitLines(output.GetText());
		}

		static string[] SplitLines(string text) {
			var lines = (text ?? string.Empty)
				.Replace("\r\n", "\n")
				.Replace('\r', '\n')
				.Split(new[] { '\n' }, StringSplitOptions.None);
			if (lines.Length != 0 && lines[lines.Length - 1].Length == 0)
				return lines.Take(lines.Length - 1).ToArray();
			return lines;
		}
	}

	/// <summary>
	/// Read-only side-by-side review surface backed by dnSpy's native text editor. Both panes
	/// deliberately prohibit user input: applying/rebasing a patch remains an explicit action
	/// in Patch Workspace. Text selection/copy stays enabled for manual merge workflows.
	/// </summary>
	sealed class ILPatchDiffWindow : Window {
		static readonly string[] ReadOnlyRoles = new[] {
			PredefinedTextViewRoles.Analyzable,
			PredefinedTextViewRoles.Document,
			PredefinedTextViewRoles.Interactive,
			PredefinedTextViewRoles.Structured,
			PredefinedTextViewRoles.Zoomable,
		};

		readonly ILPatchMethodChange change;
		readonly MethodDef? target;
		readonly IDecompilerService decompilerService;
		readonly ITextBufferFactoryService textBufferFactoryService;
		readonly IDsTextEditorFactoryService textEditorFactoryService;
		readonly IContentTypeRegistryService contentTypeRegistryService;
		readonly ComboBox modeSelector;
		readonly CheckBox showAllContext;
		readonly Button previousChangeButton;
		readonly Button nextChangeButton;
		readonly Button copyPatchedButton;
		readonly TextBlock statsText;
		readonly TextBlock statusText;
		readonly TextBlock leftHeader;
		readonly TextBlock rightHeader;
		readonly ITextBuffer leftBuffer;
		readonly ITextBuffer rightBuffer;
		readonly IDsWpfTextView leftView;
		readonly IDsWpfTextView rightView;
		readonly IDsWpfTextViewHost leftHost;
		readonly IDsWpfTextViewHost rightHost;
		IReadOnlyList<DiffDisplayRow> visibleRows = Array.Empty<DiffDisplayRow>();
		int selectedChangeRow = -1;
		bool synchronizingViewport;

		public ILPatchDiffWindow(ILPatchMethodChange change, MethodDef? target,
			IDecompilerService decompilerService, ITextBufferFactoryService textBufferFactoryService,
			IDsTextEditorFactoryService textEditorFactoryService, IContentTypeRegistryService contentTypeRegistryService) {
			this.change = change ?? throw new ArgumentNullException(nameof(change));
			this.target = target;
			this.decompilerService = decompilerService ?? throw new ArgumentNullException(nameof(decompilerService));
			this.textBufferFactoryService = textBufferFactoryService ?? throw new ArgumentNullException(nameof(textBufferFactoryService));
			this.textEditorFactoryService = textEditorFactoryService ?? throw new ArgumentNullException(nameof(textEditorFactoryService));
			this.contentTypeRegistryService = contentTypeRegistryService ?? throw new ArgumentNullException(nameof(contentTypeRegistryService));

			Title = "IL Patch Diff — " + change.Target;
			Width = 1480;
			Height = 880;
			MinWidth = 900;
			MinHeight = 520;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;

			var root = new Grid { Margin = new Thickness(8) };
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			var toolbar = new DockPanel { LastChildFill = true };
			modeSelector = new ComboBox {
				MinWidth = 180,
				Margin = new Thickness(0, 0, 8, 0),
			};
			modeSelector.Items.Add("Normalized IL");
			modeSelector.Items.Add("Decompiled C#");
			modeSelector.SelectedIndex = 0;
			modeSelector.SelectionChanged += ModeSelector_SelectionChanged;
			DockPanel.SetDock(modeSelector, Dock.Left);
			toolbar.Children.Add(modeSelector);

			showAllContext = new CheckBox {
				Content = "Show all unchanged",
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 12, 0),
			};
			showAllContext.Checked += ShowAllContext_Changed;
			showAllContext.Unchecked += ShowAllContext_Changed;
			DockPanel.SetDock(showAllContext, Dock.Left);
			toolbar.Children.Add(showAllContext);

			previousChangeButton = new Button {
				Content = "Previous change",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(0, 0, 6, 0),
				IsEnabled = false,
			};
			previousChangeButton.Click += PreviousChangeButton_Click;
			DockPanel.SetDock(previousChangeButton, Dock.Left);
			toolbar.Children.Add(previousChangeButton);

			nextChangeButton = new Button {
				Content = "Next change",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(0, 0, 12, 0),
				IsEnabled = false,
			};
			nextChangeButton.Click += NextChangeButton_Click;
			DockPanel.SetDock(nextChangeButton, Dock.Left);
			toolbar.Children.Add(nextChangeButton);

			copyPatchedButton = new Button {
				Content = "Copy patched selection",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(0, 0, 12, 0),
				ToolTip = "Copy the selection from the read-only Patched pane. If nothing is selected, copy the complete patched projection. This is useful when manually merging into dnSpy's normal Edit Method (C#) dialog.",
			};
			copyPatchedButton.Click += CopyPatchedButton_Click;
			DockPanel.SetDock(copyPatchedButton, Dock.Left);
			toolbar.Children.Add(copyPatchedButton);

			statsText = new TextBlock {
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 12, 0),
			};
			DockPanel.SetDock(statsText, Dock.Right);
			toolbar.Children.Add(statsText);

			var legend = new TextBlock {
				Text = "Read-only review — select/copy text freely; applying changes is always explicit",
				VerticalAlignment = VerticalAlignment.Center,
			};
			toolbar.Children.Add(legend);
			Grid.SetRow(toolbar, 0);
			root.Children.Add(toolbar);

			statusText = new TextBlock {
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 6, 0, 6),
			};
			Grid.SetRow(statusText, 1);
			root.Children.Add(statusText);

			var editorGrid = new Grid();
			editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
			editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			editorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			editorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			leftHeader = CreateHeader("Original / Base");
			rightHeader = CreateHeader("Patched");
			Grid.SetColumn(leftHeader, 0);
			Grid.SetRow(leftHeader, 0);
			Grid.SetColumn(rightHeader, 2);
			Grid.SetRow(rightHeader, 0);
			editorGrid.Children.Add(leftHeader);
			editorGrid.Children.Add(rightHeader);

			var initialContentType = GetContentType(ILPatchDiffViewMode.NormalizedIL);
			leftBuffer = textBufferFactoryService.CreateTextBuffer(string.Empty, initialContentType);
			rightBuffer = textBufferFactoryService.CreateTextBuffer(string.Empty, initialContentType);
			(leftView, leftHost) = CreateReadOnlyEditor(leftBuffer);
			(rightView, rightHost) = CreateReadOnlyEditor(rightBuffer);
			leftView.LayoutChanged += LeftView_LayoutChanged;
			rightView.LayoutChanged += RightView_LayoutChanged;

			Grid.SetColumn(leftHost.HostControl, 0);
			Grid.SetRow(leftHost.HostControl, 1);
			Grid.SetColumn(rightHost.HostControl, 2);
			Grid.SetRow(rightHost.HostControl, 1);
			editorGrid.Children.Add(leftHost.HostControl);
			editorGrid.Children.Add(rightHost.HostControl);

			var splitter = new GridSplitter {
				Width = 8,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
			};
			Grid.SetColumn(splitter, 1);
			Grid.SetRow(splitter, 0);
			Grid.SetRowSpan(splitter, 2);
			editorGrid.Children.Add(splitter);

			Grid.SetRow(editorGrid, 2);
			root.Children.Add(editorGrid);
			Content = root;
			Closed += ILPatchDiffWindow_Closed;
			RefreshDiff();
		}

		static TextBlock CreateHeader(string text) => new TextBlock {
			Text = text,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(4, 2, 4, 5),
		};

		(IDsWpfTextView View, IDsWpfTextViewHost Host) CreateReadOnlyEditor(ITextBuffer buffer) {
			var roles = textEditorFactoryService.CreateTextViewRoleSet(ReadOnlyRoles);
			var options = new TextViewCreatorOptions { EnableUndoHistory = false };
			var view = textEditorFactoryService.CreateTextView(buffer, roles, options);
			view.Options.SetOptionValue(DefaultWpfViewOptions.AppearanceCategory, AppearanceCategoryConstants.TextEditor);
			view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
			var host = textEditorFactoryService.CreateTextViewHost(view, false);
			return (view, host);
		}

		IContentType GetContentType(ILPatchDiffViewMode mode) {
			string name = mode == ILPatchDiffViewMode.DecompiledCSharp ? ContentTypes.CSharp : ContentTypes.IL;
			return contentTypeRegistryService.GetContentType(name) ?? textBufferFactoryService.TextContentType;
		}

		void ILPatchDiffWindow_Closed(object? sender, EventArgs e) {
			leftView.LayoutChanged -= LeftView_LayoutChanged;
			rightView.LayoutChanged -= RightView_LayoutChanged;
			if (!leftHost.IsClosed)
				leftHost.Close();
			if (!rightHost.IsClosed)
				rightHost.Close();
		}

		void LeftView_LayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) =>
			SynchronizeViewport(leftView, rightView);

		void RightView_LayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) =>
			SynchronizeViewport(rightView, leftView);

		void SynchronizeViewport(IDsWpfTextView source, IDsWpfTextView destination) {
			if (synchronizingViewport || source.IsClosed || destination.IsClosed)
				return;
			double delta = source.ViewportTop - destination.ViewportTop;
			if (Math.Abs(delta) < 0.5)
				return;
			try {
				synchronizingViewport = true;
				destination.ViewScroller.ScrollViewportVerticallyByPixels(delta);
			}
			finally {
				synchronizingViewport = false;
			}
		}

		void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) {
			if (IsLoaded || Content is not null)
				RefreshDiff();
		}

		void RefreshDiff() {
			ILPatchDiffViewMode mode = modeSelector.SelectedIndex == 1
				? ILPatchDiffViewMode.DecompiledCSharp
				: ILPatchDiffViewMode.NormalizedIL;

			string[] left;
			string[] right;
			if (mode == ILPatchDiffViewMode.DecompiledCSharp) {
				if (!ILPatchDiffTextProvider.TryGetDecompiledCSharp(change, target, decompilerService,
					out left, out right, out string error)) {
					statusText.Text = error;
					SetEditorText(mode, Array.Empty<DiffDisplayRow>());
					statsText.Text = string.Empty;
					previousChangeButton.IsEnabled = false;
					nextChangeButton.IsEnabled = false;
					return;
				}
				statusText.Text =
					"Decompiled C# is a review-only projection produced by dnSpy's C# decompiler. " +
					"The authoritative patch representation remains normalized IL.";
			}
			else {
				left = ILPatchDiffTextProvider.GetNormalizedILLines(change.BaseBody);
				right = ILPatchDiffTextProvider.GetNormalizedILLines(change.PatchedBody);
				statusText.Text =
					$"Normalized IL diff — base {ShortHash(change.BaseBody.CanonicalHash)} -> patched {ShortHash(change.PatchedBody.CanonicalHash)}.";
			}

			var rows = ILPatchDiffEngine.Compare(left, right);
			int added = rows.Count(a => a.Kind == ILPatchDiffKind.Added);
			int removed = rows.Count(a => a.Kind == ILPatchDiffKind.Removed);
			int modified = rows.Count(a => a.Kind == ILPatchDiffKind.Modified);
			int changed = added + removed + modified;
			statsText.Text = $"{changed} changed row(s)   +{added}  -{removed}  ~{modified}";
			previousChangeButton.IsEnabled = changed != 0;
			nextChangeButton.IsEnabled = changed != 0;

			visibleRows = CreateVisibleRows(rows, showAllContext.IsChecked == true, 3);
			SetEditorText(mode, visibleRows);
			selectedChangeRow = -1;
			if (changed != 0)
				SelectChange(forward: true, fromCurrent: false);
		}

		void SetEditorText(ILPatchDiffViewMode mode, IReadOnlyList<DiffDisplayRow> rows) {
			var contentType = GetContentType(mode);
			leftBuffer.ChangeContentType(contentType, null);
			rightBuffer.ChangeContentType(contentType, null);
			ReplaceAll(leftBuffer, string.Join(Environment.NewLine, rows.Select(a => a.LeftText)));
			ReplaceAll(rightBuffer, string.Join(Environment.NewLine, rows.Select(a => a.RightText)));
			leftHeader.Text = mode == ILPatchDiffViewMode.DecompiledCSharp ? "Original / Base — C#" : "Original / Base — Normalized IL";
			rightHeader.Text = mode == ILPatchDiffViewMode.DecompiledCSharp ? "Patched — C#" : "Patched — Normalized IL";
		}

		static void ReplaceAll(ITextBuffer buffer, string text) {
			var snapshot = buffer.CurrentSnapshot;
			buffer.Replace(new Span(0, snapshot.Length), text ?? string.Empty);
		}

		void CopyPatchedButton_Click(object sender, RoutedEventArgs e) {
			string text;
			if (!rightView.Selection.IsEmpty) {
				text = string.Join(Environment.NewLine,
					rightView.Selection.SelectedSpans.Select(a => a.GetText()));
			}
			else
				text = rightView.TextSnapshot.GetText();
			if (!string.IsNullOrEmpty(text))
				Clipboard.SetText(text);
		}

		void ShowAllContext_Changed(object sender, RoutedEventArgs e) => RefreshDiff();

		void PreviousChangeButton_Click(object sender, RoutedEventArgs e) =>
			SelectChange(forward: false, fromCurrent: true);

		void NextChangeButton_Click(object sender, RoutedEventArgs e) =>
			SelectChange(forward: true, fromCurrent: true);

		void SelectChange(bool forward, bool fromCurrent) {
			if (visibleRows.Count == 0)
				return;

			int start = forward ? -1 : visibleRows.Count;
			if (fromCurrent && selectedChangeRow >= 0)
				start = selectedChangeRow;
			for (int offset = 1; offset <= visibleRows.Count; offset++) {
				int index = forward
					? (start + offset + visibleRows.Count) % visibleRows.Count
					: (start - offset + visibleRows.Count) % visibleRows.Count;
				var row = visibleRows[index];
				if (row.IsSeparator || row.Kind == ILPatchDiffKind.Same)
					continue;
				selectedChangeRow = index;
				SelectEditorLine(leftView, index);
				SelectEditorLine(rightView, index);
				return;
			}
		}

		static void SelectEditorLine(IDsWpfTextView view, int lineNumber) {
			var snapshot = view.TextSnapshot;
			if (snapshot.LineCount == 0)
				return;
			lineNumber = Math.Max(0, Math.Min(lineNumber, snapshot.LineCount - 1));
			var line = snapshot.GetLineFromLineNumber(lineNumber);
			view.Caret.MoveTo(line.Start);
			view.Selection.Select(line.ExtentIncludingLineBreak, false);
			view.Caret.EnsureVisible();
		}

		static IReadOnlyList<DiffDisplayRow> CreateVisibleRows(IReadOnlyList<ILPatchDiffRow> rows,
			bool showAll, int contextLines) {
			if (showAll || rows.Count == 0)
				return rows.Select(a => new DiffDisplayRow(a)).ToArray();

			var keep = new bool[rows.Count];
			bool anyChanged = false;
			for (int i = 0; i < rows.Count; i++) {
				if (rows[i].Kind == ILPatchDiffKind.Same)
					continue;
				anyChanged = true;
				int start = Math.Max(0, i - contextLines);
				int end = Math.Min(rows.Count - 1, i + contextLines);
				for (int j = start; j <= end; j++)
					keep[j] = true;
			}
			if (!anyChanged)
				return rows.Select(a => new DiffDisplayRow(a)).ToArray();

			var result = new List<DiffDisplayRow>();
			int index = 0;
			while (index < rows.Count) {
				if (keep[index]) {
					result.Add(new DiffDisplayRow(rows[index]));
					index++;
					continue;
				}
				int hiddenStart = index;
				while (index < rows.Count && !keep[index])
					index++;
				result.Add(DiffDisplayRow.Separator(index - hiddenStart));
			}
			return result;
		}

		static string ShortHash(string? hash) =>
			string.IsNullOrEmpty(hash) ? "—" : hash.Length <= 12 ? hash : hash.Substring(0, 12);

		sealed class DiffDisplayRow {
			public ILPatchDiffKind Kind { get; }
			public bool IsSeparator { get; }
			public string LeftText { get; }
			public string RightText { get; }

			DiffDisplayRow(int hiddenCount) {
				Kind = ILPatchDiffKind.Same;
				IsSeparator = true;
				LeftText = $"// … {hiddenCount} unchanged line(s) …";
				RightText = LeftText;
			}

			public static DiffDisplayRow Separator(int hiddenCount) => new DiffDisplayRow(hiddenCount);

			public DiffDisplayRow(ILPatchDiffRow row) {
				Kind = row.Kind;
				LeftText = row.LeftText ?? string.Empty;
				RightText = row.RightText ?? string.Empty;
			}
		}
	}
}
