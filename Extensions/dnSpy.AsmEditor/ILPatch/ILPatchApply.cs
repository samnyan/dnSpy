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
using dnSpy.AsmEditor.Commands;
using dnSpy.AsmEditor.MethodBody;
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


	/// <summary>
	/// Applies one mixed ILPatch v2 document as a single dnSpy undo operation. Existing
	/// method-body replacements and type-scoped field/method topology changes are kept atomic.
	/// </summary>
	sealed class ApplyILPatchDocumentCommand : IUndoCommand {
		sealed class StructuralContext {
			readonly IDocumentTreeView documentTreeView;
			readonly Dictionary<TypeDef, TypeNode> typeNodes = new Dictionary<TypeDef, TypeNode>();
			readonly Dictionary<FieldDef, FieldNode> addedFieldNodes = new Dictionary<FieldDef, FieldNode>();
			readonly Dictionary<MethodDef, MethodNode> addedMethodNodes = new Dictionary<MethodDef, MethodNode>();
			readonly Dictionary<PropertyDef, PropertyNode> addedPropertyNodes = new Dictionary<PropertyDef, PropertyNode>();
			readonly Dictionary<EventDef, EventNode> addedEventNodes = new Dictionary<EventDef, EventNode>();
			readonly HashSet<MethodDef> addedAccessorMethods = new HashSet<MethodDef>();
			readonly HashSet<MethodDef> removedAccessorMethods = new HashSet<MethodDef>();
			DeletableNodes<FieldNode> removedFieldNodes;
			DeletableNodes<MethodNode> removedMethodNodes;
			DeletableNodes<PropertyNode> removedPropertyNodes;
			DeletableNodes<EventNode> removedEventNodes;
			bool addedNodesCreated;

			public ILPatchStructuralMaterializer.Plan Plan { get; }
			public IReadOnlyDictionary<MethodDef, CilBody> AddedMethodBodies { get; }

			public StructuralContext(IDocumentTreeView documentTreeView,
				ILPatchStructuralMaterializer.Plan plan,
				IReadOnlyDictionary<MethodDef, CilBody> addedMethodBodies) {
				this.documentTreeView = documentTreeView ?? throw new ArgumentNullException(nameof(documentTreeView));
				Plan = plan ?? throw new ArgumentNullException(nameof(plan));
				AddedMethodBodies = addedMethodBodies ?? throw new ArgumentNullException(nameof(addedMethodBodies));

				foreach (var property in plan.AddedProperties) {
					foreach (var method in property.Property.GetMethods.Concat(property.Property.SetMethods).Concat(property.Property.OtherMethods))
						addedAccessorMethods.Add(method);
				}
				foreach (var @event in plan.AddedEvents) {
					if (@event.Event.AddMethod is not null) addedAccessorMethods.Add(@event.Event.AddMethod);
					if (@event.Event.InvokeMethod is not null) addedAccessorMethods.Add(@event.Event.InvokeMethod);
					if (@event.Event.RemoveMethod is not null) addedAccessorMethods.Add(@event.Event.RemoveMethod);
					foreach (var method in @event.Event.OtherMethods) addedAccessorMethods.Add(method);
				}
				foreach (var property in plan.RemovedProperties) {
					foreach (var method in property.Property.GetMethods.Concat(property.Property.SetMethods).Concat(property.Property.OtherMethods))
						removedAccessorMethods.Add(method);
				}
				foreach (var @event in plan.RemovedEvents) {
					if (@event.Event.AddMethod is not null) removedAccessorMethods.Add(@event.Event.AddMethod);
					if (@event.Event.InvokeMethod is not null) removedAccessorMethods.Add(@event.Event.InvokeMethod);
					if (@event.Event.RemoveMethod is not null) removedAccessorMethods.Add(@event.Event.RemoveMethod);
					foreach (var method in @event.Event.OtherMethods) removedAccessorMethods.Add(method);
				}

				var addedMethodSet = new HashSet<MethodDef>(plan.AddedMethodEntries.Select(a => a.Method));
				if (addedAccessorMethods.Any(a => !addedMethodSet.Contains(a)))
					throw new NotSupportedException(
						"GUI structural replay currently requires every accessor of an added property/event to be added in the same patch.");
				var removedMethodSet = new HashSet<MethodDef>(plan.RemovedMethods.Select(a => a.Method));
				if (removedAccessorMethods.Any(a => !removedMethodSet.Contains(a)))
					throw new NotSupportedException(
						"GUI structural replay currently requires every accessor of a removed property/event to be removed in the same patch.");

				foreach (var type in plan.AddedFields.Select(a => a.Type)
					.Concat(plan.AddedMethodEntries.Select(a => a.Type))
					.Concat(plan.AddedProperties.Select(a => a.Type))
					.Concat(plan.AddedEvents.Select(a => a.Type))
					.Concat(plan.RemovedFields.Select(a => a.Type))
					.Concat(plan.RemovedMethods.Select(a => a.Type))
					.Concat(plan.RemovedProperties.Select(a => a.Type))
					.Concat(plan.RemovedEvents.Select(a => a.Type))
					.Distinct()) {
					var typeNode = documentTreeView.FindNode(type) as TypeNode ??
						throw new InvalidOperationException($"Could not find the dnSpy type node for '{type.FullName}'.");
					typeNode.TreeNode.EnsureChildrenLoaded();
					typeNodes.Add(type, typeNode);
				}

				var removedFields = plan.RemovedFields.Select(a =>
					documentTreeView.FindNode(a.Field) as FieldNode ??
					throw new InvalidOperationException($"Could not find the dnSpy field node for '{a.Field.FullName}'.")).ToArray();
				var removedMethods = plan.RemovedMethods
					.Where(a => !removedAccessorMethods.Contains(a.Method))
					.Select(a => documentTreeView.FindNode(a.Method) as MethodNode ??
						throw new InvalidOperationException($"Could not find the dnSpy method node for '{a.Method.FullName}'.")).ToArray();
				var removedProperties = plan.RemovedProperties.Select(a =>
					documentTreeView.FindNode(a.Property) as PropertyNode ??
					throw new InvalidOperationException($"Could not find the dnSpy property node for '{a.Property.FullName}'.")).ToArray();
				var removedEvents = plan.RemovedEvents.Select(a =>
					documentTreeView.FindNode(a.Event) as EventNode ??
					throw new InvalidOperationException($"Could not find the dnSpy event node for '{a.Event.FullName}'.")).ToArray();
				removedFieldNodes = new DeletableNodes<FieldNode>(removedFields);
				removedMethodNodes = new DeletableNodes<MethodNode>(removedMethods);
				removedPropertyNodes = new DeletableNodes<PropertyNode>(removedProperties);
				removedEventNodes = new DeletableNodes<EventNode>(removedEvents);
			}

			public void ExecuteAdditions() {
				Plan.AttachAdditions();

				if (!addedNodesCreated) {
					foreach (var item in Plan.AddedFields)
						addedFieldNodes.Add(item.Field, typeNodes[item.Type].Create(item.Field));
					foreach (var item in Plan.AddedMethodEntries.Where(a => !addedAccessorMethods.Contains(a.Method)))
						addedMethodNodes.Add(item.Method, typeNodes[item.Type].Create(item.Method));
					foreach (var item in Plan.AddedProperties)
						addedPropertyNodes.Add(item.Property, typeNodes[item.Type].Create(item.Property));
					foreach (var item in Plan.AddedEvents)
						addedEventNodes.Add(item.Event, typeNodes[item.Type].Create(item.Event));
					addedNodesCreated = true;
				}

				foreach (var item in Plan.AddedFields)
					typeNodes[item.Type].TreeNode.AddChild(addedFieldNodes[item.Field].TreeNode);
				foreach (var item in Plan.AddedMethodEntries) {
					if (AddedMethodBodies.TryGetValue(item.Method, out var body))
						item.Method.MethodBody = body;
					if (!addedAccessorMethods.Contains(item.Method))
						typeNodes[item.Type].TreeNode.AddChild(addedMethodNodes[item.Method].TreeNode);
				}
				foreach (var item in Plan.AddedProperties)
					typeNodes[item.Type].TreeNode.AddChild(addedPropertyNodes[item.Property].TreeNode);
				foreach (var item in Plan.AddedEvents)
					typeNodes[item.Type].TreeNode.AddChild(addedEventNodes[item.Event].TreeNode);
			}

			public void ExecuteRemovals() {
				removedPropertyNodes.Delete();
				removedEventNodes.Delete();
				removedMethodNodes.Delete();
				removedFieldNodes.Delete();
				Plan.CommitRemovals();
			}

			public void UndoRemovals() {
				Plan.RestoreRemovals();
				removedFieldNodes.Restore();
				removedMethodNodes.Restore();
				removedEventNodes.Restore();
				removedPropertyNodes.Restore();
			}

			public void UndoAdditions() {
				for (int i = Plan.AddedEvents.Count - 1; i >= 0; i--) {
					var item = Plan.AddedEvents[i];
					typeNodes[item.Type].TreeNode.Children.Remove(addedEventNodes[item.Event].TreeNode);
				}
				for (int i = Plan.AddedProperties.Count - 1; i >= 0; i--) {
					var item = Plan.AddedProperties[i];
					typeNodes[item.Type].TreeNode.Children.Remove(addedPropertyNodes[item.Property].TreeNode);
				}
				for (int i = Plan.AddedMethodEntries.Count - 1; i >= 0; i--) {
					var item = Plan.AddedMethodEntries[i];
					if (!addedAccessorMethods.Contains(item.Method))
						typeNodes[item.Type].TreeNode.Children.Remove(addedMethodNodes[item.Method].TreeNode);
				}
				for (int i = Plan.AddedFields.Count - 1; i >= 0; i--) {
					var item = Plan.AddedFields[i];
					typeNodes[item.Type].TreeNode.Children.Remove(addedFieldNodes[item.Field].TreeNode);
				}
				Plan.RollbackAdditions();
			}

			public IEnumerable<object> ModifiedObjects => typeNodes.Values.Cast<object>();
		}

		readonly IMethodAnnotations methodAnnotations;
		readonly ApplyILPatchCommand.Entry[] bodyEntries;
		readonly StructuralContext[] structuralContexts;
		readonly string description;

		public ApplyILPatchDocumentCommand(IMethodAnnotations methodAnnotations,
			IDocumentTreeView documentTreeView,
			IEnumerable<ApplyILPatchCommand.Entry> bodyEntries,
			IEnumerable<(ILPatchStructuralMaterializer.Plan Plan, IReadOnlyDictionary<MethodDef, CilBody> AddedBodies)> structuralPlans,
			string patchName) {
			this.methodAnnotations = methodAnnotations ?? throw new ArgumentNullException(nameof(methodAnnotations));
			if (documentTreeView is null)
				throw new ArgumentNullException(nameof(documentTreeView));
			this.bodyEntries = bodyEntries?.ToArray() ?? throw new ArgumentNullException(nameof(bodyEntries));
			structuralContexts = structuralPlans?.Select(a =>
				new StructuralContext(documentTreeView, a.Plan, a.AddedBodies)).ToArray() ??
				throw new ArgumentNullException(nameof(structuralPlans));
			if (this.bodyEntries.Length == 0 && structuralContexts.Length == 0)
				throw new ArgumentException("At least one body or structural change is required.");
			description = string.IsNullOrWhiteSpace(patchName)
				? "Apply IL patch"
				: $"Apply IL patch: {patchName}";
		}

		public string Description => description;

		public void Execute() {
			foreach (var context in structuralContexts)
				context.ExecuteAdditions();

			foreach (var entry in bodyEntries) {
				var method = entry.MethodNode.MethodDef;
				entry.WasBodyModified = methodAnnotations.IsBodyModified(method);
				ILPatchWorkspace.Instance.EnsureTracked(method);
				methodAnnotations.SetBodyModified(method, true);
				method.MethodBody = entry.NewBody;
			}

			foreach (var context in structuralContexts)
				context.ExecuteRemovals();
		}

		public void Undo() {
			for (int i = structuralContexts.Length - 1; i >= 0; i--)
				structuralContexts[i].UndoRemovals();

			for (int i = bodyEntries.Length - 1; i >= 0; i--) {
				var entry = bodyEntries[i];
				var method = entry.MethodNode.MethodDef;
				method.MethodBody = entry.OriginalBody;
				methodAnnotations.SetBodyModified(method, entry.WasBodyModified);
			}

			for (int i = structuralContexts.Length - 1; i >= 0; i--)
				structuralContexts[i].UndoAdditions();
		}

		public IEnumerable<object> ModifiedObjects =>
			bodyEntries.Select(a => (object)a.MethodNode)
				.Concat(structuralContexts.SelectMany(a => a.ModifiedObjects))
				.Distinct();
	}

	/// <summary>
	/// Restores one tracked method to the exact dnSpy MethodBodyOptions snapshot captured before
	/// its first ILPatch-observed edit. Undo restores the body that existed before the revert.
	/// </summary>
	sealed class RevertILPatchMethodCommand : IUndoCommand {
		readonly IMethodAnnotations methodAnnotations;
		readonly MethodNode methodNode;
		readonly MethodBodyOptions baselineOptions;
		readonly MethodBodyOptions previousOptions;
		bool wasBodyModified;

		public RevertILPatchMethodCommand(IMethodAnnotations methodAnnotations, MethodNode methodNode,
			MethodBodyOptions baselineOptions) {
			this.methodAnnotations = methodAnnotations ?? throw new ArgumentNullException(nameof(methodAnnotations));
			this.methodNode = methodNode ?? throw new ArgumentNullException(nameof(methodNode));
			this.baselineOptions = baselineOptions ?? throw new ArgumentNullException(nameof(baselineOptions));
			previousOptions = new MethodBodyOptions(methodNode.MethodDef);
		}

		public string Description => "Revert IL patch method to baseline";

		public void Execute() {
			var method = methodNode.MethodDef;
			wasBodyModified = methodAnnotations.IsBodyModified(method);
			baselineOptions.CopyTo(method);
			methodAnnotations.SetBodyModified(method, false);
		}

		public void Undo() {
			var method = methodNode.MethodDef;
			previousOptions.CopyTo(method);
			methodAnnotations.SetBodyModified(method, wasBodyModified);
		}

		public IEnumerable<object> ModifiedObjects {
			get { yield return methodNode; }
		}
	}
}
