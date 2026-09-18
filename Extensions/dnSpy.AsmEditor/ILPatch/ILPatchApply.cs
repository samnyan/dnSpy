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
using dnlib.DotNet.Emit;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// One undoable import operation. All exact method applications are kept in a single
	/// dnSpy undo entry so Ctrl+Z reverts the imported patch atomically.
	/// </summary>
	sealed class ApplyILPatchCommand : IUndoCommand {
		public sealed class Entry {
			public MethodNode MethodNode { get; }
			public CilBody NewBody { get; }
			public dnlib.DotNet.Emit.MethodBody OriginalBody { get; }
			public bool WasBodyModified { get; set; }

			public Entry(MethodNode methodNode, CilBody newBody) {
				MethodNode = methodNode ?? throw new ArgumentNullException(nameof(methodNode));
				NewBody = newBody ?? throw new ArgumentNullException(nameof(newBody));
				OriginalBody = methodNode.MethodDef.MethodBody ?? throw new ArgumentException("Method has no original body", nameof(methodNode));
			}
		}

		readonly IMethodAnnotations methodAnnotations;
		readonly Entry[] entries;
		readonly string description;

		public ApplyILPatchCommand(IMethodAnnotations methodAnnotations, IEnumerable<Entry> entries, string patchName) {
			this.methodAnnotations = methodAnnotations ?? throw new ArgumentNullException(nameof(methodAnnotations));
			this.entries = entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries));
			if (this.entries.Length == 0)
				throw new ArgumentException("At least one method is required", nameof(entries));
			description = string.IsNullOrWhiteSpace(patchName) ? "Apply IL patch" : $"Apply IL patch: {patchName}";
		}

		public string Description => description;

		public void Execute() {
			foreach (var entry in entries) {
				var method = entry.MethodNode.MethodDef;
				entry.WasBodyModified = methodAnnotations.IsBodyModified(method);
				ILPatchWorkspace.Instance.EnsureTracked(method);
				methodAnnotations.SetBodyModified(method, true);
				method.MethodBody = entry.NewBody;
			}
		}

		public void Undo() {
			for (int i = entries.Length - 1; i >= 0; i--) {
				var entry = entries[i];
				var method = entry.MethodNode.MethodDef;
				method.MethodBody = entry.OriginalBody;
				methodAnnotations.SetBodyModified(method, entry.WasBodyModified);
			}
		}

		public IEnumerable<object> ModifiedObjects => entries.Select(a => (object)a.MethodNode);
	}
}
