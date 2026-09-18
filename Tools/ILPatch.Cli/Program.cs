using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.AsmEditor.ILPatch {
	static class Program {
		sealed class ApplyOptions {
			public bool DryRun { get; set; }
			public string InputPath { get; set; } = string.Empty;
			public string? OutputPath { get; set; }
			public List<string> PatchPaths { get; } = new List<string>();
		}

		static int Main(string[] args) {
			try {
				return Run(args);
			}
			catch (Exception ex) {
				Console.Error.WriteLine("ILPatch CLI failed:");
				Console.Error.WriteLine(ex.Message);
				return 1;
			}
		}

		static int Run(string[] args) {
			if (args.Length == 0 || IsHelp(args[0])) {
				PrintUsage();
				return args.Length == 0 ? 1 : 0;
			}
			if (!StringComparer.OrdinalIgnoreCase.Equals(args[0], "apply")) {
				Console.Error.WriteLine($"Unknown command '{args[0]}'.");
				PrintUsage();
				return 1;
			}
			if (!TryParseApply(args.Skip(1).ToArray(), out var options, out string parseError)) {
				Console.Error.WriteLine(parseError);
				Console.Error.WriteLine();
				PrintApplyUsage();
				return 1;
			}

			string inputPath = Path.GetFullPath(options.InputPath);
			string? outputPath = options.OutputPath is null ? null : Path.GetFullPath(options.OutputPath);
			var patchPaths = options.PatchPaths.Select(Path.GetFullPath).ToArray();

			if (!File.Exists(inputPath))
				throw new FileNotFoundException("Input assembly was not found.", inputPath);
			foreach (string patchPath in patchPaths) {
				if (!File.Exists(patchPath))
					throw new FileNotFoundException("Patch file was not found.", patchPath);
			}
			if (!options.DryRun && outputPath is not null &&
				StringComparer.OrdinalIgnoreCase.Equals(inputPath, outputPath)) {
				throw new InvalidOperationException(
					"Input and output must be different files. ILPatch CLI never overwrites the source assembly in place.");
			}

			using var module = ModuleDefMD.Load(inputPath);
			Console.WriteLine($"Loaded: {inputPath}");
			Console.WriteLine($"Module: {module.FullName}");
			Console.WriteLine();

			int totalApplied = 0;
			int totalPresent = 0;
			foreach (string patchPath in patchPaths) {
				var document = ILPatchSerializer.Load(patchPath);
				Console.WriteLine($"== {Path.GetFileName(patchPath)} ==");

				var report = ILPatchHeadlessApplier.Apply(module, document);
				PrintReport(report);
				if (!report.Success) {
					Console.Error.WriteLine();
					Console.Error.WriteLine("Patch set contains unresolved entries. No output file was written.");
					return 2;
				}
				totalApplied += report.AppliedCount;
				totalPresent += report.AlreadyPresentCount;
				Console.WriteLine();
			}

			Console.WriteLine($"Summary: {totalApplied} applied, {totalPresent} already present.");
			if (options.DryRun) {
				Console.WriteLine("Dry run succeeded. No output file was written.");
				return 0;
			}

			if (outputPath is null)
				throw new InvalidOperationException("Output path is missing.");
			string? outputDirectory = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outputDirectory))
				Directory.CreateDirectory(outputDirectory);
			module.Write(outputPath);
			Console.WriteLine($"Wrote: {outputPath}");
			return 0;
		}

		static bool TryParseApply(string[] args, out ApplyOptions options, out string error) {
			options = new ApplyOptions();
			error = string.Empty;
			var positional = new List<string>();

			for (int i = 0; i < args.Length; i++) {
				string arg = args[i];
				if (StringComparer.Ordinal.Equals(arg, "--dry-run")) {
					options.DryRun = true;
					continue;
				}
				if (StringComparer.Ordinal.Equals(arg, "-o") ||
					StringComparer.Ordinal.Equals(arg, "--output")) {
					if (++i >= args.Length) {
						error = $"{arg} requires a filename.";
						return false;
					}
					if (options.OutputPath is not null) {
						error = "Output path was specified more than once.";
						return false;
					}
					options.OutputPath = args[i];
					continue;
				}
				if (IsHelp(arg)) {
					error = "Help requested.";
					return false;
				}
				if (arg.StartsWith("-", StringComparison.Ordinal)) {
					error = $"Unknown option '{arg}'.";
					return false;
				}
				positional.Add(arg);
			}

			if (positional.Count < 2) {
				error = "Apply requires an input assembly and at least one .ilpatch file.";
				return false;
			}
			options.InputPath = positional[0];
			options.PatchPaths.AddRange(positional.Skip(1));

			if (!options.DryRun && string.IsNullOrWhiteSpace(options.OutputPath)) {
				error = "Apply requires -o <output.dll> unless --dry-run is used.";
				return false;
			}
			return true;
		}

		static void PrintReport(ILPatchHeadlessApplyReport report) {
			foreach (var entry in report.Entries) {
				string target = entry.Target is null
					? entry.Patch.Target?.ToString() ?? "<invalid target>"
					: ILPatchMethodIdentity.Create(entry.Target).ToString();
				string prefix;
				switch (entry.Action) {
				case ILPatchHeadlessAction.AppliedExact:
					prefix = "EXACT";
					break;
				case ILPatchHeadlessAction.AppliedCleanRebase:
					prefix = "REBASE";
					break;
				case ILPatchHeadlessAction.AlreadyPresent:
					prefix = "PRESENT";
					break;
				default:
					prefix = "ERROR";
					break;
				}

				var writer = entry.Action == ILPatchHeadlessAction.Unresolved ? Console.Error : Console.Out;
				writer.WriteLine($"[{prefix,-7}] {target}");
				if (entry.Action == ILPatchHeadlessAction.Unresolved)
					writer.WriteLine($"          {entry.Message}");
			}
		}

		static bool IsHelp(string value) =>
			StringComparer.Ordinal.Equals(value, "-h") ||
			StringComparer.Ordinal.Equals(value, "--help") ||
			StringComparer.Ordinal.Equals(value, "help");

		static void PrintUsage() {
			Console.WriteLine("ILPatch - replay normalized CIL patches onto managed assemblies");
			Console.WriteLine();
			Console.WriteLine("Commands:");
			Console.WriteLine("  apply    Apply one or more .ilpatch files to an assembly");
			Console.WriteLine();
			PrintApplyUsage();
		}

		static void PrintApplyUsage() {
			Console.WriteLine("Usage:");
			Console.WriteLine("  ilpatch apply <input.dll> <patch1.ilpatch> [patch2.ilpatch ...] -o <output.dll>");
			Console.WriteLine("  ilpatch apply --dry-run <input.dll> <patch1.ilpatch> [patch2.ilpatch ...]");
			Console.WriteLine();
			Console.WriteLine("Exit codes:");
			Console.WriteLine("  0  Success");
			Console.WriteLine("  1  Usage, I/O, parse, or unexpected failure");
			Console.WriteLine("  2  Patch conflict / missing / ambiguous / unsupported target; no output written");
		}
	}
}
