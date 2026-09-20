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
using dnSpy.AsmEditor.MethodBody;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Tracks the first-seen body of a method as its baseline and the current body as the
	/// effective patch. Individual edits are retained separately so the UI can show history
	/// without making the exported patch depend on transient intermediate edits.
	/// </summary>
	sealed class ILPatchWorkspace {
		public static ILPatchWorkspace Instance { get; } = new ILPatchWorkspace();

		sealed class TrackedMethod {
			public MethodDef Method { get; }
			public ILPatchMethodBodySnapshot Baseline { get; set; }
			public MethodBodyOptions BaselineOptions { get; set; }
			public ILPatchMethodBodySnapshot Current { get; set; }

			public TrackedMethod(MethodDef method, ILPatchMethodBodySnapshot baseline, MethodBodyOptions baselineOptions) {
				Method = method;
				Baseline = baseline;
				BaselineOptions = baselineOptions;
				Current = baseline;
			}
		}

		readonly Dictionary<MethodDef, TrackedMethod> trackedMethods = new Dictionary<MethodDef, TrackedMethod>();
		readonly Dictionary<MethodDef, ILPatchMethodBodySnapshot> pendingMutations = new Dictionary<MethodDef, ILPatchMethodBodySnapshot>();
		readonly List<ILPatchEditRecord> history = new List<ILPatchEditRecord>();

		ILPatchWorkspace() {
		}

		public event EventHandler? Changed;

		public IReadOnlyList<ILPatchEditRecord> History => history;

		/// <summary>
		/// Captures a method's original CIL body the first time an editing path is about to
		/// mutate it. Calling this more than once is intentionally harmless.
		/// </summary>
		public void EnsureTracked(MethodDef method) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (method.Body is null || trackedMethods.ContainsKey(method))
				return;

			var baseline = CreateWorkspaceSnapshot(method);
			var baselineOptions = new MethodBodyOptions(method);
			trackedMethods.Add(method, new TrackedMethod(method, baseline, baselineOptions));
		}

		/// <summary>
		/// Refreshes only methods that have already been observed by an editing path. This is
		/// cheap enough to run after undo/redo events and avoids rescanning every loaded module.
		/// </summary>
		public void RefreshTrackedMethods(string description) {
			bool changed = false;
			foreach (var tracked in trackedMethods.Values.ToArray()) {
				if (tracked.Method.Body is null)
					continue;

				var current = CreateWorkspaceSnapshot(tracked.Method);
				if (StringComparer.Ordinal.Equals(tracked.Current.CanonicalHash, current.CanonicalHash))
					continue;

				history.Add(new ILPatchEditRecord {
					TimestampUtc = DateTime.UtcNow,
					Description = description ?? string.Empty,
					Method = current.Method,
					SourceModule = tracked.Method.Module,
					BeforeHash = tracked.Current.CanonicalHash,
					AfterHash = current.CanonicalHash,
				});
				tracked.Current = current;
				changed = true;
			}

			if (changed)
				Changed?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// Explicit mutation API retained for patch application and other operations that want
		/// to track a single mutation directly instead of relying on the undo-service listener.
		/// </summary>
		public void BeginMutation(MethodDef method) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (method.Body is null)
				return;

			EnsureTracked(method);
			pendingMutations[method] = CreateWorkspaceSnapshot(method);
		}

		/// <summary>
		/// Completes a mutation begun by <see cref="BeginMutation"/>. The effective change is
		/// always recomputed as baseline -> current.
		/// </summary>
		public void EndMutation(MethodDef method, string description) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (!pendingMutations.TryGetValue(method, out var before))
				return;
			pendingMutations.Remove(method);

			if (method.Body is null)
				return;

			var after = CreateWorkspaceSnapshot(method);
			if (!trackedMethods.TryGetValue(method, out var tracked))
				return;
			tracked.Current = after;

			if (!StringComparer.Ordinal.Equals(before.CanonicalHash, after.CanonicalHash)) {
				history.Add(new ILPatchEditRecord {
					TimestampUtc = DateTime.UtcNow,
					Description = description ?? string.Empty,
					Method = after.Method,
					SourceModule = method.Module,
					BeforeHash = before.CanonicalHash,
					AfterHash = after.CanonicalHash,
				});
			}

			Changed?.Invoke(this, EventArgs.Empty);
		}

		public IReadOnlyList<ILPatchMethodChange> GetEffectiveChanges() =>
			GetEffectiveChangesCore(null);

		public IReadOnlyList<ILPatchMethodChange> GetEffectiveChanges(ModuleDef module) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			return GetEffectiveChangesCore(module);
		}

		IReadOnlyList<ILPatchMethodChange> GetEffectiveChangesCore(ModuleDef? module) {
			var result = new List<ILPatchMethodChange>();
			foreach (var tracked in trackedMethods.Values) {
				if (module is not null && !ReferenceEquals(tracked.Method.Module, module))
					continue;
				if (StringComparer.Ordinal.Equals(tracked.Baseline.CanonicalHash, tracked.Current.CanonicalHash))
					continue;
				result.Add(new ILPatchMethodChange {
					Target = tracked.Baseline.Method,
					BaseModuleMvid = tracked.Method.Module?.Mvid ?? Guid.Empty,
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

		public bool TryGetTrackedBaseline(ILPatchMethodIdentity identity, out MethodDef? method,
			out MethodBodyOptions? baselineOptions) {
			if (identity is null)
				throw new ArgumentNullException(nameof(identity));

			string key = identity.ToCanonicalString();
			TrackedMethod? match = null;
			foreach (var tracked in trackedMethods.Values) {
				if (!StringComparer.Ordinal.Equals(tracked.Baseline.Method.ToCanonicalString(), key))
					continue;
				if (match is not null) {
					method = null;
					baselineOptions = null;
					return false;
				}
				match = tracked;
			}

			method = match?.Method;
			baselineOptions = match?.BaselineOptions;
			return method is not null && baselineOptions is not null;
		}

		/// <summary>
		/// Marks all tracked methods in one module as the new clean working baseline after a full
		/// repository commit. Tracking stays alive so a later dnSpy Undo/Redo can immediately
		/// become a working-tree change relative to the committed HEAD.
		/// </summary>
		public void AcceptModuleAsBaseline(ModuleDef module) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			AcceptChangesAsBaseline(module,
				trackedMethods.Values
					.Where(a => ReferenceEquals(a.Method.Module, module))
					.Select(a => a.Baseline.Method)
					.ToArray());
		}

		/// <summary>
		/// Marks only selected methods as committed. Their baseline is reset to the current body
		/// but tracking remains active; unselected methods keep their older baseline/current state.
		/// This mirrors Git: commit makes staged paths clean, while a subsequent Undo/edit makes
		/// them dirty again relative to the new HEAD.
		/// </summary>
		public void AcceptChangesAsBaseline(ModuleDef module,
			IEnumerable<ILPatchMethodIdentity> committedMethods) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (committedMethods is null)
				throw new ArgumentNullException(nameof(committedMethods));

			var keys = new HashSet<string>(
				committedMethods.Select(a => a.ToCanonicalString()), StringComparer.Ordinal);
			if (keys.Count == 0)
				return;

			bool changed = false;
			foreach (var tracked in trackedMethods.Values
				.Where(a => ReferenceEquals(a.Method.Module, module) &&
					keys.Contains(a.Baseline.Method.ToCanonicalString()))
				.ToArray()) {
				if (tracked.Method.Body is null)
					continue;
				var baseline = CreateWorkspaceSnapshot(tracked.Method);
				tracked.Baseline = baseline;
				tracked.Current = baseline;
				tracked.BaselineOptions = new MethodBodyOptions(tracked.Method);
				pendingMutations.Remove(tracked.Method);
				changed = true;
			}

			int removedHistory = history.RemoveAll(a =>
				ReferenceEquals(a.SourceModule, module) &&
				keys.Contains(a.Method.ToCanonicalString()));
			changed |= removedHistory != 0;
			if (changed)
				Changed?.Invoke(this, EventArgs.Empty);
		}

		public void RemoveModules(IEnumerable<ModuleDef> modules) {
			if (modules is null)
				throw new ArgumentNullException(nameof(modules));

			var removedModules = new HashSet<ModuleDef>(modules);
			if (removedModules.Count == 0)
				return;

			bool changed = false;
			foreach (var method in trackedMethods.Keys
				.Where(a => a.Module is not null && removedModules.Contains(a.Module))
				.ToArray()) {
				trackedMethods.Remove(method);
				pendingMutations.Remove(method);
				changed = true;
			}

			int removedHistory = history.RemoveAll(a =>
				a.SourceModule is not null && removedModules.Contains(a.SourceModule));
			changed |= removedHistory != 0;

			if (changed)
				Changed?.Invoke(this, EventArgs.Empty);
		}

		static ILPatchMethodBodySnapshot CreateWorkspaceSnapshot(MethodDef method) {
			var snapshot = CilNormalizer.CreateSnapshot(method);
			// CilNormalizer still creates a raw canonical hash for debugging. The portable patch
			// hash intentionally ignores method identity and MaxStack to reduce rebuild noise.
			snapshot.CanonicalHash = ILPatchBodyHasher.Compute(snapshot);
			return snapshot;
		}

		public void Clear() {
			trackedMethods.Clear();
			pendingMutations.Clear();
			history.Clear();
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}
}
