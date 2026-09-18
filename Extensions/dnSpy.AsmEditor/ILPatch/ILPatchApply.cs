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
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Rebinds normalized ILPatch operands to dnlib objects that already exist in the target
	/// module. v1 deliberately fails closed when a reference cannot be resolved from existing
	/// metadata; it never guesses a metadata token or parses a FullName into a new MemberRef.
	/// </summary>
	sealed class ILPatchBodyMaterializer {
		static readonly Dictionary<string, OpCode> opCodes = CreateOpCodeMap();

		readonly ModuleDef module;
		readonly Dictionary<string, ITypeDefOrRef> types = new Dictionary<string, ITypeDefOrRef>(StringComparer.Ordinal);
		readonly Dictionary<string, TypeSig> typeSigs = new Dictionary<string, TypeSig>(StringComparer.Ordinal);
		readonly Dictionary<string, IMethod> methods = new Dictionary<string, IMethod>(StringComparer.Ordinal);
		readonly Dictionary<string, IField> fields = new Dictionary<string, IField>(StringComparer.Ordinal);

		public ILPatchBodyMaterializer(ModuleDef module) {
			this.module = module ?? throw new ArgumentNullException(nameof(module));
			IndexModule();
		}

		public bool TryCreate(MethodDef target, ILPatchMethodBodySnapshot snapshot, out CilBody? body, out string error) {
			body = null;
			error = string.Empty;
			if (target is null) {
				error = "Target method is missing.";
				return false;
			}
			if (snapshot is null) {
				error = "Patched method body is missing.";
				return false;
			}
			if (!ReferenceEquals(target.Module, module)) {
				error = "Target method belongs to a different module than this materializer.";
				return false;
			}
			if (target.Body is null) {
				error = "Target method no longer has a CIL body.";
				return false;
			}
			if (snapshot.MaxStack < 0 || snapshot.MaxStack > ushort.MaxValue) {
				error = $"Invalid MaxStack value {snapshot.MaxStack}.";
				return false;
			}

			var result = new CilBody {
				InitLocals = snapshot.InitLocals,
				MaxStack = (ushort)snapshot.MaxStack,
				KeepOldMaxStack = target.Body.KeepOldMaxStack,
			};

			for (int i = 0; i < snapshot.Locals.Count; i++) {
				if (!TryResolveTypeSig(target, snapshot.Locals[i], out var typeSig)) {
					error = $"Could not resolve local #{i} type '{snapshot.Locals[i]}' from existing target metadata.";
					return false;
				}
				result.Variables.Add(new Local(typeSig));
			}

			for (int i = 0; i < snapshot.Instructions.Count; i++) {
				var source = snapshot.Instructions[i];
				if (!opCodes.TryGetValue(source.OpCode ?? string.Empty, out var opCode)) {
					error = $"Instruction #{i} uses unknown opcode '{source.OpCode}'.";
					return false;
				}
				result.Instructions.Add(new Instruction(opCode));
			}

			for (int i = 0; i < snapshot.Instructions.Count; i++) {
				var source = snapshot.Instructions[i];
				var instruction = result.Instructions[i];
				if (!TryResolveOperand(target, result, instruction.OpCode, source.Operand, out var operand, out error)) {
					error = $"Instruction #{i} ({source.OpCode}): {error}";
					return false;
				}
				instruction.Operand = operand;
			}

			foreach (var source in snapshot.ExceptionHandlers) {
				if (!Enum.TryParse(source.HandlerType, false, out ExceptionHandlerType handlerType)) {
					error = $"Unknown exception handler type '{source.HandlerType}'.";
					return false;
				}

				if (!TryGetInstruction(result, source.TryStart, false, out var tryStart) ||
					!TryGetInstruction(result, source.TryEnd, true, out var tryEnd) ||
					!TryGetInstruction(result, source.HandlerStart, false, out var handlerStart) ||
					!TryGetInstruction(result, source.HandlerEnd, true, out var handlerEnd) ||
					!TryGetInstruction(result, source.FilterStart, true, out var filterStart)) {
					error = "Exception handler contains an invalid instruction index.";
					return false;
				}

				ITypeDefOrRef? catchType = null;
				if (!string.IsNullOrEmpty(source.CatchType) && !TryResolveType(target, source.CatchType, out catchType)) {
					error = $"Could not resolve exception catch type '{source.CatchType}' from existing target metadata.";
					return false;
				}

				result.ExceptionHandlers.Add(new ExceptionHandler(handlerType) {
					CatchType = catchType,
					TryStart = tryStart,
					TryEnd = tryEnd,
					HandlerStart = handlerStart,
					HandlerEnd = handlerEnd,
					FilterStart = filterStart,
				});
			}

			result.UpdateInstructionOffsets();
			body = result;
			return true;
		}

		bool TryResolveOperand(MethodDef target, CilBody body, OpCode opCode, ILPatchOperand operand, out object? value, out string error) {
			value = null;
			error = string.Empty;
			operand ??= ILPatchOperand.None;

			switch (operand.Kind) {
			case ILPatchOperandKind.None:
				if (opCode.OperandType != OperandType.InlineNone) {
					error = $"Missing operand for opcode operand type {opCode.OperandType}.";
					return false;
				}
				return true;

			case ILPatchOperandKind.Integer:
				try {
					switch (opCode.OperandType) {
					case OperandType.ShortInlineI:
						value = StringComparer.Ordinal.Equals(opCode.Name, "unaligned.")
							? (object)checked((byte)operand.IntegerValue)
							: checked((sbyte)operand.IntegerValue);
						return true;
					case OperandType.InlineI:
						value = checked((int)operand.IntegerValue);
						return true;
					case OperandType.InlineI8:
						value = operand.IntegerValue;
						return true;
					default:
						error = $"Integer operand is incompatible with opcode operand type {opCode.OperandType}.";
						return false;
					}
				}
				catch (OverflowException) {
					error = $"Integer value {operand.IntegerValue} does not fit opcode operand type {opCode.OperandType}.";
					return false;
				}

			case ILPatchOperandKind.FloatingPoint:
				if (opCode.OperandType == OperandType.ShortInlineR) {
					value = (float)operand.FloatingPointValue;
					return true;
				}
				if (opCode.OperandType == OperandType.InlineR) {
					value = operand.FloatingPointValue;
					return true;
				}
				error = $"Floating-point operand is incompatible with opcode operand type {opCode.OperandType}.";
				return false;

			case ILPatchOperandKind.String:
				if (opCode.OperandType != OperandType.InlineString) {
					error = $"String operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				value = operand.Text ?? string.Empty;
				return true;

			case ILPatchOperandKind.Argument:
				if (opCode.OperandType != OperandType.ShortInlineVar && opCode.OperandType != OperandType.InlineVar) {
					error = $"Argument operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				if ((uint)operand.Index >= (uint)target.Parameters.Count) {
					error = $"Argument index {operand.Index} is outside target parameter range.";
					return false;
				}
				value = target.Parameters[operand.Index];
				return true;

			case ILPatchOperandKind.Local:
				if (opCode.OperandType != OperandType.ShortInlineVar && opCode.OperandType != OperandType.InlineVar) {
					error = $"Local operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				if ((uint)operand.Index >= (uint)body.Variables.Count) {
					error = $"Local index {operand.Index} is outside patched local range.";
					return false;
				}
				value = body.Variables[operand.Index];
				return true;

			case ILPatchOperandKind.BranchTarget:
				if (opCode.OperandType != OperandType.ShortInlineBrTarget && opCode.OperandType != OperandType.InlineBrTarget) {
					error = $"Branch operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				if (!TryGetInstruction(body, operand.Index, false, out var branchTarget)) {
					error = $"Branch target index {operand.Index} is invalid.";
					return false;
				}
				value = branchTarget;
				return true;

			case ILPatchOperandKind.SwitchTargets:
				if (opCode.OperandType != OperandType.InlineSwitch) {
					error = $"Switch operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				var indices = operand.Indices ?? Array.Empty<int>();
				var targets = new Instruction[indices.Length];
				for (int i = 0; i < indices.Length; i++) {
					if (!TryGetInstruction(body, indices[i], false, out var switchTarget)) {
						error = $"Switch target index {indices[i]} is invalid.";
						return false;
					}
					targets[i] = switchTarget!;
				}
				value = targets;
				return true;

			case ILPatchOperandKind.Type:
				if (opCode.OperandType != OperandType.InlineType && opCode.OperandType != OperandType.InlineTok) {
					error = $"Type operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				if (!TryResolveType(target, operand.Text, out var type)) {
					error = $"Could not resolve type '{operand.Text}' from existing target metadata.";
					return false;
				}
				value = type;
				return true;

			case ILPatchOperandKind.Method:
				if (opCode.OperandType != OperandType.InlineMethod && opCode.OperandType != OperandType.InlineTok) {
					error = $"Method operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				if (!TryResolveMethod(target, operand.Text, out var method)) {
					error = $"Could not resolve method '{operand.Text}' from existing target metadata.";
					return false;
				}
				value = method;
				return true;

			case ILPatchOperandKind.Field:
				if (opCode.OperandType != OperandType.InlineField && opCode.OperandType != OperandType.InlineTok) {
					error = $"Field operand is incompatible with opcode operand type {opCode.OperandType}.";
					return false;
				}
				if (!TryResolveField(target, operand.Text, out var field)) {
					error = $"Could not resolve field '{operand.Text}' from existing target metadata.";
					return false;
				}
				value = field;
				return true;

			case ILPatchOperandKind.Signature:
			case ILPatchOperandKind.Other:
			default:
				error = $"Operand kind {operand.Kind} is not materializable in ILPatch v1.";
				return false;
			}
		}

		bool TryResolveMethod(MethodDef target, string? fullName, out IMethod? method) {
			if (target.Body is not null) {
				foreach (var instruction in target.Body.Instructions) {
					if (instruction.Operand is IMethod candidate && StringComparer.Ordinal.Equals(candidate.FullName, fullName)) {
						method = candidate;
						return true;
					}
				}
			}
			return methods.TryGetValue(fullName ?? string.Empty, out method);
		}

		bool TryResolveField(MethodDef target, string? fullName, out IField? field) {
			if (target.Body is not null) {
				foreach (var instruction in target.Body.Instructions) {
					if (instruction.Operand is IField candidate && StringComparer.Ordinal.Equals(candidate.FullName, fullName)) {
						field = candidate;
						return true;
					}
				}
			}
			return fields.TryGetValue(fullName ?? string.Empty, out field);
		}

		bool TryResolveType(MethodDef target, string? fullName, out ITypeDefOrRef? type) {
			if (target.Body is not null) {
				foreach (var instruction in target.Body.Instructions) {
					if (instruction.Operand is ITypeDefOrRef candidate && StringComparer.Ordinal.Equals(candidate.FullName, fullName)) {
						type = candidate;
						return true;
					}
				}
				foreach (var handler in target.Body.ExceptionHandlers) {
					if (handler.CatchType is not null && StringComparer.Ordinal.Equals(handler.CatchType.FullName, fullName)) {
						type = handler.CatchType;
						return true;
					}
				}
			}
			return types.TryGetValue(fullName ?? string.Empty, out type);
		}

		bool TryResolveTypeSig(MethodDef target, string? fullName, out TypeSig typeSig) {
			if (target.Body is not null) {
				foreach (var local in target.Body.Variables) {
					if (StringComparer.Ordinal.Equals(local.Type?.FullName, fullName) && local.Type is not null) {
						typeSig = local.Type;
						return true;
					}
				}
			}
			foreach (var parameter in target.Parameters) {
				if (StringComparer.Ordinal.Equals(parameter.Type?.FullName, fullName) && parameter.Type is not null) {
					typeSig = parameter.Type;
					return true;
				}
			}
			if (StringComparer.Ordinal.Equals(target.ReturnType?.FullName, fullName) && target.ReturnType is not null) {
				typeSig = target.ReturnType;
				return true;
			}
			if (typeSigs.TryGetValue(fullName ?? string.Empty, out var resolved)) {
				typeSig = resolved;
				return true;
			}
			typeSig = null!;
			return false;
		}

		void IndexModule() {
			AddTypeSig(module.CorLibTypes.Void);
			AddTypeSig(module.CorLibTypes.Boolean);
			AddTypeSig(module.CorLibTypes.Char);
			AddTypeSig(module.CorLibTypes.SByte);
			AddTypeSig(module.CorLibTypes.Byte);
			AddTypeSig(module.CorLibTypes.Int16);
			AddTypeSig(module.CorLibTypes.UInt16);
			AddTypeSig(module.CorLibTypes.Int32);
			AddTypeSig(module.CorLibTypes.UInt32);
			AddTypeSig(module.CorLibTypes.Int64);
			AddTypeSig(module.CorLibTypes.UInt64);
			AddTypeSig(module.CorLibTypes.Single);
			AddTypeSig(module.CorLibTypes.Double);
			AddTypeSig(module.CorLibTypes.String);
			AddTypeSig(module.CorLibTypes.TypedReference);
			AddTypeSig(module.CorLibTypes.IntPtr);
			AddTypeSig(module.CorLibTypes.UIntPtr);
			AddTypeSig(module.CorLibTypes.Object);

			foreach (var type in module.GetTypes()) {
				AddType(type);
				foreach (var field in type.Fields) {
					AddField(field);
					AddTypeSig(field.FieldType);
				}
				foreach (var method in type.Methods) {
					AddMethod(method);
					AddTypeSig(method.ReturnType);
					foreach (var parameter in method.Parameters)
						AddTypeSig(parameter.Type);
					if (method.Body is null)
						continue;
					foreach (var local in method.Body.Variables)
						AddTypeSig(local.Type);
					foreach (var handler in method.Body.ExceptionHandlers)
						AddType(handler.CatchType);
					foreach (var instruction in method.Body.Instructions)
						IndexOperand(instruction.Operand);
				}
			}
		}

		void IndexOperand(object? operand) {
			if (operand is ITypeDefOrRef type)
				AddType(type);
			else if (operand is IMethod method)
				AddMethod(method);
			else if (operand is IField field)
				AddField(field);
		}

		void AddType(ITypeDefOrRef? type) {
			if (type is null || string.IsNullOrEmpty(type.FullName))
				return;
			if (!types.ContainsKey(type.FullName))
				types.Add(type.FullName, type);
		}

		void AddTypeSig(TypeSig? typeSig) {
			if (typeSig is null || string.IsNullOrEmpty(typeSig.FullName))
				return;
			if (!typeSigs.ContainsKey(typeSig.FullName))
				typeSigs.Add(typeSig.FullName, typeSig);
		}

		void AddMethod(IMethod? method) {
			if (method is null || string.IsNullOrEmpty(method.FullName))
				return;
			if (!methods.ContainsKey(method.FullName))
				methods.Add(method.FullName, method);
			AddType(method.DeclaringType);
		}

		void AddField(IField? field) {
			if (field is null || string.IsNullOrEmpty(field.FullName))
				return;
			if (!fields.ContainsKey(field.FullName))
				fields.Add(field.FullName, field);
			AddType(field.DeclaringType);
		}

		static bool TryGetInstruction(CilBody body, int index, bool allowEnd, out Instruction? instruction) {
			if (allowEnd && index == -1) {
				instruction = null;
				return true;
			}
			if ((uint)index >= (uint)body.Instructions.Count) {
				instruction = null;
				return false;
			}
			instruction = body.Instructions[index];
			return true;
		}

		static Dictionary<string, OpCode> CreateOpCodeMap() {
			var result = new Dictionary<string, OpCode>(StringComparer.Ordinal);
			foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
				if (field.FieldType != typeof(OpCode))
					continue;
				var opCode = (OpCode)field.GetValue(null)!;
				if (!string.IsNullOrEmpty(opCode.Name))
					result[opCode.Name] = opCode;
			}
			return result;
		}
	}

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
