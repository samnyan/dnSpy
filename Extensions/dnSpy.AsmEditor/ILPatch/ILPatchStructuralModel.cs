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

namespace dnSpy.AsmEditor.ILPatch {
	sealed class ILPatchTypeIdentity {
		public string AssemblyName { get; set; } = string.Empty;
		public string ModuleName { get; set; } = string.Empty;
		public string FullName { get; set; } = string.Empty;

		public static ILPatchTypeIdentity Create(TypeDef type) {
			if (type is null)
				throw new ArgumentNullException(nameof(type));
			return new ILPatchTypeIdentity {
				AssemblyName = type.Module?.Assembly?.Name?.String ?? string.Empty,
				ModuleName = type.Module?.Name?.String ?? string.Empty,
				FullName = type.FullName ?? string.Empty,
			};
		}

		public string ToCanonicalString() => $"{AssemblyName}|{ModuleName}|{FullName}";
		public override string ToString() => FullName;
	}

	sealed class ILPatchFieldIdentity {
		public ILPatchTypeIdentity DeclaringType { get; set; } = null!;
		public string Name { get; set; } = string.Empty;
		public string FieldType { get; set; } = string.Empty;

		public static ILPatchFieldIdentity Create(FieldDef field) {
			if (field is null)
				throw new ArgumentNullException(nameof(field));
			return new ILPatchFieldIdentity {
				DeclaringType = ILPatchTypeIdentity.Create(field.DeclaringType),
				Name = field.Name?.String ?? string.Empty,
				FieldType = field.FieldType?.FullName ?? string.Empty,
			};
		}

		public string ToCanonicalString() => $"{DeclaringType.ToCanonicalString()}::{Name}:{FieldType}";
		public override string ToString() => $"{DeclaringType}::{Name}: {FieldType}";
	}

	enum ILPatchTypeSigKind {
		Named,
		GenericTypeParameter,
		GenericMethodParameter,
		Pointer,
		ByRef,
		SZArray,
		Array,
		GenericInstance,
		Pinned,
		RequiredModifier,
		OptionalModifier,
	}

	sealed class ILPatchTypeSigSnapshot {
		public ILPatchTypeSigKind Kind { get; set; }
		public string FullName { get; set; } = string.Empty;
		public string DefinitionAssembly { get; set; } = string.Empty;
		public bool IsValueType { get; set; }
		public int GenericIndex { get; set; } = -1;
		public uint ArrayRank { get; set; }
		public List<uint> ArraySizes { get; } = new List<uint>();
		public List<int> ArrayLowerBounds { get; } = new List<int>();
		public ILPatchTypeSigSnapshot? ElementType { get; set; }
		public ILPatchTypeSigSnapshot? ModifierType { get; set; }
		public List<ILPatchTypeSigSnapshot> GenericArguments { get; } = new List<ILPatchTypeSigSnapshot>();

		public override string ToString() => string.IsNullOrEmpty(FullName) ? Kind.ToString() : FullName;
	}

	sealed class ILPatchMethodSignatureSnapshot {
		public int CallingConvention { get; set; }
		public uint GenericParameterCount { get; set; }
		public ILPatchTypeSigSnapshot ReturnType { get; set; } = null!;
		public List<ILPatchTypeSigSnapshot> Parameters { get; } = new List<ILPatchTypeSigSnapshot>();
		public List<ILPatchTypeSigSnapshot> ParametersAfterSentinel { get; } = new List<ILPatchTypeSigSnapshot>();
	}

	sealed class ILPatchParameterSnapshot {
		public ushort Sequence { get; set; }
		public string Name { get; set; } = string.Empty;
		public ushort Attributes { get; set; }
	}

	sealed class ILPatchFieldDefinitionSnapshot {
		public ILPatchFieldIdentity Identity { get; set; } = null!;
		public ushort Attributes { get; set; }
		public ILPatchTypeSigSnapshot FieldType { get; set; } = null!;
	}

	sealed class ILPatchMethodDefinitionSnapshot {
		public ILPatchMethodIdentity Identity { get; set; } = null!;
		public ushort Attributes { get; set; }
		public ushort ImplAttributes { get; set; }
		public ILPatchMethodSignatureSnapshot Signature { get; set; } = null!;
		public List<ILPatchParameterSnapshot> Parameters { get; } = new List<ILPatchParameterSnapshot>();
		public ILPatchMethodBodySnapshot? Body { get; set; }
	}

