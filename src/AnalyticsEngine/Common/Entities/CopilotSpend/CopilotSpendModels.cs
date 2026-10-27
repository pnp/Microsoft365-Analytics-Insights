using Common.Entities.CopilotInvestment;
using Common.Entities.UserFilters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotSpend
{
    /// <summary>
    /// Copilot spend: who consumed what, when, from which source and where in the organisation. Facts only - no value,
    /// return, break-even or recommendation, and no price, so it needs no assumptions.
    /// </summary>
    /// <remarks>
    /// Two sources are kept apart and never added: the Copilot Studio ledger is billed usage by day, so it sums over a
    /// period; the preview Copilot credit balances are each person's latest reading in a period the source does not
    /// identify, so they are reported as balances on a date, never as a sum of days.
    /// </remarks>
    public sealed class CopilotSpendSummary
    {
        [JsonProperty("generatedUtc")]
        public DateTime GeneratedUtc { get; set; }

        [JsonProperty("fromUtc")]
        public DateTime FromUtc { get; set; }

        [JsonProperty("toUtc")]
        public DateTime ToUtc { get; set; }

        [JsonProperty("windowDays")]
        public int WindowDays { get; set; }

        /// <summary>True when an administrator's or a reader's filter narrows the view; tenant-only facts are then withheld.</summary>
        [JsonProperty("filtered")]
        public bool Filtered { get; set; }

        [JsonProperty("userFilter")]
        public UserFilterEcho UserFilter { get; set; }

        [JsonProperty("globalFilter")]
        public GlobalFilterEcho GlobalFilter { get; set; }

        [JsonProperty("studio")]
        public CopilotSpendStudio Studio { get; set; } = new CopilotSpendStudio();

        [JsonProperty("balances")]
        public CopilotSpendBalances Balances { get; set; } = new CopilotSpendBalances();

        [JsonProperty("organisation")]
        public CopilotSpendOrganisation Organisation { get; set; } = new CopilotSpendOrganisation();

        /// <summary>How many people consumed anything, or carry a balance flag, in this view.</summary>
        [JsonProperty("peopleCount")]
        public int PeopleCount { get; set; }

        [JsonProperty("individualDataHidden")]
        public bool IndividualDataHidden { get; set; }

        /// <summary>The people behind every figure. Never serialised in the summary: served by the See PII people endpoint.</summary>
        [JsonIgnore]
        public List<CopilotSpendPersonRow> People { get; set; } = new List<CopilotSpendPersonRow>();

        /// <summary>
        /// A copy for a reader without See PII: no people, no grouping that names a person, and no environment small enough
        /// to stand for one (its name and amount are combined with the other small ones). Summaries are cached and shared
        /// between readers, so the cached object is never edited.
        /// </summary>
        public CopilotSpendSummary WithoutIndividualData()
        {
            var copy = (CopilotSpendSummary)MemberwiseClone();
            copy.People = new List<CopilotSpendPersonRow>();
            copy.IndividualDataHidden = true;
            if (Organisation != null && CopilotInvestmentDimensions.NamesPeople(Organisation.Dimension))
            {
                var organisation = (CopilotSpendOrganisation)Organisation.MemberwiseCopy();
                organisation.Groups = new List<CopilotSpendGroupRow>();
                organisation.AllGroups = new List<CopilotSpendGroupRow>();
                organisation.Hidden = true;
                copy.Organisation = organisation;
            }
            if (Studio != null) copy.Studio = Studio.WithSmallEnvironmentsCombined(CopilotSpendCalculator.DefaultMinGroupSize);
            return copy;
        }

        /// <summary>
        /// A copy carrying one request's filter echoes. Readers who cover the same people share a cached summary, but the
        /// administrator's filter echo withholds people's names from a reader without See PII, so the echoes are attached per
        /// request and never stored on the cached object.
        /// </summary>
        public CopilotSpendSummary WithFilterEchoes(UserFilterEcho userFilter, GlobalFilterEcho globalFilter)
        {
            var copy = (CopilotSpendSummary)MemberwiseClone();
            copy.UserFilter = userFilter;
            copy.GlobalFilter = globalFilter;
            return copy;
        }
    }

    /// <summary>Billed Copilot Studio usage from the daily ledger: a quantity that sums over the period.</summary>
    public sealed class CopilotSpendStudio
    {
        [JsonProperty("importEnabled")]
        public bool ImportEnabled { get; set; }

        [JsonProperty("hasData")]
        public bool HasData { get; set; }

        /// <summary>A stable code when the ledger could not be read; the rest of the report still shows.</summary>
        [JsonProperty("failureCode")]
        public string FailureCode { get; set; }

        [JsonProperty("lastImportUtc")]
        public DateTime? LastImportUtc { get; set; }

        [JsonProperty("lastImportFailed")]
        public bool LastImportFailed { get; set; }

        /// <summary>The ledger's first day, whatever the window.</summary>
        [JsonProperty("earliestDate")]
        public DateTime? EarliestDate { get; set; }

        /// <summary>False when the ledger starts after the window does, so the period's first days have no data, not zero.</summary>
        [JsonProperty("coversWindow")]
        public bool CoversWindow { get; set; }

        /// <summary>The ledger's latest stored day, whatever the window: how far it reaches, not proof every earlier day was imported.</summary>
        [JsonProperty("latestDate")]
        public DateTime? LatestDate { get; set; }

        /// <summary>
        /// True when the ledger's figures are known: it was read and holds rows. Otherwise (the import is off and nothing was
        /// ever imported, or the ledger could not be read) every Studio figure is null - not available - rather than zero.
        /// A ledger whose import has since been switched off but still holds rows is available: its history is real.
        /// </summary>
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>Billed credits in the window for the people in view; unfiltered, also the charges no user matched. Null when not available.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        /// <summary>People in view with billed credits in the window. Null when not available.</summary>
        [JsonProperty("people")]
        public int? People { get; set; }

        /// <summary>Of <see cref="Credits"/>, charges the user import never matched to a person. Null in a filtered view.</summary>
        [JsonProperty("unmatchedCredits")]
        public double? UnmatchedCredits { get; set; }

        /// <summary>Every calendar day of the window, in order, each saying whether the ledger recorded charges for it.</summary>
        [JsonProperty("days")]
        public List<CopilotSpendDay> Days { get; set; } = new List<CopilotSpendDay>();

        /// <summary>
        /// Billed credits per Power Platform environment for the people in view. The per-user ledger is kept by day, person
        /// and environment, so this is its finest "what": Microsoft's per-user consumption does not name the agent.
        /// </summary>
        [JsonProperty("environments")]
        public List<CopilotSpendEnvironment> Environments { get; set; } = new List<CopilotSpendEnvironment>();

        /// <summary>
        /// Billed credits per agent from the tenant's agent ledger, a different Microsoft route recorded per agent and not
        /// per person. Its total stands on its own: it need not equal <see cref="Credits"/> and is never added to it. Null in
        /// a filtered view, which it cannot be narrowed to, and when the agent ledger was not read.
        /// </summary>
        [JsonProperty("agents")]
        public List<CopilotSpendAgent> Agents { get; set; } = new List<CopilotSpendAgent>();

        /// <summary>The agent ledger's own total for the period; null when <see cref="Agents"/> is.</summary>
        [JsonProperty("agentLedgerCredits")]
        public double? AgentLedgerCredits { get; set; }

        /// <summary>A stable code when the per-day, per-environment and per-agent breakdown could not be read.</summary>
        [JsonProperty("breakdownFailureCode")]
        public string BreakdownFailureCode { get; set; }

        /// <summary>
        /// A copy whose environments used by fewer than <paramref name="minPeople"/> people are combined into one row, named
        /// by the report rather than by the tenant, and whose figures are withheld if the combination is still that small:
        /// a developer's own environment would otherwise show one person's charges under a name that identifies them.
        /// </summary>
        public CopilotSpendStudio WithSmallEnvironmentsCombined(int minPeople)
        {
            var copy = (CopilotSpendStudio)MemberwiseClone();
            var small = Environments.Where(e => e.EmptyKey == null && e.People < minPeople).ToList();
            if (small.Count == 0) return copy;
            copy.Environments = Environments.Except(small).ToList();
            var people = small.Sum(e => e.People);
            var combined = new CopilotSpendEnvironment
            {
                EmptyKey = CopilotSpendGroupKeys.OtherEnvironments,
                Folded = small.Count,
                People = people,
                Withheld = people < minPeople,
            };
            if (!combined.Withheld)
            {
                combined.Credits = Math.Round(small.Sum(e => e.Credits ?? 0), 6);
                combined.Days = small.Max(e => e.Days);
            }
            copy.Environments.Add(combined);
            return copy;
        }
    }

    /// <summary>The ledger's recording state for one calendar day.</summary>
    public static class CopilotSpendDayStatus
    {
        /// <summary>The ledger holds charges for the day.</summary>
        public const string Recorded = "recorded";

        /// <summary>The day lies within the ledger's stored days but it holds no charges for it, for the people in view.</summary>
        public const string NoneRecorded = "noneRecorded";

        /// <summary>After the ledger's latest stored day (or before its first): not reported yet, which is not zero.</summary>
        public const string NotReported = "notReported";
    }

    public sealed class CopilotSpendDay
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        /// <summary>One of <see cref="CopilotSpendDayStatus"/>.</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = CopilotSpendDayStatus.Recorded;

        /// <summary>Billed credits; null unless <see cref="Status"/> is <c>recorded</c>.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("people")]
        public int? People { get; set; }
    }

    public sealed class CopilotSpendEnvironment
    {
        [JsonProperty("environmentId")]
        public string EnvironmentId { get; set; }

        /// <summary>Null when the environment lookup was unavailable at import, or on a row the report labels itself.</summary>
        [JsonProperty("environmentName")]
        public string EnvironmentName { get; set; }

        /// <summary><c>otherEnvironments</c> on the row that combines environments too small to show on their own.</summary>
        [JsonProperty("emptyKey")]
        public string EmptyKey { get; set; }

        /// <summary>How many environments the combined row holds.</summary>
        [JsonProperty("folded")]
        public int Folded { get; set; }

        /// <summary>True when the combined row is still too small to show figures for: its credits and days are null.</summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }

        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("days")]
        public int? Days { get; set; }
    }

    /// <summary>One agent's billed credits in the tenant agent ledger. No people count: that ledger is not kept per person.</summary>
    public sealed class CopilotSpendAgent
    {
        [JsonProperty("agentId")]
        public string AgentId { get; set; }

        [JsonProperty("agentName")]
        public string AgentName { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("days")]
        public int Days { get; set; }
    }

    /// <summary>The latest preview Copilot credit balance of each person: readings on a date, not a period's spend.</summary>
    public sealed class CopilotSpendBalances
    {
        /// <summary>False when the preview tables do not exist yet (database not upgraded).</summary>
        [JsonProperty("tablesPresent")]
        public bool TablesPresent { get; set; }

        [JsonProperty("collectionEnabled")]
        public bool CollectionEnabled { get; set; }

        [JsonProperty("hasData")]
        public bool HasData { get; set; }

        [JsonProperty("failureCode")]
        public string FailureCode { get; set; }

        /// <summary>The newest sweep date in the window: each person's latest reading is taken from the few days up to it.</summary>
        [JsonProperty("snapshotDate")]
        public DateTime? SnapshotDate { get; set; }

        /// <summary>
        /// True when the newest reading is older than the selected period's first day (the collector stopped, say): it is
        /// still each person's latest reading, but it says nothing about consumption in this period.
        /// </summary>
        [JsonProperty("snapshotBeforeWindow")]
        public bool SnapshotBeforeWindow { get; set; }

        /// <summary>True when readings were found; otherwise every balance figure is null - not available - rather than zero.</summary>
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("snapshotBandFrom")]
        public DateTime? SnapshotBandFrom { get; set; }

        [JsonProperty("firstObservationDate")]
        public DateTime? FirstObservationDate { get; set; }

        /// <summary><c>verified</c>, <c>unverified</c> or <c>mixed</c>: whether the source said which period a balance covers.</summary>
        [JsonProperty("periodStatus")]
        public string PeriodStatus { get; set; }

        /// <summary>The oldest and newest time the source last changed a reading in view: its freshness, not a billing period.</summary>
        [JsonProperty("oldestFreshness")]
        public DateTimeOffset? OldestFreshness { get; set; }

        [JsonProperty("newestFreshness")]
        public DateTimeOffset? NewestFreshness { get; set; }

        /// <summary>Latest balances, in credits, of the people in view; unfiltered, also the accounts no user matched. Null when not available.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        /// <summary>People in view with a latest reading at all, zero or not.</summary>
        [JsonProperty("observedPeople")]
        public int ObservedPeople { get; set; }

        [JsonProperty("consumingPeople")]
        public int ConsumingPeople { get; set; }

        /// <summary>Readings with a category the source could not evaluate, or from a sweep that did not complete.</summary>
        [JsonProperty("partialPeople")]
        public int PartialPeople { get; set; }

        /// <summary>People with a category whose amount the source did not report - unknown, not zero.</summary>
        [JsonProperty("unknownAmountPeople")]
        public int UnknownAmountPeople { get; set; }

        /// <summary>People with a category reported in a unit other than Copilot credits, which the credit totals leave out.</summary>
        [JsonProperty("otherUnitPeople")]
        public int OtherUnitPeople { get; set; }

        [JsonProperty("limitExhaustedPeople")]
        public int LimitExhaustedPeople { get; set; }

        [JsonProperty("notEntitledPeople")]
        public int NotEntitledPeople { get; set; }

        /// <summary>Directory accounts with a reading that no imported user matches. Null in a filtered view.</summary>
        [JsonProperty("unresolvedAccounts")]
        public int? UnresolvedAccounts { get; set; }

        [JsonProperty("unresolvedCredits")]
        public double? UnresolvedCredits { get; set; }

        [JsonProperty("sweepsInWindow")]
        public int SweepsInWindow { get; set; }

        [JsonProperty("completedSweepsInWindow")]
        public int CompletedSweepsInWindow { get; set; }

        [JsonProperty("services")]
        public List<CopilotInvestmentServiceRow> Services { get; set; } = new List<CopilotInvestmentServiceRow>();

        /// <summary>Tenant-wide spending policies. Empty in a filtered view, which they do not describe.</summary>
        [JsonProperty("policies")]
        public List<CopilotInvestmentPolicyRow> Policies { get; set; } = new List<CopilotInvestmentPolicyRow>();

        /// <summary>Tenant-wide pool balances. Empty in a filtered view: a tenant total would describe people outside it.</summary>
        [JsonProperty("tenantBalances")]
        public List<CopilotInvestmentTenantBalanceRow> TenantBalances { get; set; } = new List<CopilotInvestmentTenantBalanceRow>();
    }

    public sealed class CopilotSpendOrganisation
    {
        [JsonProperty("dimension")]
        public string Dimension { get; set; }

        [JsonProperty("dimensionName")]
        public string DimensionName { get; set; }

        /// <summary>
        /// The smallest group shown on its own: 5 for a grouping any reader may see, so no group's figures describe fewer
        /// people; 1 for a grouping that names people (manager), which only a reader with See PII is served anyway.
        /// </summary>
        [JsonProperty("minGroupSize")]
        public int MinGroupSize { get; set; }

        /// <summary>Groups below <see cref="MinGroupSize"/>, combined into the <c>otherSmallGroups</c> row.</summary>
        [JsonProperty("foldedGroups")]
        public int FoldedGroups { get; set; }

        /// <summary>
        /// Groups beyond the most the page lists, combined into the <c>moreGroups</c> row. They are not small: the Excel
        /// export lists every one of them.
        /// </summary>
        [JsonProperty("moreGroups")]
        public int MoreGroups { get; set; }

        /// <summary>
        /// A stable code when the people cannot be grouped at all (<see cref="CopilotSpendGroupingUnavailable"/>): no
        /// directory, or a dimension the directory does not hold. Groups are then empty rather than all "not set".
        /// </summary>
        [JsonProperty("unavailableCode")]
        public string UnavailableCode { get; set; }

        /// <summary>True when the grouping names people and the reader may not see them.</summary>
        [JsonProperty("hidden")]
        public bool Hidden { get; set; }

        /// <summary>The groups the page lists: the largest, then the combined rows.</summary>
        [JsonProperty("groups")]
        public List<CopilotSpendGroupRow> Groups { get; set; } = new List<CopilotSpendGroupRow>();

        /// <summary>Every group, with only the small ones combined: what the Excel export lists. Not sent to the page.</summary>
        [JsonIgnore]
        public List<CopilotSpendGroupRow> AllGroups { get; set; } = new List<CopilotSpendGroupRow>();

        internal object MemberwiseCopy() => MemberwiseClone();
    }

    /// <summary>
    /// One group's consumption. A source that is not available leaves its figures null - not zero - and a combined row too
    /// small to show is <see cref="Withheld"/>, with its figures null.
    /// </summary>
    public sealed class CopilotSpendGroupRow
    {
        /// <summary>The tenant's own value (a department name, say). Null for a row the report labels itself.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary><c>notSet</c>, <c>otherSmallGroups</c> or <c>moreGroups</c> when <see cref="Label"/> is null.</summary>
        [JsonProperty("emptyKey")]
        public string EmptyKey { get; set; }

        /// <summary>How many groups a combined row holds.</summary>
        [JsonProperty("folded")]
        public int Folded { get; set; }

        /// <summary>True when a combined row still has fewer people than the smallest group shown: its figures are null.</summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("studioPeople")]
        public int? StudioPeople { get; set; }

        [JsonProperty("studioCredits")]
        public double? StudioCredits { get; set; }

        [JsonProperty("shareOfStudio")]
        public double? ShareOfStudio { get; set; }

        [JsonProperty("balancePeople")]
        public int? BalancePeople { get; set; }

        [JsonProperty("balanceCredits")]
        public double? BalanceCredits { get; set; }

        [JsonProperty("shareOfBalances")]
        public double? ShareOfBalances { get; set; }

        [JsonProperty("unknownAmountPeople")]
        public int? UnknownAmountPeople { get; set; }
    }

    /// <summary>Why the people cannot be grouped.</summary>
    public static class CopilotSpendGroupingUnavailable
    {
        /// <summary>The user directory could not be read, so nobody's department, manager or other value is known.</summary>
        public const string Directory = "directoryUnavailable";

        /// <summary>The directory does not hold this dimension (a custom organisation type since removed, say).</summary>
        public const string Dimension = "dimensionUnknown";
    }

    /// <summary>Labels of the rows the report names itself.</summary>
    public static class CopilotSpendGroupKeys
    {
        /// <summary>Groups beyond the most the page lists. Not small: listed one by one in the Excel export.</summary>
        public const string MoreGroups = "moreGroups";

        /// <summary>Environments used by too few people to show on their own to a reader without See PII.</summary>
        public const string OtherEnvironments = "otherEnvironments";
    }

    /// <summary>One person's consumption: billed Studio usage in the period beside their latest balance, never added.</summary>
    public sealed class CopilotSpendPersonRow
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        [JsonProperty("company")]
        public string Company { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("manager")]
        public string Manager { get; set; }

        /// <summary>Billed Studio credits in the period; null when the ledger is not available (unknown, not zero).</summary>
        [JsonProperty("studioCredits")]
        public double? StudioCredits { get; set; }

        [JsonProperty("studioDays")]
        public int? StudioDays { get; set; }

        /// <summary>Null when the person has no reading, or no category in Copilot credits: absent, not zero.</summary>
        [JsonProperty("balanceCredits")]
        public double? BalanceCredits { get; set; }

        [JsonProperty("balanceDate")]
        public DateTime? BalanceDate { get; set; }

        [JsonProperty("balancePartial")]
        public bool BalancePartial { get; set; }

        [JsonProperty("unknownAmounts")]
        public bool UnknownAmounts { get; set; }

        [JsonProperty("otherUnits")]
        public bool OtherUnits { get; set; }

        [JsonProperty("limitExhausted")]
        public bool LimitExhausted { get; set; }

        [JsonProperty("notEntitled")]
        public bool NotEntitled { get; set; }

        [JsonIgnore]
        public Guid? CreditObjectId { get; set; }
    }

    public sealed class CopilotSpendPeoplePage
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("rows")]
        public List<CopilotSpendPersonRow> Rows { get; set; } = new List<CopilotSpendPersonRow>();
    }

    /// <summary>One person's drill-down: their balance by category and over time, and their billed Studio use by environment.</summary>
    public sealed class CopilotSpendPersonDetail
    {
        [JsonProperty("person")]
        public CopilotSpendPersonRow Person { get; set; }

        [JsonProperty("services")]
        public List<CopilotInvestmentPersonService> Services { get; set; } = new List<CopilotInvestmentPersonService>();

        [JsonProperty("balances")]
        public List<PersonBalancePoint> Balances { get; set; } = new List<PersonBalancePoint>();

        [JsonProperty("studioEnvironments")]
        public List<CopilotInvestmentStudioEnvironment> StudioEnvironments { get; set; } = new List<CopilotInvestmentStudioEnvironment>();
    }

    /// <summary>Stable codes the API answers with; the portal writes the words.</summary>
    public static class CopilotSpendErrorCodes
    {
        public const string StudioUnavailable = "studioUnavailable";
        public const string StudioBreakdownUnavailable = "studioBreakdownUnavailable";
        public const string CreditsUnavailable = "creditsUnavailable";
    }
}
