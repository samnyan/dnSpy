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
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Converts dnlib CIL into a representation that is intentionally independent from
	/// instruction offsets, metadata RIDs/tokens and short/long opcode encodings.
	/// </summary>
	static class CilNormalizer {
		public static ILPatchMethodBodySnapshot CreateSnapshot(MethodDef method) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (method.Body is null)
				throw new ArgumentException("Method has no CIL body", nameof(method));

			var body = method.Body;
			var instructionIndices = new Dictionary<Instruction, int>();
			for (int i = 0; i < body.Instructions.Count; i++)
				instructionIndices[body.Instructions[i]] = i;

			var snapshot = new ILPatchMethodBodySnapshot {
				Method = ILPatchMethodIdentity.Create(method),
				InitLocals = body.InitLocals,
				MaxStack = body.MaxStack,
			};

			foreach (var local in body.Variables)
				snapshot.Locals.Add(local.Type?.FullName ?? string.Empty);

			foreach (var instruction in body.Instructions)
				snapshot.Instructions.Add(NormalizeInstruction(method, body, instruction, instructionIndices));

			foreach (var handler in body.ExceptionHandlers) {
				snapshot.ExceptionHandlers.Add(new ILPatchExceptionHandler {
					HandlerType = handler.HandlerType.ToString(),
					CatchType = handler.CatchType?.FullName,
					TryStart = GetInstructionIndex(instructionIndices, handler.TryStart),
					TryEnd = GetInstructionIndex(instructionIndices, handler.TryEnd),
					HandlerStart = GetInstructionIndex(instructionIndices, handler.HandlerStart),
					HandlerEnd = GetInstructionIndex(instructionIndices, handler.HandlerEnd),
					FilterStart = GetInstructionIndex(instructionIndices, handler.FilterStart),
				});
			}

			snapshot.CanonicalHash = ComputeCanonicalHash(snapshot);
			return snapshot;
		}

		static ILPatchInstruction NormalizeInstruction(MethodDef method, CilBody body, Instruction instruction, Dictionary<Instruction, int> instructionIndices) {
			string opCode = instruction.OpCode.Name;
			object? operand = instruction.Operand;

			// Normalize the dedicated constant opcodes to a single ldc.i4 form.
			if (TryGetLdcI4Constant(opCode, operand, out int constant)) {
				return new ILPatchInstruction {
					OpCode = "ldc.i4",
					Operand = new ILPatchOperand { Kind = ILPatchOperandKind.Integer, IntegerValue = constant },
				};
			}

			// Normalize implicit argument/local forms (ldarg.0, stloc.2, ...).
			if (TryGetImplicitIndex(opCode, "ldarg.", out int argumentIndex))
				return CreateIndexedInstruction("ldarg", ILPatchOperandKind.Argument, argumentIndex);
			if (TryGetImplicitIndex(opCode, "ldloc.", out int localIndex))
				return CreateIndexedInstruction("ldloc", ILPatchOperandKind.Local, localIndex);
			if (TryGetImplicitIndex(opCode, "stloc.", out localIndex))
				return CreateIndexedInstruction("stloc", ILPatchOperandKind.Local, localIndex);

			if (StringComparer.Ordinal.Equals(opCode, "ldarg.s") || StringComparer.Ordinal.Equals(opCode, "ldarg"))
				opCode = "ldarg";
			else if (StringComparer.Ordinal.Equals(opCode, "ldarga.s") || StringComparer.Ordinal.Equals(opCode, "ldarga"))
				opCode = "ldarga";
			else if (StringComparer.Ordinal.Equals(opCode, "starg.s") || StringComparer.Ordinal.Equals(opCode, "starg"))
				opCode = "starg";
			else if (StringComparer.Ordinal.Equals(opCode, "ldloc.s") || StringComparer.Ordinal.Equals(opCode, "ldloc"))
				opCode = "ldloc";
			else if (StringComparer.Ordinal.Equals(opCode, "ldloca.s") || StringComparer.Ordinal.Equals(opCode, "ldloca"))
				opCode = "ldloca";
			else if (StringComparer.Ordinal.Equals(opCode, "stloc.s") || StringComparer.Ordinal.Equals(opCode, "stloc"))
				opCode = "stloc";

			// Branch target distance is not semantic. br.s / br, leave.s / leave, etc. are equal.
			if (operand is Instruction && opCode.EndsWith(".s", StringComparison.Ordinal))
				opCode = opCode.Substring(0, opCode.Length - 2);

			return new ILPatchInstruction {
				OpCode = opCode,
				Operand = NormalizeOperand(method, body, operand, instructionIndices),
			};
		}

		static ILPatchInstruction CreateIndexedInstruction(string opCode, ILPatchOperandKind kind, int index) =>
			new ILPatchInstruction {
				OpCode = opCode,
				Operand = new ILPatchOperand { Kind = kind, Index = index },
			};

		static ILPatchOperand NormalizeOperand(MethodDef method, CilBody body, object? operand, Dictionary<Instruction, int> instructionIndices) {
			if (operand is null)
				return ILPatchOperand.None;

			if (operand is Instruction target)
				return new ILPatchOperand { Kind = ILPatchOperandKind.BranchTarget, Index = GetInstructionIndex(instructionIndices, target) };

			if (operand is IList<Instruction> targets) {
				var indices = new int[targets.Count];
				for (int i = 0; i < targets.Count; i++)
					indices[i] = GetInstructionIndex(instructionIndices, targets[i]);
				return new ILPatchOperand { Kind = ILPatchOperandKind.SwitchTargets, Indices = indices };
			}

			if (operand is Parameter parameter)
				return new ILPatchOperand { Kind = ILPatchOperandKind.Argument, Index = GetParameterIndex(method, parameter) };

			if (operand is Local local)
				return new ILPatchOperand { Kind = ILPatchOperandKind.Local, Index = GetLocalIndex(body, local) };

			if (operand is string text)
				return new ILPatchOperand { Kind = ILPatchOperandKind.String, Text = text };

			if (operand is sbyte sb)
				return Integer(sb);
			if (operand is byte b)
				return Integer(b);
			if (operand is short s)
				return Integer(s);
			if (operand is ushort us)
				return Integer(us);
			if (operand is int i32)
				return Integer(i32);
			if (operand is uint ui32)
				return Integer(ui32);
			if (operand is long i64)
				return Integer(i64);

			if (operand is float f32)
				return FloatingPoint(f32);
			if (operand is double f64)
				return FloatingPoint(f64);

			// FullName is used instead of a metadata token. Tokens/RIDs are expected to change after recompilation.
			if (operand is ITypeDefOrRef type)
				return new ILPatchOperand { Kind = ILPatchOperandKind.Type, Text = type.FullName };
			if (operand is IMethod calledMethod)
				return new ILPatchOperand { Kind = ILPatchOperandKind.Method, Text = calledMethod.FullName };
			if (operand is IField field)
				return new ILPatchOperand { Kind = ILPatchOperandKind.Field, Text = field.FullName };
			if (operand is CallingConventionSig signature)
				return new ILPatchOperand { Kind = ILPatchOperandKind.Signature, Text = signature.ToString() };

			return new ILPatchOperand { Kind = ILPatchOperandKind.Other, Text = operand.ToString() };
		}

		static ILPatchOperand Integer(long value) => new ILPatchOperand { Kind = ILPatchOperandKind.Integer, IntegerValue = value };
		static ILPatchOperand FloatingPoint(double value) => new ILPatchOperand { Kind = ILPatchOperandKind.FloatingPoint, FloatingPointValue = value };

		static bool TryGetLdcI4Constant(string opCode, object? operand, out int value) {
			value = 0;
			switch (opCode) {
			case "ldc.i4.m1": value = -1; return true;
			case "ldc.i4.0": value = 0; return true;
			case "ldc.i4.1": value = 1; return true;
			case "ldc.i4.2": value = 2; return true;
			case "ldc.i4.3": value = 3; return true;
			case "ldc.i4.4": value = 4; return true;
			case "ldc.i4.5": value = 5; return true;
			case "ldc.i4.6": value = 6; return true;
			case "ldc.i4.7": value = 7; return true;
			case "ldc.i4.8": value = 8; return true;
			case "ldc.i4.s":
				if (operand is sbyte sb) { value = sb; return true; }
				if (operand is byte b) { value = b; return true; }
				break;
			case "ldc.i4":
				if (operand is int i32) { value = i32; return true; }
				break;
			}
			return false;
		}

		static bool TryGetImplicitIndex(string opCode, string prefix, out int index) {
			index = -1;
			if (!opCode.StartsWith(prefix, StringComparison.Ordinal))
				return false;
			var suffix = opCode.Substring(prefix.Length);
			if (suffix.Length != 1 || suffix[0] < '0' || suffix[0] > '3')
				return false;
			index = suffix[0] - '0';
			return true;
		}

		static int GetParameterIndex(MethodDef method, Parameter parameter) {
			for (int i = 0; i < method.Parameters.Count; i++) {
				if (ReferenceEquals(method.Parameters[i], parameter))
					return i;
			}
			return -1;
		}

		static int GetLocalIndex(CilBody body, Local local) {
			for (int i = 0; i < body.Variables.Count; i++) {
				if (ReferenceEquals(body.Variables[i], local))
					return i;
			}
			return -1;
		}

		static int GetInstructionIndex(Dictionary<Instruction, int> indices, Instruction? instruction) {
			if (instruction is null)
				return -1;
			return indices.TryGetValue(instruction, out int index) ? index : -1;
		}

		static string ComputeCanonicalHash(ILPatchMethodBodySnapshot snapshot) {
			var builder = new StringBuilder();
			builder.AppendLine(snapshot.Method.ToCanonicalString());
			builder.Append("initlocals=").Append(snapshot.InitLocals).Append("|maxstack=").Append(snapshot.MaxStack).AppendLine();
			for (int i = 0; i < snapshot.Locals.Count; i++)
				builder.Append("local|").Append(i).Append('|').AppendLine(snapshot.Locals[i]);
			for (int i = 0; i < snapshot.Instructions.Count; i++)
				builder.Append("il|").Append(i).Append('|').AppendLine(snapshot.Instructions[i].ToCanonicalString());
			for (int i = 0; i < snapshot.ExceptionHandlers.Count; i++)
				builder.Append("eh|").Append(i).Append('|').AppendLine(snapshot.ExceptionHandlers[i].ToCanonicalString());

			byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
			using (var sha256 = SHA256.Create()) {
				var hash = sha256.ComputeHash(bytes);
				var hex = new StringBuilder(hash.Length * 2);
				foreach (byte b in hash)
					hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
				return hex.ToString();
			}
		}
	}
}
