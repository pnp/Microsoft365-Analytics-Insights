using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.Config
{
    /// <summary>
    /// Runtime settings for the Azure Cost Management import.
    ///
    /// These are App Service application settings rather than fields on the installer's saved configuration
    /// file, for the same reason as <c>UserGroupsFilter</c> and the Copilot interaction-history knobs: the
    /// correct values are not knowable at install time. The scope depends on the customer's billing
    /// arrangement, and the meter filter can only be derived from their own cost data.
    /// </summary>
    public class AzureCostImportSettings
    {
        private const char Separator = ';';

        /// <summary>
        /// Cost Management allows at most <b>two</b> group-by clauses per query - its <c>QueryDataset</c>
        /// schema declares <c>grouping</c> with <c>maxItems: 2</c>, and a request carrying more is rejected
        /// with HTTP 400. This is the hard ceiling the configured grouping is validated against.
        /// </summary>
        public const int MaxGroupByDimensions = 2;

        /// <summary>
        /// Default grouping. <c>ResourceId</c> is chosen over <c>ServiceName</c> because it is the denser of
        /// the two: an Azure resource id contains the subscription and the resource group, so those can be
        /// recovered from it, whereas a service name yields nothing else. <c>Meter</c> is what identifies the
        /// charge itself.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultGroupBy = new List<string> { "ResourceId", "Meter" };

        /// <summary>
        /// The Cost Management dimension the <see cref="MeterFilterValues"/> are matched against. Defaults to
        /// <c>ServiceName</c>, which is the coarsest useful grouping; <c>MeterCategory</c> and <c>Meter</c>
        /// are the other sensible choices. Filtering is <b>not</b> subject to the grouping limit - the
        /// <c>filter</c> property carries no <c>maxItems</c> constraint - so a filter can name a dimension
        /// the query does not group by.
        /// </summary>
        public const string DefaultMeterFilterDimension = "ServiceName";

        /// <summary>Default trailing window re-read on every run.</summary>
        /// <remarks>
        /// Five days, because Azure keeps amending an open billing period: charges continue to accrue and can
        /// change until roughly the fifth day after the period ends, and estimates for the current period are
        /// updated several times a day. Anything shorter would freeze the first (lowest) estimate for a day
        /// and never correct it.
        /// </remarks>
        public const int DefaultTrailingWindowDays = 5;

        /// <summary>Default cadence: once a day. Cost data is refreshed a few times a day at best.</summary>
        public const int DefaultIntervalHours = 24;

        /// <summary>
        /// Cost Management scopes to import, e.g. <c>/subscriptions/00000000-0000-0000-0000-000000000000</c>.
        /// Empty means the import is not configured and will not run.
        /// </summary>
        public IReadOnlyList<string> Scopes { get; set; } = new List<string>();

        /// <summary>See <see cref="DefaultMeterFilterDimension"/>.</summary>
        public string MeterFilterDimension { get; set; } = DefaultMeterFilterDimension;

        /// <summary>
        /// Values to filter <see cref="MeterFilterDimension"/> to. <b>Empty means import every meter at the
        /// scope</b>, which is the honest default: Microsoft Cowork is billed through Copilot Credits managed
        /// in the Microsoft 365 admin centre and Microsoft publishes no Azure meter name for it, so there is
        /// no correct value to ship. Importing everything lets an operator read their own data and then
        /// narrow the filter.
        /// </summary>
        public IReadOnlyList<string> MeterFilterValues { get; set; } = new List<string>();

        /// <summary>How many days back to re-read on every run. See <see cref="DefaultTrailingWindowDays"/>.</summary>
        public int TrailingWindowDays { get; set; } = DefaultTrailingWindowDays;

        /// <summary>Minimum hours between runs. 0 disables the gate.</summary>
        public int IntervalHours { get; set; } = DefaultIntervalHours;

        /// <summary>
        /// The dimensions to group the daily query by. Empty means <see cref="DefaultGroupBy"/>.
        /// </summary>
        public IReadOnlyList<string> GroupBy { get; set; } = new List<string>();

        /// <summary>
        /// The grouping actually sent, defaulted and truncated to <see cref="MaxGroupByDimensions"/>.
        /// </summary>
        /// <remarks>
        /// Truncated rather than rejected: a misconfigured extra dimension would otherwise make every query
        /// fail with an opaque HTTP 400 from Azure, which is a far worse outcome for an operator than
        /// importing on the first two and saying so in the log.
        /// </remarks>
        public IReadOnlyList<string> ResolvedGroupBy =>
            GroupBy != null && GroupBy.Count > 0
                ? GroupBy.Take(MaxGroupByDimensions).ToList()
                : DefaultGroupBy;

        /// <summary>True when more dimensions were configured than Cost Management will accept.</summary>
        public bool GroupByWasTruncated => GroupBy != null && GroupBy.Count > MaxGroupByDimensions;

        /// <summary>
        /// True when at least one scope is configured. The importer refuses to run otherwise rather than
        /// guessing a scope - there is no safe default, and querying the wrong one would silently import
        /// another subscription's spend.
        /// </summary>
        public bool IsConfigured => Scopes != null && Scopes.Count > 0;

        /// <summary>
        /// Splits a semicolon-separated App Service setting into a de-duplicated, trimmed list. Never returns
        /// null. Kept here rather than in <c>AppConfig</c> so it can be unit tested directly.
        /// </summary>
        public static IReadOnlyList<string> ParseList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

            return raw.Split(Separator)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
