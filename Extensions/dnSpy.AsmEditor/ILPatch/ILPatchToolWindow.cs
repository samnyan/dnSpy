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
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dnSpy.AsmEditor.ILPatch {
	[ExportAutoLoaded]
	sealed class ILPatchToolWindowLoader : IAutoLoaded {
		public static readonly RoutedCommand OpenToolWindow = new RoutedCommand("OpenILPatchWorkspace", typeof(ILPatchToolWindowLoader));

		[ImportingConstructor]
		ILPatchToolWindowLoader(IWpfCommandService wpfCommandService, IDsToolWindowService toolWindowService) {
			var commands = wpfCommandService.GetCommands(ControlConstants.GUID_MAINWINDOW);
			commands.Add(OpenToolWindow, new RelayCommand(a => toolWindowService.Show(ILPatchToolWindowContent.THE_GUID)));
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "IL Patch Workspace", Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 1900)]
	sealed class ILPatchViewMenuCommand : MenuItemCommand {
		ILPatchViewMenuCommand()
			: base(ILPatchToolWindowLoader.OpenToolWindow) {
		}
	}

	[Export(typeof(IToolWindowContentProvider))]
	sealed class ILPatchToolWindowContentProvider : IToolWindowContentProvider {
		ILPatchToolWindowContent? content;

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(ILPatchToolWindowContent.THE_GUID, ILPatchToolWindowContent.DEFAULT_LOCATION, 0, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) {
			if (guid != ILPatchToolWindowContent.THE_GUID)
				return null;
			return content ??= new ILPatchToolWindowContent();
		}
	}

	sealed class ILPatchToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("FA3E9EEC-F3A2-47A8-B9B7-4F005C8F2201");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		readonly ILPatchWorkspaceControl control = new ILPatchWorkspaceControl();

		public override Guid Guid => THE_GUID;
		public override string Title => "IL Patch Workspace";
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.ChangesGrid;
		public override FrameworkElement? ZoomElement => control;
	}

	sealed class ILPatchWorkspaceControl : UserControl {
		readonly ObservableCollection<ChangeRow> changes = new ObservableCollection<ChangeRow>();
		readonly ObservableCollection<HistoryRow> history = new ObservableCollection<HistoryRow>();
		readonly TextBlock summaryText;
		readonly TextBox diffText;

		public DataGrid ChangesGrid { get; }

		public ILPatchWorkspaceControl() {
			var root = new Grid { Margin = new Thickness(8) };
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			summaryText = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
			Grid.SetRow(summaryText, 0);
			root.Children.Add(summaryText);

			var reviewGrid = new Grid();
			reviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			reviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });

			ChangesGrid = CreateGrid();
			ChangesGrid.Columns.Add(CreateTextColumn("Method", nameof(ChangeRow.Method), 3));
			ChangesGrid.Columns.Add(CreateTextColumn("Base", nameof(ChangeRow.BaseHash), 1));
			ChangesGrid.Columns.Add(CreateTextColumn("Current", nameof(ChangeRow.CurrentHash), 1));
			ChangesGrid.Columns.Add(CreateTextColumn("IL", nameof(ChangeRow.InstructionCount), 0.7));
			ChangesGrid.ItemsSource = changes;
			ChangesGrid.SelectionChanged += ChangesGrid_SelectionChanged;
			Grid.SetColumn(ChangesGrid, 0);
			reviewGrid.Children.Add(ChangesGrid);

			diffText = new TextBox {
				IsReadOnly = true,
				AcceptsReturn = true,
				AcceptsTab = true,
				TextWrapping = TextWrapping.NoWrap,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				FontFamily = new FontFamily("Consolas"),
				Margin = new Thickness(8, 0, 0, 0),
			};
			Grid.SetColumn(diffText, 1);
			reviewGrid.Children.Add(diffText);

			Grid.SetRow(reviewGrid, 1);
			root.Children.Add(reviewGrid);

			var historyTitle = new TextBlock {
				Text = "Edit history",
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 8, 0, 4),
			};
			Grid.SetRow(historyTitle, 2);
			root.Children.Add(historyTitle);

			var historyGrid = CreateGrid();
			historyGrid.Columns.Add(CreateTextColumn("Time (UTC)", nameof(HistoryRow.Time), 1));
			historyGrid.Columns.Add(CreateTextColumn("Action", nameof(HistoryRow.Action), 0.7));
			historyGrid.Columns.Add(CreateTextColumn("Method", nameof(HistoryRow.Method), 3));
			historyGrid.Columns.Add(CreateTextColumn("Before", nameof(HistoryRow.BeforeHash), 1));
			historyGrid.Columns.Add(CreateTextColumn("After", nameof(HistoryRow.AfterHash), 1));
			historyGrid.ItemsSource = history;
			Grid.SetRow(historyGrid, 3);
			root.Children.Add(historyGrid);

			Content = root;
			ILPatchWorkspace.Instance.Changed += Workspace_Changed;
			Refresh();
		}

		static DataGrid CreateGrid() => new DataGrid {
			AutoGenerateColumns = false,
			CanUserAddRows = false,
			CanUserDeleteRows = false,
			IsReadOnly = true,
			SelectionMode = DataGridSelectionMode.Single,
			HeadersVisibility = DataGridHeadersVisibility.Column,
			GridLinesVisibility = DataGridGridLinesVisibility.None,
		};

		static DataGridTextColumn CreateTextColumn(string header, string property, double width) => new DataGridTextColumn {
			Header = header,
			Binding = new Binding(property),
			Width = new DataGridLength(width, DataGridLengthUnitType.Star),
		};

		void Workspace_Changed(object? sender, EventArgs e) {
			if (!Dispatcher.CheckAccess()) {
				Dispatcher.BeginInvoke(new Action(Refresh));
				return;
			}
			Refresh();
		}

		void Refresh() {
			string? selectedTarget = (ChangesGrid.SelectedItem as ChangeRow)?.Change.Target.ToCanonicalString();
			var effectiveChanges = ILPatchWorkspace.Instance.GetEffectiveChanges();
			changes.Clear();
			foreach (var change in effectiveChanges) {
				changes.Add(new ChangeRow {
					Change = change,
					Method = change.Target.ToString(),
					BaseHash = ShortHash(change.BaseBody.CanonicalHash),
					CurrentHash = ShortHash(change.PatchedBody.CanonicalHash),
					InstructionCount = $"{change.BaseBody.Instructions.Count} -> {change.PatchedBody.Instructions.Count}",
				});
			}

			history.Clear();
			foreach (var edit in ILPatchWorkspace.Instance.History.Reverse()) {
				history.Add(new HistoryRow {
					Time = edit.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss"),
					Action = edit.Description,
					Method = edit.Method.ToString(),
					BeforeHash = ShortHash(edit.BeforeHash),
					AfterHash = ShortHash(edit.AfterHash),
				});
			}

			summaryText.Text = effectiveChanges.Count == 0
				? "No effective CIL method changes are currently tracked."
				: $"{effectiveChanges.Count} method change(s) currently differ from their original baseline.";

			ChangeRow? selectedRow = null;
			if (selectedTarget is not null)
				selectedRow = changes.FirstOrDefault(a => a.Change.Target.ToCanonicalString() == selectedTarget);
			selectedRow ??= changes.FirstOrDefault();
			ChangesGrid.SelectedItem = selectedRow;
			UpdateDiff(selectedRow);
		}

		void ChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDiff(ChangesGrid.SelectedItem as ChangeRow);

		void UpdateDiff(ChangeRow? row) {
			if (row is null) {
				diffText.Text = "Select a changed method to review its normalized IL diff.";
				return;
			}
			diffText.Text = BuildInstructionDiff(row.Change);
		}

		static string BuildInstructionDiff(ILPatchMethodChange change) {
			var before = change.BaseBody.Instructions.Select(a => a.ToCanonicalString()).ToArray();
			var after = change.PatchedBody.Instructions.Select(a => a.ToCanonicalString()).ToArray();
			var builder = new StringBuilder();
			builder.AppendLine(change.Target.ToString());
			builder.AppendLine($"--- base {ShortHash(change.BaseBody.CanonicalHash)}");
			builder.AppendLine($"+++ current {ShortHash(change.PatchedBody.CanonicalHash)}");
			builder.AppendLine();

			// A dynamic-programming LCS is simple and produces a readable review for normal-sized
			// methods. Avoid quadratic memory for generated/abnormally large methods.
			if ((long)before.Length * after.Length > 1_000_000) {
				builder.AppendLine("Method is too large for inline LCS diff; showing complete normalized bodies.");
				builder.AppendLine();
				AppendFullBody(builder, '-', before);
				AppendFullBody(builder, '+', after);
				return builder.ToString();
			}

			var lcs = new int[before.Length + 1, after.Length + 1];
			for (int i = before.Length - 1; i >= 0; i--) {
				for (int j = after.Length - 1; j >= 0; j--)
					lcs[i, j] = StringComparer.Ordinal.Equals(before[i], after[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
			}

			int oldIndex = 0;
			int newIndex = 0;
			while (oldIndex < before.Length || newIndex < after.Length) {
				if (oldIndex < before.Length && newIndex < after.Length && StringComparer.Ordinal.Equals(before[oldIndex], after[newIndex])) {
					AppendDiffLine(builder, ' ', oldIndex, newIndex, before[oldIndex]);
					oldIndex++;
					newIndex++;
				}
				else if (newIndex < after.Length && (oldIndex == before.Length || lcs[oldIndex, newIndex + 1] >= lcs[oldIndex + 1, newIndex])) {
					AppendDiffLine(builder, '+', -1, newIndex, after[newIndex]);
					newIndex++;
				}
				else {
					AppendDiffLine(builder, '-', oldIndex, -1, before[oldIndex]);
					oldIndex++;
				}
			}
			return builder.ToString();
		}

		static void AppendFullBody(StringBuilder builder, char prefix, string[] lines) {
			for (int i = 0; i < lines.Length; i++)
				builder.Append(prefix).Append(' ').Append(i.ToString("D4")).Append(" | ").AppendLine(lines[i]);
		}

		static void AppendDiffLine(StringBuilder builder, char prefix, int oldIndex, int newIndex, string text) {
			builder.Append(prefix).Append(' ')
				.Append(oldIndex < 0 ? "    " : oldIndex.ToString("D4"))
				.Append(" -> ")
				.Append(newIndex < 0 ? "    " : newIndex.ToString("D4"))
				.Append(" | ")
				.AppendLine(text);
		}

		static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash.Substring(0, 12);

		sealed class ChangeRow {
			public ILPatchMethodChange Change { get; set; } = null!;
			public string Method { get; set; } = string.Empty;
			public string BaseHash { get; set; } = string.Empty;
			public string CurrentHash { get; set; } = string.Empty;
			public string InstructionCount { get; set; } = string.Empty;
		}

		sealed class HistoryRow {
			public string Time { get; set; } = string.Empty;
			public string Action { get; set; } = string.Empty;
			public string Method { get; set; } = string.Empty;
			public string BeforeHash { get; set; } = string.Empty;
			public string AfterHash { get; set; } = string.Empty;
		}
	}
}
