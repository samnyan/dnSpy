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
	sealed class ILPatchCreateReport {
		public int ChangedCount { get; internal set; }
		public int BodyChangedCount { get; internal set; }
		public int StructuralChangedCount { get; internal set; }
		public int UnchangedCount { get; internal set; }
		public int UnsupportedCount => UnsupportedReasons.Count;
		public List<string> UnsupportedReasons { get; } = new List<string>();
	}

	/// <summary>
	/// Creates a portable ILPatch document by comparing an original module with a separately
	/// saved modified copy. v2 keeps existing-method CIL body edits in Methods and groups
	/// supported member topology changes under TypeChanges.
	/// </summary>
	static class ILPatchDocumentCreator {
		public static bool TryCreate(ModuleDef original, ModuleDef modified, string name,
			out ILPatchDocument? document, out ILPatchCreateReport report) {
			if (original is null)
				throw new ArgumentNullException(nameof(original));
			if (modified is null)
				throw new ArgumentNullException(nameof(modified));

			report = new ILPatchCreateReport();
			document = null;

			if (!StringComparer.Ordinal.Equals(original.Name?.String ?? string.Empty,
				modified.Name?.String ?? string.Empty)) {
				report.UnsupportedReasons.Add(
					$"Module name changed from '{original.Name}' to '{modified.Name}'.");
			}
			if (!StringComparer.Ordinal.Equals(original.Assembly?.Name?.String ?? string.Empty,
				modified.Assembly?.Name?.String ?? string.Empty)) {
				report.UnsupportedReasons.Add(
					$"Assembly name changed from '{original.Assembly?.Name}' to '{modified.Assembly?.Name}'.");
			}

			// The general shape guard remains the final safety net. v2 explicitly owns member
			// additions/removals below; every other metadata/structure mutation still fails closed.
			foreach (var shapeChange in ILPatchAssemblyShapeGuard.Compare(original, modified)) {
				if (IsV2StructuralMemberChange(shapeChange))
					continue;
				report.UnsupportedReasons.Add("Unsupported structural change: " + shapeChange);
			}

			var originalTypes = BuildTypeIndex(original);
			var modifiedTypes = BuildTypeIndex(modified);
			var allTypeNames = new SortedSet<string>(originalTypes.Keys, StringComparer.Ordinal);
			allTypeNames.UnionWith(modifiedTypes.Keys);

			var created = new ILPatchDocument {
				FormatVersion = ILPatchDocument.CurrentFormatVersion,
				Name = name ?? string.Empty,
				CreatedUtc = DateTime.UtcNow,
			};

			foreach (string typeName in allTypeNames) {
				bool hasBefore = originalTypes.TryGetValue(typeName, out var beforeType);
				bool hasAfter = modifiedTypes.TryGetValue(typeName, out var afterType);
				if (!hasBefore || !hasAfter) {
					var sourceType = hasAfter ? afterType! : beforeType!;
					if (!ILPatchStructuralSnapshotBuilder.TryCreateType(sourceType, out var typeSnapshot, out string typeError) ||
						typeSnapshot is null) {
						report.UnsupportedReasons.Add(typeError);
						continue;
					}
					created.TypeChanges.Add(new ILPatchTypeChange {
						Target = typeSnapshot.Identity,
						Kind = hasAfter ? ILPatchTypeChangeKind.Add : ILPatchTypeChangeKind.Remove,
						TypeDefinition = typeSnapshot,
					});
					report.StructuralChangedCount++;
					report.ChangedCount++;
					continue;
				}

				var typeChange = new ILPatchTypeChange {
					Target = ILPatchTypeIdentity.Create(beforeType!),
				};
				CaptureFieldTopology(beforeType!, afterType!, typeChange, report);
				CaptureMethodTopology(beforeType!, afterType!, typeChange, report);
				CapturePropertyTopology(beforeType!, afterType!, typeChange, report);
				CaptureEventTopology(beforeType!, afterType!, typeChange, report);
				if (typeChange.HasEffectiveChange)
					created.TypeChanges.Add(typeChange);
			}

			var originalIndex = BuildMethodIndex(original);
			var modifiedIndex = BuildMethodIndex(modified);
			var commonKeys = new SortedSet<string>(originalIndex.Keys, StringComparer.Ordinal);
			commonKeys.IntersectWith(modifiedIndex.Keys);

			foreach (string key in commonKeys) {
				var originalMethods = originalIndex[key];
				var modifiedMethods = modifiedIndex[key];
				if (originalMethods.Count != 1 || modifiedMethods.Count != 1) {
					report.UnsupportedReasons.Add(
						$"Method identity '{key}' is ambiguous (original={originalMethods.Count}, modified={modifiedMethods.Count}).");
					continue;
				}

				var beforeMethod = originalMethods[0];
				var afterMethod = modifiedMethods[0];
				if (beforeMethod.Attributes != afterMethod.Attributes ||
					beforeMethod.ImplAttributes != afterMethod.ImplAttributes) {
					report.UnsupportedReasons.Add(
						$"Method metadata flags changed for '{Describe(beforeMethod)}'. v2 does not rewrite metadata on an existing method yet.");
					continue;
				}

				bool beforeHasBody = beforeMethod.Body is not null;
				bool afterHasBody = afterMethod.Body is not null;
				if (beforeHasBody != afterHasBody) {
					report.UnsupportedReasons.Add(
						$"Method '{Describe(beforeMethod)}' changed between CIL and non-CIL/no-body form.");
					continue;
				}
				if (!beforeHasBody) {
					report.UnchangedCount++;
					continue;
				}

				var baseline = CilNormalizer.CreateSnapshot(beforeMethod);
				baseline.CanonicalHash = ILPatchBodyHasher.Compute(baseline);
				var patched = CilNormalizer.CreateSnapshot(afterMethod);
				patched.CanonicalHash = ILPatchBodyHasher.Compute(patched);

				if (StringComparer.Ordinal.Equals(baseline.CanonicalHash, patched.CanonicalHash)) {
					report.UnchangedCount++;
					continue;
				}

				var identity = ILPatchMethodIdentity.Create(beforeMethod);
				baseline.Method = identity;
				patched.Method = identity;
				created.Methods.Add(new ILPatchMethodChange {
					Target = identity,
					BaseModuleMvid = original.Mvid ?? Guid.Empty,
					BaseBody = baseline,
					PatchedBody = patched,
				});
				report.BodyChangedCount++;
				report.ChangedCount++;
			}

			if (report.UnsupportedCount != 0)
				return false;

			document = created;
			return true;
		}

		static bool IsV2StructuralMemberChange(ILPatchUnsupportedWorkingChange change) =>
			StringComparer.Ordinal.Equals(change.Kind, "Field added") ||
			StringComparer.Ordinal.Equals(change.Kind, "Field removed") ||
			StringComparer.Ordinal.Equals(change.Kind, "Method metadata added") ||
			StringComparer.Ordinal.Equals(change.Kind, "Method metadata removed") ||
			StringComparer.Ordinal.Equals(change.Kind, "Property added") ||
			StringComparer.Ordinal.Equals(change.Kind, "Property removed") ||
			StringComparer.Ordinal.Equals(change.Kind, "Event added") ||
			StringComparer.Ordinal.Equals(change.Kind, "Event removed") ||
			StringComparer.Ordinal.Equals(change.Kind, "Type added") ||
			StringComparer.Ordinal.Equals(change.Kind, "Type removed");

		static void CaptureFieldTopology(TypeDef beforeType, TypeDef afterType,
			ILPatchTypeChange typeChange, ILPatchCreateReport report) {
			var before = BuildFieldIndex(beforeType);
			var after = BuildFieldIndex(afterType);
			var keys = new SortedSet<string>(before.Keys, StringComparer.Ordinal);
			keys.UnionWith(after.Keys);
			foreach (string key in keys) {
				bool hasBefore = before.TryGetValue(key, out var beforeField);
				bool hasAfter = after.TryGetValue(key, out var afterField);
				if (hasBefore && hasAfter)
					continue;
				if (hasAfter) {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateField(afterField!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.AddedFields.Add(snapshot);
				}
				else {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateField(beforeField!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.RemovedFields.Add(snapshot.Identity);
					typeChange.RemovedFieldBaselines.Add(snapshot);
				}
				report.StructuralChangedCount++;
				report.ChangedCount++;
			}
		}

		static void CaptureMethodTopology(TypeDef beforeType, TypeDef afterType,
			ILPatchTypeChange typeChange, ILPatchCreateReport report) {
			var before = BuildMethodIndex(beforeType);
			var after = BuildMethodIndex(afterType);
			var keys = new SortedSet<string>(before.Keys, StringComparer.Ordinal);
			keys.UnionWith(after.Keys);
			foreach (string key in keys) {
				bool hasBefore = before.TryGetValue(key, out var beforeMethod);
				bool hasAfter = after.TryGetValue(key, out var afterMethod);
				if (hasBefore && hasAfter)
					continue;
				if (hasAfter) {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateMethod(afterMethod!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.AddedMethods.Add(snapshot);
				}
				else {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateMethod(beforeMethod!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.RemovedMethods.Add(snapshot.Identity);
					typeChange.RemovedMethodBaselines.Add(snapshot);
				}
				report.StructuralChangedCount++;
				report.ChangedCount++;
			}
		}


		static void CapturePropertyTopology(TypeDef beforeType, TypeDef afterType,
			ILPatchTypeChange typeChange, ILPatchCreateReport report) {
			var before = BuildPropertyIndex(beforeType);
			var after = BuildPropertyIndex(afterType);
			var keys = new SortedSet<string>(before.Keys, StringComparer.Ordinal);
			keys.UnionWith(after.Keys);
			foreach (string key in keys) {
				bool hasBefore = before.TryGetValue(key, out var beforeProperty);
				bool hasAfter = after.TryGetValue(key, out var afterProperty);
				if (hasBefore && hasAfter)
					continue;
				if (hasAfter) {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateProperty(afterProperty!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.AddedProperties.Add(snapshot);
				}
				else {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateProperty(beforeProperty!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.RemovedProperties.Add(snapshot.Identity);
					typeChange.RemovedPropertyBaselines.Add(snapshot);
				}
				report.StructuralChangedCount++;
				report.ChangedCount++;
			}
		}

		static void CaptureEventTopology(TypeDef beforeType, TypeDef afterType,
			ILPatchTypeChange typeChange, ILPatchCreateReport report) {
			var before = BuildEventIndex(beforeType);
			var after = BuildEventIndex(afterType);
			var keys = new SortedSet<string>(before.Keys, StringComparer.Ordinal);
			keys.UnionWith(after.Keys);
			foreach (string key in keys) {
				bool hasBefore = before.TryGetValue(key, out var beforeEvent);
				bool hasAfter = after.TryGetValue(key, out var afterEvent);
				if (hasBefore && hasAfter)
					continue;
				if (hasAfter) {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateEvent(afterEvent!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.AddedEvents.Add(snapshot);
				}
				else {
					if (!ILPatchStructuralSnapshotBuilder.TryCreateEvent(beforeEvent!, out var snapshot, out string error) ||
						snapshot is null) {
						report.UnsupportedReasons.Add(error);
						continue;
					}
					typeChange.RemovedEvents.Add(snapshot.Identity);
					typeChange.RemovedEventBaselines.Add(snapshot);
				}
				report.StructuralChangedCount++;
				report.ChangedCount++;
			}
		}

		static Dictionary<string, TypeDef> BuildTypeIndex(ModuleDef module) {
			var result = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes()) {
				string key = type.FullName ?? string.Empty;
				if (!result.ContainsKey(key))
					result.Add(key, type);
			}
			return result;
		}

		static Dictionary<string, FieldDef> BuildFieldIndex(TypeDef type) {
			var result = new Dictionary<string, FieldDef>(StringComparer.Ordinal);
			foreach (var field in type.Fields) {
				string key = ILPatchFieldIdentity.Create(field).ToCanonicalString();
				if (!result.ContainsKey(key))
					result.Add(key, field);
			}
			return result;
		}

		static Dictionary<string, PropertyDef> BuildPropertyIndex(TypeDef type) {
			var result = new Dictionary<string, PropertyDef>(StringComparer.Ordinal);
			foreach (var property in type.Properties) {
				string key = ILPatchPropertyIdentity.Create(property).ToCanonicalString();
				if (!result.ContainsKey(key))
					result.Add(key, property);
			}
			return result;
		}

		static Dictionary<string, EventDef> BuildEventIndex(TypeDef type) {
			var result = new Dictionary<string, EventDef>(StringComparer.Ordinal);
			foreach (var @event in type.Events) {
				string key = ILPatchEventIdentity.Create(@event).ToCanonicalString();
				if (!result.ContainsKey(key))
					result.Add(key, @event);
			}
			return result;
		}

		static Dictionary<string, MethodDef> BuildMethodIndex(TypeDef type) {
			var result = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
			foreach (var method in type.Methods) {
				string key = ILPatchMethodIdentity.Create(method).ToCanonicalString();
				if (!result.ContainsKey(key))
					result.Add(key, method);
			}
			return result;
		}

		static Dictionary<string, List<MethodDef>> BuildMethodIndex(ModuleDef module) {
			var result = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					string key = ILPatchMethodIdentity.Create(method).ToCanonicalString();
					if (!result.TryGetValue(key, out var methods))
						result.Add(key, methods = new List<MethodDef>());
					methods.Add(method);
				}
			}
			return result;
		}

		static string Describe(MethodDef method) =>
			ILPatchMethodIdentity.Create(method).ToString();
	}
}
