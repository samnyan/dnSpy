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
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using dnlib.DotNet;
using dnSpy.AsmEditor.Commands;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;
using Microsoft.Win32;

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
		readonly IDsDocumentService documentService;
		readonly IUndoCommandService undoCommandService;
		readonly IMethodAnnotations methodAnnotations;
		readonly IAppService appService;
		readonly IDecompilerService decompilerService;
		ILPatchToolWindowContent? content;

		[ImportingConstructor]
		ILPatchToolWindowContentProvider(IDsDocumentService documentService, IUndoCommandService undoCommandService,
			IMethodAnnotations methodAnnotations, IAppService appService, IDecompilerService decompilerService) {
			this.documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
			this.undoCommandService = undoCommandService ?? throw new ArgumentNullException(nameof(undoCommandService));
			this.methodAnnotations = methodAnnotations ?? throw new ArgumentNullException(nameof(methodAnnotations));
			this.appService = appService ?? throw new ArgumentNullException(nameof(appService));
			this.decompilerService = decompilerService ?? throw new ArgumentNullException(nameof(decompilerService));
		}

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(ILPatchToolWindowContent.THE_GUID, ILPatchToolWindowContent.DEFAULT_LOCATION, 0, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) {
			if (guid != ILPatchToolWindowContent.THE_GUID)
				return null;
			return content ??= new ILPatchToolWindowContent(documentService, undoCommandService, methodAnnotations, appService, decompilerService);
		}
	}

	sealed class ILPatchToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("FA3E9EEC-F3A2-47A8-B9B7-4F005C8F2201");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		readonly ILPatchWorkspaceControl control;

		public ILPatchToolWindowContent(IDsDocumentService documentService, IUndoCommandService undoCommandService,
			IMethodAnnotations methodAnnotations, IAppService appService, IDecompilerService decompilerService) =>
			control = new ILPatchWorkspaceControl(documentService, undoCommandService, methodAnnotations, appService, decompilerService);

		public override Guid Guid => THE_GUID;
		public override string Title => "IL Patch Workspace";
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.ChangesGrid;
		public override FrameworkElement? ZoomElement => control;
	}

	sealed class ILPatchWorkspaceControl : UserControl {
		readonly IDsDocumentService documentService;
		readonly IUndoCommandService undoCommandService;
		readonly IMethodAnnotations methodAnnotations;
		readonly IAppService appService;
		readonly IDecompilerService decompilerService;
		readonly ObservableCollection<ChangeRow> changes = new ObservableCollection<ChangeRow>();
		readonly ObservableCollection<ImportRow> imports = new ObservableCollection<ImportRow>();
		readonly ObservableCollection<HistoryRow> history = new ObservableCollection<HistoryRow>();
		readonly Dictionary<string, ILPatchMethodIdentity> targetOverrides =
			new Dictionary<string, ILPatchMethodIdentity>(StringComparer.Ordinal);
		readonly Dictionary<ILPatchMethodChange, string> importedPatchSources =
			new Dictionary<ILPatchMethodChange, string>();
		readonly TextBlock summaryText;
		readonly TextBlock importSummaryText;
		readonly TextBlock importHintText;
		readonly TextBox diffText;
		readonly DataGrid importGrid;
		readonly ComboBox candidateSelector;
		readonly Button useCandidateButton;
		readonly Button clearCandidateButton;
		readonly Button revertButton;
		readonly Button compareChangeButton;
		readonly Button compareImportButton;
		readonly Button importButton;
		readonly Button refreshImportButton;
		readonly Button clearImportButton;
		readonly Button applyButton;
		readonly Button rebaseButton;
		readonly Button applySafeButton;
		readonly Button exportRebasedButton;
		readonly Button exportButton;
		ILPatchImportPreview? importedPreview;
		string? importedSourceLabel;
		string? importedDefaultBaseName;

		public DataGrid ChangesGrid { get; }

		public ILPatchWorkspaceControl(IDsDocumentService documentService, IUndoCommandService undoCommandService,
			IMethodAnnotations methodAnnotations, IAppService appService, IDecompilerService decompilerService) {
			this.documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
			this.undoCommandService = undoCommandService ?? throw new ArgumentNullException(nameof(undoCommandService));
			this.methodAnnotations = methodAnnotations ?? throw new ArgumentNullException(nameof(methodAnnotations));
			this.appService = appService ?? throw new ArgumentNullException(nameof(appService));
			this.decompilerService = decompilerService ?? throw new ArgumentNullException(nameof(decompilerService));

			var root = new Grid { Margin = new Thickness(8) };
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			var workspaceHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
			var header = new DockPanel { LastChildFill = true };
			var workspaceButtons = new StackPanel { Orientation = Orientation.Horizontal };
			revertButton = new Button {
				Content = "Revert Selected",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Restore the selected tracked method to the baseline captured before the first edit. This is undoable.",
			};
			revertButton.Click += RevertButton_Click;
			workspaceButtons.Children.Add(revertButton);

			compareChangeButton = new Button {
				Content = "Compare Selected...",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Open a Git-style side-by-side diff with Normalized IL and Decompiled C# modes.",
			};
			compareChangeButton.Click += CompareChangeButton_Click;
			workspaceButtons.Children.Add(compareChangeButton);

			var recoverButton = new Button {
				Content = "Recover from DLL Pair...",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				ToolTip = "Create a .ilpatch by comparing an existing original DLL with a separately saved dnSpy-modified DLL.",
			};
			recoverButton.Click += RecoverPatchFromDllPairButton_Click;
			workspaceButtons.Children.Add(recoverButton);

			exportButton = new Button {
				Content = "Export .ilpatch...",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				ToolTip = "Export all effective tracked CIL method changes as a reusable .ilpatch file.",
			};
			exportButton.Click += ExportButton_Click;
			workspaceButtons.Children.Add(exportButton);
			DockPanel.SetDock(workspaceButtons, Dock.Right);
			header.Children.Add(workspaceButtons);

			summaryText = new TextBlock {
				VerticalAlignment = VerticalAlignment.Center,
				FontWeight = FontWeights.SemiBold,
			};
			header.Children.Add(summaryText);
			workspaceHeader.Children.Add(header);
			workspaceHeader.Children.Add(new TextBlock {
				Text = "Create workflow: edit CIL in dnSpy -> review tracked changes below -> Export .ilpatch. " +
					"Existing modified DLLs can be migrated with Recover from DLL Pair. " +
					"Replay workflow: open the target/newer DLL -> Import .ilpatch -> resolve target rows if needed -> Apply Safe -> save the module normally in dnSpy.",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 4, 0, 0),
			});
			Grid.SetRow(workspaceHeader, 0);
			root.Children.Add(workspaceHeader);

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
			ChangesGrid.MouseDoubleClick += ChangesGrid_MouseDoubleClick;
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

			var replayHeader = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
			importSummaryText = new TextBlock {
				Text = "Imported patch replay",
				FontWeight = FontWeights.SemiBold,
			};
			replayHeader.Children.Add(importSummaryText);

			importHintText = new TextBlock {
				Text = "No .ilpatch imported. Load the target assembly in dnSpy first, then import the patch you want to replay.",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 4, 0, 0),
			};
			replayHeader.Children.Add(importHintText);

			var replayButtons = new WrapPanel {
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0, 6, 0, 0),
			};

			importButton = new Button {
				Content = "Import .ilpatch...",
				Padding = new Thickness(8, 2, 8, 2),
				ToolTip = "Import one or more independent .ilpatch files and preview how they match the currently loaded target assembly.",
			};
			importButton.Click += ImportButton_Click;
			replayButtons.Children.Add(importButton);

			compareImportButton = new Button {
				Content = "Compare Patch...",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Compare the selected patch entry's stored Base and Patched bodies side-by-side.",
			};
			compareImportButton.Click += CompareImportButton_Click;
			replayButtons.Children.Add(compareImportButton);

			clearImportButton = new Button {
				Content = "Clear Import",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Clear only the current import preview and target overrides. Already applied edits are not reverted.",
			};
			clearImportButton.Click += ClearImportButton_Click;
			replayButtons.Children.Add(clearImportButton);

			refreshImportButton = new Button {
				Content = "Refresh Preview",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Re-run matching and three-way analysis against the assemblies currently loaded in dnSpy. Manual target overrides are preserved.",
			};
			refreshImportButton.Click += RefreshImportButton_Click;
			replayButtons.Children.Add(refreshImportButton);

			applySafeButton = new Button {
				Content = "Apply Safe",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(16, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Recommended replay action: preflight all Exact and Clean-Rebase entries, then apply them as one undoable dnSpy command.",
			};
			applySafeButton.Click += ApplySafeButton_Click;
			replayButtons.Children.Add(applySafeButton);

			applyButton = new Button {
				Content = "Apply Exact",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Apply only entries whose current normalized body still exactly matches the stored baseline.",
			};
			applyButton.Click += ApplyButton_Click;
			replayButtons.Children.Add(applyButton);

			rebaseButton = new Button {
				Content = "Apply Clean Rebase",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Apply only BaseChanged entries whose three-way IL rebase is proven Clean.",
			};
			rebaseButton.Click += RebaseButton_Click;
			replayButtons.Children.Add(rebaseButton);

			exportRebasedButton = new Button {
				Content = "Export Rebased .ilpatch...",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(16, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Write a new patch definition based on the currently resolved target/new-version methods. The source patch file is not overwritten.",
			};
			exportRebasedButton.Click += ExportRebasedButton_Click;
			replayButtons.Children.Add(exportRebasedButton);

			replayHeader.Children.Add(replayButtons);
			Grid.SetRow(replayHeader, 2);
			root.Children.Add(replayHeader);

			importGrid = CreateGrid();
			importGrid.Columns.Add(CreateTextColumn("Status", nameof(ImportRow.Status), 0.8));
			importGrid.Columns.Add(CreateTextColumn("Source", nameof(ImportRow.Source), 1.1));
			importGrid.Columns.Add(CreateTextColumn("Method", nameof(ImportRow.Method), 2.5));
			importGrid.Columns.Add(CreateTextColumn("Current", nameof(ImportRow.CurrentHash), 1));
			importGrid.Columns.Add(CreateTextColumn("Baseline", nameof(ImportRow.BaseHash), 1));
			importGrid.Columns.Add(CreateTextColumn("Top candidate", nameof(ImportRow.Candidate), 2));
			importGrid.Columns.Add(CreateTextColumn("Score", nameof(ImportRow.Score), 0.7));
			importGrid.Columns.Add(CreateTextColumn("Rebase", nameof(ImportRow.Rebase), 0.8));
			importGrid.Columns.Add(CreateTextColumn("Details", nameof(ImportRow.Details), 2.5));
			importGrid.ItemsSource = imports;
			importGrid.SelectionChanged += ImportGrid_SelectionChanged;
			importGrid.MouseDoubleClick += ImportGrid_MouseDoubleClick;
			Grid.SetRow(importGrid, 3);
			root.Children.Add(importGrid);

			var candidateBar = new DockPanel {
				LastChildFill = true,
				Margin = new Thickness(0, 6, 0, 0),
			};
			var candidateButtons = new StackPanel { Orientation = Orientation.Horizontal };
			useCandidateButton = new Button {
				Content = "Use Candidate",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Manually retarget only this patch entry to the selected structurally similar method. This is never done automatically.",
			};
			useCandidateButton.Click += UseCandidateButton_Click;
			candidateButtons.Children.Add(useCandidateButton);

			clearCandidateButton = new Button {
				Content = "Clear Target Override",
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(8, 0, 0, 0),
				IsEnabled = false,
				ToolTip = "Return this patch entry to exact-identity matching and remove its manual target override.",
			};
			clearCandidateButton.Click += ClearCandidateButton_Click;
			candidateButtons.Children.Add(clearCandidateButton);
			DockPanel.SetDock(candidateButtons, Dock.Right);
			candidateBar.Children.Add(candidateButtons);

			var candidateLabel = new TextBlock {
				Text = "Candidate target (manual override):",
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 8, 0),
			};
			DockPanel.SetDock(candidateLabel, Dock.Left);
			candidateBar.Children.Add(candidateLabel);

			candidateSelector = new ComboBox {
				MinWidth = 420,
				IsEnabled = false,
			};
			candidateSelector.SelectionChanged += CandidateSelector_SelectionChanged;
			candidateBar.Children.Add(candidateSelector);
			Grid.SetRow(candidateBar, 4);
			root.Children.Add(candidateBar);

			var historyTitle = new TextBlock {
				Text = "Edit history",
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 8, 0, 4),
			};
			Grid.SetRow(historyTitle, 5);
			root.Children.Add(historyTitle);

			var historyGrid = CreateGrid();
			historyGrid.Columns.Add(CreateTextColumn("Time (UTC)", nameof(HistoryRow.Time), 1));
			historyGrid.Columns.Add(CreateTextColumn("Action", nameof(HistoryRow.Action), 0.7));
			historyGrid.Columns.Add(CreateTextColumn("Method", nameof(HistoryRow.Method), 3));
			historyGrid.Columns.Add(CreateTextColumn("Before", nameof(HistoryRow.BeforeHash), 1));
			historyGrid.Columns.Add(CreateTextColumn("After", nameof(HistoryRow.AfterHash), 1));
			historyGrid.ItemsSource = history;
			Grid.SetRow(historyGrid, 6);
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
				Dispatcher.BeginInvoke(new Action(RefreshWorkspaceAndImport));
				return;
			}
			RefreshWorkspaceAndImport();
		}

		void RefreshWorkspaceAndImport() {
			Refresh();
			RefreshImportedPreview();
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
			exportButton.IsEnabled = effectiveChanges.Count != 0;

			ChangeRow? selectedRow = null;
			if (selectedTarget is not null)
				selectedRow = changes.FirstOrDefault(a => a.Change.Target.ToCanonicalString() == selectedTarget);
			selectedRow ??= changes.FirstOrDefault();
			ChangesGrid.SelectedItem = selectedRow;
			revertButton.IsEnabled = selectedRow is not null;
			UpdateDiff(selectedRow);
		}

		void RevertButton_Click(object sender, RoutedEventArgs e) {
			var row = ChangesGrid.SelectedItem as ChangeRow;
			if (row is null)
				return;

			try {
				if (!ILPatchWorkspace.Instance.TryGetTrackedBaseline(row.Change.Target,
					out var method, out var baselineOptions) || method is null || baselineOptions is null) {
					MsgBox.Instance.Show($"Could not uniquely resolve the tracked baseline for '{row.Change.Target}'.");
					return;
				}

				var methodNode = appService.DocumentTreeView.FindNode(method) as MethodNode;
				if (methodNode is null) {
					MsgBox.Instance.Show($"Could not find the dnSpy document tree node for '{row.Change.Target}'.");
					return;
				}

				undoCommandService.Add(new RevertILPatchMethodCommand(methodAnnotations, methodNode, baselineOptions));
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex, "Could not revert the selected IL patch change.");
			}
		}

		void RecoverPatchFromDllPairButton_Click(object sender, RoutedEventArgs e) {
			var originalDialog = new OpenFileDialog {
				Title = "Select Original Assembly",
				Filter = ".NET assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
				CheckFileExists = true,
				Multiselect = false,
			};
			if (originalDialog.ShowDialog(Window.GetWindow(this)) != true)
				return;

			var modifiedDialog = new OpenFileDialog {
				Title = "Select Modified Assembly",
				Filter = ".NET assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
				CheckFileExists = true,
				Multiselect = false,
				InitialDirectory = Path.GetDirectoryName(originalDialog.FileName),
			};
			if (modifiedDialog.ShowDialog(Window.GetWindow(this)) != true)
				return;

			try {
				string originalPath = Path.GetFullPath(originalDialog.FileName);
				string modifiedPath = Path.GetFullPath(modifiedDialog.FileName);
				if (StringComparer.OrdinalIgnoreCase.Equals(originalPath, modifiedPath)) {
					MsgBox.Instance.Show("Original and modified assemblies must be different files.");
					return;
				}

				using var original = ModuleDefMD.Load(originalPath);
				using var modified = ModuleDefMD.Load(modifiedPath);
				string defaultName = Path.GetFileNameWithoutExtension(originalPath) + ".recovered";
				if (!ILPatchDocumentCreator.TryCreate(original, modified, defaultName,
					out var document, out var report) || document is null) {
					var message = new StringBuilder()
						.Append("Cannot recover a v1 IL patch because unsupported structural changes were detected. ")
						.Append("No patch file was written.")
						.AppendLine()
						.AppendLine();
					foreach (string reason in report.UnsupportedReasons.Take(12))
						message.Append("- ").AppendLine(reason);
					if (report.UnsupportedReasons.Count > 12)
						message.Append("- ... and ").Append(report.UnsupportedReasons.Count - 12).Append(" more");
					MsgBox.Instance.Show(message.ToString());
					return;
				}

				if (report.ChangedCount == 0) {
					MsgBox.Instance.Show(
						"No portable CIL method-body differences were found between the selected assemblies. " +
						"MaxStack/MVID/metadata-token/offset noise is intentionally ignored.");
					return;
				}

				var saveDialog = new SaveFileDialog {
					Title = "Save Recovered IL Patch",
					Filter = "IL Patch (*.ilpatch)|*.ilpatch|JSON (*.json)|*.json|All files (*.*)|*.*",
					DefaultExt = ".ilpatch",
					AddExtension = true,
					OverwritePrompt = true,
					FileName = defaultName + ".ilpatch",
				};
				if (saveDialog.ShowDialog(Window.GetWindow(this)) != true)
					return;

				ILPatchSerializer.Save(saveDialog.FileName, document);
				MsgBox.Instance.Show(
					$"Recovered {report.ChangedCount} changed CIL method(s) into:\n{saveDialog.FileName}\n\n" +
					$"{report.UnchangedCount} unchanged method(s) were ignored.");
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex, "Could not recover an IL patch from the selected DLL pair.");
			}
		}

		void ImportButton_Click(object sender, RoutedEventArgs e) {
			var dialog = new OpenFileDialog {
				Title = "Import IL Patch",
				Filter = "IL Patch (*.ilpatch)|*.ilpatch|JSON (*.json)|*.json|All files (*.*)|*.*",
				DefaultExt = ".ilpatch",
				CheckFileExists = true,
				Multiselect = true,
			};
			if (dialog.ShowDialog(Window.GetWindow(this)) != true)
				return;

			try {
				var filenames = dialog.FileNames.ToArray();
				Array.Sort(filenames, StringComparer.OrdinalIgnoreCase);
				var sources = new List<ILPatchBatchSource>(filenames.Length);
				var sourceMap = new Dictionary<ILPatchMethodChange, string>();
				foreach (string filename in filenames) {
					var document = ILPatchSerializer.Load(filename);
					string sourceName = Path.GetFileName(filename);
					sources.Add(new ILPatchBatchSource(sourceName, document));
					foreach (var patch in document.Methods)
						sourceMap[patch] = sourceName;
				}

				ILPatchDocument documentToPreview;
				string sourceLabel;
				string defaultBaseName;
				if (sources.Count == 1) {
					documentToPreview = sources[0].Document;
					sourceLabel = sources[0].Name;
					defaultBaseName = Path.GetFileNameWithoutExtension(filenames[0]);
				}
				else {
					if (!ILPatchBatchComposer.TryCombine(sources, out var combined, out string error) || combined is null) {
						MsgBox.Instance.Show(
							"Cannot import the selected patch files as one batch.\n\n" + error +
							"\n\nApply/rebase overlapping patch files sequentially instead.");
						return;
					}
					documentToPreview = combined;
					sourceLabel = $"{sources.Count} patch files";
					defaultBaseName = "batch";
				}

				importedPatchSources.Clear();
				foreach (var pair in sourceMap)
					importedPatchSources[pair.Key] = pair.Value;
				targetOverrides.Clear();
				var modules = documentService.GetDocuments().GetModules<ModuleDef>();
				var preview = ILPatchImportMatcher.CreatePreview(documentToPreview, modules, targetOverrides);
				ShowImportPreview(sourceLabel, defaultBaseName, preview);
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex);
			}
		}

		void ShowImportPreview(string sourceLabel, string defaultBaseName, ILPatchImportPreview preview) {
			string? selectedPatchId = (importGrid.SelectedItem as ImportRow)?.Result.Patch.Id;
			importedSourceLabel = sourceLabel;
			importedDefaultBaseName = defaultBaseName;
			importedPreview = preview;
			imports.Clear();
			foreach (var result in preview.Results) {
				var bestCandidate = result.StructuralCandidates.FirstOrDefault();
				var selectedCandidate = result.IsManualTarget && result.Target is not null
					? result.StructuralCandidates.FirstOrDefault(a => ReferenceEquals(a.Method, result.Target))
					: null;
				var displayedCandidate = selectedCandidate ?? bestCandidate;
				imports.Add(new ImportRow {
					Result = result,
					Status = result.IsManualTarget ? result.Status + " (manual)" : result.Status.ToString(),
					Source = importedPatchSources.TryGetValue(result.Patch, out string? source) ? source : "—",
					Method = result.Patch.Target?.ToString() ?? "<invalid patch entry>",
					CurrentHash = ShortHash(result.CurrentBodyHash),
					BaseHash = ShortHash(result.Patch.BaseBody?.CanonicalHash),
					Candidate = result.IsManualTarget && result.Target is not null
						? "Manual: " + ILPatchMethodIdentity.Create(result.Target)
						: displayedCandidate?.Identity.ToString() ?? "—",
					Score = displayedCandidate is null ? "—" : $"{displayedCandidate.Score:P1}",
					Rebase = result.RebasePreview?.Status.ToString() ?? "—",
					Details = BuildCandidateSummary(result),
				});
			}

			int cleanRebase = preview.Results.Count(a => a.Status == ILPatchImportStatus.BaseChanged && a.RebasePreview?.Status == ILPatchRebaseStatus.Clean);
			int conflictRebase = preview.Results.Count(a => a.Status == ILPatchImportStatus.BaseChanged && a.RebasePreview?.Status == ILPatchRebaseStatus.Conflict);
			int unsupportedRebase = preview.Results.Count(a => a.Status == ILPatchImportStatus.BaseChanged && a.RebasePreview?.Status == ILPatchRebaseStatus.Unsupported);
			importSummaryText.Text =
				$"{sourceLabel}: {preview.Results.Count} method(s) — " +
				$"{preview.Count(ILPatchImportStatus.Exact)} exact, " +
				$"{preview.Count(ILPatchImportStatus.AlreadyApplied)} already present, " +
				$"{preview.Count(ILPatchImportStatus.RebasedApplied)} rebased present, " +
				$"{preview.Count(ILPatchImportStatus.BaseChanged)} base changed " +
				$"(rebase: {cleanRebase} clean / {conflictRebase} conflict / {unsupportedRebase} unsupported), " +
				$"{preview.Count(ILPatchImportStatus.Missing)} missing, " +
				$"{preview.Count(ILPatchImportStatus.Ambiguous)} ambiguous, " +
				$"{preview.Count(ILPatchImportStatus.Incompatible)} incompatible.";
			int exactCount = preview.Count(ILPatchImportStatus.Exact);
			int safeCount = exactCount + cleanRebase;
			int updateableCount = preview.Results.Count(CanUpdatePatchDefinition);
			int unresolvedCount = preview.Results.Count - updateableCount;
			applyButton.IsEnabled = exactCount != 0;
			rebaseButton.IsEnabled = cleanRebase != 0;
			applySafeButton.IsEnabled = safeCount != 0;
			exportRebasedButton.IsEnabled = updateableCount != 0;
			clearImportButton.IsEnabled = true;
			refreshImportButton.IsEnabled = true;
			applyButton.Content = $"Apply Exact ({exactCount})";
			rebaseButton.Content = $"Apply Clean Rebase ({cleanRebase})";
			applySafeButton.Content = $"Apply Safe ({safeCount})";
			exportRebasedButton.Content = $"Export Rebased ({updateableCount})...";
			if (preview.Results.Count == 0) {
				importHintText.Text = "This patch document contains no method entries.";
			}
			else if (unresolvedCount == 0 && safeCount == 0) {
				importHintText.Text =
					"All imported entries are already present on the loaded target. No apply action is needed. " +
					"You can Export Rebased to move the patch baseline forward.";
			}
			else if (unresolvedCount == 0) {
				importHintText.Text =
					$"Recommended next step: Apply Safe ({safeCount}). All entries are resolved; " +
					"after applying, save the target module using dnSpy's normal Save Module command.";
			}
			else if (safeCount != 0) {
				importHintText.Text =
					$"{safeCount} entr{(safeCount == 1 ? "y is" : "ies are")} safe to apply now; {unresolvedCount} still need review. " +
					"Select unresolved rows below and choose a compatible candidate when appropriate. Apply Safe never touches unresolved rows.";
			}
			else {
				importHintText.Text =
					$"No entries are currently safe to apply. Review the {unresolvedCount} unresolved row(s), inspect the IL diff/candidates, " +
					"and use Use Candidate only when the target is semantically the same method.";
			}
			var selectedRow = selectedPatchId is null
				? null
				: imports.FirstOrDefault(a => StringComparer.Ordinal.Equals(a.Result.Patch.Id, selectedPatchId));
			importGrid.SelectedItem = selectedRow ?? imports.FirstOrDefault();
			UpdateCandidateControls(importGrid.SelectedItem as ImportRow);
		}

		void RefreshImportedPreview() {
			if (importedPreview is null || importedSourceLabel is null || importedDefaultBaseName is null)
				return;
			var modules = documentService.GetDocuments().GetModules<ModuleDef>().ToArray();
			var preview = ILPatchImportMatcher.CreatePreview(importedPreview.Document, modules, targetOverrides);
			ShowImportPreview(importedSourceLabel, importedDefaultBaseName, preview);
		}

		void RefreshImportButton_Click(object sender, RoutedEventArgs e) =>
			RefreshImportedPreview();

		void ClearImportButton_Click(object sender, RoutedEventArgs e) {
			importedPreview = null;
			importedSourceLabel = null;
			importedDefaultBaseName = null;
			targetOverrides.Clear();
			importedPatchSources.Clear();
			imports.Clear();
			importGrid.SelectedItem = null;
			candidateSelector.Items.Clear();
			candidateSelector.IsEnabled = false;
			useCandidateButton.IsEnabled = false;
			clearCandidateButton.IsEnabled = false;
			compareImportButton.IsEnabled = false;
			clearImportButton.IsEnabled = false;
			refreshImportButton.IsEnabled = false;
			applyButton.IsEnabled = false;
			rebaseButton.IsEnabled = false;
			applySafeButton.IsEnabled = false;
			exportRebasedButton.IsEnabled = false;
			applyButton.Content = "Apply Exact";
			rebaseButton.Content = "Apply Clean Rebase";
			applySafeButton.Content = "Apply Safe";
			exportRebasedButton.Content = "Export Rebased .ilpatch...";
			importSummaryText.Text = "Imported patch replay";
			importHintText.Text = "No .ilpatch imported. Load the target assembly in dnSpy first, then import the patch you want to replay.";
			var selectedChange = ChangesGrid.SelectedItem as ChangeRow;
			UpdateDiff(selectedChange);
		}

		void ApplyButton_Click(object sender, RoutedEventArgs e) =>
			ApplyImportedChanges(includeExact: true, includeCleanRebase: false, "Could not apply the imported IL patch.");

		void RebaseButton_Click(object sender, RoutedEventArgs e) =>
			ApplyImportedChanges(includeExact: false, includeCleanRebase: true, "Could not apply the clean IL patch rebase.");

		void ApplySafeButton_Click(object sender, RoutedEventArgs e) =>
			ApplyImportedChanges(includeExact: true, includeCleanRebase: true, "Could not apply the safe IL patch batch.");

		void ApplyImportedChanges(bool includeExact, bool includeCleanRebase, string failureTitle) {
			if (importedPreview is null || importedSourceLabel is null || importedDefaultBaseName is null)
				return;

			try {
				// Re-run matching immediately before materialization so a stale preview or changed
				// manual target override can never authorize a mutation.
				var modules = documentService.GetDocuments().GetModules<ModuleDef>().ToArray();
				var preview = ILPatchImportMatcher.CreatePreview(importedPreview.Document, modules, targetOverrides);
				ShowImportPreview(importedSourceLabel, importedDefaultBaseName, preview);

				var selectedResults = preview.Results
					.Where(result =>
						(includeExact && result.Status == ILPatchImportStatus.Exact) ||
						(includeCleanRebase &&
							result.Status == ILPatchImportStatus.BaseChanged &&
							result.RebasePreview?.Status == ILPatchRebaseStatus.Clean))
					.ToArray();
				if (selectedResults.Length == 0)
					return;

				var materializers = new Dictionary<ModuleDef, ILPatchBodyMaterializer>();
				var seenTargets = new HashSet<MethodDef>();
				var entries = new List<ApplyILPatchCommand.Entry>(selectedResults.Length);

				foreach (var result in selectedResults) {
					var target = result.Target;
					if (target is null || target.Module is null)
						throw new InvalidOperationException($"Patch target '{result.Patch.Target}' is no longer available.");
					if (!seenTargets.Add(target)) {
						throw new InvalidOperationException(
							$"Multiple selected patch entries resolve to '{ILPatchMethodIdentity.Create(target)}'. " +
							"Nothing was applied; resolve the target collision or apply the files sequentially.");
					}

					ILPatchMethodBodySnapshot bodySnapshot;
					if (result.Status == ILPatchImportStatus.Exact) {
						bodySnapshot = result.Patch.PatchedBody;
					}
					else {
						var rebase = result.RebasePreview;
						if (rebase is null)
							throw new InvalidOperationException($"Clean rebase analysis for '{result.Patch.Target}' is no longer available.");
						if (!ILPatchRebaseMerger.TryCreateMergedSnapshot(result.Patch, rebase,
							out var mergedSnapshot, out string mergeError) || mergedSnapshot is null) {
							MsgBox.Instance.Show(
								$"Cannot rebase '{result.Patch.Target}'.\n\n{mergeError}\n\nNo methods were modified.");
							return;
						}
						bodySnapshot = mergedSnapshot;
					}

					var methodNode = appService.DocumentTreeView.FindNode(target) as MethodNode;
					if (methodNode is null)
						throw new InvalidOperationException($"Could not find the dnSpy document tree node for '{result.Patch.Target}'.");

					if (!materializers.TryGetValue(target.Module, out var materializer))
						materializers.Add(target.Module, materializer = new ILPatchBodyMaterializer(target.Module));

					if (!materializer.TryCreate(target, bodySnapshot, out var newBody, out string bodyError) ||
						newBody is null) {
						MsgBox.Instance.Show(
							$"Cannot materialize '{result.Patch.Target}'.\n\n{bodyError}\n\nNo methods were modified.");
						return;
					}

					entries.Add(new ApplyILPatchCommand.Entry(methodNode, newBody));
				}

				string baseName = string.IsNullOrWhiteSpace(preview.Document.Name) ? "IL patch" : preview.Document.Name;
				string commandName = includeExact && includeCleanRebase
					? baseName + " (safe apply)"
					: includeCleanRebase
						? baseName + " (clean rebase)"
						: baseName;

				// Every selected result has been merged/materialized above. Only now mutate, as one
				// dnSpy undo command, so Exact + Clean-Rebase batches remain atomic.
				undoCommandService.Add(new ApplyILPatchCommand(methodAnnotations, entries, commandName));
				RefreshImportedPreview();
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex, failureTitle + " No methods were modified.");
			}
		}

		void ExportRebasedButton_Click(object sender, RoutedEventArgs e) {
			if (importedPreview is null || importedSourceLabel is null || importedDefaultBaseName is null)
				return;

			try {
				var modules = documentService.GetDocuments().GetModules<ModuleDef>().ToArray();
				var preview = ILPatchImportMatcher.CreatePreview(importedPreview.Document, modules, targetOverrides);
				ShowImportPreview(importedSourceLabel!, importedDefaultBaseName!, preview);

				var updatedDocument = ILPatchDefinitionRebaser.CreateUpdatedDocument(preview, out var report);
				if (report.UpdatedCount == 0) {
					MsgBox.Instance.Show("No imported patch entries are currently safe to rebase into a new definition.");
					return;
				}

				string sourceName = importedDefaultBaseName;
				var dialog = new SaveFileDialog {
					Title = "Export Rebased IL Patch",
					Filter = "IL Patch (*.ilpatch)|*.ilpatch|JSON (*.json)|*.json|All files (*.*)|*.*",
					DefaultExt = ".ilpatch",
					AddExtension = true,
					OverwritePrompt = true,
					FileName = sourceName + ".rebased.ilpatch",
				};
				if (dialog.ShowDialog(Window.GetWindow(this)) != true)
					return;

				ILPatchSerializer.Save(dialog.FileName, updatedDocument);

				var message = new StringBuilder()
					.Append("Saved rebased patch definition. Updated ")
					.Append(report.UpdatedCount)
					.Append(" entr")
					.Append(report.UpdatedCount == 1 ? "y" : "ies")
					.Append("; preserved ")
					.Append(report.PreservedCount)
					.Append(" unresolved entr")
					.Append(report.PreservedCount == 1 ? "y." : "ies.")
					.ToString();
				if (report.PreservedReasons.Count != 0) {
					message += "\n\nPreserved entries:\n" +
						string.Join("\n", report.PreservedReasons.Take(5).Select(a => "- " + a));
					if (report.PreservedReasons.Count > 5)
						message += $"\n- ... and {report.PreservedReasons.Count - 5} more";
				}
				MsgBox.Instance.Show(message);
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex);
			}
		}

		static bool CanUpdatePatchDefinition(ILPatchImportResult result) =>
			result.Status == ILPatchImportStatus.Exact ||
			result.Status == ILPatchImportStatus.AlreadyApplied ||
			result.Status == ILPatchImportStatus.RebasedApplied ||
			(result.Status == ILPatchImportStatus.BaseChanged &&
				result.RebasePreview?.Status == ILPatchRebaseStatus.Clean);

		void ExportButton_Click(object sender, RoutedEventArgs e) {
			if (ILPatchWorkspace.Instance.GetEffectiveChanges().Count == 0)
				return;

			var dialog = new SaveFileDialog {
				Title = "Export IL Patch",
				Filter = "IL Patch (*.ilpatch)|*.ilpatch|JSON (*.json)|*.json|All files (*.*)|*.*",
				DefaultExt = ".ilpatch",
				AddExtension = true,
				OverwritePrompt = true,
				FileName = "patch.ilpatch",
			};
			if (dialog.ShowDialog(Window.GetWindow(this)) != true)
				return;

			try {
				string name = Path.GetFileNameWithoutExtension(dialog.FileName);
				ILPatchSerializer.Save(dialog.FileName, ILPatchWorkspace.Instance.CreateDocument(name));
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex);
			}
		}

		void ChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
			var row = ChangesGrid.SelectedItem as ChangeRow;
			revertButton.IsEnabled = row is not null;
			compareChangeButton.IsEnabled = row is not null;
			if (row is null)
				return;
			importGrid.SelectedItem = null;
			UpdateDiff(row);
		}

		void ChangesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) {
			if (ChangesGrid.SelectedItem is ChangeRow)
				OpenSelectedChangeDiff();
		}

		void CompareChangeButton_Click(object sender, RoutedEventArgs e) => OpenSelectedChangeDiff();

		void OpenSelectedChangeDiff() {
			var row = ChangesGrid.SelectedItem as ChangeRow;
			if (row is null)
				return;
			ILPatchWorkspace.Instance.TryGetTrackedBaseline(row.Change.Target, out var target, out _);
			ShowDiffWindow(row.Change, target);
		}

		void ImportGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
			var row = importGrid.SelectedItem as ImportRow;
			compareImportButton.IsEnabled = row is not null;
			UpdateCandidateControls(row);
			if (row is null)
				return;
			ChangesGrid.SelectedItem = null;
			diffText.Text = BuildImportReview(row.Result);
		}

		void ImportGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) {
			if (importGrid.SelectedItem is ImportRow)
				OpenSelectedImportDiff();
		}

		void CompareImportButton_Click(object sender, RoutedEventArgs e) => OpenSelectedImportDiff();

		void OpenSelectedImportDiff() {
			var row = importGrid.SelectedItem as ImportRow;
			if (row is null)
				return;
			ShowDiffWindow(row.Result.Patch, row.Result.Target);
		}

		void ShowDiffWindow(ILPatchMethodChange change, MethodDef? target) {
			var window = new ILPatchDiffWindow(change, target, decompilerService) {
				Owner = Window.GetWindow(this),
			};
			window.Show();
		}

		void CandidateSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
			UpdateUseCandidateButton();

		void UpdateCandidateControls(ImportRow? row) {
			candidateSelector.Items.Clear();
			candidateSelector.IsEnabled = false;
			useCandidateButton.IsEnabled = false;
			clearCandidateButton.IsEnabled = false;
			if (row is null)
				return;

			string patchId = row.Result.Patch.Id ?? string.Empty;
			clearCandidateButton.IsEnabled = targetOverrides.ContainsKey(patchId);
			targetOverrides.TryGetValue(patchId, out var currentOverride);

			CandidateChoice? selectedChoice = null;
			foreach (var candidate in row.Result.StructuralCandidates) {
				var choice = new CandidateChoice(candidate,
					ILPatchImportMatcher.SignaturesCompatible(row.Result.Patch.Target, candidate.Identity));
				candidateSelector.Items.Add(choice);
				if (currentOverride is not null &&
					StringComparer.Ordinal.Equals(currentOverride.ToCanonicalString(), candidate.Identity.ToCanonicalString()))
					selectedChoice = choice;
			}

			candidateSelector.IsEnabled = candidateSelector.Items.Count != 0;
			candidateSelector.SelectedItem = selectedChoice ?? candidateSelector.Items.Cast<object>().FirstOrDefault();
			UpdateUseCandidateButton();
		}

		void UpdateUseCandidateButton() {
			var row = importGrid.SelectedItem as ImportRow;
			var choice = candidateSelector.SelectedItem as CandidateChoice;
			useCandidateButton.IsEnabled = row is not null && choice?.IsCompatible == true;
		}

		void UseCandidateButton_Click(object sender, RoutedEventArgs e) {
			var row = importGrid.SelectedItem as ImportRow;
			var choice = candidateSelector.SelectedItem as CandidateChoice;
			if (row is null || choice is null || !choice.IsCompatible)
				return;

			string patchId = row.Result.Patch.Id ?? string.Empty;
			if (string.IsNullOrEmpty(patchId)) {
				MsgBox.Instance.Show("This patch entry has no stable id and cannot keep a target override.");
				return;
			}

			targetOverrides[patchId] = choice.Candidate.Identity;
			RefreshImportedPreview();
		}

		void ClearCandidateButton_Click(object sender, RoutedEventArgs e) {
			var row = importGrid.SelectedItem as ImportRow;
			if (row is null)
				return;
			string patchId = row.Result.Patch.Id ?? string.Empty;
			if (targetOverrides.Remove(patchId))
				RefreshImportedPreview();
		}

		void UpdateDiff(ChangeRow? row) {
			if (row is null) {
				diffText.Text = "Select a changed method to review its normalized IL diff.";
				return;
			}
			diffText.Text = BuildInstructionDiff(row.Change);
		}

		static string BuildImportReview(ILPatchImportResult result) {
			var builder = new StringBuilder();
			builder.AppendLine($"Import preview: {result.Status}{(result.IsManualTarget ? " (manual target)" : string.Empty)}");
			builder.AppendLine(result.Message);
			if (result.IsManualTarget && result.Target is not null)
				builder.AppendLine($"Selected target: {ILPatchMethodIdentity.Create(result.Target)}");
			if (result.RebasePreview is not null) {
				var rebase = result.RebasePreview;
				builder.AppendLine();
				builder.AppendLine($"Three-way rebase analysis: {rebase.Status}");
				builder.AppendLine(rebase.Message);
				if (rebase.LocalsChangedByPatch || rebase.ExceptionHandlersChangedByPatch || rebase.InitLocalsChangedByPatch) {
					builder.Append("Patch body metadata changes: ")
						.Append($"locals={rebase.LocalsChangedByPatch}, ")
						.Append($"exceptionHandlers={rebase.ExceptionHandlersChangedByPatch}, ")
						.AppendLine($"initLocals={rebase.InitLocalsChangedByPatch}");
				}
				for (int i = 0; i < rebase.Hunks.Count; i++) {
					var hunk = rebase.Hunks[i];
					builder.Append("  Hunk ").Append(i + 1)
						.Append(": base [").Append(hunk.BaseStart).Append(", ").Append(hunk.BaseStart + hunk.BaseLength)
						.Append(") -> patched [").Append(hunk.PatchedStart).Append(", ").Append(hunk.PatchedStart + hunk.PatchedLength)
						.Append(") => ");
					if (hunk.NewStart >= 0)
						builder.Append("current [").Append(hunk.NewStart).Append(", ").Append(hunk.NewStart + hunk.NewLength).Append(") ");
					builder.Append(hunk.Status).Append(": ").AppendLine(hunk.Message);
				}
				builder.AppendLine(rebase.Status == ILPatchRebaseStatus.Clean
					? "This method is eligible for Apply Clean Rebase; Apply Exact will still ignore it."
					: "This BaseChanged method is not eligible for automatic rebase application.");
			}
			if (result.StructuralCandidates.Count != 0) {
				builder.AppendLine();
				builder.AppendLine("Structural candidates (advisory only; these do not authorize Apply Exact):");
				for (int i = 0; i < result.StructuralCandidates.Count; i++) {
					var candidate = result.StructuralCandidates[i];
					builder.Append(i + 1).Append(". ")
						.Append(candidate.Identity.ToString())
						.Append("  ")
						.Append(candidate.Score.ToString("P1"))
						.Append("  [")
						.Append(ShortHash(candidate.CurrentBodyHash))
						.AppendLine("]");
					builder.Append("   ").AppendLine(candidate.Explanation);
				}
			}
			builder.AppendLine();
			if (result.Patch.BaseBody is null || result.Patch.PatchedBody is null)
				return builder.AppendLine("Patch entry does not contain complete method bodies.").ToString();
			builder.Append(BuildInstructionDiff(result.Patch));
			return builder.ToString();
		}

		static string BuildCandidateSummary(ILPatchImportResult result) {
			var details = new List<string> { result.Message };
			if (result.RebasePreview is not null)
				details.Add($"Three-way analysis: {result.RebasePreview.Status} ({result.RebasePreview.Hunks.Count} hunk(s)).");

			var best = result.StructuralCandidates.FirstOrDefault();
			if (best is null)
				return string.Join(" ", details);

			string confidence;
			var second = result.StructuralCandidates.Skip(1).FirstOrDefault();
			double margin = second is null ? best.Score : best.Score - second.Score;
			if (best.Score >= 0.85 && margin >= 0.08)
				confidence = "strong structural lead";
			else if (best.Score >= 0.85)
				confidence = "high-score but ambiguous structural lead";
			else if (best.Score >= 0.70)
				confidence = "possible structural lead";
			else
				confidence = "weak structural lead";
			details.Add($"Top candidate is a {confidence} ({best.Score:P1}, margin {margin:P1}).");
			return string.Join(" ", details);
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

		static string ShortHash(string? hash) {
			if (string.IsNullOrEmpty(hash))
				return "—";
			return hash.Length <= 12 ? hash : hash.Substring(0, 12);
		}

		sealed class CandidateChoice {
			public ILPatchStructuralCandidate Candidate { get; }
			public bool IsCompatible { get; }

			public CandidateChoice(ILPatchStructuralCandidate candidate, bool isCompatible) {
				Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
				IsCompatible = isCompatible;
			}

			public override string ToString() =>
				$"{Candidate.Score:P1}  {Candidate.Identity}  [{(IsCompatible ? "compatible" : "signature mismatch")}]";
		}

		sealed class ImportRow {
			public ILPatchImportResult Result { get; set; } = null!;
			public string Status { get; set; } = string.Empty;
			public string Source { get; set; } = string.Empty;
			public string Method { get; set; } = string.Empty;
			public string CurrentHash { get; set; } = string.Empty;
			public string BaseHash { get; set; } = string.Empty;
			public string Candidate { get; set; } = string.Empty;
			public string Score { get; set; } = string.Empty;
			public string Rebase { get; set; } = string.Empty;
			public string Details { get; set; } = string.Empty;
		}

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
