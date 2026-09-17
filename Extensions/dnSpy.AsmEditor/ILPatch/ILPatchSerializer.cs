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
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace dnSpy.AsmEditor.ILPatch {
	static class ILPatchSerializer {
		static JsonSerializerSettings CreateSettings() {
			var settings = new JsonSerializerSettings {
				Formatting = Formatting.Indented,
				NullValueHandling = NullValueHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Utc,
			};
			settings.Converters.Add(new StringEnumConverter());
			return settings;
		}

		public static string Serialize(ILPatchDocument document) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			return JsonConvert.SerializeObject(document, CreateSettings());
		}

		public static ILPatchDocument Deserialize(string json) {
			if (json is null)
				throw new ArgumentNullException(nameof(json));
			var document = JsonConvert.DeserializeObject<ILPatchDocument>(json, CreateSettings());
			if (document is null)
				throw new InvalidDataException("The ILPatch document is empty or invalid.");
			if (document.FormatVersion != ILPatchDocument.CurrentFormatVersion)
				throw new NotSupportedException($"Unsupported ILPatch format version {document.FormatVersion}. Expected {ILPatchDocument.CurrentFormatVersion}.");
			return document;
		}

		public static void Save(string filename, ILPatchDocument document) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			File.WriteAllText(filename, Serialize(document));
		}

		public static ILPatchDocument Load(string filename) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			return Deserialize(File.ReadAllText(filename));
		}
	}
}
