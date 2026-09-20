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
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace dnSpy.AsmEditor.ILPatch {
	enum ILPatchRepositoryMethodChangeKind {
		AddedChange,
		Modified,
		Reverted,
	}

	sealed class ILPatchRepositoryMethodSummary {
		public ILPatchMethodIdentity Target { get; set; } = null!;
		public ILPatchRepositoryMethodChangeKind Kind { get; set; }
		public string BeforeHash { get; set; } = string.Empty;
		public string AfterHash { get; set; } = string.Empty;
	}

	enum ILPatchRepositoryStructuralChangeKind {
		AddedField,
		RemovedField,
		AddedMethod,
		RemovedMethod,
		AddedProperty,
		RemovedProperty,
		AddedEvent,
		RemovedEvent,
	}

	sealed class ILPatchRepositoryStructuralSummary {
		public ILPatchRepositoryStructuralChangeKind Kind { get; set; }
		public ILPatchTypeIdentity DeclaringType { get; set; } = null!;
		public string Member { get; set; } = string.Empty;
	}

	sealed class ILPatchRepositoryCommit {
		public const int CurrentFormatVersion = 2;
		public const int MinimumSupportedFormatVersion = 1;
		public int FormatVersion { get; set; } = CurrentFormatVersion;
		public string Id { get; set; } = string.Empty;
		public string? ParentId { get; set; }
		public string Message { get; set; } = string.Empty;
		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public string PatchFile { get; set; } = string.Empty;
		public string StateHash { get; set; } = string.Empty;
		/// <summary>
		/// v2 full metadata/member topology fingerprint. Empty on legacy v1 commits.
		/// StateHash intentionally keeps the v1 method-state algorithm for compatibility.
		/// </summary>
		public string StructureStateHash { get; set; } = string.Empty;
		public List<ILPatchRepositoryMethodSummary> Changes { get; } = new List<ILPatchRepositoryMethodSummary>();
		public List<ILPatchRepositoryStructuralSummary> StructuralChanges { get; } = new List<ILPatchRepositoryStructuralSummary>();
	}

	sealed class ILPatchRepositoryMetadata {
		public const int CurrentFormatVersion = 2;
		public const int MinimumSupportedFormatVersion = 1;
		public int FormatVersion { get; set; } = CurrentFormatVersion;
		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public string ModuleFileName { get; set; } = string.Empty;
		public string AssemblyName { get; set; } = string.Empty;
		public Guid RootMvid { get; set; }
		public string RootFileSha256 { get; set; } = string.Empty;
		public string RootStateHash { get; set; } = string.Empty;
		/// <summary>v2 full metadata/member topology fingerprint. Empty on legacy v1 repositories.</summary>
		public string RootStructureStateHash { get; set; } = string.Empty;
		public string? HeadCommitId { get; set; }
	}

	static class ILPatchModuleStateHasher {
		public static string Compute(ModuleDef module) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			var lines = new List<string>();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					var identity = ILPatchMethodIdentity.Create(method).ToCanonicalString();
					string bodyHash;
					if (method.Body is null)
						bodyHash = "<no-cil-body>";
					else {
						var snapshot = CilNormalizer.CreateSnapshot(method);
						snapshot.CanonicalHash = ILPatchBodyHasher.Compute(snapshot);
						bodyHash = snapshot.CanonicalHash;
					}
					lines.Add(identity + "|attr=" + ((uint)method.Attributes).ToString("X8") +
						"|impl=" + ((uint)method.ImplAttributes).ToString("X8") +
						"|body=" + bodyHash);
				}
			}
			lines.Sort(StringComparer.Ordinal);
			return Sha256Hex(string.Join("\n", lines));
		}

		internal static string Sha256Hex(string value) {
			using var sha = SHA256.Create();
			byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
			return string.Concat(bytes.Select(a => a.ToString("x2")));
		}

		public static string ComputeFileSha256(string filename) {
			using var stream = File.OpenRead(filename);
			using var sha = SHA256.Create();
			byte[] bytes = sha.ComputeHash(stream);
			return string.Concat(bytes.Select(a => a.ToString("x2")));
		}
	}

	/// <summary>
	/// Lightweight Git-like repository for one managed module. v1 stores one immutable root DLL
	/// and one full root-to-state .ilpatch snapshot per commit. Parent metadata preserves history
	/// and per-commit summaries while each node remains independently exportable from the root.
	/// </summary>
	sealed class ILPatchRepository {
		public const string DirectoryName = ".dnspy";
		const string MetadataFileName = "repo.json";
		const string BaseDirectoryName = "base";
		const string CommitsDirectoryName = "commits";
		const string PatchesDirectoryName = "patches";

		static readonly JsonSerializerSettings jsonSettings = CreateJsonSettings();

		public string RepositoryPath { get; }
		public ILPatchRepositoryMetadata Metadata { get; private set; }
		public string RootModulePath => Path.Combine(RepositoryPath, BaseDirectoryName, Metadata.ModuleFileName);

		ILPatchRepository(string repositoryPath, ILPatchRepositoryMetadata metadata) {
			RepositoryPath = repositoryPath;
			Metadata = metadata;
		}

		public static ILPatchRepository Initialize(string modulePath) {
			modulePath = Path.GetFullPath(modulePath ?? throw new ArgumentNullException(nameof(modulePath)));
			if (!File.Exists(modulePath))
				throw new FileNotFoundException("Repository root assembly was not found.", modulePath);
			string directory = Path.GetDirectoryName(modulePath) ?? throw new InvalidOperationException("Root assembly has no parent directory.");
			string repositoryPath = Path.Combine(directory, DirectoryName);
			string metadataPath = Path.Combine(repositoryPath, MetadataFileName);
			if (File.Exists(metadataPath))
				throw new InvalidOperationException($"An ILPatch repository already exists at '{repositoryPath}'.");

			using var module = ModuleDefMD.Load(modulePath);
			Directory.CreateDirectory(Path.Combine(repositoryPath, BaseDirectoryName));
			Directory.CreateDirectory(Path.Combine(repositoryPath, CommitsDirectoryName));
			Directory.CreateDirectory(Path.Combine(repositoryPath, PatchesDirectoryName));

			string basePath = Path.Combine(repositoryPath, BaseDirectoryName, Path.GetFileName(modulePath));
			File.Copy(modulePath, basePath, false);

			var metadata = new ILPatchRepositoryMetadata {
				CreatedUtc = DateTime.UtcNow,
				ModuleFileName = Path.GetFileName(modulePath),
				AssemblyName = module.Assembly?.Name?.String ?? string.Empty,
				RootMvid = module.Mvid ?? Guid.Empty,
				RootFileSha256 = ILPatchModuleStateHasher.ComputeFileSha256(modulePath),
				RootStateHash = ILPatchModuleStateHasher.Compute(module),
				RootStructureStateHash = ILPatchAssemblyShapeGuard.ComputeFingerprint(module),
			};
			WriteJsonAtomic(metadataPath, metadata);
			TryHideRepositoryDirectory(repositoryPath);
			return new ILPatchRepository(repositoryPath, metadata);
		}

		public static ILPatchRepository OpenForModule(string modulePath) {
			modulePath = Path.GetFullPath(modulePath ?? throw new ArgumentNullException(nameof(modulePath)));
			string directory = Path.GetDirectoryName(modulePath) ?? throw new InvalidOperationException("Module has no parent directory.");
			string repositoryPath = Path.Combine(directory, DirectoryName);
			var metadata = ReadJson<ILPatchRepositoryMetadata>(Path.Combine(repositoryPath, MetadataFileName));
			if (metadata.FormatVersion < ILPatchRepositoryMetadata.MinimumSupportedFormatVersion ||
				metadata.FormatVersion > ILPatchRepositoryMetadata.CurrentFormatVersion) {
				throw new NotSupportedException(
					$"Unsupported .dnspy repository format {metadata.FormatVersion}. " +
					$"Supported range is {ILPatchRepositoryMetadata.MinimumSupportedFormatVersion}-{ILPatchRepositoryMetadata.CurrentFormatVersion}.");
			}
			if (!StringComparer.OrdinalIgnoreCase.Equals(metadata.ModuleFileName, Path.GetFileName(modulePath)))
				throw new InvalidOperationException(
					$"The .dnspy repository tracks '{metadata.ModuleFileName}', not '{Path.GetFileName(modulePath)}'.");
			var repository = new ILPatchRepository(repositoryPath, metadata);
			repository.ValidateRootIntegrity();
			return repository;
		}

		public static bool ExistsForModule(string modulePath) {
			if (string.IsNullOrWhiteSpace(modulePath))
				return false;
			string fullPath;
			try {
				fullPath = Path.GetFullPath(modulePath);
			}
			catch {
				return false;
			}
			string? directory = Path.GetDirectoryName(fullPath);
			return directory is not null && File.Exists(Path.Combine(directory, DirectoryName, MetadataFileName));
		}

		public IReadOnlyList<ILPatchRepositoryCommit> GetHistory() {
			var result = new List<ILPatchRepositoryCommit>();
			string? id = Metadata.HeadCommitId;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			while (!string.IsNullOrEmpty(id)) {
				if (!seen.Add(id))
					throw new InvalidDataException("Repository commit graph contains a cycle.");
				var commit = LoadCommit(id);
				result.Add(commit);
				id = commit.ParentId;
			}
			return result;
		}

		public ILPatchRepositoryCommit Commit(ModuleDef currentModule,
			IReadOnlyList<ILPatchMethodChange> workingChanges, string message) =>
			CommitSelected(currentModule, workingChanges, workingChanges, message);

		/// <summary>
		/// Commits only the selected working methods while validating the complete working tree
		/// against HEAD. Unselected edits may remain in the in-memory module and are deliberately
		/// excluded from the new commit state.
		/// </summary>
		public ILPatchRepositoryCommit CommitSelected(ModuleDef currentModule,
			IReadOnlyList<ILPatchMethodChange> allWorkingChanges,
			IReadOnlyList<ILPatchMethodChange> selectedChanges, string message) {
			ValidateRootIntegrity();
			if (currentModule is null)
				throw new ArgumentNullException(nameof(currentModule));
			if (allWorkingChanges is null)
				throw new ArgumentNullException(nameof(allWorkingChanges));
			if (selectedChanges is null)
				throw new ArgumentNullException(nameof(selectedChanges));
			if (string.IsNullOrWhiteSpace(message))
				throw new ArgumentException("Commit message cannot be empty.", nameof(message));

			if (!TryGetWorkingDelta(currentModule, allWorkingChanges, out var workingDelta, out string validationError) ||
				workingDelta is null) {
				throw new InvalidOperationException(validationError);
			}

			var workingState = allWorkingChanges.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
			var selectedState = selectedChanges.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
			foreach (var pair in selectedState) {
				if (!workingState.TryGetValue(pair.Key, out var currentWorking))
					throw new InvalidOperationException(
						$"Selected change '{pair.Value.Target}' is not present in the current working tree.");
				if (!StringComparer.Ordinal.Equals(
					currentWorking.BaseBody.CanonicalHash, pair.Value.BaseBody.CanonicalHash) ||
					!StringComparer.Ordinal.Equals(
					currentWorking.PatchedBody.CanonicalHash, pair.Value.PatchedBody.CanonicalHash)) {
					throw new InvalidOperationException(
						$"Selected change '{pair.Value.Target}' is stale relative to the current working tree.");
				}
			}

			bool hasStructuralChanges = workingDelta.TypeChanges.Any(a => a.HasEffectiveChange);
			if (!hasStructuralChanges && selectedChanges.Count == 0)
				throw new ArgumentException("At least one working change must be selected for commit.", nameof(selectedChanges));
			if (hasStructuralChanges && selectedState.Count != workingState.Count) {
				throw new InvalidOperationException(
					"Working tree contains type/member structural changes. The current repository implementation " +
					"commits structural changes atomically with all method-body edits in the working tree; stage all method changes first.");
			}

			var incremental = new ILPatchDocument {
				FormatVersion = ILPatchDocument.CurrentFormatVersion,
				Name = message.Trim(),
				CreatedUtc = DateTime.UtcNow,
			};
			incremental.Methods.AddRange(selectedChanges);
			if (hasStructuralChanges)
				incremental.TypeChanges.AddRange(workingDelta.TypeChanges);

			using var parentModule = MaterializeState(Metadata.HeadCommitId);
			using var desiredModule = MaterializeState(Metadata.HeadCommitId);
			var applyReport = ILPatchHeadlessApplier.Apply(desiredModule, incremental);
			if (!applyReport.Success) {
				string details = DescribeApplyFailure(applyReport);
				throw new InvalidOperationException(
					"Could not materialize staged repository changes on top of HEAD: " + details);
			}

			using var rootModule = ModuleDefMD.Load(RootModulePath);
			if (!ILPatchDocumentCreator.TryCreate(rootModule, desiredModule, message.Trim(),
				out var document, out var createReport) || document is null) {
				throw new InvalidOperationException(
					"Could not encode the selected repository state as a ROOT-relative ILPatch: " +
					string.Join("; ", createReport.UnsupportedReasons));
			}

			var commitTime = DateTime.UtcNow;
			document.CreatedUtc = commitTime;
			document.Name = message.Trim();

			string serializedPatch = ILPatchSerializer.Serialize(document);
			string id = CreateCommitId(Metadata.HeadCommitId, commitTime, message.Trim(), serializedPatch);
			string patchRelativePath = Path.Combine(PatchesDirectoryName, id + ".ilpatch").Replace('\\', '/');
			var commit = new ILPatchRepositoryCommit {
				Id = id,
				ParentId = Metadata.HeadCommitId,
				Message = message.Trim(),
				CreatedUtc = commitTime,
				PatchFile = patchRelativePath,
				StateHash = ILPatchModuleStateHasher.Compute(desiredModule),
				StructureStateHash = ILPatchAssemblyShapeGuard.ComputeFingerprint(desiredModule),
			};

			if (!ILPatchDocumentCreator.TryCreate(parentModule, desiredModule, message.Trim(),
				out var commitDelta, out var deltaReport) || commitDelta is null) {
				throw new InvalidOperationException(
					"Could not summarize parent-to-commit changes: " +
					string.Join("; ", deltaReport.UnsupportedReasons));
			}
			PopulateSummaryFromDelta(commit, rootModule, commitDelta);

			string patchPath = Path.Combine(RepositoryPath, patchRelativePath.Replace('/', Path.DirectorySeparatorChar));
			string commitPath = GetCommitPath(id);
			WriteTextAtomic(patchPath, serializedPatch);
			WriteJsonAtomic(commitPath, commit);

			Metadata.HeadCommitId = id;
			WriteJsonAtomic(Path.Combine(RepositoryPath, MetadataFileName), Metadata);
			return commit;
		}

		public bool TryGetWorkingDelta(ModuleDef currentModule,
			IReadOnlyList<ILPatchMethodChange> workingChanges,
			out ILPatchDocument? delta, out string error) {
			delta = null;
			error = string.Empty;
			try {
				ValidateRootIntegrity();
				if (currentModule is null)
					throw new ArgumentNullException(nameof(currentModule));
				if (workingChanges is null)
					throw new ArgumentNullException(nameof(workingChanges));

				using var headModule = MaterializeState(Metadata.HeadCommitId);
				if (!ILPatchDocumentCreator.TryCreate(headModule, currentModule, "Working tree",
					out var workingDelta, out var report) || workingDelta is null) {
					error = "Working tree contains unsupported changes: " +
						string.Join("; ", report.UnsupportedReasons);
					return false;
				}

				var tracked = workingChanges.ToDictionary(
					a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
				var actual = workingDelta.Methods.ToDictionary(
					a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
				var structurallyRemovedMethods = new HashSet<string>(
					workingDelta.TypeChanges.SelectMany(a => a.RemovedMethods)
						.Select(a => a.ToCanonicalString()), StringComparer.Ordinal);

				foreach (var pair in actual) {
					if (!tracked.TryGetValue(pair.Key, out var trackedChange)) {
						error =
							$"Working tree is not based on repository HEAD, or contains an untracked method-body change at '{pair.Value.Target}'. " +
							"Export/open HEAD and refresh the ILPatch Workspace baseline before committing.";
						return false;
					}
					if (!SameBodyTransition(trackedChange, pair.Value)) {
						error =
							$"Tracked baseline for '{pair.Value.Target}' does not match repository HEAD/current working state.";
						return false;
					}
				}
				foreach (var pair in tracked) {
					if (actual.ContainsKey(pair.Key) || structurallyRemovedMethods.Contains(pair.Key))
						continue;
					error =
						$"ILPatch Workspace reports '{pair.Value.Target}' as modified, but HEAD -> Working Tree does not " +
						"contain the same body transition. The working baseline is stale.";
					return false;
				}

				delta = workingDelta;
				return true;
			}
			catch (Exception ex) {
				error = ex.Message;
				return false;
			}
		}

		static bool SameBodyTransition(ILPatchMethodChange left, ILPatchMethodChange right) =>
			StringComparer.Ordinal.Equals(left.BaseBody.CanonicalHash, right.BaseBody.CanonicalHash) &&
			StringComparer.Ordinal.Equals(left.PatchedBody.CanonicalHash, right.PatchedBody.CanonicalHash);

		static string DescribeApplyFailure(ILPatchHeadlessApplyReport report) {
			var details = report.Entries
				.Where(a => a.Action == ILPatchHeadlessAction.Unresolved)
				.Select(a => a.Message)
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.ToList();
			if (!string.IsNullOrWhiteSpace(report.StructuralMessage))
				details.Add(report.StructuralMessage);
			return details.Count == 0 ? "unknown materialization failure" : string.Join("; ", details);
		}

		static void PopulateSummaryFromDelta(ILPatchRepositoryCommit commit, ModuleDef rootModule,
			ILPatchDocument delta) {
			var rootMethods = BuildMethodMap(rootModule);
			foreach (var change in delta.Methods) {
				ILPatchRepositoryMethodChangeKind kind = ILPatchRepositoryMethodChangeKind.Modified;
				string key = change.Target.ToCanonicalString();
				if (rootMethods.TryGetValue(key, out var rootMethod) && rootMethod.Body is not null) {
					var root = CreateSnapshot(rootMethod);
					bool beforeRoot = StringComparer.Ordinal.Equals(change.BaseBody.CanonicalHash, root.CanonicalHash);
					bool afterRoot = StringComparer.Ordinal.Equals(change.PatchedBody.CanonicalHash, root.CanonicalHash);
					if (beforeRoot && !afterRoot)
						kind = ILPatchRepositoryMethodChangeKind.AddedChange;
					else if (!beforeRoot && afterRoot)
						kind = ILPatchRepositoryMethodChangeKind.Reverted;
				}
				commit.Changes.Add(new ILPatchRepositoryMethodSummary {
					Target = change.Target,
					Kind = kind,
					BeforeHash = change.BaseBody.CanonicalHash,
					AfterHash = change.PatchedBody.CanonicalHash,
				});
			}

			foreach (var typeChange in delta.TypeChanges) {
				foreach (var field in typeChange.AddedFields)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.AddedField,
						DeclaringType = typeChange.Target,
						Member = field.Identity.ToString(),
					});
				foreach (var field in typeChange.RemovedFields)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.RemovedField,
						DeclaringType = typeChange.Target,
						Member = field.ToString(),
					});
				foreach (var method in typeChange.AddedMethods)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.AddedMethod,
						DeclaringType = typeChange.Target,
						Member = method.Identity.ToString(),
					});
				foreach (var method in typeChange.RemovedMethods)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.RemovedMethod,
						DeclaringType = typeChange.Target,
						Member = method.ToString(),
					});
				foreach (var property in typeChange.AddedProperties)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.AddedProperty,
						DeclaringType = typeChange.Target,
						Member = property.Identity.ToString(),
					});
				foreach (var property in typeChange.RemovedProperties)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.RemovedProperty,
						DeclaringType = typeChange.Target,
						Member = property.ToString(),
					});
				foreach (var @event in typeChange.AddedEvents)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.AddedEvent,
						DeclaringType = typeChange.Target,
						Member = @event.Identity.ToString(),
					});
				foreach (var @event in typeChange.RemovedEvents)
					commit.StructuralChanges.Add(new ILPatchRepositoryStructuralSummary {
						Kind = ILPatchRepositoryStructuralChangeKind.RemovedEvent,
						DeclaringType = typeChange.Target,
						Member = @event.ToString(),
					});
			}
		}

		string ComputeDocumentStateHash(ILPatchDocument document) {
			using var module = MaterializeDocumentFromRoot(document);
			return ILPatchModuleStateHasher.Compute(module);
		}

		string ComputeDocumentStructureStateHash(ILPatchDocument document) {
			using var module = MaterializeDocumentFromRoot(document);
			return ILPatchAssemblyShapeGuard.ComputeFingerprint(module);
		}

		ModuleDefMD MaterializeDocumentFromRoot(ILPatchDocument document) {
			var module = ModuleDefMD.Load(RootModulePath);
			try {
				var report = ILPatchHeadlessApplier.Apply(module, document);
				if (!report.Success) {
					string details = string.Join("; ", report.Entries
						.Where(a => a.Action == ILPatchHeadlessAction.Unresolved)
						.Select(a => a.Message));
					if (!string.IsNullOrWhiteSpace(report.StructuralMessage))
						details = string.IsNullOrEmpty(details) ? report.StructuralMessage : details + "; " + report.StructuralMessage;
					throw new InvalidOperationException(
						"Could not materialize selected repository state from ROOT: " + details);
				}
				return module;
			}
			catch {
				module.Dispose();
				throw;
			}
		}

		public bool TryValidateWorkingBase(ModuleDef currentModule,
			IReadOnlyList<ILPatchMethodChange> workingChanges, out string error) =>
			TryGetWorkingDelta(currentModule, workingChanges, out _, out error);

		public ModuleDefMD MaterializeState(string? commitId) {
			ValidateRootIntegrity();
			var module = ModuleDefMD.Load(RootModulePath);
			try {
				if (string.IsNullOrEmpty(commitId)) {
					string rootState = ILPatchModuleStateHasher.Compute(module);
					if (!StringComparer.Ordinal.Equals(rootState, Metadata.RootStateHash))
						throw new InvalidDataException("Repository ROOT failed semantic state verification.");
					return module;
				}

				var commit = LoadCommit(commitId);
				var document = LoadCommitPatch(commitId);
				var report = ILPatchHeadlessApplier.Apply(module, document);
				if (!report.Success) {
					string details = string.Join("; ", report.Entries
						.Where(a => a.Action == ILPatchHeadlessAction.Unresolved)
						.Select(a => a.Message));
					throw new InvalidOperationException(
						"Could not materialize repository commit from the immutable root: " + details);
				}
				string state = ILPatchModuleStateHasher.Compute(module);
				if (!StringComparer.Ordinal.Equals(state, commit.StateHash)) {
					throw new InvalidDataException(
						$"Repository commit {ShortHash(commit.Id)} failed state verification. " +
						"The stored patch or commit metadata may be corrupted.");
				}
				if (!string.IsNullOrEmpty(commit.StructureStateHash)) {
					string structureState = ILPatchAssemblyShapeGuard.ComputeFingerprint(module);
					if (!StringComparer.Ordinal.Equals(structureState, commit.StructureStateHash)) {
						throw new InvalidDataException(
							$"Repository commit {ShortHash(commit.Id)} failed structure-state verification. " +
							"The stored patch or commit metadata may be corrupted.");
					}
				}
				return module;
			}
			catch {
				module.Dispose();
				throw;
			}
		}

		public ILPatchDocument CreateRestorePatch(ModuleDef currentModule, string? commitId) {
			if (currentModule is null)
				throw new ArgumentNullException(nameof(currentModule));

			using var desiredModule = MaterializeState(commitId);
			string name = string.IsNullOrEmpty(commitId)
				? "Restore repository ROOT"
				: "Restore repository " + ShortHash(commitId);
			if (!ILPatchDocumentCreator.TryCreate(currentModule, desiredModule, name,
				out var result, out var report) || result is null) {
				throw new InvalidOperationException(
					"Could not create restore patch: " + string.Join("; ", report.UnsupportedReasons));
			}
			return result;
		}

		public void Export(string? commitId, string outputPath) {
			ValidateRootIntegrity();
			outputPath = Path.GetFullPath(outputPath ?? throw new ArgumentNullException(nameof(outputPath)));
			if (StringComparer.OrdinalIgnoreCase.Equals(outputPath, RootModulePath))
				throw new InvalidOperationException("Export path must not overwrite the repository root assembly.");
			string? outputDirectory = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outputDirectory))
				Directory.CreateDirectory(outputDirectory);

			// ROOT is an immutable byte-for-byte capture of the original DLL. Preserve that exact
			// file when exporting ROOT instead of round-tripping it through dnlib.
			if (string.IsNullOrEmpty(commitId)) {
				File.Copy(RootModulePath, outputPath, true);
				return;
			}

			using var module = MaterializeState(commitId);
			module.Write(outputPath);
		}

		public ILPatchRepositoryCommit LoadCommit(string id) {
			if (string.IsNullOrWhiteSpace(id))
				throw new ArgumentException("Commit id is empty.", nameof(id));
			var commit = ReadJson<ILPatchRepositoryCommit>(GetCommitPath(id));
			if (commit.FormatVersion < ILPatchRepositoryCommit.MinimumSupportedFormatVersion ||
				commit.FormatVersion > ILPatchRepositoryCommit.CurrentFormatVersion) {
				throw new NotSupportedException(
					$"Unsupported repository commit format {commit.FormatVersion}. " +
					$"Supported range is {ILPatchRepositoryCommit.MinimumSupportedFormatVersion}-{ILPatchRepositoryCommit.CurrentFormatVersion}.");
			}
			if (!StringComparer.Ordinal.Equals(commit.Id, id))
				throw new InvalidDataException($"Commit file '{id}' contains id '{commit.Id}'.");
			return commit;
		}

		public ILPatchDocument LoadCommitPatch(string id) {
			var commit = LoadCommit(id);
			string patchPath = Path.Combine(RepositoryPath, commit.PatchFile.Replace('/', Path.DirectorySeparatorChar));
			return ILPatchSerializer.Load(patchPath);
		}

		/// <summary>
		/// Creates a parent-to-commit semantic delta for history review. Stored commit patches
		/// remain ROOT-to-commit snapshots so exporting a node never depends on its ancestors.
		/// </summary>
		public ILPatchDocument CreateCommitDelta(string id) {
			var commit = LoadCommit(id);
			using var parentModule = MaterializeState(commit.ParentId);
			using var currentModule = MaterializeState(id);
			if (!ILPatchDocumentCreator.TryCreate(parentModule, currentModule, commit.Message,
				out var delta, out var report) || delta is null) {
				throw new InvalidDataException(
					"Could not reconstruct parent-to-commit semantic delta: " +
					string.Join("; ", report.UnsupportedReasons));
			}
			delta.CreatedUtc = commit.CreatedUtc;
			return delta;
		}

		static Dictionary<string, MethodDef> BuildMethodMap(ModuleDef module) {
			var result = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					string key = ILPatchMethodIdentity.Create(method).ToCanonicalString();
					if (result.ContainsKey(key))
						throw new InvalidOperationException($"Method identity '{key}' is ambiguous.");
					result.Add(key, method);
				}
			}
			return result;
		}

		static void ValidateMethodShape(Dictionary<string, MethodDef> root,
			Dictionary<string, MethodDef> current) {
			if (root.Count != current.Count)
				throw new InvalidOperationException(
					$"Working module method shape differs from repository root (root={root.Count}, current={current.Count}). v1 repositories track existing CIL method bodies only.");
			foreach (var pair in root) {
				if (!current.TryGetValue(pair.Key, out var currentMethod))
					throw new InvalidOperationException($"Working module is missing or renamed method '{pair.Value.FullName}'.");
				if (pair.Value.Attributes != currentMethod.Attributes ||
					pair.Value.ImplAttributes != currentMethod.ImplAttributes)
					throw new InvalidOperationException($"Method metadata flags changed for '{pair.Value.FullName}'.");
				if ((pair.Value.Body is null) != (currentMethod.Body is null))
					throw new InvalidOperationException($"Method body kind changed for '{pair.Value.FullName}'.");
			}
		}


		static ILPatchMethodBodySnapshot CreateSnapshot(MethodDef method) {
			if (method.Body is null)
				throw new InvalidOperationException($"Method '{method.FullName}' has no CIL body.");
			var snapshot = CilNormalizer.CreateSnapshot(method);
			snapshot.CanonicalHash = ILPatchBodyHasher.Compute(snapshot);
			return snapshot;
		}

		void ValidateRootIntegrity() {
			if (!File.Exists(RootModulePath))
				throw new FileNotFoundException("Repository immutable ROOT assembly is missing.", RootModulePath);
			string fileHash = ILPatchModuleStateHasher.ComputeFileSha256(RootModulePath);
			if (!StringComparer.Ordinal.Equals(fileHash, Metadata.RootFileSha256)) {
				throw new InvalidDataException(
					"Repository immutable ROOT file hash does not match repo.json. The .dnspy/base assembly was modified or corrupted.");
			}
			using var module = ModuleDefMD.Load(RootModulePath);
			string stateHash = ILPatchModuleStateHasher.Compute(module);
			if (!StringComparer.Ordinal.Equals(stateHash, Metadata.RootStateHash)) {
				throw new InvalidDataException(
					"Repository immutable ROOT semantic state does not match repo.json.");
			}
			if (!string.IsNullOrEmpty(Metadata.RootStructureStateHash)) {
				string structureState = ILPatchAssemblyShapeGuard.ComputeFingerprint(module);
				if (!StringComparer.Ordinal.Equals(structureState, Metadata.RootStructureStateHash)) {
					throw new InvalidDataException(
						"Repository immutable ROOT structure state does not match repo.json.");
				}
			}
		}

		string GetCommitPath(string id) =>
			Path.Combine(RepositoryPath, CommitsDirectoryName, id + ".json");

		static string CreateCommitId(string? parentId, DateTime timestampUtc, string message, string patchJson) =>
			ILPatchModuleStateHasher.Sha256Hex(
				(parentId ?? "ROOT") + "\n" + timestampUtc.Ticks + "\n" + message + "\n" + patchJson);

		static string ShortHash(string hash) =>
			hash.Length <= 12 ? hash : hash.Substring(0, 12);

		static JsonSerializerSettings CreateJsonSettings() {
			var settings = new JsonSerializerSettings {
				Formatting = Formatting.Indented,
				NullValueHandling = NullValueHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Utc,
			};
			settings.Converters.Add(new StringEnumConverter());
			return settings;
		}

		static T ReadJson<T>(string path) {
			if (!File.Exists(path))
				throw new FileNotFoundException("Repository metadata file was not found.", path);
			var value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path), jsonSettings);
			return value ?? throw new InvalidDataException($"Repository JSON '{path}' is empty or invalid.");
		}

		static void WriteJsonAtomic<T>(string path, T value) =>
			WriteTextAtomic(path, JsonConvert.SerializeObject(value, jsonSettings));

		static void WriteTextAtomic(string path, string text) {
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
			string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
			File.WriteAllText(temp, text);
			if (File.Exists(path))
				File.Delete(path);
			File.Move(temp, path);
		}

		static void TryHideRepositoryDirectory(string path) {
			try {
				if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
					var info = new DirectoryInfo(path);
					info.Attributes |= System.IO.FileAttributes.Hidden;
				}
			}
			catch {
				// Repository usability must not depend on whether the filesystem supports Hidden.
			}
		}
	}
}
