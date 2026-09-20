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
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using Microsoft.Win32;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Git-like history/commit surface for one managed module. The normal Patch Workspace remains
	/// the working-tree view; this window owns repository initialization, durable commits,
	/// parent-to-commit review and exporting any historical node as a DLL.
	/// </summary>
	sealed class ILPatchRepositoryWindow : Window {
		readonly IDsDocumentService documentService;
		readonly IDecompilerService decompilerService;
		readonly ComboBox moduleSelector;
		readonly TextBlock repositoryStatus;
		readonly TextBlock workingStatus;
		readonly TextBlock diskStatus;
		readonly Button initializeButton;
		readonly Button commitButton;
		readonly TextBox commitMessage;
		readonly Button exportCommitButton;
		readonly Button compareWorkingChangeButton;
		readonly Button compareCommitChangeButton;
		readonly DataGrid workingChangesGrid;
		readonly DataGrid historyGrid;
		readonly DataGrid commitChangesGrid;
		readonly ObservableCollection<RepositoryWorkingRow> workingRows = new ObservableCollection<RepositoryWorkingRow>();
		readonly ObservableCollection<RepositoryHistoryRow> historyRows = new ObservableCollection<RepositoryHistoryRow>();
		readonly ObservableCollection<RepositoryChangeRow> changeRows = new ObservableCollection<RepositoryChangeRow>();

		ILPatchRepository? repository;
		LoadedModuleChoice? selectedModule;
		ILPatchDocument? selectedDelta;
		bool workingBaseValid;
		int workingChangeCount;

		public ILPatchRepositoryWindow(IDsDocumentService documentService, IDecompilerService decompilerService) {
			this.documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
			this.decompilerService = decompilerService ?? throw new ArgumentNullException(nameof(decompilerService));

			Title = "dnSpy Change Repository";
			Width = 1320;
			Height = 820;
			MinWidth = 900;
			MinHeight = 560;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;

			var root = new Grid { Margin = new Thickness(10) };
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			var moduleBar = new DockPanel { LastChildFill = true };
			var moduleButtons = new StackPanel { Orientation = Orientation.Horizontal };
			initializeButton = new Button {
				Content = "Initialize .dnspy",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				ToolTip = "Create a hidden .dnspy repository beside the selected module and capture the on-disk DLL as immutable ROOT.",
			};
			initializeButton.Click += InitializeButton_Click;
			moduleButtons.Children.Add(initializeButton);

			var refreshButton = new Button {
				Content = "Refresh",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				ToolTip = "Refresh loaded modules, working-tree state and repository history.",
			};
			refreshButton.Click += RefreshButton_Click;
			moduleButtons.Children.Add(refreshButton);
			DockPanel.SetDock(moduleButtons, Dock.Right);
			moduleBar.Children.Add(moduleButtons);

			var moduleLabel = new TextBlock {
				Text = "Tracked module:",
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 8, 0),
			};
			DockPanel.SetDock(moduleLabel, Dock.Left);
			moduleBar.Children.Add(moduleLabel);

			moduleSelector = new ComboBox { MinWidth = 520 };
			moduleSelector.SelectionChanged += ModuleSelector_SelectionChanged;
			moduleBar.Children.Add(moduleSelector);
			Grid.SetRow(moduleBar, 0);
			root.Children.Add(moduleBar);

			var statusPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
			repositoryStatus = new TextBlock {
				TextWrapping = TextWrapping.Wrap,
				FontWeight = FontWeights.SemiBold,
			};
			statusPanel.Children.Add(repositoryStatus);
			workingStatus = new TextBlock {
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 3, 0, 0),
			};
			statusPanel.Children.Add(workingStatus);

			diskStatus = new TextBlock {
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 3, 0, 0),
			};
			statusPanel.Children.Add(diskStatus);

			var workingHeader = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
			var stageButtons = new StackPanel { Orientation = Orientation.Horizontal };
			compareWorkingChangeButton = new Button {
				Content = "Compare Working Change...",
				Padding = new Thickness(7, 2, 7, 2),
				IsEnabled = false,
				ToolTip = "Open the selected uncommitted method in the Normalized IL / Decompiled C# diff viewer.",
			};
			compareWorkingChangeButton.Click += CompareWorkingChangeButton_Click;
			stageButtons.Children.Add(compareWorkingChangeButton);
			var stageAllButton = new Button {
				Content = "Stage All",
				Padding = new Thickness(7, 2, 7, 2),
				Margin = new Thickness(6, 0, 0, 0),
			};
			stageAllButton.Click += (sender, e) => SetAllStaged(true);
			stageButtons.Children.Add(stageAllButton);
			var unstageAllButton = new Button {
				Content = "Unstage All",
				Padding = new Thickness(7, 2, 7, 2),
				Margin = new Thickness(6, 0, 0, 0),
			};
			unstageAllButton.Click += (sender, e) => SetAllStaged(false);
			stageButtons.Children.Add(unstageAllButton);
			DockPanel.SetDock(stageButtons, Dock.Right);
			workingHeader.Children.Add(stageButtons);
			workingHeader.Children.Add(new TextBlock {
				Text = "Working Changes",
				FontWeight = FontWeights.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			});
			statusPanel.Children.Add(workingHeader);

			workingChangesGrid = new DataGrid {
				AutoGenerateColumns = false,
				CanUserAddRows = false,
				CanUserDeleteRows = false,
				IsReadOnly = false,
				HeadersVisibility = DataGridHeadersVisibility.Column,
				GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
				SelectionMode = DataGridSelectionMode.Extended,
				SelectionUnit = DataGridSelectionUnit.FullRow,
				MaxHeight = 190,
				MinHeight = 90,
			};
			workingChangesGrid.Columns.Add(new DataGridCheckBoxColumn {
				Header = "Stage",
				Binding = new Binding(nameof(RepositoryWorkingRow.IsStaged)) {
					Mode = BindingMode.TwoWay,
					UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
				},
				Width = new DataGridLength(0.55, DataGridLengthUnitType.Star),
			});
			workingChangesGrid.Columns.Add(new DataGridTextColumn {
				Header = "Method",
				Binding = new Binding(nameof(RepositoryWorkingRow.Method)),
				Width = new DataGridLength(3.8, DataGridLengthUnitType.Star),
				IsReadOnly = true,
			});
			workingChangesGrid.Columns.Add(new DataGridTextColumn {
				Header = "Base",
				Binding = new Binding(nameof(RepositoryWorkingRow.BaseHash)),
				Width = new DataGridLength(1, DataGridLengthUnitType.Star),
				IsReadOnly = true,
			});
			workingChangesGrid.Columns.Add(new DataGridTextColumn {
				Header = "Current",
				Binding = new Binding(nameof(RepositoryWorkingRow.CurrentHash)),
				Width = new DataGridLength(1, DataGridLengthUnitType.Star),
				IsReadOnly = true,
			});
			workingChangesGrid.ItemsSource = workingRows;
			workingChangesGrid.CellEditEnding += WorkingChangesGrid_CellEditEnding;
			workingChangesGrid.SelectionChanged += WorkingChangesGrid_SelectionChanged;
			workingChangesGrid.MouseDoubleClick += WorkingChangesGrid_MouseDoubleClick;
			statusPanel.Children.Add(workingChangesGrid);

			var commitBar = new DockPanel {
				LastChildFill = true,
				Margin = new Thickness(0, 7, 0, 0),
			};
			commitButton = new Button {
				Content = "Commit Staged Changes",
				Padding = new Thickness(10, 3, 10, 3),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Commit only staged methods. Unstaged methods remain in Working Changes and can be committed later.",
			};
			commitButton.Click += CommitButton_Click;
			DockPanel.SetDock(commitButton, Dock.Right);
			commitBar.Children.Add(commitButton);

			commitMessage = new TextBox {
				MinWidth = 420,
				ToolTip = "Commit message",
			};
			commitMessage.TextChanged += CommitMessage_TextChanged;
			commitBar.Children.Add(commitMessage);
			statusPanel.Children.Add(commitBar);

			Grid.SetRow(statusPanel, 1);
			root.Children.Add(statusPanel);

			var main = new Grid();
			main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
			main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });

			historyGrid = CreateGrid();
			historyGrid.Columns.Add(Column("Ref", nameof(RepositoryHistoryRow.RefName), 0.55));
			historyGrid.Columns.Add(Column("Commit", nameof(RepositoryHistoryRow.ShortId), 0.9));
			historyGrid.Columns.Add(Column("Time (UTC)", nameof(RepositoryHistoryRow.Time), 1.15));
			historyGrid.Columns.Add(Column("Message", nameof(RepositoryHistoryRow.Message), 2.4));
			historyGrid.Columns.Add(Column("Changes", nameof(RepositoryHistoryRow.ChangeCount), 0.65));
			historyGrid.ItemsSource = historyRows;
			historyGrid.SelectionChanged += HistoryGrid_SelectionChanged;
			Grid.SetColumn(historyGrid, 0);
			main.Children.Add(historyGrid);

			var changePanel = new Grid { Margin = new Thickness(8, 0, 0, 0) };
			changePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			changePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			var changeTitle = new TextBlock {
				Text = "Selected commit changes",
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 0, 0, 5),
			};
			Grid.SetRow(changeTitle, 0);
			changePanel.Children.Add(changeTitle);

			commitChangesGrid = CreateGrid();
			commitChangesGrid.Columns.Add(Column("Kind", nameof(RepositoryChangeRow.Kind), 0.8));
			commitChangesGrid.Columns.Add(Column("Method", nameof(RepositoryChangeRow.Method), 3.4));
			commitChangesGrid.Columns.Add(Column("Before", nameof(RepositoryChangeRow.BeforeHash), 1));
			commitChangesGrid.Columns.Add(Column("After", nameof(RepositoryChangeRow.AfterHash), 1));
			commitChangesGrid.ItemsSource = changeRows;
			commitChangesGrid.SelectionChanged += CommitChangesGrid_SelectionChanged;
			commitChangesGrid.MouseDoubleClick += CommitChangesGrid_MouseDoubleClick;
			Grid.SetRow(commitChangesGrid, 1);
			changePanel.Children.Add(commitChangesGrid);

			Grid.SetColumn(changePanel, 1);
			main.Children.Add(changePanel);
			Grid.SetRow(main, 2);
			root.Children.Add(main);

			var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
			compareCommitChangeButton = new Button {
				Content = "Compare Selected Change...",
				Padding = new Thickness(8, 2, 8, 2),
				IsEnabled = false,
				ToolTip = "Open parent -> selected commit in the same Normalized IL / Decompiled C# diff viewer.",
			};
			compareCommitChangeButton.Click += CompareCommitChangeButton_Click;
			actions.Children.Add(compareCommitChangeButton);

			exportCommitButton = new Button {
				Content = "Export Selected Commit DLL...",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Materialize the selected commit (or ROOT) from the immutable repository base and verify its semantic state hash before writing.",
			};
			exportCommitButton.Click += ExportCommitButton_Click;
			actions.Children.Add(exportCommitButton);
			Grid.SetRow(actions, 3);
			root.Children.Add(actions);

			Content = root;
			ILPatchWorkspace.Instance.Changed += Workspace_Changed;
			documentService.CollectionChanged += DocumentService_CollectionChanged;
			Closed += RepositoryWindow_Closed;
			RefreshModules();
		}

		static DataGrid CreateGrid() => new DataGrid {
			AutoGenerateColumns = false,
			CanUserAddRows = false,
			CanUserDeleteRows = false,
			IsReadOnly = true,
			SelectionMode = DataGridSelectionMode.Single,
			SelectionUnit = DataGridSelectionUnit.FullRow,
			HeadersVisibility = DataGridHeadersVisibility.Column,
			GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
		};

		static DataGridTextColumn Column(string header, string property, double width) =>
			new DataGridTextColumn {
				Header = header,
				Binding = new Binding(property),
				Width = new DataGridLength(width, DataGridLengthUnitType.Star),
			};

		void RepositoryWindow_Closed(object? sender, EventArgs e) {
			ILPatchWorkspace.Instance.Changed -= Workspace_Changed;
			documentService.CollectionChanged -= DocumentService_CollectionChanged;
		}

		void DocumentService_CollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			var preserve = selectedModule?.Module;
			if (Dispatcher.CheckAccess())
				RefreshModules(preserve);
			else
				Dispatcher.BeginInvoke(new Action(() => RefreshModules(preserve)));
		}

		void Workspace_Changed(object? sender, EventArgs e) {
			if (Dispatcher.CheckAccess())
				RefreshWorkingState();
			else
				Dispatcher.BeginInvoke(new Action(RefreshWorkingState));
		}

		void RefreshButton_Click(object sender, RoutedEventArgs e) {
			var previousModule = selectedModule?.Module;
			RefreshModules(previousModule);
		}

		void RefreshModules(ModuleDef? preserve = null) {
			var choices = new List<LoadedModuleChoice>();
			var seen = new HashSet<ModuleDef>();
			foreach (var root in documentService.GetDocuments()) {
				foreach (var document in root.NonLoadedDescendantsAndSelf()) {
					if (document.ModuleDef is not ModuleDef module || !seen.Add(module))
						continue;
					if (string.IsNullOrWhiteSpace(document.Filename) || !File.Exists(document.Filename))
						continue;
					choices.Add(new LoadedModuleChoice(document, module));
				}
			}
			choices.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Filename, b.Filename));
			moduleSelector.ItemsSource = choices;
			moduleSelector.SelectedItem = preserve is null
				? choices.FirstOrDefault()
				: choices.FirstOrDefault(a => ReferenceEquals(a.Module, preserve)) ?? choices.FirstOrDefault();
			if (choices.Count == 0) {
				selectedModule = null;
				repository = null;
				repositoryStatus.Text = "No disk-backed managed modules are currently loaded.";
				workingStatus.Text = string.Empty;
				diskStatus.Text = string.Empty;
				RefreshHistory();
			}
		}

		void ModuleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) {
			selectedModule = moduleSelector.SelectedItem as LoadedModuleChoice;
			OpenSelectedRepository();
		}

		void OpenSelectedRepository() {
			repository = null;
			if (selectedModule is null) {
				repositoryStatus.Text = "Select a loaded managed module.";
				workingStatus.Text = string.Empty;
				diskStatus.Text = string.Empty;
				initializeButton.IsEnabled = false;
				RefreshHistory();
				return;
			}

			try {
				bool exists = ILPatchRepository.ExistsForModule(selectedModule.Filename);
				initializeButton.IsEnabled = !exists;
				if (exists) {
					repository = ILPatchRepository.OpenForModule(selectedModule.Filename);
					repositoryStatus.Text =
						$"Repository: {repository.RepositoryPath}   HEAD: {ShortHash(repository.Metadata.HeadCommitId)}";
				}
				else {
					repositoryStatus.Text =
						"No .dnspy repository exists for this module. Initialize to capture the current on-disk DLL as immutable ROOT.";
				}
			}
			catch (Exception ex) {
				initializeButton.IsEnabled = false;
				repositoryStatus.Text = "Repository error: " + ex.Message;
			}
			RefreshHistory();
			RefreshWorkingState();
			RefreshDiskStatus();
		}

		void InitializeButton_Click(object sender, RoutedEventArgs e) {
			if (selectedModule is null)
				return;
			try {
				repository = ILPatchRepository.Initialize(selectedModule.Filename);
				repositoryStatus.Text = $"Initialized repository: {repository.RepositoryPath}   HEAD: ROOT";
				initializeButton.IsEnabled = false;
				RefreshHistory();
				RefreshWorkingState();
				RefreshDiskStatus();
			}
			catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Could not initialize .dnspy repository",
					MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		void RefreshWorkingState() {
			var previousStageState = workingRows.ToDictionary(
				a => a.Change.Target.ToCanonicalString(), a => a.IsStaged, StringComparer.Ordinal);
			workingRows.Clear();
			workingBaseValid = false;
			workingChangeCount = 0;
			if (selectedModule is null) {
				workingStatus.Text = string.Empty;
				UpdateCommitButton();
				return;
			}

			var changes = ILPatchWorkspace.Instance.GetEffectiveChanges(selectedModule.Module);
			workingChangeCount = changes.Count;
			foreach (var change in changes) {
				string key = change.Target.ToCanonicalString();
				workingRows.Add(new RepositoryWorkingRow {
					Change = change,
					IsStaged = !previousStageState.TryGetValue(key, out bool wasStaged) || wasStaged,
					Method = change.Target.ToString(),
					BaseHash = ShortHash(change.BaseBody.CanonicalHash),
					CurrentHash = ShortHash(change.PatchedBody.CanonicalHash),
				});
			}
			if (repository is null) {
				workingStatus.Text = changes.Count == 0
					? "Working tree: clean (repository not initialized)."
					: $"Working tree: {changes.Count} tracked method change(s) (repository not initialized).";
				UpdateCommitButton();
				return;
			}

			bool valid = repository.TryValidateWorkingBase(selectedModule.Module, changes, out string error);
			if (!valid) {
				workingStatus.Text = "Working tree cannot be committed: " + error;
				UpdateCommitButton();
				return;
			}

			workingBaseValid = true;
			UpdateWorkingSummary();
			UpdateCommitButton();
		}

		void WorkingChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
			compareWorkingChangeButton.IsEnabled = workingChangesGrid.SelectedItem is RepositoryWorkingRow;

		void WorkingChangesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
			if (workingChangesGrid.SelectedItem is RepositoryWorkingRow)
				OpenSelectedWorkingDiff();
		}

		void CompareWorkingChangeButton_Click(object sender, RoutedEventArgs e) =>
			OpenSelectedWorkingDiff();

		void OpenSelectedWorkingDiff() {
			if (selectedModule is null ||
				workingChangesGrid.SelectedItem is not RepositoryWorkingRow row)
				return;
			MethodDef? target = FindMethod(selectedModule.Module, row.Change.Target);
			var window = new ILPatchDiffWindow(row.Change, target, decompilerService) { Owner = this };
			window.Show();
		}

		void CommitMessage_TextChanged(object sender, TextChangedEventArgs e) => UpdateCommitButton();

		void WorkingChangesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
			Dispatcher.BeginInvoke(new Action(() => {
				UpdateWorkingSummary();
				UpdateCommitButton();
			}));

		void SetAllStaged(bool staged) {
			foreach (var row in workingRows)
				row.IsStaged = staged;
			workingChangesGrid.Items.Refresh();
			UpdateWorkingSummary();
			UpdateCommitButton();
		}

		void UpdateWorkingSummary() {
			if (!workingBaseValid || repository is null)
				return;
			int staged = workingRows.Count(a => a.IsStaged);
			workingStatus.Text = workingChangeCount == 0
				? "Working tree: clean."
				: $"Working tree: {workingChangeCount} uncommitted method change(s), {staged} staged.";
		}

		void UpdateCommitButton() {
			int staged = workingRows.Count(a => a.IsStaged);
			commitButton.Content = $"Commit Staged Changes ({staged})";
			commitButton.IsEnabled = repository is not null &&
				workingBaseValid &&
				staged != 0 &&
				!string.IsNullOrWhiteSpace(commitMessage.Text);
		}

		void CommitButton_Click(object sender, RoutedEventArgs e) {
			if (repository is null || selectedModule is null)
				return;
			string message = commitMessage.Text.Trim();
			if (string.IsNullOrEmpty(message))
				return;

			try {
				workingChangesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
				workingChangesGrid.CommitEdit(DataGridEditingUnit.Row, true);
				var changes = ILPatchWorkspace.Instance.GetEffectiveChanges(selectedModule.Module);
				if (changes.Count == 0)
					return;
				var stagedKeys = new HashSet<string>(
					workingRows.Where(a => a.IsStaged)
						.Select(a => a.Change.Target.ToCanonicalString()), StringComparer.Ordinal);
				var staged = changes
					.Where(a => stagedKeys.Contains(a.Target.ToCanonicalString()))
					.ToArray();
				if (staged.Length == 0)
					return;
				var commit = repository.CommitSelected(selectedModule.Module, changes, staged, message);
				ILPatchWorkspace.Instance.AcceptChangesAsBaseline(
					selectedModule.Module, staged.Select(a => a.Target));
				commitMessage.Clear();
				repositoryStatus.Text =
					$"Repository: {repository.RepositoryPath}   HEAD: {ShortHash(commit.Id)}";
				RefreshHistory(commit.Id);
				RefreshWorkingState();
				RefreshDiskStatus();
			}
			catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Could not commit working changes",
					MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		void RefreshDiskStatus() {
			if (selectedModule is null) {
				diskStatus.Text = string.Empty;
				return;
			}
			if (repository is null) {
				diskStatus.Text = "Disk working DLL: repository not initialized.";
				return;
			}

			try {
				using var diskModule = ModuleDefMD.Load(selectedModule.Filename);
				string diskState = ILPatchModuleStateHasher.Compute(diskModule);
				string expectedHead = repository.Metadata.HeadCommitId is null
					? repository.Metadata.RootStateHash
					: repository.LoadCommit(repository.Metadata.HeadCommitId).StateHash;
				if (StringComparer.Ordinal.Equals(diskState, expectedHead)) {
					diskStatus.Text = "Disk working DLL: matches repository HEAD.";
				}
				else if (StringComparer.Ordinal.Equals(diskState, repository.Metadata.RootStateHash)) {
					diskStatus.Text =
						"Disk working DLL: still at ROOT while repository HEAD is newer. " +
						"Before closing dnSpy, use the normal Save Module command if you want the on-disk working DLL to reopen at HEAD.";
				}
				else {
					diskStatus.Text =
						"Disk working DLL: does not match repository HEAD. This can be an unsaved/older/different state; " +
						"Refresh after saving or export a known commit explicitly.";
				}
			}
			catch (Exception ex) {
				diskStatus.Text = "Could not verify disk working DLL: " + ex.Message;
			}
		}

		void RefreshHistory(string? selectCommitId = null) {
			historyRows.Clear();
			changeRows.Clear();
			selectedDelta = null;
			compareCommitChangeButton.IsEnabled = false;
			exportCommitButton.IsEnabled = false;
			if (repository is null)
				return;

			try {
				foreach (var commit in repository.GetHistory()) {
					historyRows.Add(new RepositoryHistoryRow {
						CommitId = commit.Id,
						RefName = StringComparer.Ordinal.Equals(commit.Id, repository.Metadata.HeadCommitId) ? "HEAD" : string.Empty,
						ShortId = ShortHash(commit.Id),
						Time = commit.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
						Message = commit.Message,
						ChangeCount = commit.Changes.Count.ToString(),
						IsHead = StringComparer.Ordinal.Equals(commit.Id, repository.Metadata.HeadCommitId),
					});
				}
				historyRows.Add(new RepositoryHistoryRow {
					CommitId = null,
					RefName = repository.Metadata.HeadCommitId is null ? "HEAD" : string.Empty,
					ShortId = "ROOT",
					Time = repository.Metadata.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
					Message = "Immutable repository root",
					ChangeCount = "0",
				});

				RepositoryHistoryRow? row = selectCommitId is null
					? historyRows.FirstOrDefault()
					: historyRows.FirstOrDefault(a => StringComparer.Ordinal.Equals(a.CommitId, selectCommitId));
				historyGrid.SelectedItem = row ?? historyRows.LastOrDefault();
			}
			catch (Exception ex) {
				repositoryStatus.Text = "Repository history error: " + ex.Message;
			}
		}

		void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
			changeRows.Clear();
			selectedDelta = null;
			compareCommitChangeButton.IsEnabled = false;
			var row = historyGrid.SelectedItem as RepositoryHistoryRow;
			exportCommitButton.IsEnabled = repository is not null && row is not null;
			if (repository is null || row is null || string.IsNullOrEmpty(row.CommitId))
				return;

			try {
				var commit = repository.LoadCommit(row.CommitId);
				selectedDelta = repository.CreateCommitDelta(row.CommitId);
				var summaries = commit.Changes.ToDictionary(
					a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
				foreach (var change in selectedDelta.Methods) {
					summaries.TryGetValue(change.Target.ToCanonicalString(), out var summary);
					changeRows.Add(new RepositoryChangeRow {
						Change = change,
						Kind = summary?.Kind.ToString() ?? "Modified",
						Method = change.Target.ToString(),
						BeforeHash = ShortHash(change.BaseBody.CanonicalHash),
						AfterHash = ShortHash(change.PatchedBody.CanonicalHash),
					});
				}
			}
			catch (Exception ex) {
				repositoryStatus.Text = "Could not review selected commit: " + ex.Message;
			}
		}

		void CommitChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
			compareCommitChangeButton.IsEnabled = commitChangesGrid.SelectedItem is RepositoryChangeRow;

		void CommitChangesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
			if (commitChangesGrid.SelectedItem is RepositoryChangeRow)
				OpenSelectedCommitDiff();
		}

		void CompareCommitChangeButton_Click(object sender, RoutedEventArgs e) =>
			OpenSelectedCommitDiff();

		void OpenSelectedCommitDiff() {
			if (commitChangesGrid.SelectedItem is not RepositoryChangeRow row)
				return;
			MethodDef? target = selectedModule is null ? null : FindMethod(selectedModule.Module, row.Change.Target);
			var window = new ILPatchDiffWindow(row.Change, target, decompilerService) { Owner = this };
			window.Show();
		}

		void ExportCommitButton_Click(object sender, RoutedEventArgs e) {
			if (repository is null || selectedModule is null ||
				historyGrid.SelectedItem is not RepositoryHistoryRow row)
				return;

			string suffix = string.IsNullOrEmpty(row.CommitId) ? "ROOT" : row.ShortId;
			var dialog = new SaveFileDialog {
				Title = "Export Repository Commit DLL",
				Filter = ".NET assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
				OverwritePrompt = true,
				FileName = Path.GetFileNameWithoutExtension(selectedModule.Filename) + "." + suffix +
					Path.GetExtension(selectedModule.Filename),
			};
			if (dialog.ShowDialog(this) != true)
				return;

			try {
				if (StringComparer.OrdinalIgnoreCase.Equals(
					Path.GetFullPath(dialog.FileName), Path.GetFullPath(selectedModule.Filename))) {
					throw new InvalidOperationException(
						"Repository export will not overwrite the currently tracked working DLL. " +
						"Export to a different filename; an explicit checkout workflow can be added separately.");
				}
				repository.Export(row.CommitId, dialog.FileName);
				MessageBox.Show(this,
					$"Exported {(string.IsNullOrEmpty(row.CommitId) ? "ROOT" : row.ShortId)} to:\n{dialog.FileName}",
					"Repository export complete", MessageBoxButton.OK, MessageBoxImage.Information);
			}
			catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Could not export repository commit",
					MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		static MethodDef? FindMethod(ModuleDef module, ILPatchMethodIdentity identity) {
			string key = identity.ToCanonicalString();
			MethodDef? match = null;
			foreach (var method in module.GetTypes().SelectMany(a => a.Methods)) {
				if (!StringComparer.Ordinal.Equals(
					ILPatchMethodIdentity.Create(method).ToCanonicalString(), key))
					continue;
				if (match is not null)
					return null;
				match = method;
			}
			return match;
		}

		static string ShortHash(string? value) =>
			string.IsNullOrEmpty(value) ? "ROOT" : value.Length <= 12 ? value : value.Substring(0, 12);

		sealed class LoadedModuleChoice {
			public IDsDocument Document { get; }
			public ModuleDef Module { get; }
			public string Filename => Document.Filename;

			public LoadedModuleChoice(IDsDocument document, ModuleDef module) {
				Document = document;
				Module = module;
			}

			public override string ToString() =>
				$"{Path.GetFileName(Filename)}  —  {Filename}";
		}

		sealed class RepositoryWorkingRow {
			public ILPatchMethodChange Change { get; set; } = null!;
			public bool IsStaged { get; set; }
			public string Method { get; set; } = string.Empty;
			public string BaseHash { get; set; } = string.Empty;
			public string CurrentHash { get; set; } = string.Empty;
		}

		sealed class RepositoryHistoryRow {
			public string? CommitId { get; set; }
			public string RefName { get; set; } = string.Empty;
			public string ShortId { get; set; } = string.Empty;
			public string Time { get; set; } = string.Empty;
			public string Message { get; set; } = string.Empty;
			public string ChangeCount { get; set; } = string.Empty;
			public bool IsHead { get; set; }
		}

		sealed class RepositoryChangeRow {
			public ILPatchMethodChange Change { get; set; } = null!;
			public string Kind { get; set; } = string.Empty;
			public string Method { get; set; } = string.Empty;
			public string BeforeHash { get; set; } = string.Empty;
			public string AfterHash { get; set; } = string.Empty;
		}
	}
}
