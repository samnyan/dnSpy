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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace dnSpy.AsmEditor.ILPatch {
	/// <summary>
	/// Computes the portable method-body hash used by ILPatch. Method identity and MaxStack
	/// are deliberately excluded: neither changes the semantic CIL body that a patch targets.
	/// </summary>
	static class ILPatchBodyHasher {
		public static string Compute(ILPatchMethodBodySnapshot snapshot) {
			var builder = new StringBuilder();
			builder.Append("initlocals=").Append(snapshot.InitLocals).AppendLine();
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
