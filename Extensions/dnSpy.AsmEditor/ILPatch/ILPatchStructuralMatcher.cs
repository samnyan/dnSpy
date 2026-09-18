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
using dnlib.DotNet;

namespace dnSpy.AsmEditor.ILPatch {
	sealed class ILPatchStructuralCandidate {
		public MethodDef Method { get; }
		public ILPatchMethodIdentity Identity { get; }
		public double Score { get; }
		public string CurrentBodyHash { get; }
		public string Explanation { get; }

		public ILPatchStructuralCandidate(MethodDef method, ILPatchMethodIdentity identity, double score,
			string currentBodyHash, string explanation) {
			Method = method ?? throw new ArgumentNullException(nameof(method));
			Identity = identity ?? throw new ArgumentNullException(nameof(identity));
			Score = score;
			CurrentBodyHash = currentBodyHash ?? string.Empty;
			Explanation = explanation ?? string.Empty;
		}
	}

	/// <summary>
	/// Scores a stored baseline body against current methods without mutating anything.
	/// This is intentionally advisory: a high score only improves Preview and does not
	/// authorize patch application. The three-way rebase layer will make that decision later.
	/// </summary>
	static class ILPatchStructuralMatcher {
		const int MaxCandidates = 5;

		public sealed class Catalog {
			sealed class CandidateHeader {
				public MethodDef Method { get; }
				public ILPatchMethodIdentity Identity { get; }

				public CandidateHeader(MethodDef method, ILPatchMethodIdentity identity) {
					Method = method;
					Identity = identity;
				}
			}

			sealed class CandidateData {
				public ILPatchMethodIdentity Identity { get; }
				public Fingerprint Fingerprint { get; }
				public string BodyHash { get; }

				public CandidateData(ILPatchMethodIdentity identity, Fingerprint fingerprint, string bodyHash) {
					Identity = identity;
					Fingerprint = fingerprint;
					BodyHash = bodyHash;
				}
			}

			readonly Dictionary<string, List<CandidateHeader>> coarseIndex =
				new Dictionary<string, List<CandidateHeader>>(StringComparer.Ordinal);
			readonly Dictionary<MethodDef, CandidateData> cache = new Dictionary<MethodDef, CandidateData>();

			internal Catalog(IEnumerable<MethodDef> methods) {
				foreach (var method in methods.Where(a => a is not null && a.Body is not null).Distinct()) {
					var identity = ILPatchMethodIdentity.Create(method);
					string key = CreateCoarseKey(identity);
					if (!coarseIndex.TryGetValue(key, out var bucket))
						coarseIndex.Add(key, bucket = new List<CandidateHeader>());
					bucket.Add(new CandidateHeader(method, identity));
				}
			}

			public IReadOnlyList<ILPatchStructuralCandidate> FindCandidates(ILPatchMethodChange patch) {
				if (patch is null)
					throw new ArgumentNullException(nameof(patch));
				if (patch.Target is null || patch.BaseBody is null)
					return Array.Empty<ILPatchStructuralCandidate>();

				if (!coarseIndex.TryGetValue(CreateCoarseKey(patch.Target), out var bucket))
					return Array.Empty<ILPatchStructuralCandidate>();

				var source = new Fingerprint(patch.Target, patch.BaseBody);
				var candidates = new List<ILPatchStructuralCandidate>(bucket.Count);

				foreach (var header in bucket) {
					var current = GetCandidateData(header);
					double score = Score(source, current.Fingerprint, out string explanation);
					candidates.Add(new ILPatchStructuralCandidate(header.Method, current.Identity, score, current.BodyHash, explanation));
				}

				return candidates
					.OrderByDescending(a => a.Score)
					.ThenBy(a => a.Identity.ToCanonicalString(), StringComparer.Ordinal)
					.Take(MaxCandidates)
					.ToArray();
			}