	/// <summary>
	/// Structural changes are grouped by declaring type. Existing method-body edits remain in
	/// ILPatchDocument.Methods; this collection represents member topology changes around them.
	/// </summary>
	sealed class ILPatchTypeChange {
		public string Id { get; set; } = Guid.NewGuid().ToString("N");
		public ILPatchTypeIdentity Target { get; set; } = null!;
		public List<ILPatchFieldDefinitionSnapshot> AddedFields { get; } = new List<ILPatchFieldDefinitionSnapshot>();
		public List<ILPatchFieldIdentity> RemovedFields { get; } = new List<ILPatchFieldIdentity>();
		public List<ILPatchMethodDefinitionSnapshot> AddedMethods { get; } = new List<ILPatchMethodDefinitionSnapshot>();
		public List<ILPatchMethodIdentity> RemovedMethods { get; } = new List<ILPatchMethodIdentity>();

		public bool HasEffectiveChange =>
			AddedFields.Count != 0 || RemovedFields.Count != 0 ||
			AddedMethods.Count != 0 || RemovedMethods.Count != 0;
	}

	static class ILPatchStructuralSnapshotBuilder {
		public static bool TryCreateField(FieldDef field, out ILPatchFieldDefinitionSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (field is null) {
				error = "Field is missing.";
				return false;
			}
			if (field.HasCustomAttributes || field.Constant is not null || field.MarshalType is not null ||
				field.ImplMap is not null || field.FieldOffset is not null ||
				(field.InitialValue is not null && field.InitialValue.Length != 0)) {
				error = $"Field '{field.FullName}' uses custom attributes, constants, marshal/RVA/layout metadata or P/Invoke metadata that structural patch v2 does not capture yet.";
				return false;
			}
			if (field.FieldType is null || !TryCreateTypeSig(field.FieldType, out var fieldType, out error))
				return false;

			snapshot = new ILPatchFieldDefinitionSnapshot {
				Identity = ILPatchFieldIdentity.Create(field),
				Attributes = (ushort)field.Attributes,
				FieldType = fieldType!,
			};
			return true;
		}

		public static bool TryCreateMethod(MethodDef method, out ILPatchMethodDefinitionSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (method is null) {
				error = "Method is missing.";
				return false;
			}
			if (method.HasCustomAttributes || method.GenericParameters.Count != 0 ||
				method.DeclSecurities.Count != 0 || method.Overrides.Count != 0 || method.ImplMap is not null) {
				error = $"Method '{method.FullName}' uses custom attributes, generic parameters, security/override metadata or P/Invoke metadata that structural patch v2 does not capture yet.";
				return false;
			}
			if (method.MethodSig is null || !TryCreateMethodSig(method.MethodSig, out var signature, out error))
				return false;

			var result = new ILPatchMethodDefinitionSnapshot {
				Identity = ILPatchMethodIdentity.Create(method),
				Attributes = (ushort)method.Attributes,
				ImplAttributes = (ushort)method.ImplAttributes,
				Signature = signature!,
			};
			foreach (var parameter in method.ParamDefs.OrderBy(a => a.Sequence)) {
				if (parameter.HasCustomAttributes || parameter.Constant is not null || parameter.MarshalType is not null) {
					error = $"Parameter #{parameter.Sequence} on '{method.FullName}' uses metadata that structural patch v2 does not capture yet.";
					return false;
				}
				result.Parameters.Add(new ILPatchParameterSnapshot {
					Sequence = parameter.Sequence,
					Name = parameter.Name?.String ?? string.Empty,
					Attributes = (ushort)parameter.Attributes,
				});
			}

			if (method.Body is not null) {
				result.Body = CilNormalizer.CreateSnapshot(method);
				result.Body.CanonicalHash = ILPatchBodyHasher.Compute(result.Body);
			}
			else if (method.MethodBody is not null) {
				error = $"Method '{method.FullName}' has a non-CIL body, which structural patch v2 cannot capture yet.";
				return false;
			}

			snapshot = result;
			return true;
		}

		public static bool TryCreateMethodSig(MethodSig signature, out ILPatchMethodSignatureSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (signature is null) {
				error = "Method signature is missing.";
				return false;
			}
			if (signature.RetType is null || !TryCreateTypeSig(signature.RetType, out var returnType, out error))
				return false;
			var result = new ILPatchMethodSignatureSnapshot {
				CallingConvention = (int)signature.CallingConvention,
				GenericParameterCount = signature.GenParamCount,
				ReturnType = returnType!,
			};
			foreach (var parameter in signature.Params) {
				if (!TryCreateTypeSig(parameter, out var parameterType, out error))
					return false;
				result.Parameters.Add(parameterType!);
			}
			if (signature.ParamsAfterSentinel is not null) {
				foreach (var parameter in signature.ParamsAfterSentinel) {
					if (!TryCreateTypeSig(parameter, out var parameterType, out error))
						return false;
					result.ParametersAfterSentinel.Add(parameterType!);
				}
			}
			snapshot = result;
			return true;
		}

