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


	sealed class ILPatchPropertyIdentity {
		public ILPatchTypeIdentity DeclaringType { get; set; } = null!;
		public string Name { get; set; } = string.Empty;
		public bool HasThis { get; set; }
		public string ReturnType { get; set; } = string.Empty;
		public List<string> Parameters { get; } = new List<string>();

		public static ILPatchPropertyIdentity Create(PropertyDef property) {
			if (property is null)
				throw new ArgumentNullException(nameof(property));
			var signature = property.PropertySig;
			var result = new ILPatchPropertyIdentity {
				DeclaringType = ILPatchTypeIdentity.Create(property.DeclaringType),
				Name = property.Name?.String ?? string.Empty,
				HasThis = signature?.HasThis == true,
				ReturnType = signature?.RetType?.FullName ?? string.Empty,
			};
			if (signature is not null)
				result.Parameters.AddRange(signature.Params.Select(a => a.FullName ?? string.Empty));
			return result;
		}

		public string ToCanonicalString() =>
			$"{DeclaringType.ToCanonicalString()}::{Name}|this={HasThis}|ret={ReturnType}|args={string.Join(",", Parameters)}";
		public override string ToString() => $"{DeclaringType}::{Name}";
	}

	sealed class ILPatchEventIdentity {
		public ILPatchTypeIdentity DeclaringType { get; set; } = null!;
		public string Name { get; set; } = string.Empty;
		public string EventType { get; set; } = string.Empty;

		public static ILPatchEventIdentity Create(EventDef @event) {
			if (@event is null)
				throw new ArgumentNullException(nameof(@event));
			return new ILPatchEventIdentity {
				DeclaringType = ILPatchTypeIdentity.Create(@event.DeclaringType),
				Name = @event.Name?.String ?? string.Empty,
				EventType = @event.EventType?.FullName ?? string.Empty,
			};
		}

		public string ToCanonicalString() =>
			$"{DeclaringType.ToCanonicalString()}::{Name}:{EventType}";
		public override string ToString() => $"{DeclaringType}::{Name}";
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

	sealed class ILPatchPropertySignatureSnapshot {
		public bool HasThis { get; set; }
		public ILPatchTypeSigSnapshot ReturnType { get; set; } = null!;
		public List<ILPatchTypeSigSnapshot> Parameters { get; } = new List<ILPatchTypeSigSnapshot>();
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

	enum ILPatchTypeChangeKind {
		Modify,
		Add,
		Remove,
	}

	sealed class ILPatchTypeDefinitionSnapshot {
		public ILPatchTypeIdentity Identity { get; set; } = null!;
		public string Namespace { get; set; } = string.Empty;
		public string Name { get; set; } = string.Empty;
		public uint Attributes { get; set; }
		public ILPatchTypeSigSnapshot? BaseType { get; set; }
		public ILPatchTypeIdentity? DeclaringType { get; set; }
		public List<ILPatchTypeIdentity>? NestedTypes { get; set; }
		public List<ILPatchFieldDefinitionSnapshot> Fields { get; } = new List<ILPatchFieldDefinitionSnapshot>();
		public List<ILPatchMethodDefinitionSnapshot> Methods { get; } = new List<ILPatchMethodDefinitionSnapshot>();
		public List<ILPatchPropertyDefinitionSnapshot> Properties { get; } = new List<ILPatchPropertyDefinitionSnapshot>();
		public List<ILPatchEventDefinitionSnapshot> Events { get; } = new List<ILPatchEventDefinitionSnapshot>();
	}

	sealed class ILPatchPropertyDefinitionSnapshot {
		public ILPatchPropertyIdentity Identity { get; set; } = null!;
		public ushort Attributes { get; set; }
		public ILPatchPropertySignatureSnapshot Signature { get; set; } = null!;
		public List<ILPatchMethodIdentity> GetMethods { get; } = new List<ILPatchMethodIdentity>();
		public List<ILPatchMethodIdentity> SetMethods { get; } = new List<ILPatchMethodIdentity>();
		public List<ILPatchMethodIdentity> OtherMethods { get; } = new List<ILPatchMethodIdentity>();
	}

	sealed class ILPatchEventDefinitionSnapshot {
		public ILPatchEventIdentity Identity { get; set; } = null!;
		public ushort Attributes { get; set; }
		public ILPatchTypeSigSnapshot EventType { get; set; } = null!;
		public ILPatchMethodIdentity? AddMethod { get; set; }
		public ILPatchMethodIdentity? InvokeMethod { get; set; }
		public ILPatchMethodIdentity? RemoveMethod { get; set; }
		public List<ILPatchMethodIdentity> OtherMethods { get; } = new List<ILPatchMethodIdentity>();
	}

	/// <summary>
	/// Structural changes are grouped by declaring type. Existing method-body edits remain in
	/// ILPatchDocument.Methods; this collection represents member topology changes around them.
	/// </summary>
	sealed class ILPatchTypeChange {
		public string Id { get; set; } = Guid.NewGuid().ToString("N");
		public ILPatchTypeIdentity Target { get; set; } = null!;
		public ILPatchTypeChangeKind Kind { get; set; } = ILPatchTypeChangeKind.Modify;
		public ILPatchTypeDefinitionSnapshot? TypeDefinition { get; set; }
		public List<ILPatchFieldDefinitionSnapshot> AddedFields { get; } = new List<ILPatchFieldDefinitionSnapshot>();
		public List<ILPatchFieldIdentity> RemovedFields { get; } = new List<ILPatchFieldIdentity>();
		public List<ILPatchFieldDefinitionSnapshot> RemovedFieldBaselines { get; } = new List<ILPatchFieldDefinitionSnapshot>();
		public List<ILPatchMethodDefinitionSnapshot> AddedMethods { get; } = new List<ILPatchMethodDefinitionSnapshot>();
		public List<ILPatchMethodIdentity> RemovedMethods { get; } = new List<ILPatchMethodIdentity>();
		public List<ILPatchMethodDefinitionSnapshot> RemovedMethodBaselines { get; } = new List<ILPatchMethodDefinitionSnapshot>();
		public List<ILPatchPropertyDefinitionSnapshot> AddedProperties { get; } = new List<ILPatchPropertyDefinitionSnapshot>();
		public List<ILPatchPropertyIdentity> RemovedProperties { get; } = new List<ILPatchPropertyIdentity>();
		public List<ILPatchPropertyDefinitionSnapshot> RemovedPropertyBaselines { get; } = new List<ILPatchPropertyDefinitionSnapshot>();
		public List<ILPatchEventDefinitionSnapshot> AddedEvents { get; } = new List<ILPatchEventDefinitionSnapshot>();
		public List<ILPatchEventIdentity> RemovedEvents { get; } = new List<ILPatchEventIdentity>();
		public List<ILPatchEventDefinitionSnapshot> RemovedEventBaselines { get; } = new List<ILPatchEventDefinitionSnapshot>();

		public bool HasEffectiveChange =>
			Kind != ILPatchTypeChangeKind.Modify ||
			AddedFields.Count != 0 || RemovedFields.Count != 0 ||
			AddedMethods.Count != 0 || RemovedMethods.Count != 0 ||
			AddedProperties.Count != 0 || RemovedProperties.Count != 0 ||
			AddedEvents.Count != 0 || RemovedEvents.Count != 0;
	}


	static class ILPatchStructuralSnapshotComparer {
		public static bool AreEquivalent(ILPatchFieldDefinitionSnapshot expected,
			ILPatchFieldDefinitionSnapshot current, out string reason) =>
			Compare(FieldFingerprint(expected), FieldFingerprint(current),
				"The target field no longer matches the structural baseline captured by this patch.", out reason);

		public static bool AreEquivalent(ILPatchMethodDefinitionSnapshot expected,
			ILPatchMethodDefinitionSnapshot current, out string reason) =>
			Compare(MethodFingerprint(expected), MethodFingerprint(current),
				"The target method no longer matches the structural baseline captured by this patch.", out reason);

		public static bool AreEquivalent(ILPatchPropertyDefinitionSnapshot expected,
			ILPatchPropertyDefinitionSnapshot current, out string reason) =>
			Compare(PropertyFingerprint(expected), PropertyFingerprint(current),
				"The target property no longer matches the structural baseline captured by this patch.", out reason);

		public static bool AreEquivalent(ILPatchEventDefinitionSnapshot expected,
			ILPatchEventDefinitionSnapshot current, out string reason) =>
			Compare(EventFingerprint(expected), EventFingerprint(current),
				"The target event no longer matches the structural baseline captured by this patch.", out reason);

		public static bool AreEquivalent(ILPatchTypeDefinitionSnapshot expected,
			ILPatchTypeDefinitionSnapshot current, out string reason) {
			if (expected is null)
				throw new ArgumentNullException(nameof(expected));
			if (current is null)
				throw new ArgumentNullException(nameof(current));
			bool includeNestedTypes = expected.NestedTypes is not null;
			return Compare(TypeFingerprint(expected, includeNestedTypes), TypeFingerprint(current, includeNestedTypes),
				"The target type no longer matches the structural baseline captured by this patch.", out reason);
		}

		static bool Compare(string expected, string current, string mismatchReason, out string reason) {
			if (StringComparer.Ordinal.Equals(expected, current)) {
				reason = string.Empty;
				return true;
			}
			reason = mismatchReason;
			return false;
		}

		static string TypeFingerprint(ILPatchTypeDefinitionSnapshot type, bool includeNestedTypes) =>
			string.Join("|", new[] {
				type.Identity.ToCanonicalString(),
				type.Namespace ?? string.Empty,
				type.Name ?? string.Empty,
				type.Attributes.ToString(System.Globalization.CultureInfo.InvariantCulture),
				TypeSigFingerprint(type.BaseType),
				type.DeclaringType?.ToCanonicalString() ?? string.Empty,
				includeNestedTypes
					? JoinSorted((type.NestedTypes ?? new List<ILPatchTypeIdentity>()).Select(a => a.ToCanonicalString()))
					: string.Empty,
				JoinSorted(type.Fields.Select(FieldFingerprint)),
				JoinSorted(type.Methods.Select(MethodFingerprint)),
				JoinSorted(type.Properties.Select(PropertyFingerprint)),
				JoinSorted(type.Events.Select(EventFingerprint)),
			});

		static string FieldFingerprint(ILPatchFieldDefinitionSnapshot field) =>
			$"{field.Identity.ToCanonicalString()}|{field.Attributes}|{TypeSigFingerprint(field.FieldType)}";

		static string MethodFingerprint(ILPatchMethodDefinitionSnapshot method) =>
			$"{method.Identity.ToCanonicalString()}|{method.Attributes}|{method.ImplAttributes}|" +
			$"{MethodSigFingerprint(method.Signature)}|" +
			$"{string.Join(";", method.Parameters.OrderBy(a => a.Sequence).Select(a => $"{a.Sequence}:{a.Name}:{a.Attributes}"))}|" +
			$"{method.Body?.CanonicalHash ?? string.Empty}";

		static string PropertyFingerprint(ILPatchPropertyDefinitionSnapshot property) =>
			$"{property.Identity.ToCanonicalString()}|{property.Attributes}|{PropertySigFingerprint(property.Signature)}|" +
			$"get={JoinSorted(property.GetMethods.Select(a => a.ToCanonicalString()))}|" +
			$"set={JoinSorted(property.SetMethods.Select(a => a.ToCanonicalString()))}|" +
			$"other={JoinSorted(property.OtherMethods.Select(a => a.ToCanonicalString()))}";

		static string EventFingerprint(ILPatchEventDefinitionSnapshot @event) =>
			$"{@event.Identity.ToCanonicalString()}|{@event.Attributes}|{TypeSigFingerprint(@event.EventType)}|" +
			$"add={@event.AddMethod?.ToCanonicalString() ?? string.Empty}|" +
			$"invoke={@event.InvokeMethod?.ToCanonicalString() ?? string.Empty}|" +
			$"remove={@event.RemoveMethod?.ToCanonicalString() ?? string.Empty}|" +
			$"other={JoinSorted(@event.OtherMethods.Select(a => a.ToCanonicalString()))}";

		static string MethodSigFingerprint(ILPatchMethodSignatureSnapshot signature) =>
			$"{signature.CallingConvention}:{signature.GenericParameterCount}:{TypeSigFingerprint(signature.ReturnType)}:" +
			$"{string.Join(",", signature.Parameters.Select(TypeSigFingerprint))}:" +
			$"{string.Join(",", signature.ParametersAfterSentinel.Select(TypeSigFingerprint))}";

		static string PropertySigFingerprint(ILPatchPropertySignatureSnapshot signature) =>
			$"{signature.HasThis}:{TypeSigFingerprint(signature.ReturnType)}:" +
			$"{string.Join(",", signature.Parameters.Select(TypeSigFingerprint))}";

		static string TypeSigFingerprint(ILPatchTypeSigSnapshot? type) {
			if (type is null)
				return string.Empty;
			return $"{type.Kind}:{type.FullName}:{type.DefinitionAssembly}:{type.IsValueType}:{type.GenericIndex}:" +
				$"{type.ArrayRank}:{string.Join(",", type.ArraySizes)}:{string.Join(",", type.ArrayLowerBounds)}:" +
				$"elem=({TypeSigFingerprint(type.ElementType)}):mod=({TypeSigFingerprint(type.ModifierType)}):" +
				$"args=({string.Join(",", type.GenericArguments.Select(TypeSigFingerprint))})";
		}

		static string JoinSorted(IEnumerable<string> values) =>
			string.Join(";", values.OrderBy(a => a, StringComparer.Ordinal));
	}

	static class ILPatchStructuralSnapshotBuilder {

		public static bool TryCreateType(TypeDef type, out ILPatchTypeDefinitionSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (type is null) {
				error = "Type is missing.";
				return false;
			}
			if (type.IsGlobalModuleType) {
				error = "The global <Module> type cannot be structurally added or removed.";
				return false;
			}
			if (type.HasCustomAttributes || type.GenericParameters.Count != 0 ||
				type.Interfaces.Count != 0 || type.ClassLayout is not null ||
				type.DeclSecurities.Count != 0) {
				error = $"Type '{type.FullName}' uses custom attributes, generic parameters, interfaces, explicit layout or declarative security that structural patch v2 does not capture yet.";
				return false;
			}

			ILPatchTypeSigSnapshot? baseType = null;
			if (type.BaseType is not null) {
				TypeSig source;
				if (type.BaseType is TypeSpec typeSpec)
					source = typeSpec.TypeSig;
				else if (type.BaseType.ResolveTypeDef()?.IsValueType == true)
					source = new ValueTypeSig(type.BaseType);
				else
					source = new ClassSig(type.BaseType);
				if (!TryCreateTypeSig(source, out baseType, out error))
					return false;
			}

			var result = new ILPatchTypeDefinitionSnapshot {
				Identity = ILPatchTypeIdentity.Create(type),
				Namespace = type.Namespace?.String ?? string.Empty,
				Name = type.Name?.String ?? string.Empty,
				Attributes = (uint)type.Attributes,
				BaseType = baseType,
				DeclaringType = type.DeclaringType is null ? null : ILPatchTypeIdentity.Create(type.DeclaringType),
				NestedTypes = type.NestedTypes.Select(ILPatchTypeIdentity.Create).ToList(),
			};

			foreach (var field in type.Fields) {
				if (!TryCreateField(field, out var fieldSnapshot, out error) || fieldSnapshot is null)
					return false;
				result.Fields.Add(fieldSnapshot);
			}
			foreach (var method in type.Methods) {
				if (!TryCreateMethod(method, out var methodSnapshot, out error) || methodSnapshot is null)
					return false;
				result.Methods.Add(methodSnapshot);
			}
			foreach (var property in type.Properties) {
				if (!TryCreateProperty(property, out var propertySnapshot, out error) || propertySnapshot is null)
					return false;
				result.Properties.Add(propertySnapshot);
			}
			foreach (var @event in type.Events) {
				if (!TryCreateEvent(@event, out var eventSnapshot, out error) || eventSnapshot is null)
					return false;
				result.Events.Add(eventSnapshot);
			}

			snapshot = result;
			return true;
		}

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


		public static bool TryCreateProperty(PropertyDef property,
			out ILPatchPropertyDefinitionSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (property is null) {
				error = "Property is missing.";
				return false;
			}
			if (property.HasCustomAttributes || property.Constant is not null) {
				error = $"Property '{property.FullName}' uses custom attributes or a constant that structural patch v2 does not capture yet.";
				return false;
			}
			var propertySig = property.PropertySig;
			if (propertySig is null || propertySig.RetType is null) {
				error = $"Property '{property.FullName}' has no usable PropertySig.";
				return false;
			}
			if (propertySig.ExplicitThis) {
				error = $"Property '{property.FullName}' uses ExplicitThis, which structural patch v2 does not capture yet.";
				return false;
			}
			if (!TryCreateTypeSig(propertySig.RetType, out var returnType, out error))
				return false;
			var signature = new ILPatchPropertySignatureSnapshot {
				HasThis = propertySig.HasThis,
				ReturnType = returnType!,
			};
			foreach (var parameter in propertySig.Params) {
				if (!TryCreateTypeSig(parameter, out var parameterType, out error))
					return false;
				signature.Parameters.Add(parameterType!);
			}
			var result = new ILPatchPropertyDefinitionSnapshot {
				Identity = ILPatchPropertyIdentity.Create(property),
				Attributes = (ushort)property.Attributes,
				Signature = signature,
			};
			result.GetMethods.AddRange(property.GetMethods.Select(ILPatchMethodIdentity.Create));
			result.SetMethods.AddRange(property.SetMethods.Select(ILPatchMethodIdentity.Create));
			result.OtherMethods.AddRange(property.OtherMethods.Select(ILPatchMethodIdentity.Create));
			snapshot = result;
			return true;
		}

		public static bool TryCreateEvent(EventDef @event,
			out ILPatchEventDefinitionSnapshot? snapshot, out string error) {
			snapshot = null;
			error = string.Empty;
			if (@event is null) {
				error = "Event is missing.";
				return false;
			}
			if (@event.HasCustomAttributes) {
				error = $"Event '{@event.FullName}' uses custom attributes that structural patch v2 does not capture yet.";
				return false;
			}
			if (@event.EventType is null) {
				error = $"Event '{@event.FullName}' has no event type.";
				return false;
			}
			TypeSig eventTypeSig;
			if (@event.EventType is TypeSpec typeSpec)
				eventTypeSig = typeSpec.TypeSig;
			else if (@event.EventType.ResolveTypeDef()?.IsValueType == true)
				eventTypeSig = new ValueTypeSig(@event.EventType);
			else
				eventTypeSig = new ClassSig(@event.EventType);
			if (!TryCreateTypeSig(eventTypeSig, out var eventType, out error))
				return false;
			var result = new ILPatchEventDefinitionSnapshot {
				Identity = ILPatchEventIdentity.Create(@event),
				Attributes = (ushort)@event.Attributes,
				EventType = eventType!,
				AddMethod = @event.AddMethod is null ? null : ILPatchMethodIdentity.Create(@event.AddMethod),
				InvokeMethod = @event.InvokeMethod is null ? null : ILPatchMethodIdentity.Create(@event.InvokeMethod),
				RemoveMethod = @event.RemoveMethod is null ? null : ILPatchMethodIdentity.Create(@event.RemoveMethod),
			};
			result.OtherMethods.AddRange(@event.OtherMethods.Select(ILPatchMethodIdentity.Create));
			snapshot = result;
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