			CandidateData GetCandidateData(CandidateHeader header) {
				if (cache.TryGetValue(header.Method, out var data))
					return data;
				var body = CilNormalizer.CreateSnapshot(header.Method);
				body.CanonicalHash = ILPatchBodyHasher.Compute(body);
				data = new CandidateData(header.Identity, new Fingerprint(header.Identity, body), body.CanonicalHash);
				cache.Add(header.Method, data);
				return data;
			}
		}

		public static Catalog CreateCatalog(IEnumerable<MethodDef> methods) {
			if (methods is null)
				throw new ArgumentNullException(nameof(methods));
			return new Catalog(methods);
		}

		sealed class Fingerprint {
			public ILPatchMethodIdentity Identity { get; }
			public int InstructionCount { get; }
			public HashSet<string> OpcodeNgrams { get; } = new HashSet<string>(StringComparer.Ordinal);
			public HashSet<string> Calls { get; } = new HashSet<string>(StringComparer.Ordinal);
			public HashSet<string> Fields { get; } = new HashSet<string>(StringComparer.Ordinal);
			public HashSet<string> Strings { get; } = new HashSet<string>(StringComparer.Ordinal);
			public HashSet<string> Types { get; } = new HashSet<string>(StringComparer.Ordinal);
			public HashSet<long> Constants { get; } = new HashSet<long>();
			public HashSet<string> LocalTypes { get; } = new HashSet<string>(StringComparer.Ordinal);
			public string[] HandlerTypes { get; }

			public Fingerprint(ILPatchMethodIdentity identity, ILPatchMethodBodySnapshot body) {
				Identity = identity;
				InstructionCount = body.Instructions.Count;
				foreach (string local in body.Locals)
					LocalTypes.Add(local ?? string.Empty);
				HandlerTypes = body.ExceptionHandlers.Select(a => a.HandlerType ?? string.Empty).ToArray();

				var opcodes = new string[body.Instructions.Count];
				for (int i = 0; i < body.Instructions.Count; i++) {
					var instruction = body.Instructions[i];
					opcodes[i] = instruction.OpCode ?? string.Empty;
					var operand = instruction.Operand ?? ILPatchOperand.None;
					switch (operand.Kind) {
					case ILPatchOperandKind.Method:
						if (!string.IsNullOrEmpty(operand.Text))
							Calls.Add(operand.Text);
						break;
					case ILPatchOperandKind.Field:
						if (!string.IsNullOrEmpty(operand.Text))
							Fields.Add(operand.Text);
						break;
					case ILPatchOperandKind.String:
						Strings.Add(operand.Text ?? string.Empty);
						break;
					case ILPatchOperandKind.Type:
						if (!string.IsNullOrEmpty(operand.Text))
							Types.Add(operand.Text);
						break;
					case ILPatchOperandKind.Integer:
						Constants.Add(operand.IntegerValue);
						break;
					}
				}

				int ngramSize = opcodes.Length >= 3 ? 3 : 1;
				if (ngramSize == 1) {
					foreach (string opCode in opcodes)
						OpcodeNgrams.Add(opCode);
				}
				else {
					for (int i = 0; i <= opcodes.Length - ngramSize; i++)
						OpcodeNgrams.Add(string.Join("\u001F", opcodes.Skip(i).Take(ngramSize)));
				}
			}
		}

		static string CreateCoarseKey(ILPatchMethodIdentity identity) =>
			$"{identity.AssemblyName}\u001F{identity.ModuleName}\u001F{identity.HasThis}\u001F{identity.GenericArity}\u001F{identity.ParameterTypes.Count}";

