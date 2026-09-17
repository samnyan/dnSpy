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
using dnSpy.AsmEditor.ILPatch;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.AsmEditor.Compiler {
	abstract class EditCodeCommandBase : IUndoCommand {
		readonly AddUpdatedNodesHelper addUpdatedNodesHelper;
		readonly MethodDef[] editedMethods;

		protected EditCodeCommandBase(Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider, ModuleDocumentNode modNode, ModuleImporter importer) {
			addUpdatedNodesHelper = addUpdatedNodesHelperProvider.Value.Create(modNode, importer);
			editedMethods = GetEditedMethods(importer).Distinct().ToArray();
		}

		static IEnumerable<MethodDef> GetEditedMethods(ModuleImporter importer) {
			foreach (var type in importer.MergedNonNestedTypes) {
				foreach (var method in GetEditedMethods(type))
					yield return method;
			}
		}

		static IEnumerable<MethodDef> GetEditedMethods(MergedImportedType type) {
			foreach (var editedMethod in type.EditedMethods)
				yield return editedMethod.OriginalMethod;

			foreach (var nestedType in type.NewOrExistingNestedTypes) {
				if (nestedType is not MergedImportedType mergedNestedType)
					continue;
				foreach (var method in GetEditedMethods(mergedNestedType))
					yield return method;
			}
		}

		public abstract string Description { get; }

		public void Execute() {
			foreach (var method in editedMethods)
				ILPatchWorkspace.Instance.EnsureTracked(method);
			addUpdatedNodesHelper.Execute();
		}

		public void Undo() => addUpdatedNodesHelper.Undo();
		public IEnumerable<object> ModifiedObjects => addUpdatedNodesHelper.ModifiedObjects;
	}
}
