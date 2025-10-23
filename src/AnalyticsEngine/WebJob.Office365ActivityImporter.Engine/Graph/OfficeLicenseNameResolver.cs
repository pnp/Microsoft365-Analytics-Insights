using CsvHelper;
using CsvHelper.Configuration.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// CSV wrapper for license names
    /// </summary>
    public class OfficeLicenseNameResolver
    {
        private List<OfficeNamesCsvImportLine> _records = new List<OfficeNamesCsvImportLine>();
        public OfficeLicenseNameResolver()
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Format: "{Namespace}.{Folder}.{filename}.{Extension}"
            const string RESOURCE_NAME = "WebJob.Office365ActivityImporter.Engine.Resources.Product_names_and_service_plan_identifiers_for_licensing.csv";
            using (var stream = assembly.GetManifestResourceStream(RESOURCE_NAME))
                if (stream != null)
                {
                    using (var sr = new StreamReader(stream))
                    {
                        using (var csv = new CsvReader(sr, System.Globalization.CultureInfo.InvariantCulture))
                        {
                            _records = csv.GetRecords<OfficeNamesCsvImportLine>().ToList();
                        }
                    }
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(RESOURCE_NAME), $"No resource found by name '{RESOURCE_NAME}'");
                }

        }

        public string GetDisplayNameFor(string id)
        {
            var result = _records.Where(r => r.IdString.ToLower() == id.ToLower()).FirstOrDefault();
            if (result == null)
                return null;

            return result.DisplayName;
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
