using CsvHelper;
using CsvHelper.Configuration.Attributes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Resolves a SKU part number (e.g. <c>ENTERPRISEPACK</c>) to Microsoft's product display name
    /// (e.g. "Office 365 E3"), using the licensing CSV Microsoft publishes, embedded in this assembly.
    /// </summary>
    /// <remarks>
    /// Source: https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference
    /// </remarks>
    public class OfficeLicenseNameResolver : IOfficeLicenseNameResolver
    {
        /// <summary>
        /// Manifest name of the embedded CSV. Format: "{Namespace}.{Folder}.{filename}.{Extension}"
        /// </summary>
        public const string RESOURCE_NAME = "WebJob.Office365ActivityImporter.Engine.Resources.Product_names_and_service_plan_identifiers_for_licensing.csv";

        // Keyed by part number after NormaliseWhitespace, compared case-insensitively.
        private readonly Dictionary<string, string> _displayNamesBySku = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public OfficeLicenseNameResolver()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(RESOURCE_NAME))
            {
                if (stream == null)
                {
                    throw new ArgumentOutOfRangeException(nameof(RESOURCE_NAME), $"No resource found by name '{RESOURCE_NAME}'");
                }

                using (var reader = new StreamReader(stream))
                {
                    Load(reader);
                }
            }
        }

        /// <summary>
        /// Reads a CSV in the format of Microsoft's published file instead of the embedded copy. The
        /// caller keeps ownership of <paramref name="csv"/>.
        /// </summary>
        public OfficeLicenseNameResolver(TextReader csv)
        {
            if (csv == null) throw new ArgumentNullException(nameof(csv));
            Load(csv);
        }

        /// <summary>
        /// Every SKU part number the list can resolve, normalised.
        /// </summary>
        public IReadOnlyCollection<string> SkuPartNumbers => _displayNamesBySku.Keys;

        /// <summary>
        /// The product display name for SKU part number <paramref name="id"/>, or null when Microsoft's
        /// list has no such part number. Case-insensitive, and blind to leading, trailing and
        /// non-breaking whitespace on either side.
        /// </summary>
        public string GetDisplayNameFor(string id)
        {
            var sku = NormaliseWhitespace(id);
            if (sku.Length == 0)
            {
                return null;
            }

            return _displayNamesBySku.TryGetValue(sku, out var displayName) ? displayName : null;
        }

        private void Load(TextReader reader)
        {
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture, leaveOpen: true))
            {
                foreach (var record in csv.GetRecords<OfficeNamesCsvImportLine>())
                {
                    var sku = NormaliseWhitespace(record.IdString);
                    var displayName = NormaliseWhitespace(record.DisplayName);
                    if (sku.Length == 0 || displayName.Length == 0)
                    {
                        continue;
                    }

                    // The first row for a part number wins, as it always has. Microsoft's file lists a
                    // few part numbers under two product names (VIRTUAL_AGENT_USL is both "Microsoft
                    // Copilot Studio User License" and "Power Virtual Agent User License"), and licence
                    // types are stored by name, so picking differently would rename them.
                    if (!_displayNamesBySku.ContainsKey(sku))
                    {
                        _displayNamesBySku.Add(sku, displayName);
                    }
                }
            }
        }

        /// <summary>
        /// Trims <paramref name="value"/> and turns any whitespace left inside it into an ordinary space.
        /// </summary>
        /// <remarks>
        /// Microsoft's published CSV has invisible characters in a handful of part numbers that Graph
        /// does not send: a trailing tab on <c>RIGHTSMANAGEMENT_CE_GOV</c>, a trailing non-breaking
        /// space on <c>MCOCAP_FACULTY</c>, trailing spaces on three <c>_USGOV_GCCHIGH</c> SKUs, and
        /// non-breaking spaces instead of spaces in <c>O365_w/o Teams Bundle_M3</c> (Microsoft 365 E3
        /// EEA (no Teams)). Compared verbatim, those SKUs never resolved: they were stored under their
        /// raw part numbers and reported as unlisted in the anonymous stats. The characters are in
        /// Microsoft's current download too, so refreshing the file does not remove them.
        /// </remarks>
        private static string NormaliseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // String.Trim() removes every Unicode whitespace character, tab and non-breaking space included.
            var chars = value.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsWhiteSpace(chars[i]))
                {
                    chars[i] = ' ';
                }
            }

            return new string(chars);
        }
    }

    public class OfficeNamesCsvImportLine
    {
        [Name("Product_Display_Name")]
        public string DisplayName { get; set; }

        [Name("String_Id")]
        public string IdString { get; set; }

        public override string ToString()
        {
            return $"{this.IdString} ({this.DisplayName})";
        }
    }
}
