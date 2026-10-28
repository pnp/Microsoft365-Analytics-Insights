using Common.Entities.Copilot;
using Common.Entities.CopilotAdoption;
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
        /// A copy for a reader without See PII: no people, no grouping that names a person, no environment small enough
        /// to stand for one (its name and amount are combined with the other small ones), and distributions without the
        /// named lists or any figure that describes fewer people than the smallest group shown. Summaries are cached and
        /// shared between readers, so the cached object is never edited.
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
            if (Studio != null)
            {
                copy.Studio = Studio.WithSmallEnvironmentsCombined(CopilotSpendCalculator.DefaultMinGroupSize);
                copy.Studio.Distribution = Studio.Distribution?.Anonymised(CopilotSpendCalculator.DefaultMinGroupSize);
            }
            if (Balances != null)
            {
                copy.Balances = Balances.Copy();
                copy.Balances.Distribution = Balances.Distribution?.Anonymised(CopilotSpendCalculator.DefaultMinGroupSize);
            }
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
        /// How the billed credits matched to people in view are spread across them. Null when the ledger is not available.
        /// Credits no imported user matched are not in it, so its total can be below <see cref="Credits"/>.
        /// </summary>
        [JsonProperty("distribution")]
        public CopilotSpendDistribution Distribution { get; set; }

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

        /// <summary>
        /// How the latest balances of the people in view are spread across those whose balance is above zero. Null when no
        /// reading is available. Accounts no imported user matched are not in it.
        /// </summary>
        [JsonProperty("distribution")]
        public CopilotSpendDistribution Distribution { get; set; }

        internal CopilotSpendBalances Copy() => (CopilotSpendBalances)MemberwiseClone();
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

    /// <summary>
    /// One person's drill-down: where they stand among the people in view, their balance by category and over time, their
    /// billed Studio use by day and by environment, and what they used Copilot for - never the text of a prompt.
    /// </summary>
    public sealed class CopilotSpendPersonDetail
    {
        [JsonProperty("person")]
        public CopilotSpendPersonRow Person { get; set; }

        [JsonProperty("ranks")]
        public CopilotSpendPersonRanks Ranks { get; set; } = new CopilotSpendPersonRanks();

        [JsonProperty("services")]
        public List<CopilotInvestmentPersonService> Services { get; set; } = new List<CopilotInvestmentPersonService>();

        [JsonProperty("balances")]
        public List<PersonBalancePoint> Balances { get; set; } = new List<PersonBalancePoint>();

        [JsonProperty("studioEnvironments")]
        public List<CopilotInvestmentStudioEnvironment> StudioEnvironments { get; set; } = new List<CopilotInvestmentStudioEnvironment>();

        /// <summary>
        /// Every calendar day of the period with the person's billed Studio credits, each with the ledger's recording state
        /// (<see cref="CopilotSpendDayStatus"/>). Null when the ledger is not available or the person's days could not be read.
        /// </summary>
        [JsonProperty("studioDays")]
        public List<CopilotSpendDay> StudioDays { get; set; }

        [JsonProperty("usage")]
        public CopilotSpendPersonUsage Usage { get; set; } = new CopilotSpendPersonUsage();

        /// <summary>
        /// <see cref="CopilotSpendErrorCodes.PersonUsageUnavailable"/> when the person's prompts, Studio days and resources
        /// could not be read. The rest of the drill-down still shows, and those parts read as not available, never as zero.
        /// </summary>
        [JsonProperty("usageFailureCode")]
        public string UsageFailureCode { get; set; }
    }

    /// <summary>
    /// Where one person stands among the people in view, for each source on its own: 1 is the largest consumer, people with
    /// the same amount share a rank, and a share is of the credits matched to people in view. Null for a source the person
    /// consumed nothing from, or that is not available.
    /// </summary>
    public sealed class CopilotSpendPersonRanks
    {
        [JsonProperty("studioRank")]
        public int? StudioRank { get; set; }

        /// <summary>People in view with billed Studio credits: what the rank is out of. Null when the ledger is not available.</summary>
        [JsonProperty("studioConsumers")]
        public int? StudioConsumers { get; set; }

        [JsonProperty("studioShare")]
        public double? StudioShare { get; set; }

        [JsonProperty("balanceRank")]
        public int? BalanceRank { get; set; }

        /// <summary>People in view with a latest balance above zero. Null when no reading is available.</summary>
        [JsonProperty("balanceConsumers")]
        public int? BalanceConsumers { get; set; }

        [JsonProperty("balanceShare")]
        public double? BalanceShare { get; set; }
    }

    /// <summary>
    /// What one person used Copilot for in the period: the activity behind their spend. Counts, apps, agents, classified goals
    /// and key phrases - never the text of a prompt or a response, which the product does not store. A part this deployment
    /// cannot assess has its flag false and reads as not available, never as zero.
    /// </summary>
    public sealed class CopilotSpendPersonUsage
    {
        /// <summary>False when the Copilot audit tables are not available, so no turn figure is known.</summary>
        [JsonProperty("turnsAssessed")]
        public bool TurnsAssessed { get; set; }

        /// <summary>Copilot turns in the period, each counted once however many audit records it has.</summary>
        [JsonProperty("turns")]
        public long Turns { get; set; }

        /// <summary>Of <see cref="Turns"/>, those with Microsoft 365 Copilot itself, with no agent.</summary>
        [JsonProperty("noAgentTurns")]
        public long NoAgentTurns { get; set; }

        /// <summary>Of <see cref="Turns"/>, those with an agent other than Copilot Cowork, outside Copilot Studio's test pane.</summary>
        [JsonProperty("agentTurns")]
        public long AgentTurns { get; set; }

        [JsonProperty("coworkTurns")]
        public long CoworkTurns { get; set; }

        /// <summary>Of <see cref="Turns"/>, those in Copilot Studio's test pane: a maker trying out an agent they are building.</summary>
        [JsonProperty("makerTestTurns")]
        public long MakerTestTurns { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        /// <summary>
        /// The importer's own credit estimate over these turns. An estimate, never a bill: it prices customer-built
        /// custom-engine agents only, and it is never added to billed credits or balances. Null when no turn carries one.
        /// </summary>
        [JsonProperty("estimateCredits")]
        public double? EstimateCredits { get; set; }

        /// <summary>Of <see cref="Turns"/>, those the estimate priced above zero.</summary>
        [JsonProperty("pricedTurns")]
        public long PricedTurns { get; set; }

        /// <summary>Every calendar day of the period, so a quiet day keeps its place on the axis. Empty when turns are not assessed.</summary>
        [JsonProperty("days")]
        public List<CopilotSpendUsageDay> Days { get; set; } = new List<CopilotSpendUsageDay>();

        /// <summary>Turns by the app Copilot was used in, as the audit log names it.</summary>
        [JsonProperty("apps")]
        public List<CopilotSpendAppUse> Apps { get; set; } = new List<CopilotSpendAppUse>();

        /// <summary>The agents the person used, the most estimated credits first, then the most turns.</summary>
        [JsonProperty("agents")]
        public List<CopilotSpendPersonAgent> Agents { get; set; } = new List<CopilotSpendPersonAgent>();

        /// <summary>False when the interaction history was not imported, so nothing below is known.</summary>
        [JsonProperty("promptsAssessed")]
        public bool PromptsAssessed { get; set; }

        /// <summary>The person's prompts in the period. Null when not assessed.</summary>
        [JsonProperty("prompts")]
        public CopilotSpendPromptTotals Prompts { get; set; }

        /// <summary>Prompts by the app they were written in, as the interaction history names it.</summary>
        [JsonProperty("promptApps")]
        public List<CopilotSpendAppUse> PromptApps { get; set; } = new List<CopilotSpendAppUse>();

        /// <summary>False when prompt classification has never run, so no goal is known.</summary>
        [JsonProperty("categoriesAssessed")]
        public bool CategoriesAssessed { get; set; }

        /// <summary>Classified prompts by goal, the most first; a goal renamed between taxonomy versions is one row.</summary>
        [JsonProperty("categories")]
        public List<CopilotSpendPersonCategory> Categories { get; set; } = new List<CopilotSpendPersonCategory>();

        /// <summary>Prompts the classifier could not place in any goal.</summary>
        [JsonProperty("notClassifiedPrompts")]
        public long NotClassifiedPrompts { get; set; }

        /// <summary>False when key phrase extraction has never run, so no phrase is known.</summary>
        [JsonProperty("phrasesAssessed")]
        public bool PhrasesAssessed { get; set; }

        /// <summary>The key phrases found most often in the person's prompts.</summary>
        [JsonProperty("phrases")]
        public List<CopilotSpendPhrase> Phrases { get; set; } = new List<CopilotSpendPhrase>();

        /// <summary>The person's latest prompts, newest first: when, where, what kind and how much was attached - never the text.</summary>
        [JsonProperty("recent")]
        public List<CopilotSpendRecentPrompt> Recent { get; set; } = new List<CopilotSpendRecentPrompt>();

        [JsonProperty("resourcesAssessed")]
        public bool ResourcesAssessed { get; set; }

        /// <summary>How the audit log typed what Copilot referenced for the person: files, sites, web searches and so on.</summary>
        [JsonProperty("resourceTypes")]
        public List<AdoptionResourceTypeRow> ResourceTypes { get; set; } = new List<AdoptionResourceTypeRow>();
    }

    public sealed class CopilotSpendUsageDay
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        [JsonProperty("turns")]
        public long Turns { get; set; }

        /// <summary>Of <see cref="Turns"/>, those with any agent: an agent, Copilot Cowork or a Copilot Studio test.</summary>
        [JsonProperty("agentTurns")]
        public long AgentTurns { get; set; }
    }

    /// <summary>Use of one app: turns, or prompts, and on how many days. The app is named as the source names it.</summary>
    public sealed class CopilotSpendAppUse
    {
        [JsonProperty("app")]
        public string App { get; set; }

        [JsonProperty("count")]
        public long Count { get; set; }

        [JsonProperty("days")]
        public int Days { get; set; }
    }

    /// <summary>One agent a person used, with the importer's estimate for those turns: an estimate, never a bill.</summary>
    public sealed class CopilotSpendPersonAgent
    {
        [JsonIgnore]
        public int Id { get; set; }

        /// <summary>The agent's id as the audit log reported it; null when the agent is no longer stored.</summary>
        [JsonProperty("agentId")]
        public string AgentId { get; set; }

        /// <summary>The agent's name as the tenant set it; null when not known.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>One of <see cref="CopilotSpendAgentKindKeys"/>.</summary>
        [JsonProperty("kindKey")]
        public string KindKey { get; set; } = CopilotSpendAgentKindKeys.Unknown;

        /// <summary>One of <see cref="CopilotAgentOriginKeys"/>.</summary>
        [JsonProperty("originKey")]
        public string OriginKey { get; set; } = CopilotAgentOriginKeys.Unknown;

        [JsonProperty("cowork")]
        public bool Cowork { get; set; }

        [JsonProperty("turns")]
        public long Turns { get; set; }

        /// <summary>Of <see cref="Turns"/>, those in Copilot Studio's test pane.</summary>
        [JsonProperty("testTurns")]
        public long TestTurns { get; set; }

        [JsonProperty("estimateCredits")]
        public double? EstimateCredits { get; set; }

        [JsonProperty("pricedTurns")]
        public long PricedTurns { get; set; }

        [JsonProperty("days")]
        public int Days { get; set; }

        [JsonProperty("lastDate")]
        public DateTime? LastDate { get; set; }
    }

    /// <summary>
    /// The stable keys the drill-down sends for <see cref="CopilotAgentKind"/>. The portal maps each to translated text, so
    /// these are a contract with the SPA: never rename one.
    /// </summary>
    public static class CopilotSpendAgentKindKeys
    {
        public const string CustomEngine = "customEngine";
        public const string Declarative = "declarative";
        public const string Unknown = "unknown";

        public static string For(CopilotAgentKind kind)
        {
            switch (kind)
            {
                case CopilotAgentKind.CustomEngine: return CustomEngine;
                case CopilotAgentKind.Declarative: return Declarative;
                default: return Unknown;
            }
        }
    }

    /// <summary>One classified goal of a person's prompts, named from the taxonomy that classified most of them.</summary>
    public sealed class CopilotSpendPersonCategory
    {
        [JsonProperty("categoryId")]
        public string CategoryId { get; set; }

        /// <summary>The name the taxonomy gave the goal; null when its taxonomy could not be read.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>The portal's translation key for a built-in goal; null for one the tenant named or renamed.</summary>
        [JsonProperty("nameKey")]
        public string NameKey { get; set; }

        [JsonProperty("humanMode")]
        public string HumanMode { get; set; }

        [JsonProperty("prompts")]
        public long Prompts { get; set; }

        [JsonProperty("lastUtc")]
        public DateTime? LastUtc { get; set; }
    }

    /// <summary>One person's prompts in the period: how many, how long and what they carried. Counts only.</summary>
    public sealed class CopilotSpendPromptTotals
    {
        [JsonProperty("prompts")]
        public long Prompts { get; set; }

        [JsonProperty("conversations")]
        public int Conversations { get; set; }

        [JsonProperty("days")]
        public int Days { get; set; }

        [JsonProperty("averageWords")]
        public double? AverageWords { get; set; }

        [JsonProperty("withAttachments")]
        public int WithAttachments { get; set; }

        [JsonProperty("withLinks")]
        public int WithLinks { get; set; }

        [JsonProperty("withMentions")]
        public int WithMentions { get; set; }

        [JsonProperty("withContext")]
        public int WithContext { get; set; }

        [JsonProperty("firstUtc")]
        public DateTime? FirstUtc { get; set; }

        [JsonProperty("lastUtc")]
        public DateTime? LastUtc { get; set; }
    }

    /// <summary>A key phrase Azure AI Language found in a person's prompts, as it found it: tenant text, never translated.</summary>
    public sealed class CopilotSpendPhrase
    {
        [JsonProperty("phrase")]
        public string Phrase { get; set; }

        [JsonProperty("prompts")]
        public long Prompts { get; set; }

        [JsonProperty("lastUtc")]
        public DateTime? LastUtc { get; set; }
    }

    /// <summary>One of a person's latest prompts: when, in which app, its goal and key phrases, and what it carried. Never its text.</summary>
    public sealed class CopilotSpendRecentPrompt
    {
        [JsonIgnore]
        public int InteractionId { get; set; }

        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        /// <summary>The app the prompt was written in, as the interaction history names it.</summary>
        [JsonProperty("appClass")]
        public string AppClass { get; set; }

        [JsonProperty("words")]
        public int Words { get; set; }

        [JsonProperty("attachments")]
        public int Attachments { get; set; }

        [JsonProperty("links")]
        public int Links { get; set; }

        [JsonProperty("mentions")]
        public int Mentions { get; set; }

        [JsonProperty("context")]
        public int Context { get; set; }

        /// <summary>The classified goal; null when the prompt has not been classified.</summary>
        [JsonProperty("categoryId")]
        public string CategoryId { get; set; }

        [JsonIgnore]
        public string TaxonomyVersion { get; set; }

        [JsonProperty("categoryName")]
        public string CategoryName { get; set; }

        [JsonProperty("categoryNameKey")]
        public string CategoryNameKey { get; set; }

        /// <summary>Up to <see cref="SqlCopilotSpendFactsSource.PhrasesPerPrompt"/> of the prompt's key phrases.</summary>
        [JsonProperty("phrases")]
        public List<string> Phrases { get; set; } = new List<string>();
    }

    /// <summary>
    /// How one source's credits are spread across the people in view who consumed any: how concentrated the spend is, who
    /// spends the most and the least, and how many people sit at each level. Built from the person rows, so credits no
    /// imported user matched are not in it.
    /// </summary>
    /// <remarks>
    /// A reader without See PII is sent <see cref="Anonymised"/>: no named list, no smallest or largest amount, and no
    /// figure that could be narrowed to fewer than <see cref="MinPeople"/> people, whether on its own or by subtracting
    /// one share of the total from another.
    /// </remarks>
    public sealed class CopilotSpendDistribution
    {
        public const double Top10Fraction = 0.1;
        public const double Top20Fraction = 0.2;

        /// <summary>People with an amount above zero: everyone below is about them.</summary>
        [JsonProperty("consumers")]
        public int Consumers { get; set; }

        /// <summary>The fewest people any figure describes to a reader without See PII.</summary>
        [JsonProperty("minPeople")]
        public int MinPeople { get; set; }

        /// <summary>Balances only: people in view whose latest reading is zero. Null for the Studio ledger, which records only charges.</summary>
        [JsonProperty("zeroPeople")]
        public int? ZeroPeople { get; set; }

        [JsonProperty("total")]
        public double? Total { get; set; }

        [JsonProperty("median")]
        public double? Median { get; set; }

        [JsonProperty("mean")]
        public double? Mean { get; set; }

        /// <summary>The nearest-rank 90th percentile: nine in ten consumers are at or below it.</summary>
        [JsonProperty("p90")]
        public double? P90 { get; set; }

        [JsonProperty("min")]
        public double? Min { get; set; }

        [JsonProperty("max")]
        public double? Max { get; set; }

        /// <summary>0 when everyone consumed the same; towards 1 when a few consumed nearly everything.</summary>
        [JsonProperty("gini")]
        public double? Gini { get; set; }

        /// <summary>The share of the total consumed by the tenth of consumers with the largest amounts (at least one person).</summary>
        [JsonProperty("top10Share")]
        public double? Top10Share { get; set; }

        [JsonProperty("top20Share")]
        public double? Top20Share { get; set; }

        /// <summary>The fewest people, the largest first, whose amounts make up at least half the total.</summary>
        [JsonProperty("peopleForHalf")]
        public int? PeopleForHalf { get; set; }

        /// <summary>True when <see cref="PeopleForHalf"/> is withheld because it is below <see cref="MinPeople"/>.</summary>
        [JsonProperty("halfFromFewerThanMin")]
        public bool HalfFromFewerThanMin { get; set; }

        /// <summary>The cumulative share of the total against the share of consumers, the largest first, from (0, 0) to (1, 1).</summary>
        [JsonProperty("pareto")]
        public List<CopilotSpendParetoPoint> Pareto { get; set; } = new List<CopilotSpendParetoPoint>();

        /// <summary>Consumers by amount, in bands on a 1-2-5 scale so that both small and large amounts can be read.</summary>
        [JsonProperty("bands")]
        public List<CopilotSpendBand> Bands { get; set; } = new List<CopilotSpendBand>();

        /// <summary>The largest consumers, largest first. Empty for a reader without See PII.</summary>
        [JsonProperty("top")]
        public List<CopilotSpendRankedPerson> Top { get; set; } = new List<CopilotSpendRankedPerson>();

        /// <summary>The smallest consumers above zero, smallest first. Empty for a reader without See PII.</summary>
        [JsonProperty("lowest")]
        public List<CopilotSpendRankedPerson> Lowest { get; set; } = new List<CopilotSpendRankedPerson>();

        /// <summary>True when there are too few consumers to describe to a reader without See PII: only the count is sent.</summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }

        /// <summary>
        /// A copy for a reader without See PII. Distributions are cached and shared between readers, so this is never applied
        /// to the cached object. Every share of the total is a sum over the people above a cut, and two cuts closer together
        /// than <paramref name="minPeople"/> would give, by subtraction, the sum of fewer people than that: so the cuts kept
        /// (the top-share figures and the points of the curve) are at least that far apart, and from both ends.
        /// </summary>
        public CopilotSpendDistribution Anonymised(int minPeople)
        {
            var copy = (CopilotSpendDistribution)MemberwiseClone();
            copy.MinPeople = minPeople;
            copy.Top = new List<CopilotSpendRankedPerson>();
            copy.Lowest = new List<CopilotSpendRankedPerson>();
            copy.Min = null;
            copy.Max = null;
            var n = Consumers;
            // No one consumed any: there is nothing to withhold and nothing to narrow, so the report says so rather than "too few".
            if (n == 0) return copy;
            if (n < minPeople)
            {
                copy.Withheld = true;
                copy.Total = copy.Median = copy.Mean = copy.P90 = copy.Gini = copy.Top10Share = copy.Top20Share = null;
                copy.PeopleForHalf = null;
                copy.HalfFromFewerThanMin = false;
                copy.Pareto = new List<CopilotSpendParetoPoint>();
                copy.Bands = new List<CopilotSpendBand>();
                return copy;
            }

            // A percentile is one person's amount: shown only when enough people are at or above it that it is not the largest few.
            if (n - CopilotInvestmentStatistics.NearestRank(n, 0.9) + 1 < minPeople) copy.P90 = null;

            var cuts = new List<int> { 0, n };
            bool Fits(int k) => cuts.All(c => c == k || Math.Abs(c - k) >= minPeople);
            var top10 = CopilotInvestmentStatistics.TopCount(n, Top10Fraction);
            if (Top10Share.HasValue && Fits(top10)) cuts.Add(top10);
            else copy.Top10Share = null;
            var top20 = CopilotInvestmentStatistics.TopCount(n, Top20Fraction);
            if (Top20Share.HasValue && Fits(top20)) cuts.Add(top20);
            else copy.Top20Share = null;

            copy.Pareto = new List<CopilotSpendParetoPoint>();
            foreach (var point in Pareto)
            {
                if (point.People != 0 && point.People != n)
                {
                    if (!Fits(point.People)) continue;
                    cuts.Add(point.People);
                }
                copy.Pareto.Add(point);
            }

            if (PeopleForHalf.HasValue && PeopleForHalf.Value < minPeople)
            {
                copy.PeopleForHalf = null;
                copy.HalfFromFewerThanMin = true;
            }
            copy.Bands = BandsOfAtLeast(Bands, minPeople);
            return copy;
        }

        /// <summary>
        /// Adjacent bands combined until each holds no one or at least <paramref name="minPeople"/> people, so no band narrows
        /// the largest or smallest consumers to a few. Their amounts are left out: a band's total is a sum over a cut too.
        /// </summary>
        internal static List<CopilotSpendBand> BandsOfAtLeast(IEnumerable<CopilotSpendBand> bands, int minPeople)
        {
            var merged = new List<CopilotSpendBand>();
            CopilotSpendBand current = null;
            foreach (var band in bands ?? Enumerable.Empty<CopilotSpendBand>())
            {
                if (current == null && band.People == 0)
                {
                    merged.Add(new CopilotSpendBand { From = band.From, To = band.To });
                    continue;
                }
                if (current == null) current = new CopilotSpendBand { From = band.From, To = band.To, People = band.People };
                else
                {
                    current.To = band.To;
                    current.People += band.People;
                }
                if (current.People >= minPeople)
                {
                    merged.Add(current);
                    current = null;
                }
            }
            if (current != null)
            {
                // The largest amounts still hold too few people: they join the band below, with any empty bands between.
                var last = merged.FindLastIndex(b => b.People > 0);
                if (last < 0) merged.Add(current);
                else
                {
                    merged[last].To = current.To;
                    merged[last].People += current.People;
                    merged.RemoveRange(last + 1, merged.Count - last - 1);
                }
            }
            return merged;
        }
    }

    public sealed class CopilotSpendParetoPoint
    {
        /// <summary>How many consumers, the largest first.</summary>
        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("peopleShare")]
        public double PeopleShare { get; set; }

        /// <summary>Their share of the total.</summary>
        [JsonProperty("creditShare")]
        public double CreditShare { get; set; }
    }

    /// <summary>Consumers whose amount is at least <see cref="From"/> and below <see cref="To"/>.</summary>
    public sealed class CopilotSpendBand
    {
        [JsonProperty("from")]
        public double From { get; set; }

        [JsonProperty("to")]
        public double To { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        /// <summary>Their credits together. Null for a reader without See PII.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }
    }

    /// <summary>A named consumer in a distribution's largest or smallest list: only ever sent to a reader with See PII.</summary>
    public sealed class CopilotSpendRankedPerson
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        /// <summary>Their share of the distribution's total.</summary>
        [JsonProperty("share")]
        public double Share { get; set; }
    }

    /// <summary>Stable codes the API answers with; the portal writes the words.</summary>
    public static class CopilotSpendErrorCodes
    {
        public const string StudioUnavailable = "studioUnavailable";
        public const string StudioBreakdownUnavailable = "studioBreakdownUnavailable";
        public const string CreditsUnavailable = "creditsUnavailable";

        /// <summary>A person's own prompts, Studio days and resources could not be read; the rest of their drill-down shows.</summary>
        public const string PersonUsageUnavailable = "personUsageUnavailable";
    }
}
