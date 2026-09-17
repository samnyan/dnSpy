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

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Tracks the first-seen body of a method as its baseline and the current body as the
	/// effective patch. Individual edits are retained separately so the UI can show history
	/// without making the exported patch depend on transient intermediate edits.
	/// </summary>
	sealed class ILPatchWorkspace {
		sealed class TrackedMethod {
			public MethodDef Method { get; }
			public ILPatchMethodBodySnapshot Baseline { get; }
			public ILPatchMethodBodySnapshot Current { get; set; }

			public TrackedMethod(MethodDef method, ILPatchMethodBodySnapshot baseline) {
				Method = method;
				Baseline = baseline;
				Current = baseline;
			}
		}

		readonly Dictionary<MethodDef, TrackedMethod> trackedMethods = new Dictionary<MethodDef, TrackedMethod>();
		readonly Dictionary<MethodDef, ILPatchMethodBodySnapshot> pendingMutations = new Dictionary<MethodDef, ILPatchMethodBodySnapshot>();
		readonly List<ILPatchEditRecord> history = new List<ILPatchEditRecord>();

		public event EventHandler? Changed;

		public IReadOnlyList<ILPatchEditRecord> History => history;

		/// <summary>
		/// Must be called before an undoable operation mutates a method. The first snapshot
		/// becomes the stable baseline for the lifetime of this workspace.
		/// </summary>
		public void BeginMutation(MethodDef method) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (method.Body is null)
				return;

			var before = CilNormalizer.CreateSnapshot(method);
			if (!trackedMethods.ContainsKey(method))
				trackedMethods.Add(method, new TrackedMethod(method, before));
			pendingMutations[method] = before;
		}

		/// <summary>
		/// Completes a mutation begun by <see cref="BeginMutation"/>. This method is suitable
		/// for normal execute, undo and redo paths: the effective change is always recomputed
		/// as baseline -> current.
		/// </summary>
		public void EndMutation(MethodDef method, string description) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (!pendingMutations.TryGetValue(method, out var before))
				return;
			pendingMutations.Remove(method);

			if (method.Body is null)
				return;

			var after = CilNormalizer.CreateSnapshot(method);
			if (!trackedMethods.TryGetValue(method, out var tracked))
				return;
			tracked.Current = after;

			if (!StringComparer.Ordinal.Equals(before.CanonicalHash, after.CanonicalHash)) {
				history.Add(new ILPatchEditRecord {
					TimestampUtc = DateTime.UtcNow,
					Description = description ?? string.Empty,
					Method = after.Method,
					BeforeHash = before.CanonicalHash,
					AfterHash = after.CanonicalHash,
				});
			}

			Changed?.Invoke(this, EventArgs.Empty);
		}

		public IReadOnlyList<ILPatchMethodChange> GetEffectiveChanges() {
			var result = new List<ILPatchMethodChange>();
			foreach (var tracked in trackedMethods.Values) {
				if (StringComparer.Ordinal.Equals(tracked.Baseline.CanonicalHash, tracked.Current.CanonicalHash))
					continue;
				result.Add(new ILPatchMethodChange {
					Target = tracked.Baseline.Method,
					BaseBody = tracked.Baseline,
					PatchedBody = tracked.Current,
				});
			}
			return result
				.OrderBy(a => a.Target.AssemblyName, StringComparer.Ordinal)
				.ThenBy(a => a.Target.DeclaringType, StringComparer.Ordinal)
				.ThenBy(a => a.Target.MethodName, StringComparer.Ordinal)
				.ToArray();
		}

		public ILPatchDocument CreateDocument(string name) {
			var document = new ILPatchDocument { Name = name ?? string.Empty };
			document.Methods.AddRange(GetEffectiveChanges());
			return document;
		}

		public void Clear() {
			trackedMethods.Clear();
			pendingMutations.Clear();
			history.Clear();
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}
}
