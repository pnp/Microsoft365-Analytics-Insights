using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Common.Entities.Licences
{
    /// <summary>
    /// Microsoft's published licensing reference ("Product names and service plan identifiers for licensing"),
    /// as shipped in this build: SKU part number, SKU GUID, product display name and the service plans each SKU
    /// carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user import files licences under the display name this file gives a part number, so the file decides
    /// how licence types are named and therefore which SKUs share a licence type. The portal's licence
    /// diagnostics read the same file to explain that, to map the SKU GUIDs Microsoft Graph reports onto part
    /// numbers, and to show when the build's copy is older than a SKU a tenant holds.
    /// </para>
    /// <para>
    /// Source: https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference.
    /// The importer's <c>OfficeLicenseNameResolver</c> reads the same embedded copy, and a test holds the two
    /// name resolutions identical.
    /// </para>
    /// </remarks>
    public sealed class LicensingCatalogue
    {
        /// <summary>Manifest name of the embedded CSV in <c>Common.Entities</c>.</summary>
        public const string ResourceName = "Common.Entities.Resources.Product_names_and_service_plan_identifiers_for_licensing.csv";

        /// <summary>
        /// Service plans that make a SKU carry a Microsoft 365 Copilot seat: Copilot in the Microsoft 365 apps
        /// and Microsoft 365 Copilot Chat grounded in work data. Every SKU Microsoft lists with both is sold as,
        /// or includes, the Copilot seat.
        /// </summary>
        public static readonly string[] CopilotSeatServicePlans = { "M365_COPILOT_APPS", "M365_COPILOT_BUSINESS_CHAT" };

        private static readonly Lazy<LicensingCatalogue> EmbeddedCatalogue = new Lazy<LicensingCatalogue>(LoadEmbedded);

        private readonly Dictionary<string, CatalogueSku> _byPartNumber = new Dictionary<string, CatalogueSku>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, CatalogueSku> _byGuid = new Dictionary<Guid, CatalogueSku>();

        private LicensingCatalogue(string contentHash)
        {
            ContentHash = contentHash;
        }

        /// <summary>The copy embedded in this build. Parsed once per process.</summary>
        public static LicensingCatalogue Embedded => EmbeddedCatalogue.Value;

        /// <summary>
        /// The first twelve hex digits of the file's SHA-256, so an administrator can tell two builds' copies apart.
        /// The file carries no publication date of its own, so none is claimed.
        /// </summary>
        public string ContentHash { get; }

        /// <summary>How many distinct SKU part numbers the file lists.</summary>
        public int SkuCount => _byPartNumber.Count;

        /// <summary>Opens the embedded CSV. The caller disposes the stream.</summary>
        public static Stream OpenEmbeddedCsv()
        {
            var stream = typeof(LicensingCatalogue).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException($"No embedded resource named '{ResourceName}'.");
            }
            return stream;
        }

        /// <summary>The SKU with this part number, or null when the file does not list it.</summary>
        public CatalogueSku FindByPartNumber(string skuPartNumber)
        {
            var key = NormaliseWhitespace(skuPartNumber);
            return key.Length > 0 && _byPartNumber.TryGetValue(key, out var sku) ? sku : null;
        }

        /// <summary>The SKU with this GUID (Graph's <c>skuId</c>), or null when the file does not list it.</summary>
        public CatalogueSku FindByGuid(Guid skuId)
        {
            return _byGuid.TryGetValue(skuId, out var sku) ? sku : null;
        }

        /// <summary>Reads a CSV in the format of Microsoft's published file. The caller keeps ownership of <paramref name="csv"/>.</summary>
        public static LicensingCatalogue Parse(TextReader csv)
        {
            if (csv == null) throw new ArgumentNullException(nameof(csv));
            var text = csv.ReadToEnd();
            return Parse(text, Hash(Encoding.UTF8.GetBytes(text)));
        }

        private static LicensingCatalogue LoadEmbedded()
        {
            using (var stream = OpenEmbeddedCsv())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                using (var reader = new StreamReader(new MemoryStream(bytes)))
                {
                    return Parse(reader.ReadToEnd(), Hash(bytes));
                }
            }
        }

        private static LicensingCatalogue Parse(string text, string hash)
        {
            var catalogue = new LicensingCatalogue(hash);
            var rows = CsvRows(text).ToList();
            if (rows.Count == 0)
            {
                return catalogue;
            }

            var header = rows[0];
            var nameIndex = header.FindIndex(h => string.Equals(h.Trim().TrimStart('\uFEFF'), "Product_Display_Name", StringComparison.OrdinalIgnoreCase));
            var skuIndex = header.FindIndex(h => string.Equals(h.Trim(), "String_Id", StringComparison.OrdinalIgnoreCase));
            var guidIndex = header.FindIndex(h => string.Equals(h.Trim(), "GUID", StringComparison.OrdinalIgnoreCase));
            var planIndex = header.FindIndex(h => string.Equals(h.Trim(), "Service_Plan_Name", StringComparison.OrdinalIgnoreCase));
            if (nameIndex < 0 || skuIndex < 0)
            {
                throw new InvalidDataException("The licensing CSV has no Product_Display_Name or String_Id column.");
            }

            foreach (var row in rows.Skip(1))
            {
                var partNumber = NormaliseWhitespace(Field(row, skuIndex));
                var displayName = NormaliseWhitespace(Field(row, nameIndex));
                if (partNumber.Length == 0 || displayName.Length == 0)
                {
                    continue;
                }

                if (!catalogue._byPartNumber.TryGetValue(partNumber, out var sku))
                {
                    // The first row for a part number names it, exactly as the importer names licence types.
                    sku = new CatalogueSku(partNumber, displayName);
                    catalogue._byPartNumber.Add(partNumber, sku);
                }

                if (Guid.TryParse(Field(row, guidIndex)?.Trim(), out var guid) && guid != Guid.Empty)
                {
                    sku.AddGuid(guid);
                    if (!catalogue._byGuid.ContainsKey(guid))
                    {
                        catalogue._byGuid.Add(guid, sku);
                    }
                }

                var plan = NormaliseWhitespace(Field(row, planIndex));
                if (plan.Length > 0)
                {
                    sku.AddServicePlan(plan);
                }
            }

            return catalogue;
        }

        private static string Field(List<string> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

        /// <summary>RFC 4180 records: quoted fields may hold commas, doubled quotes and line breaks.</summary>
        internal static IEnumerable<List<string>> CsvRows(string text)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var rowHasContent = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        rowHasContent = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        rowHasContent = true;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        if (rowHasContent || field.Length > 0)
                        {
                            row.Add(field.ToString());
                            yield return row;
                        }
                        row = new List<string>();
                        field.Clear();
                        rowHasContent = false;
                        break;
                    default:
                        field.Append(c);
                        rowHasContent = true;
                        break;
                }
            }

            if (rowHasContent || field.Length > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }

        /// <summary>
        /// Trims, and turns whitespace inside the value into ordinary spaces - the same normalisation the importer
        /// applies, because Microsoft's file has tabs and non-breaking spaces in a few part numbers Graph does not send.
        /// </summary>
        internal static string NormaliseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

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

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes);
                return BitConverter.ToString(digest, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    /// <summary>One SKU in the <see cref="LicensingCatalogue"/>.</summary>
    public sealed class CatalogueSku
    {
        private readonly List<Guid> _guids = new List<Guid>();
        private readonly HashSet<string> _servicePlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal CatalogueSku(string partNumber, string displayName)
        {
            PartNumber = partNumber;
            DisplayName = displayName;
        }

        public string PartNumber { get; }

        /// <summary>The name the importer files this SKU's licence type under.</summary>
        public string DisplayName { get; }

        public IReadOnlyList<Guid> Guids => _guids;

        public IReadOnlyCollection<string> ServicePlans => _servicePlans;

        /// <summary>
        /// True when the file lists every one of <see cref="LicensingCatalogue.CopilotSeatServicePlans"/> in this
        /// SKU, i.e. Microsoft sells it as, or bundles it with, the Microsoft 365 Copilot seat.
        /// </summary>
        public bool CarriesCopilotSeatServicePlans =>
            LicensingCatalogue.CopilotSeatServicePlans.All(plan => _servicePlans.Contains(plan));

        internal void AddGuid(Guid guid)
        {
            if (!_guids.Contains(guid)) _guids.Add(guid);
        }

        internal void AddServicePlan(string plan) => _servicePlans.Add(plan);
    }
}
