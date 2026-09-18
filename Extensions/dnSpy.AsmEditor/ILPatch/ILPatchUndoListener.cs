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
using System.ComponentModel.Composition;
using dnlib.DotNet;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Extension;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Keeps the effective ILPatch workspace in sync with dnSpy's undoable assembly edits.
	/// Editing paths only need to call <see cref="ILPatchWorkspace.EnsureTracked"/> before
	/// their first mutation; this listener then observes the resulting Add/Undo/Redo state.
	/// </summary>
	[ExportAutoLoaded]
	sealed class ILPatchUndoListener : IAutoLoaded {
		[ImportingConstructor]
		ILPatchUndoListener(IUndoCommandService undoCommandService, IDsDocumentService documentService) {
			undoCommandService.OnEvent += UndoCommandService_OnEvent;
			documentService.CollectionChanged += DocumentService_CollectionChanged;
		}

		void UndoCommandService_OnEvent(object? sender, UndoCommandServiceEventArgs e) {
			switch (e.Type) {
			case UndoCommandServiceEventType.Add:
			case UndoCommandServiceEventType.Undo:
			case UndoCommandServiceEventType.Redo:
				ILPatchWorkspace.Instance.RefreshTrackedMethods(e.Type.ToString());
				break;
			}
		}

		void DocumentService_CollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			switch (e.Type) {
			case NotifyDocumentCollectionType.Remove:
			case NotifyDocumentCollectionType.Clear:
				ILPatchWorkspace.Instance.RemoveModules(e.Documents.GetModules<ModuleDef>());
				break;
			}
		}
	}
}