		public static bool TryCreateTypeSig(TypeSig type, out ILPatchTypeSigSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (type is null) {
				error = "Type signature is missing.";
				return false;
			}

			switch (type) {
			case CorLibTypeSig corlib:
				snapshot = Named(corlib.TypeDefOrRef, corlib.ElementType == ElementType.ValueType ||
					corlib.ElementType != ElementType.String && corlib.ElementType != ElementType.Object);
				return true;
			case ClassSig @class:
				snapshot = Named(@class.TypeDefOrRef, false);
				return true;
			case ValueTypeSig valueType:
				snapshot = Named(valueType.TypeDefOrRef, true);
				return true;
			case GenericVar genericVar:
				snapshot = new ILPatchTypeSigSnapshot {
					Kind = ILPatchTypeSigKind.GenericTypeParameter,
					GenericIndex = checked((int)genericVar.Number),
					FullName = genericVar.FullName ?? string.Empty,
				};
				return true;
			case GenericMVar genericMVar:
				snapshot = new ILPatchTypeSigSnapshot {
					Kind = ILPatchTypeSigKind.GenericMethodParameter,
					GenericIndex = checked((int)genericMVar.Number),
					FullName = genericMVar.FullName ?? string.Empty,
				};
				return true;
			case PtrSig pointer:
				return Wrap(ILPatchTypeSigKind.Pointer, pointer.Next, out snapshot, out error);
			case ByRefSig byRef:
				return Wrap(ILPatchTypeSigKind.ByRef, byRef.Next, out snapshot, out error);
			case SZArraySig szArray:
				return Wrap(ILPatchTypeSigKind.SZArray, szArray.Next, out snapshot, out error);
			case ArraySig array:
				if (!TryCreateTypeSig(array.Next, out var arrayElement, out error))
					return false;
				var arraySnapshot = new ILPatchTypeSigSnapshot {
					Kind = ILPatchTypeSigKind.Array,
					FullName = array.FullName ?? string.Empty,
					ArrayRank = array.Rank,
					ElementType = arrayElement,
				};
				arraySnapshot.ArraySizes.AddRange(array.Sizes);
				arraySnapshot.ArrayLowerBounds.AddRange(array.LowerBounds);
				snapshot = arraySnapshot;
				return true;
			case GenericInstSig genericInstance:
				if (!TryCreateTypeSig(genericInstance.GenericType, out var genericType, out error))
					return false;
				if (genericType is null || genericType.Kind != ILPatchTypeSigKind.Named) {
					error = $"Generic instance '{type.FullName}' has an unsupported generic type.";
					return false;
				}
				var genericSnapshot = new ILPatchTypeSigSnapshot {
					Kind = ILPatchTypeSigKind.GenericInstance,
					FullName = type.FullName ?? string.Empty,
					ElementType = genericType,
				};
				foreach (var argument in genericInstance.GenericArguments) {
					if (!TryCreateTypeSig(argument, out var argSnapshot, out error))
						return false;
					genericSnapshot.GenericArguments.Add(argSnapshot!);
				}
				snapshot = genericSnapshot;
				return true;
			case PinnedSig pinned:
				return Wrap(ILPatchTypeSigKind.Pinned, pinned.Next, out snapshot, out error);
			case CModReqdSig required:
				return Modifier(ILPatchTypeSigKind.RequiredModifier, required.Modifier, required.Next, out snapshot, out error);
			case CModOptSig optional:
				return Modifier(ILPatchTypeSigKind.OptionalModifier, optional.Modifier, optional.Next, out snapshot, out error);
			default:
				error = $"Type signature '{type.FullName}' uses unsupported element kind {type.ElementType}.";
				return false;
			}
		}

		static ILPatchTypeSigSnapshot Named(ITypeDefOrRef type, bool isValueType) => new ILPatchTypeSigSnapshot {
			Kind = ILPatchTypeSigKind.Named,
			FullName = type?.FullName ?? string.Empty,
			DefinitionAssembly = type?.DefinitionAssembly?.Name?.String ?? string.Empty,
			IsValueType = isValueType,
		};

		static bool Wrap(ILPatchTypeSigKind kind, TypeSig element, out ILPatchTypeSigSnapshot? snapshot, out string error) {
			if (!TryCreateTypeSig(element, out var child, out error)) {
				snapshot = null;
				return false;
			}
			snapshot = new ILPatchTypeSigSnapshot {
				Kind = kind,
				FullName = element?.FullName ?? string.Empty,
				ElementType = child,
			};
			return true;
		}

		static bool Modifier(ILPatchTypeSigKind kind, ITypeDefOrRef modifier, TypeSig element,
			out ILPatchTypeSigSnapshot? snapshot, out string error) {
			if (!TryCreateTypeSig(element, out var child, out error)) {
				snapshot = null;
				return false;
			}
			snapshot = new ILPatchTypeSigSnapshot {
				Kind = kind,
				FullName = element?.FullName ?? string.Empty,
				ElementType = child,
				ModifierType = Named(modifier, modifier?.ResolveTypeDef()?.IsValueType == true),
			};
			return true;
		}
	}
}
