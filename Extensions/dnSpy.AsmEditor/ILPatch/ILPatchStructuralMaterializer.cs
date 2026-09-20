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
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Materializes the replayable subset of ILPatch v2 type/member topology changes.
	/// Additions are attached before method bodies are resolved so new methods/fields can
	/// reference one another. Removals are committed last.
	/// </summary>
	static class ILPatchStructuralMaterializer {
		internal sealed class Plan {
			readonly ModuleDef module;
			readonly List<(TypeDef? Parent, TypeDef Type)> addedTypes;
			readonly List<(TypeDef? Parent, TypeDef Type, int Index)> removedTypes;
			readonly List<(TypeDef Type, FieldDef Field)> addedFields;
			readonly List<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> addedMethods;
			readonly List<(TypeDef Type, PropertyDef Property)> addedProperties;
			readonly List<(TypeDef Type, EventDef Event)> addedEvents;
			readonly List<(TypeDef Type, FieldDef Field, int Index)> removedFields;
			readonly List<(TypeDef Type, MethodDef Method, int Index)> removedMethods;
			readonly List<(TypeDef Type, PropertyDef Property, int Index)> removedProperties;
			readonly List<(TypeDef Type, EventDef Event, int Index)> removedEvents;
			bool additionsAttached;

			public int OperationCount =>
				addedTypes.Count + removedTypes.Count +
				addedFields.Count + addedMethods.Count + addedProperties.Count + addedEvents.Count +
				removedFields.Count + removedMethods.Count + removedProperties.Count + removedEvents.Count;

			public IReadOnlyList<(TypeDef? Parent, TypeDef Type)> AddedTypes => addedTypes;
			public IReadOnlyList<(TypeDef? Parent, TypeDef Type, int Index)> RemovedTypes => removedTypes;
			public IReadOnlyList<(TypeDef Type, FieldDef Field)> AddedFields => addedFields;
			public IReadOnlyList<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> AddedMethodEntries => addedMethods;
			public IReadOnlyList<(TypeDef Type, PropertyDef Property)> AddedProperties => addedProperties;
			public IReadOnlyList<(TypeDef Type, EventDef Event)> AddedEvents => addedEvents;
			public IReadOnlyList<(TypeDef Type, FieldDef Field, int Index)> RemovedFields => removedFields;
			public IReadOnlyList<(TypeDef Type, MethodDef Method, int Index)> RemovedMethods => removedMethods;
			public IReadOnlyList<(TypeDef Type, PropertyDef Property, int Index)> RemovedProperties => removedProperties;
			public IReadOnlyList<(TypeDef Type, EventDef Event, int Index)> RemovedEvents => removedEvents;
			public IReadOnlyList<(MethodDef Method, ILPatchMethodBodySnapshot? Body)> AddedMethods =>
				addedMethods.Select(a => (a.Method, a.Body)).ToArray();

			internal Plan(ModuleDef module,
				List<(TypeDef? Parent, TypeDef Type)> addedTypes,
				List<(TypeDef? Parent, TypeDef Type, int Index)> removedTypes,
				List<(TypeDef Type, FieldDef Field)> addedFields,
				List<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> addedMethods,
				List<(TypeDef Type, PropertyDef Property)> addedProperties,
				List<(TypeDef Type, EventDef Event)> addedEvents,
				List<(TypeDef Type, FieldDef Field, int Index)> removedFields,
				List<(TypeDef Type, MethodDef Method, int Index)> removedMethods,
				List<(TypeDef Type, PropertyDef Property, int Index)> removedProperties,
				List<(TypeDef Type, EventDef Event, int Index)> removedEvents) {
				this.module = module;
				this.addedTypes = addedTypes;
				this.removedTypes = removedTypes;
				this.addedFields = addedFields;
				this.addedMethods = addedMethods;
				this.addedProperties = addedProperties;
				this.addedEvents = addedEvents;
				this.removedFields = removedFields;
				this.removedMethods = removedMethods;
				this.removedProperties = removedProperties;
				this.removedEvents = removedEvents;
			}

			public void AttachAdditions() {
				if (additionsAttached)
					return;
				foreach (var item in addedTypes.OrderBy(a => TypeDepth(a.Type))) {
					if (item.Parent is null)
						module.Types.Add(item.Type);
					else {
						item.Type.DeclaringType2 = null;
						item.Parent.NestedTypes.Add(item.Type);
					}
				}
				foreach (var item in addedFields)
					item.Type.Fields.Add(item.Field);
				foreach (var item in addedMethods)
					item.Type.Methods.Add(item.Method);
				foreach (var item in addedProperties)
					item.Type.Properties.Add(item.Property);
				foreach (var item in addedEvents)
					item.Type.Events.Add(item.Event);
				additionsAttached = true;
			}

			public void RollbackAdditions() {
				if (!additionsAttached)
					return;
				for (int i = addedEvents.Count - 1; i >= 0; i--)
					addedEvents[i].Type.Events.Remove(addedEvents[i].Event);
				for (int i = addedProperties.Count - 1; i >= 0; i--)
					addedProperties[i].Type.Properties.Remove(addedProperties[i].Property);
				for (int i = addedMethods.Count - 1; i >= 0; i--)
					addedMethods[i].Type.Methods.Remove(addedMethods[i].Method);
				for (int i = addedFields.Count - 1; i >= 0; i--)
					addedFields[i].Type.Fields.Remove(addedFields[i].Field);
				foreach (var item in addedTypes.OrderByDescending(a => TypeDepth(a.Type))) {
					if (item.Parent is null)
						module.Types.Remove(item.Type);
					else
						item.Parent.NestedTypes.Remove(item.Type);
				}
				additionsAttached = false;
			}

			public void CommitRemovals() {
				foreach (var item in removedProperties)
					item.Type.Properties.Remove(item.Property);
				foreach (var item in removedEvents)
					item.Type.Events.Remove(item.Event);
				foreach (var item in removedMethods)
					item.Type.Methods.Remove(item.Method);
				foreach (var item in removedFields)
					item.Type.Fields.Remove(item.Field);
				foreach (var item in removedTypes.OrderByDescending(a => TypeDepth(a.Type))) {
					if (item.Parent is null)
						module.Types.Remove(item.Type);
					else
						item.Parent.NestedTypes.Remove(item.Type);
				}
			}

			public void RestoreRemovals() {
				foreach (var item in removedFields.OrderBy(a => a.Index))
					item.Type.Fields.Insert(item.Index, item.Field);
				foreach (var item in removedMethods.OrderBy(a => a.Index))
					item.Type.Methods.Insert(item.Index, item.Method);
				foreach (var item in removedProperties.OrderBy(a => a.Index))
					item.Type.Properties.Insert(item.Index, item.Property);
				foreach (var item in removedEvents.OrderBy(a => a.Index))
					item.Type.Events.Insert(item.Index, item.Event);
				foreach (var item in removedTypes.OrderBy(a => TypeDepth(a.Type))) {
					if (item.Parent is null)
						module.Types.Insert(item.Index, item.Type);
					else
						item.Parent.NestedTypes.Insert(item.Index, item.Type);
				}
			}

			static int TypeDepth(TypeDef type) {
				int depth = 0;
				for (var current = type.DeclaringType2; current is not null; current = current.DeclaringType2)
					depth++;
				return depth;
			}
		}

		public static bool TryPrepare(ModuleDef module, IReadOnlyList<ILPatchTypeChange> changes,
			out Plan? plan, out string error) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (changes is null)
				throw new ArgumentNullException(nameof(changes));

			plan = null;
			error = string.Empty;
			var addedTypes = new List<(TypeDef? Parent, TypeDef Type)>();
			var removedTypes = new List<(TypeDef? Parent, TypeDef Type, int Index)>();
			var addedFields = new List<(TypeDef, FieldDef)>();
			var addedMethods = new List<(TypeDef, MethodDef, ILPatchMethodBodySnapshot?)>();
			var addedProperties = new List<(TypeDef, PropertyDef)>();
			var addedEvents = new List<(TypeDef, EventDef)>();
			var removedFields = new List<(TypeDef, FieldDef, int)>();
			var removedMethods = new List<(TypeDef, MethodDef, int)>();
			var removedProperties = new List<(TypeDef, PropertyDef, int)>();
			var removedEvents = new List<(TypeDef, EventDef, int)>();

			var addedTypeMap = new Dictionary<string, (ILPatchTypeDefinitionSnapshot Definition, TypeDef Type)>(StringComparer.Ordinal);
			foreach (var change in changes.Where(a => a.Kind == ILPatchTypeChangeKind.Add)) {
				if (change.TypeDefinition is null) {
					error = $"Added type '{change.Target}' is missing its type definition snapshot.";
					return false;
				}
				string key = change.Target.ToCanonicalString();
				if (addedTypeMap.ContainsKey(key)) {
					error = $"Added type '{change.Target}' appears more than once in the patch.";
					return false;
				}
				var shell = new TypeDefUser(change.TypeDefinition.Namespace, change.TypeDefinition.Name, null) {
					Attributes = (TypeAttributes)change.TypeDefinition.Attributes,
				};
				addedTypeMap.Add(key, (change.TypeDefinition, shell));
			}

			foreach (var pair in addedTypeMap.Values) {
				TypeDef? parent = null;
				if (pair.Definition.DeclaringType is not null) {
					if (addedTypeMap.TryGetValue(pair.Definition.DeclaringType.ToCanonicalString(), out var pendingParent))
						parent = pendingParent.Type;
					else if (!TryResolveType(module, pair.Definition.DeclaringType, out parent, out error))
						return false;
					pair.Type.DeclaringType2 = parent;
				}
				addedTypes.Add((parent, pair.Type));
			}

			var resolver = new TypeSigResolver(module, addedTypeMap.Values.Select(a => (ITypeDefOrRef)a.Type));
			foreach (var pair in addedTypeMap.Values) {
				if (pair.Definition.BaseType is not null) {
					if (!resolver.TryCreate(pair.Definition.BaseType, out var baseTypeSig, out error))
						return false;
					if (baseTypeSig is TypeDefOrRefSig tdor)
						pair.Type.BaseType = tdor.TypeDefOrRef;
					else
						pair.Type.BaseType = new TypeSpecUser(baseTypeSig!);
				}
			}

			foreach (var change in changes) {
				if (change is null || change.Target is null) {
					error = "Structural change set is missing its declaring type identity.";
					return false;
				}

				TypeDef? type;
				ILPatchTypeChange effectiveChange = change;
				if (change.Kind == ILPatchTypeChangeKind.Add) {
					if (!addedTypeMap.TryGetValue(change.Target.ToCanonicalString(), out var pending)) {
						error = $"Added type shell '{change.Target}' was not prepared.";
						return false;
					}
					type = pending.Type;
					var definition = pending.Definition;
					effectiveChange = new ILPatchTypeChange { Target = change.Target };
					effectiveChange.AddedFields.AddRange(definition.Fields);
					effectiveChange.AddedMethods.AddRange(definition.Methods);
					effectiveChange.AddedProperties.AddRange(definition.Properties);
					effectiveChange.AddedEvents.AddRange(definition.Events);
				}
				else {
					if (!TryResolveType(module, change.Target, out type, out error))
						return false;
					if (change.Kind == ILPatchTypeChangeKind.Remove) {
						if (change.TypeDefinition is null) {
							error = $"Removed type '{change.Target}' is missing its type definition snapshot.";
							return false;
						}
						TypeDef? parent = type!.DeclaringType;
						int index = parent is null ? module.Types.IndexOf(type) : parent.NestedTypes.IndexOf(type);
						if (index < 0) {
							error = $"Removed type '{change.Target}' is not attached to its expected owner.";
							return false;
						}
						removedTypes.Add((parent, type, index));
						continue;
					}
				}

				foreach (var removed in effectiveChange.RemovedFields) {
					var candidates = type!.Fields.Where(a =>
						StringComparer.Ordinal.Equals(ILPatchFieldIdentity.Create(a).ToCanonicalString(),
							removed.ToCanonicalString())).ToArray();
					if (candidates.Length != 1) {
						error = candidates.Length == 0
							? $"Field to remove '{removed}' was not found."
							: $"Field to remove '{removed}' is ambiguous ({candidates.Length} matches).";
						return false;
					}
					removedFields.Add((type, candidates[0], type.Fields.IndexOf(candidates[0])));
				}

				foreach (var removed in effectiveChange.RemovedMethods) {
					var candidates = type!.Methods.Where(a =>
						StringComparer.Ordinal.Equals(ILPatchMethodIdentity.Create(a).ToCanonicalString(),
							removed.ToCanonicalString())).ToArray();
					if (candidates.Length != 1) {
						error = candidates.Length == 0
							? $"Method to remove '{removed}' was not found."
							: $"Method to remove '{removed}' is ambiguous ({candidates.Length} matches).";
						return false;
					}
					removedMethods.Add((type, candidates[0], type.Methods.IndexOf(candidates[0])));
				}

				foreach (var removed in effectiveChange.RemovedProperties) {
					var candidates = type!.Properties.Where(a =>
						StringComparer.Ordinal.Equals(ILPatchPropertyIdentity.Create(a).ToCanonicalString(),
							removed.ToCanonicalString())).ToArray();
					if (candidates.Length != 1) {
						error = candidates.Length == 0
							? $"Property to remove '{removed}' was not found."
							: $"Property to remove '{removed}' is ambiguous ({candidates.Length} matches).";
						return false;
					}
					removedProperties.Add((type, candidates[0], type.Properties.IndexOf(candidates[0])));
				}

				foreach (var removed in effectiveChange.RemovedEvents) {
					var candidates = type!.Events.Where(a =>
						StringComparer.Ordinal.Equals(ILPatchEventIdentity.Create(a).ToCanonicalString(),
							removed.ToCanonicalString())).ToArray();
					if (candidates.Length != 1) {
						error = candidates.Length == 0
							? $"Event to remove '{removed}' was not found."
							: $"Event to remove '{removed}' is ambiguous ({candidates.Length} matches).";
						return false;
					}
					removedEvents.Add((type, candidates[0], type.Events.IndexOf(candidates[0])));
				}

				foreach (var added in effectiveChange.AddedFields) {
					if (added?.Identity is null || added.FieldType is null) {
						error = $"Type '{effectiveChange.Target}' contains an incomplete added-field definition.";
						return false;
					}
					if (type!.Fields.Any(a => StringComparer.Ordinal.Equals(
						ILPatchFieldIdentity.Create(a).ToCanonicalString(), added.Identity.ToCanonicalString()))) {
						error = $"Field to add '{added.Identity}' already exists.";
						return false;
					}
					if (!resolver.TryCreate(added.FieldType, out var fieldType, out error))
						return false;
					var field = new FieldDefUser(added.Identity.Name, new FieldSig(fieldType!),
						(FieldAttributes)added.Attributes);
					addedFields.Add((type, field));
				}

				foreach (var added in effectiveChange.AddedMethods) {
					if (added?.Identity is null || added.Signature is null) {
						error = $"Type '{effectiveChange.Target}' contains an incomplete added-method definition.";
						return false;
					}
					if (type!.Methods.Any(a => StringComparer.Ordinal.Equals(
						ILPatchMethodIdentity.Create(a).ToCanonicalString(), added.Identity.ToCanonicalString()))) {
						error = $"Method to add '{added.Identity}' already exists.";
						return false;
					}
					if (!resolver.TryCreate(added.Signature, out var methodSig, out error))
						return false;
					var method = new MethodDefUser(added.Identity.MethodName, methodSig!,
						(MethodImplAttributes)added.ImplAttributes, (MethodAttributes)added.Attributes);
					foreach (var parameter in added.Parameters) {
						method.ParamDefs.Add(new ParamDefUser(parameter.Name, parameter.Sequence,
							(ParamAttributes)parameter.Attributes));
					}
					if (added.Body is not null)
						method.Body = new CilBody();
					addedMethods.Add((type, method, added.Body));
				}

				foreach (var added in effectiveChange.AddedProperties) {
					if (added?.Identity is null || added.Signature is null) {
						error = $"Type '{effectiveChange.Target}' contains an incomplete added-property definition.";
						return false;
					}
					if (type!.Properties.Any(a => StringComparer.Ordinal.Equals(
						ILPatchPropertyIdentity.Create(a).ToCanonicalString(), added.Identity.ToCanonicalString()))) {
						error = $"Property to add '{added.Identity}' already exists.";
						return false;
					}
					if (!resolver.TryCreate(added.Signature, out var propertySig, out error))
						return false;
					var property = new PropertyDefUser(added.Identity.Name, propertySig!, (PropertyAttributes)added.Attributes);
					foreach (var accessor in added.GetMethods) {
						if (!TryResolveMethod(type, accessor, addedMethods, out var method, out error))
							return false;
						property.GetMethods.Add(method!);
					}
					foreach (var accessor in added.SetMethods) {
						if (!TryResolveMethod(type, accessor, addedMethods, out var method, out error))
							return false;
						property.SetMethods.Add(method!);
					}
					foreach (var accessor in added.OtherMethods) {
						if (!TryResolveMethod(type, accessor, addedMethods, out var method, out error))
							return false;
						property.OtherMethods.Add(method!);
					}
					addedProperties.Add((type, property));
				}

				foreach (var added in effectiveChange.AddedEvents) {
					if (added?.Identity is null || added.EventType is null) {
						error = $"Type '{effectiveChange.Target}' contains an incomplete added-event definition.";
						return false;
					}
					if (type!.Events.Any(a => StringComparer.Ordinal.Equals(
						ILPatchEventIdentity.Create(a).ToCanonicalString(), added.Identity.ToCanonicalString()))) {
						error = $"Event to add '{added.Identity}' already exists.";
						return false;
					}
					if (!resolver.TryCreate(added.EventType, out var eventTypeSig, out error))
						return false;
					ITypeDefOrRef eventType = eventTypeSig is TypeDefOrRefSig typeDefOrRefSig
						? typeDefOrRefSig.TypeDefOrRef
						: new TypeSpecUser(eventTypeSig!);
					var @event = new EventDefUser(added.Identity.Name, eventType, (EventAttributes)added.Attributes);
					if (added.AddMethod is not null) {
						if (!TryResolveMethod(type, added.AddMethod, addedMethods, out var method, out error))
							return false;
						@event.AddMethod = method;
					}
					if (added.InvokeMethod is not null) {
						if (!TryResolveMethod(type, added.InvokeMethod, addedMethods, out var method, out error))
							return false;
						@event.InvokeMethod = method;
					}
					if (added.RemoveMethod is not null) {
						if (!TryResolveMethod(type, added.RemoveMethod, addedMethods, out var method, out error))
							return false;
						@event.RemoveMethod = method;
					}
					foreach (var accessor in added.OtherMethods) {
						if (!TryResolveMethod(type, accessor, addedMethods, out var method, out error))
							return false;
						@event.OtherMethods.Add(method!);
					}
					addedEvents.Add((type, @event));
				}
			}


			plan = new Plan(module, addedTypes, removedTypes,
				addedFields, addedMethods, addedProperties, addedEvents,
				removedFields, removedMethods, removedProperties, removedEvents);
			return true;
		}


		static bool TryResolveMethod(TypeDef type, ILPatchMethodIdentity identity,
			IReadOnlyList<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> pendingMethods,
			out MethodDef? method, out string error) {
			method = null;
			error = string.Empty;
			var candidates = type.Methods
				.Concat(pendingMethods.Where(a => ReferenceEquals(a.Type, type)).Select(a => a.Method))
				.Where(a => MethodMatches(type, a, identity))
				.Distinct()
				.ToArray();
			if (candidates.Length != 1) {
				error = candidates.Length == 0
					? $"Accessor method '{identity}' was not found while materializing structural metadata."
					: $"Accessor method '{identity}' is ambiguous ({candidates.Length} matches).";
				return false;
			}
			method = candidates[0];
			return true;
		}

		static bool MethodMatches(TypeDef declaringType, MethodDef method, ILPatchMethodIdentity identity) {
			if (!StringComparer.Ordinal.Equals(declaringType.FullName ?? string.Empty, identity.DeclaringType) ||
				!StringComparer.Ordinal.Equals(method.Name?.String ?? string.Empty, identity.MethodName) ||
				!StringComparer.Ordinal.Equals(method.ReturnType?.FullName ?? string.Empty, identity.ReturnType) ||
				method.GenericParameters.Count != identity.GenericArity ||
				(method.MethodSig?.HasThis == true) != identity.HasThis)
				return false;
			if (declaringType.Module is not null) {
				if (!string.IsNullOrEmpty(identity.ModuleName) &&
					!StringComparer.Ordinal.Equals(declaringType.Module.Name?.String ?? string.Empty, identity.ModuleName))
					return false;
				if (!string.IsNullOrEmpty(identity.AssemblyName) &&
					!StringComparer.Ordinal.Equals(declaringType.Module.Assembly?.Name?.String ?? string.Empty, identity.AssemblyName))
					return false;
			}
			var parameters = method.MethodSig?.Params ?? Array.Empty<TypeSig>();
			if (parameters.Count != identity.ParameterTypes.Count)
				return false;
			for (int i = 0; i < parameters.Count; i++) {
				if (!StringComparer.Ordinal.Equals(parameters[i].FullName ?? string.Empty, identity.ParameterTypes[i]))
					return false;
			}
			return true;
		}

		static bool TryResolveType(ModuleDef module, ILPatchTypeIdentity identity,
			out TypeDef? type, out string error) {
			type = null;
			error = string.Empty;
			if (!string.IsNullOrEmpty(identity.ModuleName) &&
				!StringComparer.Ordinal.Equals(identity.ModuleName, module.Name?.String ?? string.Empty)) {
				error = $"Structural target type '{identity}' belongs to module '{identity.ModuleName}', not '{module.Name}'.";
				return false;
			}
			if (!string.IsNullOrEmpty(identity.AssemblyName) &&
				!StringComparer.Ordinal.Equals(identity.AssemblyName, module.Assembly?.Name?.String ?? string.Empty)) {
				error = $"Structural target type '{identity}' belongs to assembly '{identity.AssemblyName}', not '{module.Assembly?.Name}'.";
				return false;
			}
			var matches = module.GetTypes()
				.Where(a => StringComparer.Ordinal.Equals(a.FullName, identity.FullName))
				.ToArray();
			if (matches.Length != 1) {
				error = matches.Length == 0
					? $"Structural target type '{identity.FullName}' was not found."
					: $"Structural target type '{identity.FullName}' is ambiguous ({matches.Length} matches).";
				return false;
			}
			type = matches[0];
			return true;
		}

		sealed class TypeSigResolver {
			readonly ModuleDef module;
			readonly Dictionary<string, TypeSig> corlib = new Dictionary<string, TypeSig>(StringComparer.Ordinal);
			readonly Dictionary<string, ITypeDefOrRef> named = new Dictionary<string, ITypeDefOrRef>(StringComparer.Ordinal);

			public TypeSigResolver(ModuleDef module, IEnumerable<ITypeDefOrRef>? additionalTypes = null) {
				this.module = module;
				AddCorLib(module.CorLibTypes.Void);
				AddCorLib(module.CorLibTypes.Boolean);
				AddCorLib(module.CorLibTypes.Char);
				AddCorLib(module.CorLibTypes.SByte);
				AddCorLib(module.CorLibTypes.Byte);
				AddCorLib(module.CorLibTypes.Int16);
				AddCorLib(module.CorLibTypes.UInt16);
				AddCorLib(module.CorLibTypes.Int32);
				AddCorLib(module.CorLibTypes.UInt32);
				AddCorLib(module.CorLibTypes.Int64);
				AddCorLib(module.CorLibTypes.UInt64);
				AddCorLib(module.CorLibTypes.Single);
				AddCorLib(module.CorLibTypes.Double);
				AddCorLib(module.CorLibTypes.String);
				AddCorLib(module.CorLibTypes.TypedReference);
				AddCorLib(module.CorLibTypes.IntPtr);
				AddCorLib(module.CorLibTypes.UIntPtr);
				AddCorLib(module.CorLibTypes.Object);

				foreach (var type in module.GetTypes())
					AddNamed(type);
				foreach (var type in module.GetTypeRefs())
					AddNamed(type);
				if (additionalTypes is not null) {
					foreach (var type in additionalTypes)
						AddNamed(type);
				}
			}

			void AddCorLib(TypeSig type) {
				if (type is null || string.IsNullOrEmpty(type.FullName))
					return;
				if (!corlib.ContainsKey(type.FullName))
					corlib.Add(type.FullName, type);
				if (type is TypeDefOrRefSig tdor)
					AddNamed(tdor.TypeDefOrRef);
			}

			void AddNamed(ITypeDefOrRef? type) {
				if (type is null || string.IsNullOrEmpty(type.FullName) || named.ContainsKey(type.FullName))
					return;
				named.Add(type.FullName, type);
			}

			public bool TryCreate(ILPatchPropertySignatureSnapshot snapshot, out PropertySig? signature, out string error) {
				signature = null;
				error = string.Empty;
				if (snapshot is null || snapshot.ReturnType is null) {
					error = "Property signature snapshot is incomplete.";
					return false;
				}
				if (!TryCreate(snapshot.ReturnType, out var returnType, out error))
					return false;
				var parameters = new TypeSig[snapshot.Parameters.Count];
				for (int i = 0; i < parameters.Length; i++) {
					if (!TryCreate(snapshot.Parameters[i], out var parameter, out error))
						return false;
					parameters[i] = parameter!;
				}
				signature = new PropertySig(snapshot.HasThis, returnType!, parameters);
				return true;
			}

			public bool TryCreate(ILPatchMethodSignatureSnapshot snapshot, out MethodSig? signature, out string error) {
				signature = null;
				error = string.Empty;
				if (!TryCreate(snapshot.ReturnType, out var returnType, out error))
					return false;
				var parameters = new TypeSig[snapshot.Parameters.Count];
				for (int i = 0; i < parameters.Length; i++) {
					if (!TryCreate(snapshot.Parameters[i], out var parameter, out error))
						return false;
					parameters[i] = parameter!;
				}
				var result = new MethodSig((CallingConvention)snapshot.CallingConvention,
					snapshot.GenericParameterCount, returnType!, parameters);
				if (snapshot.ParametersAfterSentinel.Count != 0) {
					result.ParamsAfterSentinel = new List<TypeSig>();
					foreach (var source in snapshot.ParametersAfterSentinel) {
						if (!TryCreate(source, out var parameter, out error))
							return false;
						result.ParamsAfterSentinel.Add(parameter!);
					}
				}
				signature = result;
				return true;
			}

			public bool TryCreate(ILPatchTypeSigSnapshot snapshot, out TypeSig? type, out string error) {
				type = null;
				error = string.Empty;
				if (snapshot is null) {
					error = "Type signature snapshot is missing.";
					return false;
				}

				switch (snapshot.Kind) {
				case ILPatchTypeSigKind.Named:
					if (corlib.TryGetValue(snapshot.FullName ?? string.Empty, out var corlibType)) {
						type = corlibType;
						return true;
					}
					if (!named.TryGetValue(snapshot.FullName ?? string.Empty, out var namedType)) {
						error = $"Could not resolve structural signature type '{snapshot.FullName}' from target metadata.";
						return false;
					}
					type = snapshot.IsValueType ? new ValueTypeSig(namedType) : new ClassSig(namedType);
					return true;

				case ILPatchTypeSigKind.GenericTypeParameter:
					if (snapshot.GenericIndex < 0) {
						error = "Generic type parameter index is invalid.";
						return false;
					}
					type = new GenericVar(snapshot.GenericIndex);
					return true;

				case ILPatchTypeSigKind.GenericMethodParameter:
					if (snapshot.GenericIndex < 0) {
						error = "Generic method parameter index is invalid.";
						return false;
					}
					type = new GenericMVar(snapshot.GenericIndex);
					return true;

				case ILPatchTypeSigKind.Pointer:
				return Wrap(snapshot, child => new PtrSig(child), out type, out error);
				case ILPatchTypeSigKind.ByRef:
					return Wrap(snapshot, child => new ByRefSig(child), out type, out error);
				case ILPatchTypeSigKind.SZArray:
					return Wrap(snapshot, child => new SZArraySig(child), out type, out error);
				case ILPatchTypeSigKind.Pinned:
					return Wrap(snapshot, child => new PinnedSig(child), out type, out error);

				case ILPatchTypeSigKind.Array:
					if (!TryCreate(snapshot.ElementType!, out var element, out error))
						return false;
					type = new ArraySig(element!, snapshot.ArrayRank, snapshot.ArraySizes, snapshot.ArrayLowerBounds);
					return true;

				case ILPatchTypeSigKind.GenericInstance:
					if (!TryCreate(snapshot.ElementType!, out var generic, out error))
						return false;
					if (!(generic is ClassOrValueTypeSig genericType)) {
						error = $"Generic instance '{snapshot.FullName}' does not resolve to a class/value type.";
						return false;
					}
					var instance = new GenericInstSig(genericType);
					foreach (var source in snapshot.GenericArguments) {
						if (!TryCreate(source, out var argument, out error))
							return false;
						instance.GenericArguments.Add(argument!);
					}
					type = instance;
					return true;

				case ILPatchTypeSigKind.RequiredModifier:
				return Modifier(snapshot, required: true, out type, out error);
				case ILPatchTypeSigKind.OptionalModifier:
					return Modifier(snapshot, required: false, out type, out error);
				default:
					error = $"Unsupported structural signature kind {snapshot.Kind}.";
					return false;
				}
			}

			bool Wrap(ILPatchTypeSigSnapshot snapshot, Func<TypeSig, TypeSig> creator,
				out TypeSig? type, out string error) {
				type = null;
				error = string.Empty;
				if (snapshot.ElementType is null || !TryCreate(snapshot.ElementType, out var child, out error))
					return false;
				type = creator(child!);
				return true;
			}

			bool Modifier(ILPatchTypeSigSnapshot snapshot, bool required,
				out TypeSig? type, out string error) {
				type = null;
				if (snapshot.ModifierType is null || snapshot.ElementType is null) {
					error = "Modifier signature is incomplete.";
					return false;
				}
				if (!named.TryGetValue(snapshot.ModifierType.FullName ?? string.Empty, out var modifier)) {
					error = $"Could not resolve signature modifier type '{snapshot.ModifierType.FullName}'.";
					return false;
				}
				if (!TryCreate(snapshot.ElementType, out var child, out error))
					return false;
				type = required ? new CModReqdSig(modifier, child!) : new CModOptSig(modifier, child!);
				return true;
			}
		}
	}
}
