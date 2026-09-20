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

	sealed class ILPatchRepositoryCommit {
		public const int CurrentFormatVersion = 1;
		public int FormatVersion { get; set; } = CurrentFormatVersion;
		public string Id { get; set; } = string.Empty;
		public string? ParentId { get; set; }
		public string Message { get; set; } = string.Empty;
		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public string PatchFile { get; set; } = string.Empty;
		public string StateHash { get; set; } = string.Empty;
		public List<ILPatchRepositoryMethodSummary> Changes { get; } = new List<ILPatchRepositoryMethodSummary>();
	}

	sealed class ILPatchRepositoryMetadata {
		public const int CurrentFormatVersion = 1;
		public int FormatVersion { get; set; } = CurrentFormatVersion;
		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public string ModuleFileName { get; set; } = string.Empty;
		public string AssemblyName { get; set; } = string.Empty;
		public Guid RootMvid { get; set; }
		public string RootFileSha256 { get; set; } = string.Empty;
		public string RootStateHash { get; set; } = string.Empty;
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
			lines.Add("shape|" + ILPatchAssemblyShapeGuard.ComputeFingerprint(module));
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
			if (metadata.FormatVersion != ILPatchRepositoryMetadata.CurrentFormatVersion)
				throw new NotSupportedException($"Unsupported .dnspy repository format {metadata.FormatVersion}.");
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
			if (selectedChanges.Count == 0)
				throw new ArgumentException("At least one working change must be selected for commit.", nameof(selectedChanges));
			if (string.IsNullOrWhiteSpace(message))
				throw new ArgumentException("Commit message cannot be empty.", nameof(message));

			using var rootModule = ModuleDefMD.Load(RootModulePath);
			ILPatchAssemblyShapeGuard.ThrowIfUnsupported(rootModule, currentModule);
			var rootMethods = BuildMethodMap(rootModule);
			var currentMethods = BuildMethodMap(currentModule);
			ValidateMethodShape(rootMethods, currentMethods);

			ILPatchDocument parentDocument = Metadata.HeadCommitId is null
				? new ILPatchDocument { Name = "ROOT" }
				: LoadCommitPatch(Metadata.HeadCommitId);
			var parentState = parentDocument.Methods.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
			var workingState = allWorkingChanges.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
			var selectedState = selectedChanges.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);

			ValidateWorkingBase(rootMethods, currentMethods, parentState, workingState);
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

			var fullState = new Dictionary<string, ILPatchMethodChange>(parentState, StringComparer.Ordinal);
			foreach (var pair in selectedState) {
				if (!rootMethods.TryGetValue(pair.Key, out var rootMethod))
					throw new InvalidOperationException($"Working change '{pair.Value.Target}' does not exist in the repository root.");

				var rootSnapshot = CreateSnapshot(rootMethod);
				var working = pair.Value;
				if (StringComparer.Ordinal.Equals(rootSnapshot.CanonicalHash, working.PatchedBody.CanonicalHash)) {
					fullState.Remove(pair.Key);
					continue;
				}

				parentState.TryGetValue(pair.Key, out var previous);
				fullState[pair.Key] = new ILPatchMethodChange {
					Id = previous?.Id ?? working.Id,
					Target = rootSnapshot.Method,
					BaseModuleMvid = rootModule.Mvid ?? Guid.Empty,
					BaseBody = rootSnapshot,
					PatchedBody = working.PatchedBody,
				};
			}

			var commitTime = DateTime.UtcNow;
			var document = new ILPatchDocument {
				Name = message.Trim(),
				CreatedUtc = commitTime,
			};
			document.Methods.AddRange(fullState.Values
				.OrderBy(a => a.Target.ToCanonicalString(), StringComparer.Ordinal));

			string serializedPatch = ILPatchSerializer.Serialize(document);
			string id = CreateCommitId(Metadata.HeadCommitId, commitTime, message.Trim(), serializedPatch);
			string patchRelativePath = Path.Combine(PatchesDirectoryName, id + ".ilpatch").Replace('\\', '/');
			var commit = new ILPatchRepositoryCommit {
				Id = id,
				ParentId = Metadata.HeadCommitId,
				Message = message.Trim(),
				CreatedUtc = commitTime,
				PatchFile = patchRelativePath,
				StateHash = ComputeDocumentStateHash(document),
			};
			PopulateSummary(commit, rootMethods, parentState, fullState);

			string patchPath = Path.Combine(RepositoryPath, patchRelativePath.Replace('/', Path.DirectorySeparatorChar));
			string commitPath = GetCommitPath(id);
			WriteTextAtomic(patchPath, serializedPatch);
			WriteJsonAtomic(commitPath, commit);

			Metadata.HeadCommitId = id;
			WriteJsonAtomic(Path.Combine(RepositoryPath, MetadataFileName), Metadata);
			return commit;
		}

		string ComputeDocumentStateHash(ILPatchDocument document) {
			using var module = ModuleDefMD.Load(RootModulePath);
			var report = ILPatchHeadlessApplier.Apply(module, document);
			if (!report.Success) {
				string details = string.Join("; ", report.Entries
					.Where(a => a.Action == ILPatchHeadlessAction.Unresolved)
					.Select(a => a.Message));
				throw new InvalidOperationException(
					"Could not materialize selected repository state from ROOT: " + details);
			}
			return ILPatchModuleStateHasher.Compute(module);
		}

		public bool TryValidateWorkingBase(ModuleDef currentModule,
			IReadOnlyList<ILPatchMethodChange> workingChanges, out string error) {
			try {
				ValidateRootIntegrity();
				using var rootModule = ModuleDefMD.Load(RootModulePath);
				ILPatchAssemblyShapeGuard.ThrowIfUnsupported(rootModule, currentModule);
				var rootMethods = BuildMethodMap(rootModule);
				var currentMethods = BuildMethodMap(currentModule);
				ValidateMethodShape(rootMethods, currentMethods);
				ILPatchDocument parentDocument = Metadata.HeadCommitId is null
					? new ILPatchDocument { Name = "ROOT" }
					: LoadCommitPatch(Metadata.HeadCommitId);
				var parentState = parentDocument.Methods.ToDictionary(
					a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
				var workingState = workingChanges.ToDictionary(
					a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
				ValidateWorkingBase(rootMethods, currentMethods, parentState, workingState);
				error = string.Empty;
				return true;
			}
			catch (Exception ex) {
				error = ex.Message;
				return false;
			}
		}

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
			var currentMethods = BuildMethodMap(currentModule);
			var desiredMethods = BuildMethodMap(desiredModule);
			ValidateMethodShape(desiredMethods, currentMethods);

			var result = new ILPatchDocument {
				Name = string.IsNullOrEmpty(commitId)
					? "Restore repository ROOT"
					: "Restore repository " + ShortHash(commitId),
				CreatedUtc = DateTime.UtcNow,
			};

			foreach (var pair in desiredMethods.OrderBy(a => a.Key, StringComparer.Ordinal)) {
				var desired = pair.Value;
				var current = currentMethods[pair.Key];
				if (desired.Body is null)
					continue;

				var before = CreateSnapshot(current);
				var after = CreateSnapshot(desired);
				if (StringComparer.Ordinal.Equals(before.CanonicalHash, after.CanonicalHash))
					continue;

				// Restore patches target the currently loaded method identity/module while preserving
				// the selected repository state's portable body snapshot.
				after.Method = before.Method;
				result.Methods.Add(new ILPatchMethodChange {
					Target = before.Method,
					BaseModuleMvid = currentModule.Mvid ?? Guid.Empty,
					BaseBody = before,
					PatchedBody = after,
				});
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
			if (commit.FormatVersion != ILPatchRepositoryCommit.CurrentFormatVersion)
				throw new NotSupportedException($"Unsupported repository commit format {commit.FormatVersion}.");
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
			var currentDocument = LoadCommitPatch(id);
			var parentDocument = commit.ParentId is null
				? new ILPatchDocument { Name = "ROOT" }
				: LoadCommitPatch(commit.ParentId);
			var currentState = currentDocument.Methods.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);
			var parentState = parentDocument.Methods.ToDictionary(
				a => a.Target.ToCanonicalString(), a => a, StringComparer.Ordinal);

			using var rootModule = ModuleDefMD.Load(RootModulePath);
			var rootMethods = BuildMethodMap(rootModule);
			var delta = new ILPatchDocument {
				Name = commit.Message,
				CreatedUtc = commit.CreatedUtc,
			};

			foreach (var summary in commit.Changes) {
				string key = summary.Target.ToCanonicalString();
				if (!rootMethods.TryGetValue(key, out var rootMethod) || rootMethod.Body is null)
					throw new InvalidDataException($"Repository commit references missing ROOT method '{summary.Target}'.");
				var rootSnapshot = CreateSnapshot(rootMethod);
				parentState.TryGetValue(key, out var parentChange);
				currentState.TryGetValue(key, out var currentChange);
				var before = parentChange?.PatchedBody ?? rootSnapshot;
				var after = currentChange?.PatchedBody ?? rootSnapshot;
				if (StringComparer.Ordinal.Equals(before.CanonicalHash, after.CanonicalHash))
					continue;
				delta.Methods.Add(new ILPatchMethodChange {
					Id = currentChange?.Id ?? parentChange?.Id ?? Guid.NewGuid().ToString("N"),
					Target = rootSnapshot.Method,
					BaseModuleMvid = rootModule.Mvid ?? Guid.Empty,
					BaseBody = before,
					PatchedBody = after,
				});
			}
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

		static void ValidateWorkingBase(Dictionary<string, MethodDef> rootMethods,
			Dictionary<string, MethodDef> currentMethods,
			Dictionary<string, ILPatchMethodChange> parentState,
			Dictionary<string, ILPatchMethodChange> workingState) {
			foreach (var pair in rootMethods) {
				if (pair.Value.Body is null)
					continue;
				var rootSnapshot = CreateSnapshot(pair.Value);
				string expectedHeadHash = parentState.TryGetValue(pair.Key, out var parentChange)
					? parentChange.PatchedBody.CanonicalHash
					: rootSnapshot.CanonicalHash;

				string actualBaseHash;
				if (workingState.TryGetValue(pair.Key, out var workingChange)) {
					actualBaseHash = workingChange.BaseBody.CanonicalHash;
				}
				else {
					var currentSnapshot = CreateSnapshot(currentMethods[pair.Key]);
					actualBaseHash = currentSnapshot.CanonicalHash;
				}

				if (!StringComparer.Ordinal.Equals(expectedHeadHash, actualBaseHash)) {
					throw new InvalidOperationException(
						$"Working tree is not based on repository HEAD at '{pair.Value.FullName}'. " +
						$"Expected {ShortHash(expectedHeadHash)}, found {ShortHash(actualBaseHash)}. " +
						"Export/open HEAD before committing new changes, or start a new repository.");
				}
			}
			foreach (var key in workingState.Keys) {
				if (!rootMethods.ContainsKey(key))
					throw new InvalidOperationException($"Working patch contains a method that is not present in the repository root: {key}");
			}
		}

		static void PopulateSummary(ILPatchRepositoryCommit commit,
			Dictionary<string, MethodDef> rootMethods,
			Dictionary<string, ILPatchMethodChange> parent,
			Dictionary<string, ILPatchMethodChange> current) {
			var keys = new SortedSet<string>(parent.Keys, StringComparer.Ordinal);
			keys.UnionWith(current.Keys);
			foreach (string key in keys) {
				var rootSnapshot = CreateSnapshot(rootMethods[key]);
				string before = parent.TryGetValue(key, out var parentChange)
					? parentChange.PatchedBody.CanonicalHash
					: rootSnapshot.CanonicalHash;
				string after = current.TryGetValue(key, out var currentChange)
					? currentChange.PatchedBody.CanonicalHash
					: rootSnapshot.CanonicalHash;
				if (StringComparer.Ordinal.Equals(before, after))
					continue;

				ILPatchRepositoryMethodChangeKind kind;
				bool beforeRoot = StringComparer.Ordinal.Equals(before, rootSnapshot.CanonicalHash);
				bool afterRoot = StringComparer.Ordinal.Equals(after, rootSnapshot.CanonicalHash);
				if (beforeRoot && !afterRoot)
					kind = ILPatchRepositoryMethodChangeKind.AddedChange;
				else if (!beforeRoot && afterRoot)
					kind = ILPatchRepositoryMethodChangeKind.Reverted;
				else
					kind = ILPatchRepositoryMethodChangeKind.Modified;

				commit.Changes.Add(new ILPatchRepositoryMethodSummary {
					Target = currentChange?.Target ?? parentChange?.Target ?? rootSnapshot.Method,
					Kind = kind,
					BeforeHash = before,
					AfterHash = after,
				});
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
