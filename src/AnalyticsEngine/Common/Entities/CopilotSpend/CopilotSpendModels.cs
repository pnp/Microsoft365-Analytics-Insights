using Common.Entities.CopilotInvestment;
using Common.Entities.UserFilters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

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
        /// A copy for a reader without See PII: no people, and no grouping that names a person. Summaries are cached and
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
                organisation.Hidden = true;
                copy.Organisation = organisation;
            }
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

        /// <summary>Billed credits in the window for the people in view; unfiltered, also the charges no user matched.</summary>
        [JsonProperty("credits")]
        public double Credits { get; set; }

        /// <summary>People in view with billed credits in the window.</summary>
        [JsonProperty("people")]
        public int People { get; set; }

        /// <summary>Of <see cref="Credits"/>, charges the user import never matched to a person. Null in a filtered view.</summary>
        [JsonProperty("unmatchedCredits")]
        public double? UnmatchedCredits { get; set; }

        [JsonProperty("days")]
        public List<CopilotSpendDay> Days { get; set; } = new List<CopilotSpendDay>();

        [JsonProperty("agents")]
        public List<CopilotSpendAgent> Agents { get; set; } = new List<CopilotSpendAgent>();

        /// <summary>A stable code when the per-day and per-agent breakdown could not be read.</summary>
        [JsonProperty("breakdownFailureCode")]
        public string BreakdownFailureCode { get; set; }
    }

    public sealed class CopilotSpendDay
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }
    }

    public sealed class CopilotSpendAgent
    {
        [JsonProperty("agentId")]
        public string AgentId { get; set; }

        [JsonProperty("agentName")]
        public string AgentName { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

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

        /// <summary>Latest balances, in credits, of the people in view; unfiltered, also the accounts no user matched.</summary>
        [JsonProperty("credits")]
        public double Credits { get; set; }

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

        [JsonProperty("minGroupSize")]
        public int MinGroupSize { get; set; }

        [JsonProperty("foldedGroups")]
        public int FoldedGroups { get; set; }

        /// <summary>True when the grouping names people and the reader may not see them.</summary>
        [JsonProperty("hidden")]
        public bool Hidden { get; set; }

        [JsonProperty("groups")]
        public List<CopilotSpendGroupRow> Groups { get; set; } = new List<CopilotSpendGroupRow>();

        internal object MemberwiseCopy() => MemberwiseClone();
    }

    public sealed class CopilotSpendGroupRow
    {
        /// <summary>The tenant's own value (a department name, say). Null for a row the report labels itself.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary><c>notSet</c> or <c>otherSmallGroups</c> when <see cref="Label"/> is null.</summary>
        [JsonProperty("emptyKey")]
        public string EmptyKey { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("studioPeople")]
        public int StudioPeople { get; set; }

        [JsonProperty("studioCredits")]
        public double StudioCredits { get; set; }

        [JsonProperty("shareOfStudio")]
        public double? ShareOfStudio { get; set; }

        [JsonProperty("balancePeople")]
        public int BalancePeople { get; set; }

        [JsonProperty("balanceCredits")]
        public double BalanceCredits { get; set; }

        [JsonProperty("shareOfBalances")]
        public double? ShareOfBalances { get; set; }

        [JsonProperty("unknownAmountPeople")]
        public int UnknownAmountPeople { get; set; }
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

        [JsonProperty("studioCredits")]
        public double StudioCredits { get; set; }

        [JsonProperty("studioDays")]
        public int StudioDays { get; set; }

        [JsonProperty("studioAgents")]
        public int StudioAgents { get; set; }

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

    /// <summary>One person's drill-down: their balance by category and over time, and their billed Studio use by agent.</summary>
    public sealed class CopilotSpendPersonDetail
    {
        [JsonProperty("person")]
        public CopilotSpendPersonRow Person { get; set; }

        [JsonProperty("services")]
        public List<CopilotInvestmentPersonService> Services { get; set; } = new List<CopilotInvestmentPersonService>();

        [JsonProperty("balances")]
        public List<PersonBalancePoint> Balances { get; set; } = new List<PersonBalancePoint>();

        [JsonProperty("studioAgents")]
        public List<CopilotInvestmentStudioAgent> StudioAgents { get; set; } = new List<CopilotInvestmentStudioAgent>();
    }

    /// <summary>Stable codes the API answers with; the portal writes the words.</summary>
    public static class CopilotSpendErrorCodes
    {
        public const string StudioUnavailable = "studioUnavailable";
        public const string StudioBreakdownUnavailable = "studioBreakdownUnavailable";
        public const string CreditsUnavailable = "creditsUnavailable";
    }
}
