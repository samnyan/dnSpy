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
			readonly List<(TypeDef Type, FieldDef Field)> addedFields;
			readonly List<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> addedMethods;
			readonly List<(TypeDef Type, FieldDef Field, int Index)> removedFields;
			readonly List<(TypeDef Type, MethodDef Method, int Index)> removedMethods;
			bool additionsAttached;

			public int OperationCount =>
				addedFields.Count + addedMethods.Count + removedFields.Count + removedMethods.Count;

			public IReadOnlyList<(TypeDef Type, FieldDef Field)> AddedFields => addedFields;
			public IReadOnlyList<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> AddedMethodEntries => addedMethods;
			public IReadOnlyList<(TypeDef Type, FieldDef Field, int Index)> RemovedFields => removedFields;
			public IReadOnlyList<(TypeDef Type, MethodDef Method, int Index)> RemovedMethods => removedMethods;
			public IReadOnlyList<(MethodDef Method, ILPatchMethodBodySnapshot? Body)> AddedMethods =>
				addedMethods.Select(a => (a.Method, a.Body)).ToArray();

			internal Plan(List<(TypeDef Type, FieldDef Field)> addedFields,
				List<(TypeDef Type, MethodDef Method, ILPatchMethodBodySnapshot? Body)> addedMethods,
				List<(TypeDef Type, FieldDef Field, int Index)> removedFields,
				List<(TypeDef Type, MethodDef Method, int Index)> removedMethods) {
				this.addedFields = addedFields;
				this.addedMethods = addedMethods;
				this.removedFields = removedFields;
				this.removedMethods = removedMethods;
			}

			public void AttachAdditions() {
				if (additionsAttached)
					return;
				foreach (var item in addedFields)
					item.Type.Fields.Add(item.Field);
				foreach (var item in addedMethods)
					item.Type.Methods.Add(item.Method);
				additionsAttached = true;
			}

			public void RollbackAdditions() {
				if (!additionsAttached)
					return;
				for (int i = addedMethods.Count - 1; i >= 0; i--)
					addedMethods[i].Type.Methods.Remove(addedMethods[i].Method);
				for (int i = addedFields.Count - 1; i >= 0; i--)
					addedFields[i].Type.Fields.Remove(addedFields[i].Field);
				additionsAttached = false;
			}

			public void CommitRemovals() {
				foreach (var item in removedMethods)
					item.Type.Methods.Remove(item.Method);
				foreach (var item in removedFields)
					item.Type.Fields.Remove(item.Field);
			}

			public void RestoreRemovals() {
				foreach (var item in removedFields.OrderBy(a => a.Index))
					item.Type.Fields.Insert(item.Index, item.Field);
				foreach (var item in removedMethods.OrderBy(a => a.Index))
					item.Type.Methods.Insert(item.Index, item.Method);
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
			var addedFields = new List<(TypeDef, FieldDef)>();
			var addedMethods = new List<(TypeDef, MethodDef, ILPatchMethodBodySnapshot?)>();
			var removedFields = new List<(TypeDef, FieldDef, int)>();
			var removedMethods = new List<(TypeDef, MethodDef, int)>();
			var resolver = new TypeSigResolver(module);

			foreach (var change in changes) {
				if (change is null || change.Target is null) {
					error = "Structural change set is missing its declaring type identity.";
					return false;
				}
				if (!TryResolveType(module, change.Target, out var type, out error))
					return false;

				foreach (var removed in change.RemovedFields) {
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

				foreach (var removed in change.RemovedMethods) {
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

				foreach (var added in change.AddedFields) {
					if (added?.Identity is null || added.FieldType is null) {
						error = $"Type '{change.Target}' contains an incomplete added-field definition.";
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

				foreach (var added in change.AddedMethods) {
					if (added?.Identity is null || added.Signature is null) {
						error = $"Type '{change.Target}' contains an incomplete added-method definition.";
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
			}


			plan = new Plan(addedFields, addedMethods, removedFields, removedMethods);
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

			public TypeSigResolver(ModuleDef module) {
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
