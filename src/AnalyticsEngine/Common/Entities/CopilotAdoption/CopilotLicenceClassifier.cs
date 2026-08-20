using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Decides which rows of <c>dbo.license_types</c> represent a paid <b>Microsoft 365 Copilot seat</b>
    /// - the licence the adoption tool is about - as opposed to the many other Microsoft products that
    /// merely have the word "Copilot" in their name (Copilot Studio, Copilot for Sales, Sales Copilot,
    /// Power Virtual Agents...).
    ///
    /// Why this needs to be more than a name match: the user import stores the SKU <i>part number</i> in
    /// <see cref="LicenseType.SKUID"/> and a friendly product name in <see cref="LicenseType.Name"/>,
    /// resolved from Microsoft's published licensing CSV. When Microsoft ships a SKU that is newer than
    /// the CSV shipped in this build, the resolver has no display name and the part number is stored as
    /// the name as well. So a classifier that only looked at the display name would silently miss every
    /// brand-new Copilot SKU - exactly the ones a customer is most likely to have just bought - and a
    /// classifier that only looked at a hard-coded list of exact SKUs would miss them too.
    ///
    /// The rules below are therefore, in order:
    /// <list type="number">
    ///   <item>An explicit exclusion list of "Copilot-branded but not a Microsoft 365 Copilot seat" SKUs.</item>
    ///   <item>A prefix match on the SKU part number, which catches present and future variants
    ///         (<c>M365_Copilot</c>, <c>Microsoft_365_Copilot</c>, <c>Microsoft_365_Copilot_EDU</c>, ...).</item>
    ///   <item>A conservative display-name fallback for SKUs whose part number Microsoft renames.</item>
    /// </list>
    ///
    /// Classification is deliberately <b>visible and overridable</b> rather than silent: the API exposes
    /// what it decided for every licence type, and callers can override the set of licence-type ids for
    /// a request. A misclassified SKU is then an obvious, correctable fact on screen rather than a wrong
    /// number in a board pack.
    /// </summary>
    public static class CopilotLicenceClassifier
    {
        /// <summary>
        /// SKU part-number prefixes that identify a Microsoft 365 Copilot seat. Matched
        /// case-insensitively, after <see cref="ExcludedSkuPrefixes"/> has had its say.
        ///
        /// Prefixes (not exact values) so variants Microsoft has not shipped yet - regional, education,
        /// government and "business" editions all follow the same stem - are counted automatically
        /// instead of quietly dropping out of the adoption numbers until this file is next edited.
        /// </summary>
        public static readonly string[] SeatSkuPrefixes = new[]
        {
            "M365_COPILOT",             // e.g. M365_Copilot
            "MICROSOFT_365_COPILOT",    // e.g. Microsoft_365_Copilot, Microsoft_365_Copilot_EDU
        };

        /// <summary>
        /// SKU part-number prefixes that are Copilot-branded but are NOT a Microsoft 365 Copilot seat.
        /// Checked first, so a future SKU that matches both a seat prefix and an exclusion is excluded.
        ///
        /// These are real, separately-sold products. Counting them as seats would inflate the licensed
        /// population and make the adoption rate look far worse than it is - the failure mode that
        /// matters most here, because this tool is used to justify spend.
        /// </summary>
        public static readonly string[] ExcludedSkuPrefixes = new[]
        {
            "MICROSOFT_COPILOT_FOR_SALES",  // Microsoft 365 Copilot for Sales - a separate add-on
            "MICROSOFT_VIVA_SALES",         // Microsoft Sales Copilot (the former name of the above)
            "MICROSOFT_COPILOT_STUDIO",     // Copilot Studio - an authoring tool, not a Copilot seat
            "COPILOT_STUDIO",
            "POWER_VIRTUAL_AGENTS",         // Copilot Studio's previous name
            "VIRTUAL_AGENT_USL",            // Copilot Studio user licences
            "CCIBOTS",                      // Copilot Studio viral trial
        };

        /// <summary>
        /// Words that disqualify a display-name match in the fallback rule. A display name only counts
        /// when it looks like the Microsoft 365 Copilot product AND contains none of these.
        /// </summary>
        private static readonly string[] ExcludedNameWords = new[]
        {
            "studio", "sales", "virtual agent", "github", "security", "dynamics", "power ", "bot",
        };

        /// <summary>
        /// Display-name fragments that, combined with the word "copilot", indicate the Microsoft 365
        /// Copilot seat. Microsoft has already renamed this product once ("Microsoft Copilot for
        /// Microsoft 365" -> "Microsoft 365 Copilot"), so both orderings are accepted.
        /// </summary>
        private static readonly string[] SeatNameQualifiers = new[]
        {
            "microsoft 365", "m365", "office 365",
        };

        /// <summary>
        /// True when this licence type is a paid Microsoft 365 Copilot seat.
        /// </summary>
        /// <param name="skuPartNumber">
        /// <c>license_types.sku_id</c> - despite the column name this holds the SKU <i>part number</i>
        /// (e.g. <c>Microsoft_365_Copilot</c>), which is what the user import writes.
        /// </param>
        /// <param name="displayName">
        /// <c>license_types.name</c> - the resolved product name, or the part number again when the
        /// shipped licensing CSV had no entry for it.
        /// </param>
        public static bool IsCopilotSeat(string skuPartNumber, string displayName)
        {
            var sku = Normalise(skuPartNumber);

            if (sku.Length > 0)
            {
                if (StartsWithAny(sku, ExcludedSkuPrefixes))
                {
                    return false;
                }

                if (StartsWithAny(sku, SeatSkuPrefixes))
                {
                    return true;
                }
            }

            return IsSeatDisplayName(displayName);
        }

        /// <summary>
        /// The display-name fallback: a name that mentions Copilot together with the Microsoft 365
        /// family, and mentions none of the other Copilot-branded products.
        ///
        /// Deliberately conservative. A false positive here silently adds seats that were never bought,
        /// which would understate adoption; a false negative is visible in the licence-types list and
        /// can be corrected with an explicit override.
        /// </summary>
        private static bool IsSeatDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return false;
            }

            var name = displayName.Trim().ToLowerInvariant();

            if (!name.Contains("copilot"))
            {
                return false;
            }

            if (ExcludedNameWords.Any(word => name.Contains(word)))
            {
                return false;
            }

            return SeatNameQualifiers.Any(qualifier => name.Contains(qualifier));
        }

        /// <summary>
        /// Classifies every supplied licence type, preserving input order. Used by the API so an admin
        /// can see - and challenge - exactly which SKUs were counted as Copilot seats.
        /// </summary>
        public static List<LicenceTypeClassification> Classify(IEnumerable<LicenceTypeRow> licenceTypes)
        {
            if (licenceTypes == null)
            {
                return new List<LicenceTypeClassification>();
            }

            return licenceTypes
                .Select(licenceType => new LicenceTypeClassification
                {
                    Id = licenceType.Id,
                    Name = licenceType.Name,
                    SkuPartNumber = licenceType.SkuPartNumber,
                    AssignedUsers = licenceType.AssignedUsers,
                    IsCopilotSeat = IsCopilotSeat(licenceType.SkuPartNumber, licenceType.Name),
                })
                .ToList();
        }

        /// <summary>
        /// The licence-type ids to treat as Copilot seats for a request: the caller's explicit override
        /// when supplied (intersected with what actually exists, so a stale id cannot silently widen or
        /// break the query), otherwise everything <see cref="IsCopilotSeat"/> accepts.
        /// </summary>
        public static List<int> ResolveSeatLicenceTypeIds(
            IEnumerable<LicenceTypeRow> licenceTypes,
            IEnumerable<int> overrideIds)
        {
            var all = Classify(licenceTypes);

            if (overrideIds != null)
            {
                var requested = new HashSet<int>(overrideIds);
                if (requested.Count > 0)
                {
                    return all.Where(l => requested.Contains(l.Id)).Select(l => l.Id).ToList();
                }
            }

            return all.Where(l => l.IsCopilotSeat).Select(l => l.Id).ToList();
        }

        private static bool StartsWithAny(string normalisedSku, IEnumerable<string> prefixes)
        {
            return prefixes.Any(prefix => normalisedSku.StartsWith(prefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// Upper-cases and trims a SKU part number for prefix matching. A tenant has tens of licence
        /// types, not thousands, so the allocation is irrelevant here.
        /// </summary>
        private static string Normalise(string skuPartNumber)
        {
            return string.IsNullOrWhiteSpace(skuPartNumber)
                ? string.Empty
                : skuPartNumber.Trim().ToUpperInvariant();
        }
    }
}
