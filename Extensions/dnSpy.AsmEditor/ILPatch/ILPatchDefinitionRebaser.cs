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
	sealed class ILPatchDefinitionUpdateReport {
		public int UpdatedCount { get; internal set; }
		public int PreservedCount { get; internal set; }
		public List<string> PreservedReasons { get; } = new List<string>();
	}

	/// <summary>
	/// Rewrites an imported patch definition onto the currently resolved target/baseline when
	/// that relationship is already proven by Exact, AlreadyApplied/RebasedApplied, or a Clean
	/// three-way rebase. Unsupported/conflicting entries are copied unchanged.
	///
	/// This never writes the source .ilpatch file. The caller receives a new document and can
	/// explicitly save it to a new path.
	/// </summary>
	static class ILPatchDefinitionRebaser {
		public static ILPatchDocument CreateUpdatedDocument(ILPatchImportPreview preview,
			out ILPatchDefinitionUpdateReport report) {
			if (preview is null)
				throw new ArgumentNullException(nameof(preview));

			report = new ILPatchDefinitionUpdateReport();
			var document = new ILPatchDocument {
				FormatVersion = ILPatchDocument.CurrentFormatVersion,
				Name = preview.Document.Name,
				CreatedUtc = DateTime.UtcNow,
			};

			// Structural records are already semantic definitions. Rebasing existing method
			// baselines must never silently strip them from a v2 document.
			document.TypeChanges.AddRange(preview.Document.TypeChanges);

			foreach (var result in preview.Results) {
				if (TryCreateUpdatedChange(result, out var updated, out string error) && updated is not null) {
					document.Methods.Add(updated);
					report.UpdatedCount++;
				}
				else {
					document.Methods.Add(CloneChange(result.Patch));
					report.PreservedCount++;
					report.PreservedReasons.Add($"{result.Patch.Id}: {error}");
				}
			}

			return document;
		}

		public static bool TryCreateUpdatedChange(ILPatchImportResult result,
			out ILPatchMethodChange? updated, out string error) {
			updated = null;
			error = string.Empty;
			if (result is null) {
				error = "Import result is missing.";
				return false;
			}
			if (result.Target is null || result.Target.Body is null) {
				error = "No resolved CIL target is available.";
				return false;
			}
			if (result.Patch.BaseBody is null || result.Patch.PatchedBody is null) {
				error = "Patch entry does not contain complete method bodies.";
				return false;
			}

			var target = result.Target;
			var identity = ILPatchMethodIdentity.Create(target);
			var current = CilNormalizer.CreateSnapshot(target);
			current.CanonicalHash = ILPatchBodyHasher.Compute(current);

			ILPatchMethodBodySnapshot baseBody;
			ILPatchMethodBodySnapshot patchedBody;

			switch (result.Status) {
			case ILPatchImportStatus.Exact:
				baseBody = CloneBody(current, identity);
				patchedBody = CloneBody(result.Patch.PatchedBody, identity);
				break;

			case ILPatchImportStatus.AlreadyApplied:
			case ILPatchImportStatus.RebasedApplied:
				if (!ILPatchRebasedAppliedDetector.TryRecoverAppliedBase(result.Patch, target, current,
					out var recoveredBase, out _) || recoveredBase is null) {
					error = "The applied patch could not be cleanly reversed and round-tripped to recover its current-version baseline.";
					return false;
				}
				baseBody = CloneBody(recoveredBase, identity);
				patchedBody = CloneBody(current, identity);
				break;

			case ILPatchImportStatus.BaseChanged:
				if (result.RebasePreview?.Status != ILPatchRebaseStatus.Clean) {
					error = $"BaseChanged entry is not a clean rebase ({result.RebasePreview?.Status.ToString() ?? "no analysis"}).";
					return false;
				}
				if (!ILPatchRebaseMerger.TryCreateMergedSnapshot(result.Patch, result.RebasePreview,
					out var merged, out string mergeError) || merged is null) {
					error = "Clean rebase could not be materialized: " + mergeError;
					return false;
				}
				baseBody = CloneBody(current, identity);
				patchedBody = CloneBody(merged, identity);
				break;

			default:
				error = $"Import status {result.Status} cannot be safely rebased into a new patch definition.";
				return false;
			}

			updated = new ILPatchMethodChange {
				Id = result.Patch.Id,
				Target = CloneIdentity(identity),
				BaseModuleMvid = target.Module?.Mvid ?? result.Patch.BaseModuleMvid,
				BaseBody = baseBody,
				PatchedBody = patchedBody,
			};
			return true;
		}

		static ILPatchMethodChange CloneChange(ILPatchMethodChange source) {
			var identity = CloneIdentity(source.Target);
			return new ILPatchMethodChange {
				Id = source.Id,
				Target = identity,
				BaseModuleMvid = source.BaseModuleMvid,
				BaseBody = CloneBody(source.BaseBody, identity),
				PatchedBody = CloneBody(source.PatchedBody, identity),
			};
		}

		static ILPatchMethodBodySnapshot CloneBody(ILPatchMethodBodySnapshot source, ILPatchMethodIdentity method) {
			var result = new ILPatchMethodBodySnapshot {
				Method = CloneIdentity(method),
				InitLocals = source.InitLocals,
				MaxStack = source.MaxStack,
			};
			result.Locals.AddRange(source.Locals);
			foreach (var instruction in source.Instructions) {
				result.Instructions.Add(new ILPatchInstruction {
					OpCode = instruction.OpCode,
					Operand = CloneOperand(instruction.Operand ?? ILPatchOperand.None),
				});
			}
			foreach (var handler in source.ExceptionHandlers) {
				result.ExceptionHandlers.Add(new ILPatchExceptionHandler {
					HandlerType = handler.HandlerType,
					CatchType = handler.CatchType,
					TryStart = handler.TryStart,
					TryEnd = handler.TryEnd,
					HandlerStart = handler.HandlerStart,
					HandlerEnd = handler.HandlerEnd,
					FilterStart = handler.FilterStart,
				});
			}
			result.CanonicalHash = ILPatchBodyHasher.Compute(result);
			return result;
		}

		static ILPatchOperand CloneOperand(ILPatchOperand source) => new ILPatchOperand {
			Kind = source.Kind,
			Text = source.Text,
			IntegerValue = source.IntegerValue,
			FloatingPointValue = source.FloatingPointValue,
			Index = source.Index,
			Indices = source.Indices?.ToArray(),
		};

		static ILPatchMethodIdentity CloneIdentity(ILPatchMethodIdentity source) {
			var result = new ILPatchMethodIdentity {
				AssemblyName = source.AssemblyName,
				ModuleName = source.ModuleName,
				DeclaringType = source.DeclaringType,
				MethodName = source.MethodName,
				ReturnType = source.ReturnType,
				GenericArity = source.GenericArity,
				HasThis = source.HasThis,
			};
			result.ParameterTypes.AddRange(source.ParameterTypes);
			return result;
		}
	}
}
