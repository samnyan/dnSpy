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
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

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

	sealed class ILPatchInlineDiffTextBlock : TextBlock {
		public static readonly DependencyProperty FragmentsProperty =
			DependencyProperty.Register(nameof(Fragments), typeof(IEnumerable<ILPatchInlineFragment>),
				typeof(ILPatchInlineDiffTextBlock),
				new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnFragmentsChanged));
		public static readonly DependencyProperty ChangedBackgroundProperty =
			DependencyProperty.Register(nameof(ChangedBackground), typeof(Brush),
				typeof(ILPatchInlineDiffTextBlock),
				new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender, OnFragmentsChanged));

		public IEnumerable<ILPatchInlineFragment>? Fragments {
			get => (IEnumerable<ILPatchInlineFragment>?)GetValue(FragmentsProperty);
			set => SetValue(FragmentsProperty, value);
		}

		public Brush ChangedBackground {
			get => (Brush)GetValue(ChangedBackgroundProperty);
			set => SetValue(ChangedBackgroundProperty, value);
		}

		static void OnFragmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((ILPatchInlineDiffTextBlock)d).Rebuild();

		void Rebuild() {
			Inlines.Clear();
			foreach (var fragment in Fragments ?? Array.Empty<ILPatchInlineFragment>()) {
				var run = new Run(fragment.Text);
				if (fragment.IsChanged) {
					run.FontWeight = FontWeights.SemiBold;
					run.Background = ChangedBackground;
				}
				Inlines.Add(run);
			}
		}
	}

	sealed class ILPatchDiffWindow : Window {
		readonly ILPatchMethodChange change;
		readonly MethodDef? target;
		readonly IDecompilerService decompilerService;
		readonly ComboBox modeSelector;
		readonly CheckBox showAllContext;
		readonly Button previousChangeButton;
		readonly Button nextChangeButton;
		readonly TextBlock statsText;
		readonly TextBlock statusText;
		readonly DataGrid diffGrid;
		IReadOnlyList<DiffDisplayRow> visibleRows = Array.Empty<DiffDisplayRow>();

		public ILPatchDiffWindow(ILPatchMethodChange change, MethodDef? target,
			IDecompilerService decompilerService) {
			this.change = change ?? throw new ArgumentNullException(nameof(change));
			this.target = target;
			this.decompilerService = decompilerService ?? throw new ArgumentNullException(nameof(decompilerService));

			Title = "IL Patch Diff — " + change.Target;
			Width = 1400;
			Height = 850;
			MinWidth = 850;
			MinHeight = 500;
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

			statsText = new TextBlock {
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 12, 0),
			};
			DockPanel.SetDock(statsText, Dock.Right);
			toolbar.Children.Add(statsText);

			var legend = new TextBlock {
				Text = "~ modified    - removed    + added",
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

			diffGrid = new DataGrid {
				AutoGenerateColumns = false,
				CanUserAddRows = false,
				CanUserDeleteRows = false,
				IsReadOnly = true,
				HeadersVisibility = DataGridHeadersVisibility.Column,
				GridLinesVisibility = DataGridGridLinesVisibility.None,
				SelectionMode = DataGridSelectionMode.Single,
				FontFamily = new FontFamily("Consolas"),
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			};
			diffGrid.Columns.Add(TextColumn("Old", nameof(DiffDisplayRow.LeftLine), 0.45));
			diffGrid.Columns.Add(InlineColumn("Original / Base", nameof(DiffDisplayRow.LeftFragments), 4,
				new SolidColorBrush(Color.FromArgb(120, 220, 70, 70))));
			diffGrid.Columns.Add(TextColumn("", nameof(DiffDisplayRow.Marker), 0.35));
			diffGrid.Columns.Add(TextColumn("New", nameof(DiffDisplayRow.RightLine), 0.45));
			diffGrid.Columns.Add(InlineColumn("Patched", nameof(DiffDisplayRow.RightFragments), 4,
				new SolidColorBrush(Color.FromArgb(120, 40, 180, 80))));
			diffGrid.LoadingRow += DiffGrid_LoadingRow;
			Grid.SetRow(diffGrid, 2);
			root.Children.Add(diffGrid);

			Content = root;
			RefreshDiff();
		}

		static DataGridTextColumn TextColumn(string header, string property, double width) =>
			new DataGridTextColumn {
				Header = header,
				Binding = new Binding(property),
				Width = new DataGridLength(width, DataGridLengthUnitType.Star),
			};

		static DataGridTemplateColumn InlineColumn(string header, string property, double width, Brush changedBackground) {
			var factory = new FrameworkElementFactory(typeof(ILPatchInlineDiffTextBlock));
			factory.SetBinding(ILPatchInlineDiffTextBlock.FragmentsProperty, new Binding(property));
			factory.SetValue(ILPatchInlineDiffTextBlock.ChangedBackgroundProperty, changedBackground);
			factory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
			factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
			return new DataGridTemplateColumn {
				Header = header,
				CellTemplate = new DataTemplate { VisualTree = factory },
				Width = new DataGridLength(width, DataGridLengthUnitType.Star),
			};
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
					diffGrid.ItemsSource = Array.Empty<DiffDisplayRow>();
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
			diffGrid.ItemsSource = visibleRows;
			if (changed != 0)
				SelectChange(forward: true, fromCurrent: false);
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
			if (fromCurrent && diffGrid.SelectedItem is DiffDisplayRow selected) {
				for (int i = 0; i < visibleRows.Count; i++) {
					if (ReferenceEquals(visibleRows[i], selected)) {
						start = i;
						break;
					}
				}
			}
			for (int offset = 1; offset <= visibleRows.Count; offset++) {
				int index = forward
					? (start + offset + visibleRows.Count) % visibleRows.Count
					: (start - offset + visibleRows.Count) % visibleRows.Count;
				var row = visibleRows[index];
				if (row.IsSeparator || row.Kind == ILPatchDiffKind.Same)
					continue;
				diffGrid.SelectedItem = row;
				diffGrid.ScrollIntoView(row);
				return;
			}
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

		void DiffGrid_LoadingRow(object sender, DataGridRowEventArgs e) {
			if (!(e.Row.Item is DiffDisplayRow row))
				return;
			if (row.IsSeparator) {
				e.Row.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
				return;
			}
			switch (row.Kind) {
			case ILPatchDiffKind.Added:
				e.Row.Background = new SolidColorBrush(Color.FromArgb(34, 40, 180, 80));
				break;
			case ILPatchDiffKind.Removed:
				e.Row.Background = new SolidColorBrush(Color.FromArgb(34, 220, 70, 70));
				break;
			case ILPatchDiffKind.Modified:
				e.Row.Background = new SolidColorBrush(Color.FromArgb(34, 220, 170, 50));
				break;
			default:
				e.Row.ClearValue(BackgroundProperty);
				break;
			}
		}

		static string ShortHash(string? hash) =>
			string.IsNullOrEmpty(hash) ? "—" : hash.Length <= 12 ? hash : hash.Substring(0, 12);

		sealed class DiffDisplayRow {
			public ILPatchDiffKind Kind { get; }
			public bool IsSeparator { get; }
			public string LeftLine { get; }
			public string LeftText { get; }
			public IReadOnlyList<ILPatchInlineFragment> LeftFragments { get; }
			public string Marker { get; }
			public string RightLine { get; }
			public string RightText { get; }
			public IReadOnlyList<ILPatchInlineFragment> RightFragments { get; }

			DiffDisplayRow(int hiddenCount) {
				Kind = ILPatchDiffKind.Same;
				IsSeparator = true;
				LeftLine = string.Empty;
				RightLine = string.Empty;
				Marker = "…";
				LeftText = $"… {hiddenCount} unchanged line(s) …";
				RightText = LeftText;
				LeftFragments = new[] { new ILPatchInlineFragment(LeftText, false) };
				RightFragments = new[] { new ILPatchInlineFragment(RightText, false) };
			}

			public static DiffDisplayRow Separator(int hiddenCount) => new DiffDisplayRow(hiddenCount);

			public DiffDisplayRow(ILPatchDiffRow row) {
				Kind = row.Kind;
				LeftLine = row.LeftLineNumber?.ToString() ?? string.Empty;
				LeftText = row.LeftText;
				RightLine = row.RightLineNumber?.ToString() ?? string.Empty;
				RightText = row.RightText;
				if (row.Kind == ILPatchDiffKind.Modified) {
					var inline = ILPatchInlineDiffEngine.Compare(row.LeftText, row.RightText);
					LeftFragments = inline.Left;
					RightFragments = inline.Right;
				}
				else {
					LeftFragments = new[] {
						new ILPatchInlineFragment(row.LeftText, row.Kind == ILPatchDiffKind.Removed),
					};
					RightFragments = new[] {
						new ILPatchInlineFragment(row.RightText, row.Kind == ILPatchDiffKind.Added),
					};
				}
				switch (row.Kind) {
				case ILPatchDiffKind.Added:
					Marker = "+";
					break;
				case ILPatchDiffKind.Removed:
					Marker = "-";
					break;
				case ILPatchDiffKind.Modified:
					Marker = "~";
					break;
				default:
					Marker = string.Empty;
					break;
				}
			}
		}
	}
}
