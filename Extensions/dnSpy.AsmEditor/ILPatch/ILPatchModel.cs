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
	/// Versioned in-memory representation of an .ilpatch file.
	///
	/// v1 deliberately stores both the baseline and patched method bodies. The duplicated
	/// data makes three-way rebasing possible later without depending on the original DLL.
	/// </summary>
	sealed class ILPatchDocument {
		public const int CurrentFormatVersion = 1;

		public int FormatVersion { get; set; } = CurrentFormatVersion;
		public string Name { get; set; } = string.Empty;
		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public List<ILPatchMethodChange> Methods { get; } = new List<ILPatchMethodChange>();
	}

	sealed class ILPatchMethodChange {
		public string Id { get; set; } = Guid.NewGuid().ToString("N");
		public ILPatchMethodIdentity Target { get; set; } = null!;
		public Guid BaseModuleMvid { get; set; }
		public ILPatchMethodBodySnapshot BaseBody { get; set; } = null!;
		public ILPatchMethodBodySnapshot PatchedBody { get; set; } = null!;

		public bool HasEffectiveChange =>
			!StringComparer.Ordinal.Equals(BaseBody.CanonicalHash, PatchedBody.CanonicalHash);
	}

	sealed class ILPatchMethodIdentity {
		public string AssemblyName { get; set; } = string.Empty;
		public string ModuleName { get; set; } = string.Empty;
		public string DeclaringType { get; set; } = string.Empty;
		public string MethodName { get; set; } = string.Empty;
		public string ReturnType { get; set; } = string.Empty;
		public List<string> ParameterTypes { get; } = new List<string>();
		public int GenericArity { get; set; }
		public bool HasThis { get; set; }

		public static ILPatchMethodIdentity Create(MethodDef method) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));

			var identity = new ILPatchMethodIdentity {
				AssemblyName = method.Module?.Assembly?.Name?.String ?? string.Empty,
				ModuleName = method.Module?.Name?.String ?? string.Empty,
				DeclaringType = method.DeclaringType?.FullName ?? string.Empty,
				MethodName = method.Name?.String ?? string.Empty,
				ReturnType = method.ReturnType?.FullName ?? string.Empty,
				GenericArity = method.GenericParameters.Count,
				HasThis = method.MethodSig?.HasThis == true,
			};

			foreach (var parameter in method.Parameters.Where(a => !a.IsHiddenThisParameter))
				identity.ParameterTypes.Add(parameter.Type?.FullName ?? string.Empty);

			return identity;
		}

		public string ToCanonicalString() =>
			$"{AssemblyName}|{ModuleName}|{DeclaringType}::{MethodName}`{GenericArity}({string.Join(",", ParameterTypes)}):{ReturnType}|this={HasThis}";

		public override string ToString() =>
			$"{DeclaringType}::{MethodName}({string.Join(", ", ParameterTypes)})";
	}

	sealed class ILPatchMethodBodySnapshot {
		public ILPatchMethodIdentity Method { get; set; } = null!;
		public bool InitLocals { get; set; }
		public int MaxStack { get; set; }
		public List<string> Locals { get; } = new List<string>();
		public List<ILPatchInstruction> Instructions { get; } = new List<ILPatchInstruction>();
		public List<ILPatchExceptionHandler> ExceptionHandlers { get; } = new List<ILPatchExceptionHandler>();
		public string CanonicalHash { get; set; } = string.Empty;
	}

	sealed class ILPatchInstruction {
		public string OpCode { get; set; } = string.Empty;
		public ILPatchOperand Operand { get; set; } = ILPatchOperand.None;

		public string ToCanonicalString() => $"{OpCode}|{Operand.ToCanonicalString()}";
		public override string ToString() => Operand.Kind == ILPatchOperandKind.None ? OpCode : $"{OpCode} {Operand.DisplayValue}";
	}

	enum ILPatchOperandKind {
		None,
		Integer,
		FloatingPoint,
		String,
		Argument,
		Local,
		BranchTarget,
		SwitchTargets,
		Type,
		Method,
		Field,
		Signature,
		Other,
	}

	sealed class ILPatchOperand {
		// Never expose a shared mutable singleton here. Json.NET may populate an existing
		// property instance during deserialization; sharing one None object across instructions
		// makes a later operand overwrite every earlier instruction that referenced it.
		public static ILPatchOperand None => new ILPatchOperand();

		public ILPatchOperandKind Kind { get; set; }
		public string? Text { get; set; }
		public long IntegerValue { get; set; }
		public double FloatingPointValue { get; set; }
		public int Index { get; set; } = -1;
		public int[]? Indices { get; set; }

		public string DisplayValue {
			get {
				switch (Kind) {
				case ILPatchOperandKind.None:
					return string.Empty;
				case ILPatchOperandKind.Integer:
					return IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
				case ILPatchOperandKind.FloatingPoint:
					return FloatingPointValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
				case ILPatchOperandKind.Argument:
					return $"arg:{Index}";
				case ILPatchOperandKind.Local:
					return $"local:{Index}";
				case ILPatchOperandKind.BranchTarget:
					return $"instruction:{Index}";
				case ILPatchOperandKind.SwitchTargets:
					return $"instructions:[{string.Join(",", Indices ?? Array.Empty<int>())}]";
				default:
					return Text ?? string.Empty;
				}
			}
		}

		public string ToCanonicalString() => $"{Kind}:{DisplayValue}";
	}

	sealed class ILPatchExceptionHandler {
		public string HandlerType { get; set; } = string.Empty;
		public string? CatchType { get; set; }
		public int TryStart { get; set; } = -1;
		public int TryEnd { get; set; } = -1;
		public int HandlerStart { get; set; } = -1;
		public int HandlerEnd { get; set; } = -1;
		public int FilterStart { get; set; } = -1;

		public string ToCanonicalString() =>
			$"{HandlerType}|{CatchType}|{TryStart}:{TryEnd}|{HandlerStart}:{HandlerEnd}|{FilterStart}";
	}

	/// <summary>
	/// One user-visible edit event. Effective patches are still calculated from the first
	/// baseline to the current body; this history exists for review/audit only.
	/// </summary>
	sealed class ILPatchEditRecord {
		public DateTime TimestampUtc { get; set; }
		public string Description { get; set; } = string.Empty;
		public ILPatchMethodIdentity Method { get; set; } = null!;
		public string BeforeHash { get; set; } = string.Empty;
		public string AfterHash { get; set; } = string.Empty;
	}
}
