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
    /// A licence that <i>includes</i> the seat counts too, whatever it is called. Microsoft 365 E7 is a
    /// suite rather than a Copilot-branded SKU, but Microsoft's licensing reference lists every service
    /// plan of the standalone Microsoft 365 Copilot SKU in it. Leaving it out counted E7 holders as
    /// unlicensed: their Copilot use was reported as unlicensed demand, and they could be recommended
    /// for a Copilot licence they already hold.
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
    ///   <item>An explicit exclusion list of "Copilot-branded but not a Microsoft 365 Copilot licence" SKUs.</item>
    ///   <item>A prefix match on the SKU part number, which catches present and future variants
    ///         (<c>M365_Copilot</c>, <c>Microsoft_365_Copilot</c>, <c>Microsoft_365_Copilot_EDU</c>, ...)
    ///         and suites that include the seat (<c>MICROSOFT_365_E7</c>).</item>
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
        /// SKU part-number prefixes that identify a licence carrying a Microsoft 365 Copilot seat. Matched
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
            "MICROSOFT_365_E7",         // Microsoft 365 E7 - a suite, but it carries every Copilot seat service plan
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
            "MICROSOFT_COPILOT_FOR_FINANCE", // Microsoft 365 Copilot for Finance (Preview) trial - no Copilot seat plans
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
            "studio", "sales", "finance", "virtual agent", "github", "security", "dynamics", "power ", "bot",
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
            return Explain(skuPartNumber, displayName).IsCopilotSeat;
        }

        /// <summary>
        /// Identifies this version of the rules, so a diagnostic or an export can say which rules decided
        /// a classification. Change it whenever a prefix, exclusion or name rule changes.
        /// </summary>
        public const string RuleSetVersion = "2026-10-e7";

        /// <summary>
        /// The same decision as <see cref="IsCopilotSeat"/>, with the rule that made it, so an administrator
        /// can see - and challenge - why a SKU was or was not counted.
        /// </summary>
        public static CopilotSeatClassification Explain(string skuPartNumber, string displayName)
        {
            var sku = Normalise(skuPartNumber);

            if (sku.Length > 0)
            {
                var excluded = FirstMatch(sku, ExcludedSkuPrefixes);
                if (excluded != null)
                {
                    return new CopilotSeatClassification(false, CopilotSeatClassificationReasons.ExcludedSkuPrefix, excluded);
                }

                var seat = FirstMatch(sku, SeatSkuPrefixes);
                if (seat != null)
                {
                    return new CopilotSeatClassification(true, CopilotSeatClassificationReasons.SeatSkuPrefix, seat);
                }
            }

            return ExplainDisplayName(displayName);
        }

        /// <summary>
        /// The display-name fallback: a name that mentions Copilot together with the Microsoft 365
        /// family, and mentions none of the other Copilot-branded products.
        ///
        /// Deliberately conservative. A false positive here silently adds seats that were never bought,
        /// which would understate adoption; a false negative is visible in the licence-types list and
        /// can be corrected with an explicit override.
        /// </summary>
        private static CopilotSeatClassification ExplainDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return new CopilotSeatClassification(false, CopilotSeatClassificationReasons.NoRuleMatched, null);
            }

            var name = displayName.Trim().ToLowerInvariant();

            if (!name.Contains("copilot"))
            {
                return new CopilotSeatClassification(false, CopilotSeatClassificationReasons.NoRuleMatched, null);
            }

            var excludedWord = ExcludedNameWords.FirstOrDefault(word => name.Contains(word));
            if (excludedWord != null)
            {
                return new CopilotSeatClassification(false, CopilotSeatClassificationReasons.ExcludedNameWord, excludedWord.Trim());
            }

            var qualifier = SeatNameQualifiers.FirstOrDefault(q => name.Contains(q));
            return qualifier != null
                ? new CopilotSeatClassification(true, CopilotSeatClassificationReasons.SeatDisplayName, "copilot + " + qualifier)
                : new CopilotSeatClassification(false, CopilotSeatClassificationReasons.CopilotNameWithoutQualifier, null);
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
                .Select(licenceType =>
                {
                    var explanation = Explain(licenceType.SkuPartNumber, licenceType.Name);
                    return new LicenceTypeClassification
                    {
                        Id = licenceType.Id,
                        Name = licenceType.Name,
                        SkuPartNumber = licenceType.SkuPartNumber,
                        AssignedUsers = licenceType.AssignedUsers,
                        PurchasedUnits = licenceType.PurchasedUnits,
                        UnassignedUnits = licenceType.PurchasedUnits.HasValue && licenceType.PurchasedUnits.Value >= licenceType.AssignedUsers
                            ? licenceType.PurchasedUnits.Value - licenceType.AssignedUsers
                            : (int?)null,
                        PurchasedUnitsRefreshedUtc = licenceType.PurchasedUnitsRefreshedUtc,
                        IsCopilotSeat = explanation.IsCopilotSeat,
                        ClassificationReason = explanation.Reason,
                        MatchedRule = explanation.MatchedRule,
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Rewrites <paramref name="classifications"/> to what a report with an explicit seat override
        /// actually counted, recording where the override disagreed with the rules. The automatic
        /// <see cref="LicenceTypeClassification.MatchedRule"/> is kept, so the rule the override replaced
        /// is still visible.
        /// </summary>
        public static void ApplyOverride(IEnumerable<LicenceTypeClassification> classifications, IEnumerable<int> effectiveSeatIds)
        {
            if (classifications == null) return;
            var effective = new HashSet<int>(effectiveSeatIds ?? Enumerable.Empty<int>());
            foreach (var licence in classifications)
            {
                var counted = effective.Contains(licence.Id);
                if (counted != licence.IsCopilotSeat)
                {
                    licence.ClassificationReason = counted
                        ? CopilotSeatClassificationReasons.ReportOverrideIncluded
                        : CopilotSeatClassificationReasons.ReportOverrideExcluded;
                }
                licence.IsCopilotSeat = counted;
            }
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

        private static string FirstMatch(string normalisedSku, IEnumerable<string> prefixes)
        {
            return prefixes.FirstOrDefault(prefix => normalisedSku.StartsWith(prefix, StringComparison.Ordinal));
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
