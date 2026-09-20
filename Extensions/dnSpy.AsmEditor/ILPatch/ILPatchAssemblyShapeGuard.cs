/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.AsmEditor.ILPatch {
	sealed class ILPatchUnsupportedWorkingChange {
		public string Kind { get; }
		public string Target { get; }
		public string Detail { get; }

		public ILPatchUnsupportedWorkingChange(string kind, string target, string detail) {
			Kind = kind;
			Target = target;
			Detail = detail;
		}

		public override string ToString() =>
			string.IsNullOrEmpty(Detail) ? $"{Kind}: {Target}" : $"{Kind}: {Target} ({Detail})";
	}

	/// <summary>
	/// v1 repository safety boundary. ILPatch can faithfully persist existing CIL method-body
	/// changes only, so every other assembly-shape mutation must be detected before commit.
	/// </summary>
	static class ILPatchAssemblyShapeGuard {
		sealed class ShapeEntry {
			public string Kind { get; }
			public string Target { get; }
			public string Value { get; }

			public ShapeEntry(string kind, string target, string value) {
				Kind = kind;
				Target = target;
				Value = value;
			}
		}

		public static string ComputeFingerprint(ModuleDef module) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			var entries = Build(module);
			var lines = entries
				.OrderBy(a => a.Key, StringComparer.Ordinal)
				.Select(a => a.Key + "=" + a.Value.Value);
			return Sha256Hex(string.Join("\n", lines));
		}

		public static IReadOnlyList<ILPatchUnsupportedWorkingChange> Compare(ModuleDef baseline, ModuleDef current) {
			if (baseline is null)
				throw new ArgumentNullException(nameof(baseline));
			if (current is null)
				throw new ArgumentNullException(nameof(current));

			var left = Build(baseline);
			var right = Build(current);
			var keys = new SortedSet<string>(left.Keys, StringComparer.Ordinal);
			keys.UnionWith(right.Keys);
			var result = new List<ILPatchUnsupportedWorkingChange>();
			foreach (string key in keys) {
				bool hasLeft = left.TryGetValue(key, out var before);
				bool hasRight = right.TryGetValue(key, out var after);
				if (!hasLeft) {
					result.Add(new ILPatchUnsupportedWorkingChange(
						(after ?? throw new InvalidOperationException()).Kind + " added",
						after.Target, string.Empty));
				}
				else if (!hasRight) {
					result.Add(new ILPatchUnsupportedWorkingChange(
						(before ?? throw new InvalidOperationException()).Kind + " removed",
						before.Target, string.Empty));
				}
				else if (!StringComparer.Ordinal.Equals(before!.Value, after!.Value)) {
					result.Add(new ILPatchUnsupportedWorkingChange(
						before.Kind + " modified", before.Target, DescribeValueChange(before.Value, after.Value)));
				}
			}
			return result;
		}

		public static void ThrowIfUnsupported(ModuleDef baseline, ModuleDef current) {
			var changes = Compare(baseline, current);
			if (changes.Count == 0)
				return;
			const int maxShown = 12;
			string details = string.Join("\n", changes.Take(maxShown).Select(a => "  - " + a));
			if (changes.Count > maxShown)
				details += $"\n  - ... and {changes.Count - maxShown} more";
			throw new InvalidOperationException(
				$"Working tree contains {changes.Count} unsupported metadata/structure change(s). " +
				"Repository v1 can commit existing CIL method-body changes only.\n" + details);
		}

		static Dictionary<string, ShapeEntry> Build(ModuleDef module) {
			var result = new Dictionary<string, ShapeEntry>(StringComparer.Ordinal);
			void Add(string key, string kind, string target, string value) {
				if (result.ContainsKey(key))
					throw new InvalidOperationException($"Assembly shape identity '{key}' is ambiguous.");
				result.Add(key, new ShapeEntry(kind, target, value));
			}

			var assembly = module.Assembly;
			Add("assembly", "Assembly metadata", assembly?.FullName ?? "<module>",
				$"attrs={(uint)(assembly?.Attributes ?? 0):X8}|hash={(uint)(assembly?.HashAlgorithm ?? 0):X8}|ca={SerializeCustomAttributes(assembly?.CustomAttributes)}");
			Add("module", "Module metadata", module.Name?.String ?? string.Empty,
				$"runtime={module.RuntimeVersion}|kind={module.Kind}|char={(uint)module.Characteristics:X8}|dll={(uint)module.DllCharacteristics:X8}|machine={(uint)module.Machine:X8}|ca={SerializeCustomAttributes(module.CustomAttributes)}");

			foreach (var type in module.GetTypes()) {
				string typeName = type.FullName;
				Add("type|" + typeName, "Type", typeName,
					$"attrs={(uint)type.Attributes:X8}|base={type.BaseType?.FullName ?? string.Empty}|layout={type.ClassLayout?.PackingSize}:{type.ClassLayout?.ClassSize}|gp={SerializeGenericParameters(type.GenericParameters)}|ifaces={string.Join(",", type.Interfaces.Select(a => a.Interface?.FullName ?? string.Empty).OrderBy(a => a, StringComparer.Ordinal))}|ca={SerializeCustomAttributes(type.CustomAttributes)}");

				foreach (var field in type.Fields) {
					string target = field.FullName;
					Add("field|" + target, "Field", target,
						$"attrs={(ushort)field.Attributes:X4}|const={SerializeConstant(field.Constant)}|initial={Hex(field.InitialValue)}|ca={SerializeCustomAttributes(field.CustomAttributes)}");
				}
				foreach (var property in type.Properties) {
					string target = property.FullName;
					Add("property|" + target, "Property", target,
						$"attrs={(ushort)property.Attributes:X4}|const={SerializeConstant(property.Constant)}|ca={SerializeCustomAttributes(property.CustomAttributes)}");
				}
				foreach (var @event in type.Events) {
					string target = @event.FullName;
					Add("event|" + target, "Event", target,
						$"attrs={(ushort)@event.Attributes:X4}|type={@event.EventType?.FullName ?? string.Empty}|ca={SerializeCustomAttributes(@event.CustomAttributes)}");
				}
				foreach (var method in type.Methods) {
					string target = ILPatchMethodIdentity.Create(method).ToCanonicalString();
					Add("method|" + target, "Method metadata", method.FullName,
						$"attrs={(ushort)method.Attributes:X4}|impl={(ushort)method.ImplAttributes:X4}|gp={SerializeGenericParameters(method.GenericParameters)}|params={SerializeParams(method.ParamDefs)}|ca={SerializeCustomAttributes(method.CustomAttributes)}");
				}
			}

			foreach (var resource in module.Resources) {
				string name = resource.Name?.String ?? string.Empty;
				Add("resource|" + name, "Resource", name,
					$"type={resource.ResourceType}|attrs={(uint)resource.Attributes:X8}|payload={SerializeResource(resource)}|ca={SerializeCustomAttributes(resource.CustomAttributes)}");
			}
			return result;
		}

		static string SerializeResource(Resource resource) {
			if (resource is EmbeddedResource embedded) {
				var reader = embedded.CreateReader();
				if (reader.Length > int.MaxValue)
					throw new InvalidOperationException($"Embedded resource '{resource.Name}' is too large to fingerprint.");
				using var sha = SHA256.Create();
				return "sha256:" + Hex(sha.ComputeHash(reader.ReadBytes((int)reader.Length)));
			}
			if (resource is AssemblyLinkedResource assemblyLinked)
				return "assembly:" + (assemblyLinked.Assembly?.FullName ?? string.Empty);
			if (resource is LinkedResource linked)
				return "file:" + (linked.FileName?.String ?? string.Empty) + "|hash=" + Hex(linked.Hash);
			return resource.ToString() ?? string.Empty;
		}

		static string SerializeParams(IList<ParamDef> parameters) =>
			string.Join(";", parameters
				.OrderBy(a => a.Sequence)
				.Select(a => $"{a.Sequence}:{a.Name}:{(ushort)a.Flags:X4}:{SerializeConstant(a.Constant)}:{SerializeCustomAttributes(a.CustomAttributes)}"));

		static string SerializeGenericParameters(IList<GenericParam> parameters) =>
			string.Join(";", parameters
				.OrderBy(a => a.Number)
				.Select(a => $"{a.Number}:{a.Name}:{(ushort)a.Attributes:X4}:" +
					string.Join(",", a.GenericParamConstraints.Select(c => c.Constraint?.FullName ?? string.Empty).OrderBy(v => v, StringComparer.Ordinal)) +
					":" + SerializeCustomAttributes(a.CustomAttributes)));

		static string SerializeConstant(Constant? constant) =>
			constant is null ? string.Empty : $"{constant.Type}:{SerializeValue(constant.Value)}";

		static string SerializeCustomAttributes(IList<CustomAttribute>? attributes) {
			if (attributes is null || attributes.Count == 0)
				return string.Empty;
			return string.Join(";", attributes.Select(SerializeCustomAttribute).OrderBy(a => a, StringComparer.Ordinal));
		}

		static string SerializeCustomAttribute(CustomAttribute attribute) {
			if (attribute.RawData is not null)
				return attribute.TypeFullName + "|raw=" + Hex(attribute.RawData);
			string ctor = attribute.Constructor?.ToString() ?? string.Empty;
			string args = string.Join(",", attribute.ConstructorArguments.Select(SerializeArgument));
			string named = string.Join(",", attribute.NamedArguments
				.Select(a => $"{(a.IsField ? "field" : "property")}:{a.Name}:{a.Type}:{SerializeArgument(a.Argument)}")
				.OrderBy(a => a, StringComparer.Ordinal));
			return $"{attribute.TypeFullName}|ctor={ctor}|args={args}|named={named}";
		}

		static string SerializeArgument(CAArgument argument) =>
			$"{argument.Type}:{SerializeValue(argument.Value)}";

		static string SerializeValue(object? value) {
			if (value is null)
				return "null";
			if (value is byte[] bytes)
				return "bytes:" + Hex(bytes);
			if (value is CAArgument argument)
				return SerializeArgument(argument);
			if (value is IList<CAArgument> arguments)
				return "[" + string.Join(",", arguments.Select(SerializeArgument)) + "]";
			if (value is UTF8String utf8)
				return utf8.String ?? string.Empty;
			if (value is IFormattable formattable)
				return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
			if (value is IEnumerable enumerable && value is not string) {
				var items = new List<string>();
				foreach (object? item in enumerable)
					items.Add(SerializeValue(item));
				return "[" + string.Join(",", items) + "]";
			}
			return value.ToString() ?? string.Empty;
		}

		static string DescribeValueChange(string before, string after) {
			string Short(string value) => value.Length <= 180 ? value : value.Substring(0, 177) + "...";
			return $"before={Short(before)}; after={Short(after)}";
		}

		static string Hex(byte[]? bytes) =>
			bytes is null || bytes.Length == 0 ? string.Empty : string.Concat(bytes.Select(a => a.ToString("x2")));

		static string Sha256Hex(string value) {
			using var sha = SHA256.Create();
			return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
		}
	}
}
