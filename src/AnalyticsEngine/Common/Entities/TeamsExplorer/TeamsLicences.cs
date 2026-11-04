using Common.Entities.Licences;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// Which licences give a person Microsoft Teams, so the Teams Explorer can tell a licensed person who did
    /// nothing from someone it was never going to see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The usage reports cannot say who is dormant. The importer reads Graph's Teams user-activity report one date
    /// at a time, and Microsoft documents that form as listing the users who performed activity on that date, so
    /// somebody who did nothing never appears in it. Dormancy is therefore worked out from the directory: an
    /// enabled person holding a licence that includes Teams, with nothing recorded across a period whose every
    /// day was imported.
    /// </para>
    /// <para>
    /// A licence includes Teams when Microsoft's licensing reference (<see cref="LicensingCatalogue"/>, shipped in
    /// this build) lists one of <see cref="TeamsServicePlans"/> in it. The user import stores each licence's SKU
    /// part number in <c>license_types.sku_id</c>, which is what is matched. The service plan is what decides,
    /// not the product name: an EEA suite sold without Teams carries no Teams plan, and Microsoft Teams
    /// Enterprise, sold beside it, does. A SKU newer than this build's copy of the file is not recognised, so its
    /// holders are left out of the dormant population rather than guessed into it - nobody is called dormant on
    /// the strength of a licence this build cannot read.
    /// </para>
    /// <para>
    /// Teams Rooms and shared-device licences (<see cref="DeviceSkuPrefixes"/>) carry the Teams plan too, but are
    /// assigned to resource accounts for meeting rooms and phones, which are not people an enablement programme
    /// can reach. They do not make someone part of the population.
    /// </para>
    /// </remarks>
    public static class TeamsLicences
    {
        /// <summary>
        /// The service plans that give a person the Teams client: commercial, the US government clouds, the
        /// free, Essentials and trial or exploratory editions.
        /// </summary>
        public static readonly string[] TeamsServicePlans =
        {
            "TEAMS1", "TEAMS_GOV", "TEAMS_AR_GCCHIGH", "TEAMS_AR_DOD", "TEAMS_FREE", "TeamsEss", "MCO_TEAMS_IW",
        };

        /// <summary>SKU part-number prefixes of Teams Rooms and shared-device licences, matched case-insensitively.</summary>
        public static readonly string[] DeviceSkuPrefixes =
        {
            "MEETING_ROOM", "MICROSOFT_TEAMS_ROOMS", "MTR_", "MCOCAP",
        };

        private static readonly Lazy<IReadOnlyList<string>> EmbeddedPartNumbers =
            new Lazy<IReadOnlyList<string>>(() => SkuPartNumbers(LicensingCatalogue.Embedded));

        /// <summary>The part numbers of every SKU in the build's licensing reference that gives a person Teams.</summary>
        public static IReadOnlyList<string> EmbeddedSkuPartNumbers => EmbeddedPartNumbers.Value;

        /// <summary>True when <paramref name="sku"/> gives a person Teams.</summary>
        public static bool IncludesTeams(CatalogueSku sku)
        {
            if (sku == null || IsDeviceSku(sku.PartNumber)) return false;
            return sku.ServicePlans.Any(plan => TeamsServicePlans.Contains(plan, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>The part numbers of every SKU in <paramref name="catalogue"/> that gives a person Teams, sorted.</summary>
        public static IReadOnlyList<string> SkuPartNumbers(LicensingCatalogue catalogue)
        {
            if (catalogue == null) throw new ArgumentNullException(nameof(catalogue));

            return catalogue.Skus
                .Where(IncludesTeams)
                .Select(sku => sku.PartNumber)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(partNumber => partNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsDeviceSku(string partNumber)
        {
            return !string.IsNullOrEmpty(partNumber)
                && DeviceSkuPrefixes.Any(prefix => partNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