		static double Score(Fingerprint source, Fingerprint candidate, out string explanation) {
			double weightedScore = 0;
			double totalWeight = 0;
			var details = new List<string>();

			AddScore("sig", 0.20, SignatureSimilarity(source.Identity, candidate.Identity), ref weightedScore, ref totalWeight, details);
			AddScore("op", 0.30, Jaccard(source.OpcodeNgrams, candidate.OpcodeNgrams), ref weightedScore, ref totalWeight, details);
			AddScore("len", 0.05, CountSimilarity(source.InstructionCount, candidate.InstructionCount), ref weightedScore, ref totalWeight, details);
			AddScore("eh", 0.05, SequenceSimilarity(source.HandlerTypes, candidate.HandlerTypes), ref weightedScore, ref totalWeight, details);
			AddOptionalSetScore("call", 0.15, source.Calls, candidate.Calls, ref weightedScore, ref totalWeight, details);
			AddOptionalSetScore("field", 0.10, source.Fields, candidate.Fields, ref weightedScore, ref totalWeight, details);
			AddOptionalSetScore("str", 0.10, source.Strings, candidate.Strings, ref weightedScore, ref totalWeight, details);
			AddOptionalSetScore("type", 0.05, source.Types, candidate.Types, ref weightedScore, ref totalWeight, details);
			AddOptionalSetScore("const", 0.04, source.Constants, candidate.Constants, ref weightedScore, ref totalWeight, details);
			AddOptionalSetScore("local", 0.04, source.LocalTypes, candidate.LocalTypes, ref weightedScore, ref totalWeight, details);

			double score = totalWeight == 0 ? 0 : weightedScore / totalWeight;

			// Names are intentionally only small bonuses. They help when the method simply changed
			// internally, but do not drown out structural evidence after a rename/obfuscation pass.
			if (StringComparer.Ordinal.Equals(source.Identity.MethodName, candidate.Identity.MethodName))
				score += 0.025;
			if (StringComparer.Ordinal.Equals(source.Identity.DeclaringType, candidate.Identity.DeclaringType))
				score += 0.025;
			score = Math.Max(0, Math.Min(1, score));

			explanation = string.Join(", ", details) +
				$", total={(score * 100).ToString("F1", CultureInfo.InvariantCulture)}%";
			return score;
		}

		static double SignatureSimilarity(ILPatchMethodIdentity source, ILPatchMethodIdentity candidate) {
			double matches = StringComparer.Ordinal.Equals(source.ReturnType, candidate.ReturnType) ? 1 : 0;
			double total = 1;
			for (int i = 0; i < source.ParameterTypes.Count; i++) {
				total++;
				if (StringComparer.Ordinal.Equals(source.ParameterTypes[i], candidate.ParameterTypes[i]))
					matches++;
			}
			return matches / total;
		}

		static double SequenceSimilarity(IReadOnlyList<string> source, IReadOnlyList<string> candidate) {
			if (source.Count == 0 && candidate.Count == 0)
				return 1;
			int max = Math.Max(source.Count, candidate.Count);
			if (max == 0)
				return 1;
			int matches = 0;
			for (int i = 0; i < Math.Min(source.Count, candidate.Count); i++) {
				if (StringComparer.Ordinal.Equals(source[i], candidate[i]))
					matches++;
			}
			return (double)matches / max;
		}

		static double CountSimilarity(int source, int candidate) {
			if (source == 0 && candidate == 0)
				return 1;
			int max = Math.Max(source, candidate);
			return max == 0 ? 1 : (double)Math.Min(source, candidate) / max;
		}

		static double Jaccard<T>(ISet<T> source, ISet<T> candidate) {
			if (source.Count == 0 && candidate.Count == 0)
				return 1;
			if (source.Count == 0 || candidate.Count == 0)
				return 0;

			int intersection = 0;
			foreach (var value in source) {
				if (candidate.Contains(value))
					intersection++;
			}
			int union = source.Count + candidate.Count - intersection;
			return union == 0 ? 1 : (double)intersection / union;
		}

		static void AddScore(string name, double weight, double score, ref double weightedScore,
			ref double totalWeight, List<string> details) {
			weightedScore += weight * score;
			totalWeight += weight;
			details.Add($"{name}={(score * 100).ToString("F0", CultureInfo.InvariantCulture)}%");
		}

		static void AddOptionalSetScore<T>(string name, double weight, ISet<T> source, ISet<T> candidate,
			ref double weightedScore, ref double totalWeight, List<string> details) {
			if (source.Count == 0)
				return;
			AddScore(name, weight, Jaccard(source, candidate), ref weightedScore, ref totalWeight, details);
		}
	}
}
