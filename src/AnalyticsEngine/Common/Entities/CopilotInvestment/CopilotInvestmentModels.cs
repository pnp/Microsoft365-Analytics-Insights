using Common.Entities.UserFilters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// The Copilot investment report: what is being spent, what observed work supports it, where value and spend
    /// concentrate, the break-even requirement and its sensitivity, and how far each conclusion can be trusted.
    /// </summary>
    /// <remarks>
    /// <para><b>Facts and stable keys, never sentences.</b> The portal writes every sentence in the reader's
    /// language from the codes and figures here. Tenant data (names, departments, policy names, service ids) is
    /// passed through verbatim.</para>
    /// <para><b>Money is modelled.</b> Every amount comes from an administrator's (or the reader's own what-if)
    /// price or hourly cost applied to observed counts. None is an invoice; none is a measured saving.</para>
    /// </remarks>
    public sealed class CopilotInvestmentSummary
    {
        [JsonProperty("generatedUtc")]
        public DateTime GeneratedUtc { get; set; }

        [JsonProperty("fromUtc")]
        public DateTime FromUtc { get; set; }

        [JsonProperty("toUtc")]
        public DateTime ToUtc { get; set; }

        [JsonProperty("windowDays")]
        public int WindowDays { get; set; }

        /// <summary>Working days the window holds, on the Copilot Adoption report's own definition.</summary>
        [JsonProperty("weekdays")]
        public double Weekdays { get; set; }

        /// <summary>The window as a share of an average month (365.2425 / 12 days).</summary>
        [JsonProperty("monthsInWindow")]
        public double MonthsInWindow { get; set; }

        [JsonProperty("filtered")]
        public bool Filtered { get; set; }

        [JsonProperty("userFilter")]
        public UserFilterEcho UserFilter { get; set; }

        [JsonProperty("globalFilter")]
        public GlobalFilterEcho GlobalFilter { get; set; }

        [JsonIgnore]
        public string GlobalFilterDescription { get; set; }

        [JsonIgnore]
        public string UserFilterDescription { get; set; }

        [JsonProperty("assumptions")]
        public CopilotInvestmentAssumptionsView Assumptions { get; set; } = new CopilotInvestmentAssumptionsView();

        [JsonProperty("sources")]
        public CopilotInvestmentSources Sources { get; set; } = new CopilotInvestmentSources();

        [JsonProperty("investment")]
        public CopilotInvestmentSpend Investment { get; set; } = new CopilotInvestmentSpend();

        [JsonProperty("evidence")]
        public CopilotInvestmentEvidence Evidence { get; set; } = new CopilotInvestmentEvidence();

        [JsonProperty("value")]
        public CopilotInvestmentValue Value { get; set; } = new CopilotInvestmentValue();

        [JsonProperty("breakEven")]
        public CopilotInvestmentBreakEven BreakEven { get; set; } = new CopilotInvestmentBreakEven();

        [JsonProperty("sensitivity")]
        public CopilotInvestmentSensitivity Sensitivity { get; set; } = new CopilotInvestmentSensitivity();

        /// <summary>What it would take to break even, and what reclaiming and reassigning seats would change.</summary>
        [JsonProperty("path")]
        public CopilotInvestmentPath Path { get; set; } = new CopilotInvestmentPath();

        /// <summary>Weekly active seat holders over recent complete weeks. Tenant-wide, so only for an unfiltered view.</summary>
        [JsonProperty("trajectory")]
        public CopilotInvestmentTrajectory Trajectory { get; set; } = new CopilotInvestmentTrajectory();

        [JsonProperty("cohorts")]
        public List<CopilotInvestmentCohort> Cohorts { get; set; } = new List<CopilotInvestmentCohort>();

        [JsonProperty("organisation")]
        public CopilotInvestmentOrganisation Organisation { get; set; } = new CopilotInvestmentOrganisation();

        [JsonProperty("concentration")]
        public CopilotInvestmentConcentration Concentration { get; set; } = new CopilotInvestmentConcentration();

        [JsonProperty("metered")]
        public CopilotInvestmentMetered Metered { get; set; } = new CopilotInvestmentMetered();

        [JsonProperty("services")]
        public List<CopilotInvestmentServiceRow> Services { get; set; } = new List<CopilotInvestmentServiceRow>();

        /// <summary>Spending policies as the source last described them. Tenant-wide: never narrowed by a filter.</summary>
        [JsonProperty("policies")]
        public List<CopilotInvestmentPolicyRow> Policies { get; set; } = new List<CopilotInvestmentPolicyRow>();

        /// <summary>The tenant's credit balances per billing method. Tenant-wide, shown with that label in a filtered view too.</summary>
        [JsonProperty("tenantBalances")]
        public List<CopilotInvestmentTenantBalanceRow> TenantBalances { get; set; } = new List<CopilotInvestmentTenantBalanceRow>();

        [JsonProperty("actions")]
        public List<CopilotInvestmentAction> Actions { get; set; } = new List<CopilotInvestmentAction>();

        [JsonProperty("findings")]
        public List<CopilotInvestmentFinding> Findings { get; set; } = new List<CopilotInvestmentFinding>();

        [JsonProperty("trust")]
        public CopilotInvestmentTrust Trust { get; set; } = new CopilotInvestmentTrust();

        /// <summary>True when per-person rows were left out because the reader lacks See PII.</summary>
        [JsonProperty("individualDataHidden")]
        public bool IndividualDataHidden { get; set; }

        /// <summary>The people behind every figure. Never serialised in the summary: served by the See PII people endpoint.</summary>
        [JsonIgnore]
        public List<CopilotInvestmentPersonRow> People { get; set; } = new List<CopilotInvestmentPersonRow>();

        /// <summary>A copy without anything that names or isolates a person, for a reader without See PII.</summary>
        public CopilotInvestmentSummary WithoutIndividualData()
        {
            var copy = (CopilotInvestmentSummary)MemberwiseClone();
            copy.People = new List<CopilotInvestmentPersonRow>();
            copy.IndividualDataHidden = true;
            // A manager roll-up names a person in every row label, so it is not served without See PII.
            if (Organisation != null && CopilotInvestmentDimensions.NamesPeople(Organisation.Dimension))
            {
                var organisation = (CopilotInvestmentOrganisation)Organisation.MemberwiseCopy();
                organisation.Groups = new List<CopilotInvestmentGroupRow>();
                organisation.Hidden = true;
                copy.Organisation = organisation;
            }
            return copy;
        }
    }

    /// <summary>The org dimensions a breakdown may use, and which of them name people.</summary>
    public static class CopilotInvestmentDimensions
    {
        public const string Department = UserFilterDimensions.Department;
        public const string Manager = UserFilterDimensions.Manager;

        public static bool NamesPeople(string dimension) =>
            dimension == UserFilterDimensions.Manager || dimension == UserFilterDimensions.ManagementChain
            || dimension == UserFilterDimensions.UserName;

        /// <summary>
        /// Whether a breakdown may group by this dimension at all. The chain, sign-in name and postal code are not
        /// groupings, and the account-status and user-type tokens are filter values rather than tenant data.
        /// </summary>
        public static bool Groupable(string dimension) =>
            !string.IsNullOrEmpty(dimension) && dimension != UserFilterDimensions.ManagementChain && dimension != UserFilterDimensions.UserName
            && dimension != UserFilterDimensions.PostalCode && dimension != UserFilterDimensions.AccountStatus
            && dimension != UserFilterDimensions.UserType && UserFilterDimensions.IsWellFormed(dimension);
    }

    public sealed class CopilotInvestmentAssumptionsView
    {
        /// <summary><c>complete</c>, <c>partial</c>, <c>missing</c> or <c>unavailable</c> (the store could not be read).</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = CopilotInvestmentAssumptionStatus.Missing;

        /// <summary>True when any figure came from the reader's own what-if rather than the tenant defaults.</summary>
        [JsonProperty("scenarioApplied")]
        public bool ScenarioApplied { get; set; }

        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; set; }

        [JsonProperty("note")]
        public string Note { get; set; }

        [JsonProperty("values")]
        public CopilotInvestmentAssumptions Values { get; set; } = new CopilotInvestmentAssumptions();

        [JsonProperty("provenance")]
        public Dictionary<string, string> Provenance { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Inputs the modelled return still needs.</summary>
        [JsonProperty("missingForValue")]
        public List<string> MissingForValue { get; set; } = new List<string>();

        [JsonProperty("storageDurable")]
        public bool StorageDurable { get; set; }
    }

    public static class CopilotInvestmentAssumptionStatus
    {
        public const string Complete = "complete";
        public const string Partial = "partial";
        public const string Missing = "missing";
        public const string Unavailable = "unavailable";
    }

    public sealed class CopilotInvestmentSources
    {
        [JsonProperty("adoption")]
        public CopilotInvestmentAdoptionSource Adoption { get; set; } = new CopilotInvestmentAdoptionSource();

        [JsonProperty("credits")]
        public CopilotInvestmentCreditSource Credits { get; set; } = new CopilotInvestmentCreditSource();

        [JsonProperty("studio")]
        public CopilotInvestmentStudioSource Studio { get; set; } = new CopilotInvestmentStudioSource();
    }

    public sealed class CopilotInvestmentAdoptionSource
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("auditAvailable")]
        public bool AuditAvailable { get; set; }

        [JsonProperty("usageReportAvailable")]
        public bool UsageReportAvailable { get; set; }

        [JsonProperty("usageReportDate")]
        public DateTime? UsageReportDate { get; set; }

        [JsonProperty("figuresIncomplete")]
        public bool FiguresIncomplete { get; set; }

        [JsonProperty("licensedUsersCapped")]
        public bool LicensedUsersCapped { get; set; }

        /// <summary>Seat holders in this view the adoption analysis did not score because of its row cap.</summary>
        [JsonProperty("holdersNotAnalysed")]
        public int HoldersNotAnalysed { get; set; }

        [JsonProperty("auditSignalHolders")]
        public int AuditSignalHolders { get; set; }

        [JsonProperty("usageReportSignalHolders")]
        public int UsageReportSignalHolders { get; set; }

        [JsonProperty("analysisGeneratedUtc")]
        public DateTime? AnalysisGeneratedUtc { get; set; }
    }

    public sealed class CopilotInvestmentCreditSource
    {
        [JsonProperty("tablesPresent")]
        public bool TablesPresent { get; set; }

        /// <summary>The installer's preview switch for collection. History is reported whether or not collection is still on.</summary>
        [JsonProperty("collectionEnabled")]
        public bool CollectionEnabled { get; set; }

        [JsonProperty("hasData")]
        public bool HasData { get; set; }

        [JsonProperty("firstObservationDate")]
        public DateTime? FirstObservationDate { get; set; }

        [JsonProperty("snapshotDate")]
        public DateTime? SnapshotDate { get; set; }

        [JsonProperty("snapshotBandFrom")]
        public DateTime? SnapshotBandFrom { get; set; }

        /// <summary><c>unverified</c>, <c>verified</c>, <c>mixed</c>, or null with no data.</summary>
        [JsonProperty("periodStatus")]
        public string PeriodStatus { get; set; }

        [JsonProperty("observedUsers")]
        public int ObservedUsers { get; set; }

        /// <summary>Directory objects with a balance that no imported user matches. Reported only for an unfiltered view.</summary>
        [JsonProperty("unresolvedObjects")]
        public int? UnresolvedObjects { get; set; }

        [JsonProperty("unresolvedCredits")]
        public double? UnresolvedCredits { get; set; }

        [JsonProperty("partialUsers")]
        public int PartialUsers { get; set; }

        [JsonProperty("staleUsers")]
        public int StaleUsers { get; set; }

        [JsonProperty("oldestFreshness")]
        public DateTimeOffset? OldestFreshness { get; set; }

        [JsonProperty("newestFreshness")]
        public DateTimeOffset? NewestFreshness { get; set; }

        [JsonProperty("sweepsInWindow")]
        public int SweepsInWindow { get; set; }

        [JsonProperty("completedSweepsInWindow")]
        public int CompletedSweepsInWindow { get; set; }

        /// <summary>The latest collector sweep's directory coverage. Tenant-wide.</summary>
        [JsonProperty("directoryUsers")]
        public long? DirectoryUsers { get; set; }

        [JsonProperty("collectorObserved")]
        public long? CollectorObserved { get; set; }

        [JsonProperty("collectorFailed")]
        public long? CollectorFailed { get; set; }

        [JsonProperty("collectorDenied")]
        public long? CollectorDenied { get; set; }

        [JsonProperty("collectorPending")]
        public long? CollectorPending { get; set; }

        [JsonProperty("collectorPhase")]
        public string CollectorPhase { get; set; }

        [JsonProperty("collectorLastCompleted")]
        public DateTimeOffset? CollectorLastCompleted { get; set; }

        [JsonProperty("contextObservedAt")]
        public DateTimeOffset? ContextObservedAt { get; set; }

        [JsonProperty("failureCode")]
        public string FailureCode { get; set; }
    }

    public sealed class CopilotInvestmentStudioSource
    {
        [JsonProperty("hasData")]
        public bool HasData { get; set; }

        [JsonProperty("importEnabled")]
        public bool ImportEnabled { get; set; }

        [JsonProperty("lastImportUtc")]
        public DateTime? LastImportUtc { get; set; }

        [JsonProperty("lastImportFailed")]
        public bool LastImportFailed { get; set; }

        [JsonProperty("unresolvedCredits")]
        public double? UnresolvedCredits { get; set; }

        [JsonProperty("failureCode")]
        public string FailureCode { get; set; }
    }

    /// <summary>What the investment is, by source, each in its own unit. Never one sum of unlike things.</summary>
    public sealed class CopilotInvestmentSpend
    {
        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("seats")]
        public CopilotInvestmentSeats Seats { get; set; } = new CopilotInvestmentSeats();

        [JsonProperty("studio")]
        public CopilotInvestmentStudioSpend Studio { get; set; } = new CopilotInvestmentStudioSpend();

        [JsonProperty("credits")]
        public CopilotInvestmentCreditBalances Credits { get; set; } = new CopilotInvestmentCreditBalances();

        /// <summary>
        /// The modelled cost the return is measured against: analysed seat holders' seats, plus their own billed
        /// Copilot Studio credits when a Studio price is set. Null when the seat price is missing.
        /// </summary>
        [JsonProperty("modelledWindowCost")]
        public double? ModelledWindowCost { get; set; }

        [JsonProperty("modelledMonthlyCost")]
        public double? ModelledMonthlyCost { get; set; }

        /// <summary>The seat part of <see cref="ModelledMonthlyCost"/>: the analysed holders' seats, a month.</summary>
        [JsonProperty("monthlySeatCost")]
        public double? MonthlySeatCost { get; set; }

        /// <summary>
        /// The Copilot Studio part of <see cref="ModelledMonthlyCost"/>: the analysed holders' own billed credits, a month.
        /// Null when no Studio price is set. Zero when the ledger could not be read, which the confidence section says.
        /// </summary>
        [JsonProperty("monthlyStudioCost")]
        public double? MonthlyStudioCost { get; set; }
    }

    public sealed class CopilotInvestmentSeats
    {
        /// <summary>Every seat holder in scope: analysed, measured only in another period, and not analysed.</summary>
        [JsonProperty("holders")]
        public int Holders { get; set; }

        /// <summary>The holders whose activity was measured in this window: the population the return is about.</summary>
        [JsonProperty("analysedHolders")]
        public int AnalysedHolders { get; set; }

        /// <summary>Holders whose use is known only from Microsoft's usage report, for its own period.</summary>
        [JsonProperty("otherPeriodHolders")]
        public int OtherPeriodHolders { get; set; }

        /// <summary>Holders the adoption analysis did not score (its per-run cap).</summary>
        [JsonProperty("notAnalysedHolders")]
        public int NotAnalysedHolders { get; set; }

        /// <summary><c>licenceHistory</c>, <c>currentAssignments</c> or <c>mixed</c>.</summary>
        [JsonProperty("basis")]
        public string Basis { get; set; }

        [JsonProperty("seatDays")]
        public double SeatDays { get; set; }

        [JsonProperty("monthlyRunRate")]
        public double? MonthlyRunRate { get; set; }

        [JsonProperty("windowCost")]
        public double? WindowCost { get; set; }

        [JsonProperty("analysedWindowCost")]
        public double? AnalysedWindowCost { get; set; }

        /// <summary>The analysed holders' seat cost a month, divided by those of them with any Copilot activity. Seats only.</summary>
        [JsonProperty("costPerActiveHolderMonthly")]
        public double? CostPerActiveHolderMonthly { get; set; }
    }

    public sealed class CopilotInvestmentStudioSpend
    {
        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        /// <summary>
        /// The same ledger over the equal-length period before the window. Null unless the ledger reaches back over the
        /// whole of that period, so a comparison is only made like for like.
        /// </summary>
        [JsonProperty("priorCredits")]
        public double? PriorCredits { get; set; }

        [JsonProperty("priorUsers")]
        public int? PriorUsers { get; set; }

        [JsonProperty("holderCredits")]
        public double HolderCredits { get; set; }

        [JsonProperty("windowValue")]
        public double? WindowValue { get; set; }

        [JsonProperty("holderWindowValue")]
        public double? HolderWindowValue { get; set; }
    }

    /// <summary>Latest preview balances. A balance in the source's own (unverified) period, never spend.</summary>
    public sealed class CopilotInvestmentCreditBalances
    {
        [JsonProperty("observedUsers")]
        public int ObservedUsers { get; set; }

        [JsonProperty("consumingUsers")]
        public int ConsumingUsers { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("holderCredits")]
        public double HolderCredits { get; set; }

        [JsonProperty("nonHolderCredits")]
        public double NonHolderCredits { get; set; }

        /// <summary>The latest balances valued at the credit price. A balance valuation, not a period cost.</summary>
        [JsonProperty("balanceValue")]
        public double? BalanceValue { get; set; }

        [JsonProperty("periodStatus")]
        public string PeriodStatus { get; set; }

        [JsonProperty("otherUnitUsers")]
        public int OtherUnitUsers { get; set; }

        [JsonProperty("unknownAmountUsers")]
        public int UnknownAmountUsers { get; set; }

        [JsonProperty("limitExhaustedUsers")]
        public int LimitExhaustedUsers { get; set; }

        [JsonProperty("nearLimitUsers")]
        public int NearLimitUsers { get; set; }

        [JsonProperty("notEntitledUsers")]
        public int NotEntitledUsers { get; set; }
    }

    public sealed class CopilotInvestmentEvidence
    {
        [JsonProperty("holders")]
        public int Holders { get; set; }

        [JsonProperty("analysedHolders")]
        public int AnalysedHolders { get; set; }

        [JsonProperty("activeHolders")]
        public int ActiveHolders { get; set; }

        [JsonProperty("activeShare")]
        public double? ActiveShare { get; set; }

        [JsonProperty("idleHolders")]
        public int IdleHolders { get; set; }

        [JsonProperty("idleTooNewToJudge")]
        public int IdleTooNewToJudge { get; set; }

        [JsonProperty("totalActiveDays")]
        public long TotalActiveDays { get; set; }

        [JsonProperty("medianActiveDays")]
        public double? MedianActiveDays { get; set; }

        [JsonProperty("p25ActiveDays")]
        public double? P25ActiveDays { get; set; }

        [JsonProperty("p75ActiveDays")]
        public double? P75ActiveDays { get; set; }

        [JsonProperty("activeDaysPerActiveHolderMonthly")]
        public double? ActiveDaysPerActiveHolderMonthly { get; set; }

        /// <summary>Share of available working days an average active holder used Copilot on.</summary>
        [JsonProperty("habitShare")]
        public double? HabitShare { get; set; }

        [JsonProperty("multiAppHolders")]
        public int MultiAppHolders { get; set; }

        [JsonProperty("agentHolders")]
        public int AgentHolders { get; set; }

        [JsonProperty("coworkHolders")]
        public int CoworkHolders { get; set; }

        [JsonProperty("bands")]
        public List<CopilotInvestmentBandCount> Bands { get; set; } = new List<CopilotInvestmentBandCount>();

        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }

        [JsonProperty("unlicensedActiveDays")]
        public long UnlicensedActiveDays { get; set; }

        [JsonProperty("recommendedCandidates")]
        public int RecommendedCandidates { get; set; }

        [JsonProperty("distribution")]
        public List<CopilotInvestmentDistributionBucket> Distribution { get; set; } = new List<CopilotInvestmentDistributionBucket>();
    }

    public sealed class CopilotInvestmentBandCount
    {
        [JsonProperty("band")]
        public string Band { get; set; }

        [JsonProperty("holders")]
        public int Holders { get; set; }
    }

    /// <summary>
    /// Seat holders by active days per month. The seat-only break-even is itself a bucket edge, so every bucket is wholly
    /// below it or wholly at or above it - <see cref="BelowBreakEven"/> says which, and is null when there is no break-even.
    /// </summary>
    public sealed class CopilotInvestmentDistributionBucket
    {
        /// <summary><c>none</c> for no use, otherwise <c>range</c>; the page words it from the two edges.</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("minDaysPerMonth")]
        public double MinDaysPerMonth { get; set; }

        [JsonProperty("maxDaysPerMonth")]
        public double? MaxDaysPerMonth { get; set; }

        [JsonProperty("holders")]
        public int Holders { get; set; }

        [JsonProperty("belowBreakEven")]
        public bool? BelowBreakEven { get; set; }
    }

    public static class CopilotInvestmentValueUnavailable
    {
        /// <summary>A figure the value model needs has not been set.</summary>
        public const string Assumptions = "assumptions";

        /// <summary>No seat holder's activity was measured in this window, so there is nothing like for like to value.</summary>
        public const string NoMatchedHolders = "noMatchedHolders";
    }

    public sealed class CopilotInvestmentValue
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>Why value is not modelled (<see cref="CopilotInvestmentValueUnavailable"/>); null when it is.</summary>
        [JsonProperty("unavailableReason")]
        public string UnavailableReason { get; set; }

        [JsonProperty("missing")]
        public List<string> Missing { get; set; } = new List<string>();

        [JsonProperty("scenarios")]
        public List<CopilotInvestmentScenarioResult> Scenarios { get; set; } = new List<CopilotInvestmentScenarioResult>();

        [JsonProperty("valuePerActiveHolderMonthly")]
        public double? ValuePerActiveHolderMonthly { get; set; }
    }

    public sealed class CopilotInvestmentScenarioResult
    {
        [JsonProperty("scenario")]
        public string Scenario { get; set; }

        [JsonProperty("minutesPerActiveDay")]
        public double MinutesPerActiveDay { get; set; }

        [JsonProperty("realisation")]
        public double Realisation { get; set; }

        [JsonProperty("assistedHours")]
        public double AssistedHours { get; set; }

        [JsonProperty("realisedHours")]
        public double RealisedHours { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }

        [JsonProperty("monthlyValue")]
        public double MonthlyValue { get; set; }

        [JsonProperty("roiMultiple")]
        public double? RoiMultiple { get; set; }

        [JsonProperty("net")]
        public double? Net { get; set; }

        [JsonProperty("holdersAtOrAboveBreakEven")]
        public int HoldersAtOrAboveBreakEven { get; set; }
    }

    public sealed class CopilotInvestmentBreakEven
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>Minutes per active day the whole analysed seat investment needs, at base realisation.</summary>
        [JsonProperty("minutesPerActiveDay")]
        public double? MinutesPerActiveDay { get; set; }

        [JsonProperty("marginOfSafety")]
        public double? MarginOfSafety { get; set; }

        /// <summary>Active days a month one seat needs at the base assumptions.</summary>
        [JsonProperty("activeDaysPerMonthPerSeat")]
        public double? ActiveDaysPerMonthPerSeat { get; set; }

        /// <summary>Realised minutes per working day one seat needs at base realisation.</summary>
        [JsonProperty("minutesPerWeekdayPerSeat")]
        public double? MinutesPerWeekdayPerSeat { get; set; }

        [JsonProperty("holdersAtOrAbove")]
        public int HoldersAtOrAbove { get; set; }

        [JsonProperty("holdersBelow")]
        public int HoldersBelow { get; set; }

        [JsonProperty("holdersNoEvidence")]
        public int HoldersNoEvidence { get; set; }

        [JsonProperty("shareAtOrAbove")]
        public double? ShareAtOrAbove { get; set; }
    }

    public sealed class CopilotInvestmentSensitivity
    {
        [JsonProperty("grid")]
        public List<CopilotInvestmentSensitivityCell> Grid { get; set; } = new List<CopilotInvestmentSensitivityCell>();

        [JsonProperty("roiBaseHourlyMinus20")]
        public double? RoiBaseHourlyMinus20 { get; set; }

        [JsonProperty("roiBaseHourlyPlus20")]
        public double? RoiBaseHourlyPlus20 { get; set; }

        /// <summary>
        /// A stress case: the base return if each seat holder's latest preview balance were one month of extra spend on
        /// top of everything else, valued at the credit price. A floor rather than an estimate - the source period is
        /// unverified, and the balances may overlap consumption the Copilot Studio ledger already bills.
        /// </summary>
        [JsonProperty("roiBaseIfBalancesMonthly")]
        public double? RoiBaseIfBalancesMonthly { get; set; }

        /// <summary>The base return without the seats that are safe to reclaim. Only their seat cost leaves: any Studio use stays billed.</summary>
        [JsonProperty("roiBaseAfterReclaim")]
        public double? RoiBaseAfterReclaim { get; set; }
    }

    /// <summary>
    /// What it would take to break even on the base assumptions, and what the seat levers change. Every figure is
    /// modelled from the same analysed holders as the return, a month at a time.
    /// </summary>
    public sealed class CopilotInvestmentPath
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("monthlyCost")]
        public double? MonthlyCost { get; set; }

        [JsonProperty("monthlyValueBase")]
        public double? MonthlyValueBase { get; set; }

        /// <summary>Monthly cost minus base monthly value: positive is a shortfall, negative a surplus.</summary>
        [JsonProperty("monthlyGap")]
        public double? MonthlyGap { get; set; }

        /// <summary>The modelled value of one active day on the base assumptions.</summary>
        [JsonProperty("valuePerActiveDay")]
        public double? ValuePerActiveDay { get; set; }

        /// <summary>The extra active seat-days a month that would close a shortfall on the base assumptions.</summary>
        [JsonProperty("extraActiveDaysPerMonth")]
        public double? ExtraActiveDaysPerMonth { get; set; }

        /// <summary>The same, spread over the holders who already use Copilot.</summary>
        [JsonProperty("extraDaysPerActiveHolder")]
        public double? ExtraDaysPerActiveHolder { get; set; }

        [JsonProperty("activeHolders")]
        public int ActiveHolders { get; set; }

        [JsonProperty("minutesNeeded")]
        public double? MinutesNeeded { get; set; }

        [JsonProperty("minutesAssumed")]
        public double? MinutesAssumed { get; set; }

        /// <summary>Idle seats that are safe to reclaim, and the seat cost a month they would free.</summary>
        [JsonProperty("reclaimable")]
        public int Reclaimable { get; set; }

        [JsonProperty("reclaimMonthly")]
        public double? ReclaimMonthly { get; set; }

        [JsonProperty("roiAfterReclaim")]
        public double? RoiAfterReclaim { get; set; }

        /// <summary>People without a seat already active on at least the seat-only break-even days a month.</summary>
        [JsonProperty("evidencedWithoutSeat")]
        public int EvidencedWithoutSeat { get; set; }

        /// <summary>How many of them reclaimed seats could cover, and the seats - and cost a month - still to buy.</summary>
        [JsonProperty("reassignable")]
        public int Reassignable { get; set; }

        [JsonProperty("netNewSeats")]
        public int NetNewSeats { get; set; }

        [JsonProperty("netNewMonthly")]
        public double? NetNewMonthly { get; set; }
    }

    /// <summary>Weekly active seat holders over recent complete weeks, from the adoption analysis' audit trend.</summary>
    public sealed class CopilotInvestmentTrajectory
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>Why there is no trend (<see cref="CopilotInvestmentTrajectoryUnavailable"/>); null when there is one.</summary>
        [JsonProperty("unavailableReason")]
        public string UnavailableReason { get; set; }

        /// <summary>The current seat holders the weekly counts are a share of.</summary>
        [JsonProperty("seatHolders")]
        public int SeatHolders { get; set; }

        [JsonProperty("weeks")]
        public List<CopilotInvestmentTrajectoryWeek> Weeks { get; set; } = new List<CopilotInvestmentTrajectoryWeek>();
    }

    public sealed class CopilotInvestmentTrajectoryWeek
    {
        [JsonProperty("weekStart")]
        public DateTime WeekStart { get; set; }

        /// <summary>Null for a week the audit import does not cover - absent, not zero.</summary>
        [JsonProperty("activeHolders")]
        public double? ActiveHolders { get; set; }

        [JsonProperty("share")]
        public double? Share { get; set; }
    }

    public static class CopilotInvestmentTrajectoryUnavailable
    {
        /// <summary>The view is narrowed by a filter, and the trend is tenant-wide.</summary>
        public const string Filtered = "filtered";

        /// <summary>The trend comes from the Copilot audit import, which is not available.</summary>
        public const string NoAudit = "noAudit";

        /// <summary>Fewer than two measured complete weeks.</summary>
        public const string NoData = "noData";
    }

    public sealed class CopilotInvestmentSensitivityCell
    {
        [JsonProperty("minutesScenario")]
        public string MinutesScenario { get; set; }

        [JsonProperty("realisationScenario")]
        public string RealisationScenario { get; set; }

        [JsonProperty("roiMultiple")]
        public double? RoiMultiple { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }
    }

    public sealed class CopilotInvestmentCohort
    {
        [JsonProperty("band")]
        public string Band { get; set; }

        [JsonProperty("holders")]
        public int Holders { get; set; }

        [JsonProperty("medianActiveDays")]
        public double? MedianActiveDays { get; set; }

        [JsonProperty("activeDays")]
        public long ActiveDays { get; set; }

        [JsonProperty("shareOfHolders")]
        public double? ShareOfHolders { get; set; }

        [JsonProperty("shareOfActiveDays")]
        public double? ShareOfActiveDays { get; set; }

        [JsonProperty("cost")]
        public double? Cost { get; set; }

        [JsonProperty("valueBase")]
        public double? ValueBase { get; set; }

        [JsonProperty("roiBase")]
        public double? RoiBase { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("studioCredits")]
        public double StudioCredits { get; set; }
    }

    public sealed class CopilotInvestmentOrganisation
    {
        [JsonProperty("dimension")]
        public string Dimension { get; set; }

        /// <summary>A custom organisation type's own name (tenant data); null for a built-in attribute.</summary>
        [JsonProperty("dimensionName")]
        public string DimensionName { get; set; }

        [JsonProperty("groups")]
        public List<CopilotInvestmentGroupRow> Groups { get; set; } = new List<CopilotInvestmentGroupRow>();

        [JsonProperty("foldedGroups")]
        public int FoldedGroups { get; set; }

        [JsonProperty("minGroupSize")]
        public int MinGroupSize { get; set; }

        /// <summary>True when the dimension names people and the reader lacks See PII.</summary>
        [JsonProperty("hidden")]
        public bool Hidden { get; set; }

        internal object MemberwiseCopy() => MemberwiseClone();
    }

    public sealed class CopilotInvestmentGroupRow
    {
        /// <summary>The group's value as tenant data; null when <see cref="EmptyKey"/> names a product-authored bucket.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary><c>notSet</c> or <c>otherSmallGroups</c>; null when <see cref="Label"/> is tenant data.</summary>
        [JsonProperty("emptyKey")]
        public string EmptyKey { get; set; }

        [JsonProperty("holders")]
        public int Holders { get; set; }

        [JsonProperty("activeHolders")]
        public int ActiveHolders { get; set; }

        [JsonProperty("idleHolders")]
        public int IdleHolders { get; set; }

        [JsonProperty("activeShare")]
        public double? ActiveShare { get; set; }

        [JsonProperty("activeDaysPerHolderMonthly")]
        public double? ActiveDaysPerHolderMonthly { get; set; }

        [JsonProperty("cost")]
        public double? Cost { get; set; }

        [JsonProperty("valueBase")]
        public double? ValueBase { get; set; }

        [JsonProperty("roiBase")]
        public double? RoiBase { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("creditConsumers")]
        public int CreditConsumers { get; set; }

        [JsonProperty("studioCredits")]
        public double StudioCredits { get; set; }

        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }
    }

    public sealed class CopilotInvestmentConcentration
    {
        [JsonProperty("activeDays")]
        public CopilotInvestmentDistribution ActiveDays { get; set; }

        [JsonProperty("credits")]
        public CopilotInvestmentDistribution Credits { get; set; }

        /// <summary>
        /// Credit consumers split at <see cref="QuadrantMedianCredits"/> (the median balance) and at
        /// <see cref="QuadrantDaysPerMonth"/> active days a month. Deliberately different from the stricter
        /// high-spend-thin-evidence flag, and labelled by these thresholds rather than by that flag's name.
        /// </summary>
        [JsonProperty("quadrants")]
        public List<CopilotInvestmentQuadrant> Quadrants { get; set; } = new List<CopilotInvestmentQuadrant>();

        [JsonProperty("quadrantMedianCredits")]
        public double? QuadrantMedianCredits { get; set; }

        [JsonProperty("quadrantDaysPerMonth")]
        public double QuadrantDaysPerMonth { get; set; }
    }

    public sealed class CopilotInvestmentDistribution
    {
        [JsonProperty("population")]
        public int Population { get; set; }

        [JsonProperty("topShare")]
        public double TopShare { get; set; }

        /// <summary>The share of the population <see cref="TopShare"/> is measured over, 0 to 1.</summary>
        [JsonProperty("topFraction")]
        public double TopFraction { get; set; }

        [JsonProperty("gini")]
        public double Gini { get; set; }

        /// <summary>Cumulative share of the total held by the bottom 0%, 10%, ... 100% of the population.</summary>
        [JsonProperty("lorenz")]
        public List<double> Lorenz { get; set; } = new List<double>();
    }

    public sealed class CopilotInvestmentQuadrant
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }
    }

    /// <summary>People without a seat: metered consumption and the evidence they already show.</summary>
    public sealed class CopilotInvestmentMetered
    {
        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        [JsonProperty("activeDays")]
        public long ActiveDays { get; set; }

        [JsonProperty("studioUsers")]
        public int StudioUsers { get; set; }

        [JsonProperty("studioCredits")]
        public double StudioCredits { get; set; }

        [JsonProperty("studioWindowValue")]
        public double? StudioWindowValue { get; set; }

        [JsonProperty("creditUsers")]
        public int CreditUsers { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("creditBalanceValue")]
        public double? CreditBalanceValue { get; set; }

        /// <summary>People whose billed Studio credits per month already cost at least a seat.</summary>
        [JsonProperty("studioAboveSeatPrice")]
        public int? StudioAboveSeatPrice { get; set; }

        /// <summary>People whose latest balance would cost at least a seat IF it were one month of use (unverified).</summary>
        [JsonProperty("balanceAboveSeatPriceIfMonthly")]
        public int? BalanceAboveSeatPriceIfMonthly { get; set; }

        [JsonProperty("evidencedCandidates")]
        public int? EvidencedCandidates { get; set; }

        [JsonProperty("recommendedCandidates")]
        public int RecommendedCandidates { get; set; }
    }

    public sealed class CopilotInvestmentServiceRow
    {
        /// <summary>The source's service id, exactly as returned - never merged with a differently-cased one.</summary>
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("consumers")]
        public int Consumers { get; set; }

        [JsonProperty("quantity")]
        public double Quantity { get; set; }

        [JsonProperty("unknownAmountUsers")]
        public int UnknownAmountUsers { get; set; }

        [JsonProperty("limitExhaustedUsers")]
        public int LimitExhaustedUsers { get; set; }

        [JsonProperty("notEntitledUsers")]
        public int NotEntitledUsers { get; set; }

        [JsonProperty("unevaluatedUsers")]
        public int UnevaluatedUsers { get; set; }

        [JsonProperty("policies")]
        public List<string> Policies { get; set; } = new List<string>();
    }

    public sealed class CopilotInvestmentQuantity
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("value")]
        public double? Value { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }
    }

    public sealed class CopilotInvestmentPolicyRow
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("billingMethods")]
        public string BillingMethods { get; set; }

        [JsonProperty("periodLimit")]
        public CopilotInvestmentQuantity PeriodLimit { get; set; }

        [JsonProperty("perUserLimit")]
        public CopilotInvestmentQuantity PerUserLimit { get; set; }

        [JsonProperty("services")]
        public List<CopilotInvestmentPolicyService> Services { get; set; } = new List<CopilotInvestmentPolicyService>();

        [JsonProperty("assignedGroups")]
        public int? AssignedGroups { get; set; }

        [JsonProperty("modifiedAt")]
        public DateTimeOffset? ModifiedAt { get; set; }
    }

    public sealed class CopilotInvestmentPolicyService
    {
        [JsonProperty("serviceId")]
        public string ServiceId { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }
    }

    public sealed class CopilotInvestmentTenantBalanceRow
    {
        [JsonProperty("billingMethod")]
        public string BillingMethod { get; set; }

        [JsonProperty("purchased")]
        public double? Purchased { get; set; }

        [JsonProperty("consumed")]
        public double? Consumed { get; set; }

        [JsonProperty("remaining")]
        public double? Remaining { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("lastModified")]
        public DateTimeOffset? LastModified { get; set; }
    }

    /// <summary>One quantified recommendation. <see cref="Code"/> is stable; the portal writes the sentence.</summary>
    public sealed class CopilotInvestmentAction
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("monthlyAmount")]
        public double? MonthlyAmount { get; set; }

        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("roiBaseAfter")]
        public double? RoiBaseAfter { get; set; }

        [JsonProperty("metric")]
        public double? Metric { get; set; }

        /// <summary>The person signal the People tab filters to for this action.</summary>
        [JsonProperty("signal")]
        public string Signal { get; set; }

        /// <summary>
        /// The person flag carrying exactly this action's membership, so "See the N people" lists the same N people
        /// the card counts. Null for an action about the tenant rather than people.
        /// </summary>
        [JsonProperty("flag")]
        public string Flag { get; set; }

        /// <summary>What <see cref="MonthlyAmount"/> is (<see cref="CopilotInvestmentActionEffects"/>): amounts of different kinds are never ranked against each other.</summary>
        [JsonProperty("effect")]
        public string Effect { get; set; }

        /// <summary>True when the figure rests on an explicit what-if the source does not confirm.</summary>
        [JsonProperty("assumesMonthlyBalances")]
        public bool AssumesMonthlyBalances { get; set; }
    }

    /// <summary>What an action's monthly amount measures.</summary>
    public static class CopilotInvestmentActionEffects
    {
        /// <summary>Seat cost a month made available: a cash saving only where seats can be reduced.</summary>
        public const string FreesSeatCost = "freesSeatCost";
        /// <summary>Seat cost a month held by seats nobody used, pending a review.</summary>
        public const string IdleSeatCost = "idleSeatCost";
        /// <summary>The modelled value a month still needed for these seats to break even.</summary>
        public const string ValueShortfall = "valueShortfall";
        /// <summary>Seat cost a month that would be added.</summary>
        public const string AddsSeatCost = "addsSeatCost";
        /// <summary>A balance valued as if it were a month's spend - an explicit what-if.</summary>
        public const string BalanceIfMonthly = "balanceIfMonthly";
        /// <summary>Billed Copilot Studio credits a month, valued at the Studio price.</summary>
        public const string MeteredSpend = "meteredSpend";
        public const string None = "none";

        public static readonly string[] All = { FreesSeatCost, IdleSeatCost, ValueShortfall, AddsSeatCost, BalanceIfMonthly, MeteredSpend, None };
    }

    public static class CopilotInvestmentActionCodes
    {
        public const string ReclaimIdle = "reclaimIdle";
        public const string ReviewIdle = "reviewIdle";
        public const string EnableBelowBreakEven = "enableBelowBreakEven";
        public const string EnableLightEvidence = "enableLightEvidence";
        public const string ExpandEvidenced = "expandEvidenced";
        public const string ExpandRecommended = "expandRecommended";
        public const string ReviewHighSpendThinEvidence = "reviewHighSpendThinEvidence";
        public const string RaiseLimitEvidenced = "raiseLimitEvidenced";
        public const string LicenceMeteredHeavy = "licenceMeteredHeavy";
        public const string ImproveCoverage = "improveCoverage";
        public const string SetAssumptions = "setAssumptions";

        public static readonly string[] All =
        {
            ReclaimIdle, ReviewIdle, EnableBelowBreakEven, EnableLightEvidence, ExpandEvidenced, ExpandRecommended,
            ReviewHighSpendThinEvidence, RaiseLimitEvidenced, LicenceMeteredHeavy, ImproveCoverage, SetAssumptions,
        };
    }

    /// <summary>One headline finding for the executive summary, as a code and its figures.</summary>
    public sealed class CopilotInvestmentFinding
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary><c>good</c>, <c>warning</c>, <c>critical</c>, <c>opportunity</c> or <c>neutral</c>.</summary>
        [JsonProperty("tone")]
        public string Tone { get; set; }

        [JsonProperty("values")]
        public Dictionary<string, double?> Values { get; set; } = new Dictionary<string, double?>(StringComparer.Ordinal);
    }

    public static class CopilotInvestmentFindingCodes
    {
        public const string RunRate = "runRate";
        public const string ValueRange = "valueRange";
        public const string BreakEven = "breakEven";
        public const string IdleSeats = "idleSeats";
        public const string EvidenceConcentration = "evidenceConcentration";
        public const string CreditConcentration = "creditConcentration";
        public const string HighSpendThinEvidence = "highSpendThinEvidence";
        public const string ExpandCandidates = "expandCandidates";
        public const string LimitBlocked = "limitBlocked";
        public const string AssumptionsMissing = "assumptionsMissing";
        public const string BalancesUnverified = "balancesUnverified";
        public const string StudioTrend = "studioTrend";
        public const string Trajectory = "trajectory";
        public const string NoMatchedHolders = "noMatchedHolders";
        public const string NoSeats = "noSeats";

        public static readonly string[] All =
        {
            RunRate, ValueRange, BreakEven, IdleSeats, EvidenceConcentration, CreditConcentration, HighSpendThinEvidence,
            ExpandCandidates, LimitBlocked, AssumptionsMissing, BalancesUnverified, StudioTrend, Trajectory, NoMatchedHolders, NoSeats,
        };
    }

    public sealed class CopilotInvestmentTrust
    {
        /// <summary><c>high</c>, <c>medium</c> or <c>low</c>: the lowest of the areas.</summary>
        [JsonProperty("level")]
        public string Level { get; set; } = "high";

        /// <summary>
        /// Confidence per question, because one label cannot answer three: is the evidence of use complete, is the cost
        /// complete, and whose assumptions are these. Preview balances are a fourth area, outside the return.
        /// </summary>
        [JsonProperty("areas")]
        public List<CopilotInvestmentTrustArea> Areas { get; set; } = new List<CopilotInvestmentTrustArea>();

        [JsonProperty("reasons")]
        public List<CopilotInvestmentTrustReason> Reasons { get; set; } = new List<CopilotInvestmentTrustReason>();
    }

    public sealed class CopilotInvestmentTrustArea
    {
        /// <summary>One of <see cref="CopilotInvestmentTrustAreas"/>.</summary>
        [JsonProperty("area")]
        public string Area { get; set; }

        [JsonProperty("level")]
        public string Level { get; set; } = "high";
    }

    public static class CopilotInvestmentTrustAreas
    {
        public const string Evidence = "evidence";
        public const string Cost = "cost";
        public const string Assumptions = "assumptions";
        public const string Balances = "balances";

        public static readonly string[] All = { Evidence, Cost, Assumptions, Balances };
    }

    public sealed class CopilotInvestmentTrustReason
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary><c>low</c>, <c>medium</c> or <c>info</c>: how far this reason lowers confidence.</summary>
        [JsonProperty("impact")]
        public string Impact { get; set; }

        /// <summary>The question it affects (<see cref="CopilotInvestmentTrustAreas"/>).</summary>
        [JsonProperty("area")]
        public string Area { get; set; }

        [JsonProperty("values")]
        public Dictionary<string, double?> Values { get; set; } = new Dictionary<string, double?>(StringComparer.Ordinal);
    }

    public static class CopilotInvestmentTrustCodes
    {
        public const string AdoptionUnavailable = "adoptionUnavailable";
        public const string AuditMissing = "auditMissing";
        public const string UsageReportSignal = "usageReportSignal";
        public const string FiguresIncomplete = "figuresIncomplete";
        public const string HoldersNotAnalysed = "holdersNotAnalysed";
        public const string AssumptionsMissing = "assumptionsMissing";
        public const string AssumptionsUnavailable = "assumptionsUnavailable";
        public const string ScenarioApplied = "scenarioApplied";
        public const string AssumedTimeValue = "assumedTimeValue";
        public const string SeatsCurrentAssignments = "seatsCurrentAssignments";
        public const string BalancesUnverified = "balancesUnverified";
        public const string CreditCoverage = "creditCoverage";
        public const string CreditsStale = "creditsStale";
        public const string CreditsPartial = "creditsPartial";
        public const string CreditsUnresolved = "creditsUnresolved";
        public const string CreditsMissing = "creditsMissing";
        public const string CreditsUnavailable = "creditsUnavailable";
        public const string ShortWindow = "shortWindow";
        public const string StudioImportFailed = "studioImportFailed";
        public const string StudioUnavailable = "studioUnavailable";

        public static readonly string[] All =
        {
            AdoptionUnavailable, AuditMissing, UsageReportSignal, FiguresIncomplete, HoldersNotAnalysed, AssumptionsMissing,
            AssumptionsUnavailable, ScenarioApplied, AssumedTimeValue, SeatsCurrentAssignments, BalancesUnverified, CreditCoverage,
            CreditsStale, CreditsPartial, CreditsUnresolved, CreditsMissing, CreditsUnavailable, ShortWindow, StudioImportFailed,
            StudioUnavailable,
        };
    }

    /// <summary>One person's investment case. Served only to a reader with See PII.</summary>
    public sealed class CopilotInvestmentPersonRow
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("company")]
        public string Company { get; set; }

        [JsonProperty("manager")]
        public string Manager { get; set; }

        [JsonProperty("licensed")]
        public bool Licensed { get; set; }

        [JsonProperty("seatLicences")]
        public string SeatLicences { get; set; }

        [JsonProperty("seatDays")]
        public double? SeatDays { get; set; }

        [JsonProperty("seatBasis")]
        public string SeatBasis { get; set; }

        [JsonProperty("band")]
        public string Band { get; set; }

        [JsonProperty("adoptionScore")]
        public double? AdoptionScore { get; set; }

        [JsonProperty("signalSource")]
        public string SignalSource { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        [JsonProperty("activeDaysPerMonth")]
        public double ActiveDaysPerMonth { get; set; }

        [JsonProperty("habitShare")]
        public double? HabitShare { get; set; }

        [JsonProperty("appsUsed")]
        public int AppsUsed { get; set; }

        [JsonProperty("agentsUsed")]
        public int AgentsUsed { get; set; }

        [JsonProperty("coworkInteractions")]
        public long CoworkInteractions { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        [JsonProperty("lastActivityUtc")]
        public DateTime? LastActivityUtc { get; set; }

        [JsonProperty("tooNewToJudge")]
        public bool TooNewToJudge { get; set; }

        [JsonProperty("reclaimEligibility")]
        public string ReclaimEligibility { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        [JsonProperty("recommendedForLicence")]
        public bool RecommendedForLicence { get; set; }

        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("creditsValue")]
        public double? CreditsValue { get; set; }

        [JsonProperty("creditObservationDate")]
        public DateTime? CreditObservationDate { get; set; }

        [JsonProperty("studioCredits")]
        public double StudioCredits { get; set; }

        [JsonProperty("studioCost")]
        public double? StudioCost { get; set; }

        [JsonProperty("seatCost")]
        public double? SeatCost { get; set; }

        [JsonProperty("cost")]
        public double? Cost { get; set; }

        [JsonProperty("hoursBase")]
        public double? HoursBase { get; set; }

        [JsonProperty("valueConservative")]
        public double? ValueConservative { get; set; }

        [JsonProperty("valueBase")]
        public double? ValueBase { get; set; }

        [JsonProperty("valueUpside")]
        public double? ValueUpside { get; set; }

        [JsonProperty("valueToCostBase")]
        public double? ValueToCostBase { get; set; }

        /// <summary>
        /// Active days a month this person's own modelled cost (seat plus their billed Studio credits) needs to break even
        /// on the base assumptions. Differs from the seat-only figure by exactly their Studio cost.
        /// </summary>
        [JsonProperty("breakEvenDaysPerMonth")]
        public double? BreakEvenDaysPerMonth { get; set; }

        /// <summary>Extra active days a month this seat would need to reach its own base break-even; 0 when it already does.</summary>
        [JsonProperty("breakEvenGapDaysPerMonth")]
        public double? BreakEvenGapDaysPerMonth { get; set; }

        [JsonProperty("signal")]
        public string Signal { get; set; }

        [JsonProperty("flags")]
        public List<string> Flags { get; set; } = new List<string>();

        /// <summary>The directory object behind the person's balance, for the drill-down's own reads. Never serialised.</summary>
        [JsonIgnore]
        public Guid? CreditObjectId { get; set; }
    }

    public static class CopilotInvestmentSignals
    {
        public const string Supported = "supported";
        public const string Plausible = "plausible";
        public const string BelowBreakEven = "belowBreakEven";
        public const string NoEvidence = "noEvidence";
        public const string TooNewToJudge = "tooNewToJudge";
        public const string StrongEvidence = "strongEvidence";
        public const string Developing = "developing";
        public const string LightEvidence = "lightEvidence";
        public const string ExpandCandidate = "expandCandidate";
        public const string Metered = "metered";
        public const string ObservedUnlicensed = "observedUnlicensed";

        /// <summary>A seat holder whose use is known only from Microsoft's usage report, for its own period: not valued.</summary>
        public const string OtherPeriod = "otherPeriod";

        /// <summary>A seat holder the adoption analysis did not score (its per-run cap): not valued.</summary>
        public const string NotAnalysed = "notAnalysed";

        public static readonly string[] All =
        {
            Supported, Plausible, BelowBreakEven, NoEvidence, TooNewToJudge, StrongEvidence, Developing, LightEvidence,
            ExpandCandidate, Metered, ObservedUnlicensed, OtherPeriod, NotAnalysed,
        };
    }

    public static class CopilotInvestmentFlags
    {
        public const string LimitExhausted = "limitExhausted";
        public const string NearLimit = "nearLimit";
        public const string NotEntitled = "notEntitled";
        public const string HighSpendThinEvidence = "highSpendThinEvidence";
        public const string CreditsPartial = "creditsPartial";
        public const string OtherUnit = "otherUnit";
        public const string ReclaimExcluded = "reclaimExcluded";
        public const string AccountDisabled = "accountDisabled";
        public const string ReclaimCandidate = "reclaimCandidate";

        /// <summary>An idle seat the adoption rules do not mark safe to reclaim: someone should look before deciding.</summary>
        public const string IdleReview = "idleReview";

        /// <summary>An active seat whose base-case value is below its own modelled cost.</summary>
        public const string BelowBaseBreakEven = "belowBaseBreakEven";

        /// <summary>No seat, but active on at least the days a month that would pay for one.</summary>
        public const string ExpandEvidenced = "expandEvidenced";

        /// <summary>At or near a credit limit while showing strong, regular use of their own.</summary>
        public const string LimitBlockedEvidenced = "limitBlockedEvidenced";

        /// <summary>No seat, and billed Copilot Studio credits already worth at least a seat each month.</summary>
        public const string MeteredAboveSeat = "meteredAboveSeat";

        public static readonly string[] All =
        {
            LimitExhausted, NearLimit, NotEntitled, HighSpendThinEvidence, CreditsPartial, OtherUnit, ReclaimExcluded,
            AccountDisabled, ReclaimCandidate, IdleReview, BelowBaseBreakEven, ExpandEvidenced, LimitBlockedEvidenced,
            MeteredAboveSeat,
        };
    }

    public sealed class CopilotInvestmentPeoplePage
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("rows")]
        public List<CopilotInvestmentPersonRow> Rows { get; set; } = new List<CopilotInvestmentPersonRow>();

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("valueAvailable")]
        public bool ValueAvailable { get; set; }
    }

    /// <summary>One person's evidence in depth, for the drill-down.</summary>
    public sealed class CopilotInvestmentPersonDetail
    {
        [JsonProperty("person")]
        public CopilotInvestmentPersonRow Person { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("activityAssessed")]
        public bool ActivityAssessed { get; set; }

        [JsonProperty("apps")]
        public List<CopilotInvestmentAppUse> Apps { get; set; } = new List<CopilotInvestmentAppUse>();

        [JsonProperty("weeks")]
        public List<CopilotInvestmentWeek> Weeks { get; set; } = new List<CopilotInvestmentWeek>();

        [JsonProperty("agentTurns")]
        public long AgentTurns { get; set; }

        [JsonProperty("coworkTurns")]
        public long CoworkTurns { get; set; }

        [JsonProperty("totalTurns")]
        public long TotalTurns { get; set; }

        [JsonProperty("activeWeeks")]
        public int ActiveWeeks { get; set; }

        [JsonProperty("promptsAssessed")]
        public bool PromptsAssessed { get; set; }

        [JsonProperty("prompts")]
        public long Prompts { get; set; }

        [JsonProperty("responses")]
        public long Responses { get; set; }

        [JsonProperty("promptApps")]
        public List<CopilotInvestmentAppUse> PromptApps { get; set; } = new List<CopilotInvestmentAppUse>();

        [JsonProperty("promptCategories")]
        public List<CopilotInvestmentCategoryUse> PromptCategories { get; set; } = new List<CopilotInvestmentCategoryUse>();

        [JsonProperty("creditServices")]
        public List<CopilotInvestmentPersonService> CreditServices { get; set; } = new List<CopilotInvestmentPersonService>();

        [JsonProperty("balances")]
        public List<PersonBalancePoint> Balances { get; set; } = new List<PersonBalancePoint>();

        [JsonProperty("studioAgents")]
        public List<CopilotInvestmentStudioAgent> StudioAgents { get; set; } = new List<CopilotInvestmentStudioAgent>();

        [JsonProperty("evidence")]
        public List<string> Evidence { get; set; } = new List<string>();
    }

    public sealed class CopilotInvestmentAppUse
    {
        /// <summary>The source's own host or app-class name, verbatim.</summary>
        [JsonProperty("app")]
        public string App { get; set; }

        [JsonProperty("turns")]
        public long Turns { get; set; }

        [JsonProperty("days")]
        public int Days { get; set; }
    }

    public sealed class CopilotInvestmentWeek
    {
        [JsonProperty("weekStart")]
        public DateTime WeekStart { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        [JsonProperty("turns")]
        public long Turns { get; set; }
    }

    public sealed class CopilotInvestmentCategoryUse
    {
        [JsonProperty("categoryId")]
        public string CategoryId { get; set; }

        [JsonProperty("humanMode")]
        public string HumanMode { get; set; }

        [JsonProperty("prompts")]
        public long Prompts { get; set; }
    }

    public sealed class CopilotInvestmentPersonService
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("policyId")]
        public string PolicyId { get; set; }

        [JsonProperty("consumed")]
        public double? Consumed { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("allowed")]
        public bool? Allowed { get; set; }

        [JsonProperty("decisionReason")]
        public string DecisionReason { get; set; }

        [JsonProperty("perUserLimit")]
        public double? PerUserLimit { get; set; }

        [JsonProperty("perUserLimitKind")]
        public string PerUserLimitKind { get; set; }

        [JsonProperty("remaining")]
        public double? Remaining { get; set; }

        [JsonProperty("lastModified")]
        public DateTimeOffset? LastModified { get; set; }

        [JsonProperty("unevaluatedReason")]
        public string UnevaluatedReason { get; set; }
    }

    public sealed class CopilotInvestmentStudioAgent
    {
        [JsonProperty("agentId")]
        public string AgentId { get; set; }

        [JsonProperty("agentName")]
        public string AgentName { get; set; }

        [JsonProperty("credits")]
        public double Credits { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }
    }

    /// <summary>Quantity kinds as the source types them: <c>limited</c>, <c>unlimited</c> or <c>other</c> (a type this build does not know).</summary>
    public static class CopilotInvestmentQuantityKinds
    {
        public const string Limited = "limited";
        public const string Unlimited = "unlimited";
        public const string Other = "other";

        public static string Of(string odataType)
        {
            if (string.IsNullOrEmpty(odataType)) return null;
            if (string.Equals(odataType, "#microsoft.graph.limitedCostManagementQuantity", StringComparison.Ordinal)) return Limited;
            if (string.Equals(odataType, "#microsoft.graph.unlimitedCostManagementQuantity", StringComparison.Ordinal)) return Unlimited;
            return Other;
        }
    }
}
