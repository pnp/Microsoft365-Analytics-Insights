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

        /// <summary>
        /// A copy without anything that names or isolates a person, for a reader without See PII: no people, no grouping that
        /// names one, and no figure that one to four people are behind, or that a figure shown beside it would give back
        /// (<see cref="CopilotInvestmentWithholding"/>). The cached summary is never edited.
        /// </summary>
        public CopilotInvestmentSummary WithoutIndividualData()
        {
            var copy = (CopilotInvestmentSummary)MemberwiseClone();
            copy.People = new List<CopilotInvestmentPersonRow>();
            copy.IndividualDataHidden = true;
            // The page's own floor, never below the portal's: in practice both are five.
            var minPeople = Math.Max(Organisation?.MinGroupSize ?? 0, SmallPopulations.MinPeopleWithoutSeePii);
            var decisions = CopilotInvestmentWithholding.Apply(this, copy, minPeople);
            // A manager roll-up names a person in every row label, so it is not served without See PII.
            if (Organisation != null && CopilotInvestmentDimensions.NamesPeople(Organisation.Dimension))
            {
                var organisation = (CopilotInvestmentOrganisation)Organisation.MemberwiseCopy();
                organisation.Groups = new List<CopilotInvestmentGroupRow>();
                organisation.Hidden = true;
                copy.Organisation = organisation;
            }
            else if (Organisation != null)
            {
                copy.Organisation = Organisation.WithComplementsWithheld(Math.Max(Organisation.MinGroupSize, SmallPopulations.MinPeopleWithoutSeePii),
                    decisions.StudioPriced, decisions.GroupTotalsWithheld);
            }
            if (Concentration != null)
            {
                copy.Concentration = Concentration.WithoutSmallPopulations(minPeople, decisions.QuadrantTotalWithheld);
                // A concentration finding quotes its curve's top share: it goes with the curve.
                var evidenceGone = Concentration.ActiveDays != null && copy.Concentration.ActiveDays == null;
                var creditsGone = Concentration.Credits != null && copy.Concentration.Credits == null;
                if ((evidenceGone || creditsGone) && copy.Findings != null)
                    copy.Findings = copy.Findings.Where(f => !(evidenceGone && f.Code == CopilotInvestmentFindingCodes.EvidenceConcentration)
                        && !(creditsGone && f.Code == CopilotInvestmentFindingCodes.CreditConcentration)).ToList();
            }
            copy.Services = CopilotInvestmentServiceRow.WithoutSmallPopulations(Services, minPeople);
            return copy;
        }

        /// <summary>
        /// A copy carrying one request's filter echoes and descriptions. Readers who cover the same people share a cached
        /// summary, but what the administrator's filter may show differs between them (people's names are withheld from a
        /// reader without See PII), so the echoes are attached per request and the cached object never holds them.
        /// </summary>
        public CopilotInvestmentSummary WithFilterEchoes(UserFilterEcho userFilter, string userFilterDescription, GlobalFilterEcho globalFilter,
            string globalFilterDescription)
        {
            var copy = (CopilotInvestmentSummary)MemberwiseClone();
            copy.UserFilter = userFilter;
            copy.UserFilterDescription = userFilterDescription;
            copy.GlobalFilter = globalFilter;
            copy.GlobalFilterDescription = globalFilterDescription;
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
        /// Null when no Studio price is set, or when <see cref="StudioCostWithheld"/>. Zero when the ledger could not be read,
        /// which the confidence section says.
        /// </summary>
        [JsonProperty("monthlyStudioCost")]
        public double? MonthlyStudioCost { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="MonthlyStudioCost"/> is null because one to four analysed
        /// holders were billed for Copilot Studio: it would be their own charges. The seats beside it can be worked out from
        /// the holders and the seat price, so the modelled cost, and every figure computed from it (the return, the net, the
        /// break-even minutes, the path's gap), are null too.
        /// </summary>
        [JsonProperty("studioCostWithheld")]
        public bool StudioCostWithheld { get; set; }

        internal CopilotInvestmentSpend Copy() => (CopilotInvestmentSpend)MemberwiseClone();
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

        /// <summary>Billed credits of everyone in view; null when <see cref="CreditsWithheld"/>.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Credits"/> and <see cref="WindowValue"/> are null: one to four
        /// people are behind them, or they less the parts shown beside them would be one to four people's.
        /// </summary>
        [JsonProperty("creditsWithheld")]
        public bool CreditsWithheld { get; set; }

        /// <summary>
        /// The same ledger over the equal-length period before the window. Null unless the ledger reaches back over the
        /// whole of that period, so a comparison is only made like for like, or when <see cref="PriorCreditsWithheld"/>.
        /// </summary>
        [JsonProperty("priorCredits")]
        public double? PriorCredits { get; set; }

        /// <summary>True when, for a reader without See PII, <see cref="PriorCredits"/> is null: one to four people were billed then.</summary>
        [JsonProperty("priorCreditsWithheld")]
        public bool PriorCreditsWithheld { get; set; }

        [JsonProperty("priorUsers")]
        public int? PriorUsers { get; set; }

        /// <summary>Billed credits of the seat holders in view; null when <see cref="HolderCreditsWithheld"/>.</summary>
        [JsonProperty("holderCredits")]
        public double? HolderCredits { get; set; }

        /// <summary>As <see cref="CreditsWithheld"/>, for <see cref="HolderCredits"/> and <see cref="HolderWindowValue"/>.</summary>
        [JsonProperty("holderCreditsWithheld")]
        public bool HolderCreditsWithheld { get; set; }

        [JsonProperty("windowValue")]
        public double? WindowValue { get; set; }

        [JsonProperty("holderWindowValue")]
        public double? HolderWindowValue { get; set; }

        /// <summary>
        /// People with a billed amount other than zero, in the parts the figures add up: analysed seat holders (the cost the
        /// return is measured against), other seat holders, and people without a seat (the metered figures), in that order.
        /// </summary>
        [JsonIgnore]
        internal int[] Contributors { get; set; }

        /// <summary>People billed an amount other than zero in the period before the window: who <see cref="PriorCredits"/> describes.</summary>
        [JsonIgnore]
        internal int PriorContributors { get; set; }

        internal CopilotInvestmentStudioSpend Copy() => (CopilotInvestmentStudioSpend)MemberwiseClone();
    }

    /// <summary>Latest preview balances. A balance in the source's own (unverified) period, never spend.</summary>
    public sealed class CopilotInvestmentCreditBalances
    {
        [JsonProperty("observedUsers")]
        public int ObservedUsers { get; set; }

        [JsonProperty("consumingUsers")]
        public int ConsumingUsers { get; set; }

        /// <summary>Every latest balance in view; null when <see cref="CreditsWithheld"/>.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Credits"/> and <see cref="BalanceValue"/> are null: one to four
        /// people in view have a reading other than zero.
        /// </summary>
        [JsonProperty("creditsWithheld")]
        public bool CreditsWithheld { get; set; }

        /// <summary>The analysed seat holders' balances; null when <see cref="HolderCreditsWithheld"/>.</summary>
        [JsonProperty("holderCredits")]
        public double? HolderCredits { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="HolderCredits"/> is null: one to four people are behind it, or
        /// the total less it would be one to four other people's.
        /// </summary>
        [JsonProperty("holderCreditsWithheld")]
        public bool HolderCreditsWithheld { get; set; }

        /// <summary>Everyone else's balances; null when <see cref="NonHolderCreditsWithheld"/>.</summary>
        [JsonProperty("nonHolderCredits")]
        public double? NonHolderCredits { get; set; }

        /// <summary>As <see cref="HolderCreditsWithheld"/>, for <see cref="NonHolderCredits"/>.</summary>
        [JsonProperty("nonHolderCreditsWithheld")]
        public bool NonHolderCreditsWithheld { get; set; }

        /// <summary>The latest balances valued at the credit price. A balance valuation, not a period cost.</summary>
        [JsonProperty("balanceValue")]
        public double? BalanceValue { get; set; }

        /// <summary>
        /// Balances other than zero, adjustments below zero included, in the parts the figures add up: analysed seat holders,
        /// other seat holders, people without a seat who are rows of the report (the metered figures), and quiet balances that
        /// add nobody to it, in that order.
        /// </summary>
        [JsonIgnore]
        internal int[] Contributors { get; set; }

        internal CopilotInvestmentCreditBalances Copy() => (CopilotInvestmentCreditBalances)MemberwiseClone();

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

        /// <summary>Active days of the people without a seat; null when <see cref="UnlicensedActiveDaysWithheld"/>.</summary>
        [JsonProperty("unlicensedActiveDays")]
        public long? UnlicensedActiveDays { get; set; }

        /// <summary>True when, for a reader without See PII, <see cref="UnlicensedActiveDays"/> is null: one to four people without a seat were active.</summary>
        [JsonProperty("unlicensedActiveDaysWithheld")]
        public bool UnlicensedActiveDaysWithheld { get; set; }

        [JsonProperty("recommendedCandidates")]
        public int RecommendedCandidates { get; set; }

        [JsonProperty("distribution")]
        public List<CopilotInvestmentDistributionBucket> Distribution { get; set; } = new List<CopilotInvestmentDistributionBucket>();

        internal CopilotInvestmentEvidence Copy() => (CopilotInvestmentEvidence)MemberwiseClone();
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

        internal CopilotInvestmentValue Copy() => (CopilotInvestmentValue)MemberwiseClone();
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

        internal CopilotInvestmentScenarioResult Copy() => (CopilotInvestmentScenarioResult)MemberwiseClone();
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

        internal CopilotInvestmentBreakEven Copy() => (CopilotInvestmentBreakEven)MemberwiseClone();
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

        internal CopilotInvestmentSensitivity Copy() => (CopilotInvestmentSensitivity)MemberwiseClone();
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

        internal CopilotInvestmentPath Copy() => (CopilotInvestmentPath)MemberwiseClone();
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

        internal CopilotInvestmentSensitivityCell Copy() => (CopilotInvestmentSensitivityCell)MemberwiseClone();
    }

    public sealed class CopilotInvestmentCohort
    {
        [JsonProperty("band")]
        public string Band { get; set; }

        [JsonProperty("holders")]
        public int Holders { get; set; }

        /// <summary>Null with no holders, or when <see cref="Withheld"/>.</summary>
        [JsonProperty("medianActiveDays")]
        public double? MedianActiveDays { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        [JsonProperty("activeDays")]
        public long? ActiveDays { get; set; }

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

        /// <summary>Null when <see cref="CreditsWithheld"/>.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        /// <summary>Null when <see cref="StudioWithheld"/>.</summary>
        [JsonProperty("studioCredits")]
        public double? StudioCredits { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, the band's activity figures - active days, their median and share, the
        /// modelled value, the cost and the return - are null: one to four holders in it, or one to four of them active, or
        /// the band is withheld with such bands so that the report's totals less the bands shown cannot give them back.
        /// The holders and their share stay: they are counts.
        /// </summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Cost"/> and <see cref="RoiBase"/> are null: the band's
        /// activity figures are withheld, or the cost carries Copilot Studio credits of one to four people.
        /// </summary>
        [JsonProperty("costWithheld")]
        public bool CostWithheld { get; set; }

        /// <summary>As <see cref="Withheld"/>, for <see cref="Credits"/>: the balances of one to four people.</summary>
        [JsonProperty("creditsWithheld")]
        public bool CreditsWithheld { get; set; }

        /// <summary>As <see cref="Withheld"/>, for <see cref="StudioCredits"/>: the Copilot Studio credits of one to four people.</summary>
        [JsonProperty("studioWithheld")]
        public bool StudioWithheld { get; set; }

        /// <summary>
        /// The people behind the band's figures: holders, active holders, holders with a balance other than zero and holders
        /// with Copilot Studio credits other than zero, in that order.
        /// </summary>
        [JsonIgnore]
        internal int[] Populations { get; set; }

        /// <summary>
        /// Copies of the bands for a reader without See PII. Every analysed holder is in exactly one band, and the bands add
        /// up to figures the reader is given (the active days and value of the evidence and the return, the analysed holders'
        /// balances and Studio credits, the modelled cost), so each kind of figure is withheld in a band with one to
        /// <paramref name="minPeople"/> - 1 people behind it, and in enough other bands that the withheld ones together hold
        /// nobody or at least that many (<see cref="SmallPopulations.WithholdComplements"/>). When the total the bands' balances
        /// or Studio credits add up to is itself withheld, one band's are withheld too, so that adding the bands up does not
        /// give it back (<see cref="SmallPopulations.WithholdOneOf"/>). Bands are copied, never edited.
        /// </summary>
        /// <param name="studioPriced">Whether the cost carries the holders' Copilot Studio credits: a Studio price is set.</param>
        internal static List<CopilotInvestmentCohort> WithoutSmallPopulations(List<CopilotInvestmentCohort> cohorts, int minPeople, bool studioPriced,
            bool creditsTotalWithheld, bool studioTotalWithheld)
        {
            if (cohorts == null) return null;
            int Size(CopilotInvestmentCohort c, int kind) => c.Populations != null && kind < c.Populations.Length ? c.Populations[kind] : 0;

            var evidence = cohorts.Select(c => SmallPopulations.IsSmall(Size(c, 0), minPeople) || SmallPopulations.IsSmall(Size(c, 1), minPeople)).ToArray();
            SmallPopulations.WithholdComplements(cohorts.Select(c => new[] { Size(c, 0), Size(c, 1) }).ToList(), evidence, minPeople);

            bool[] OneKind(int kind, bool totalWithheld)
            {
                var people = cohorts.Select(c => Size(c, kind)).ToList();
                var withheld = people.Select(p => SmallPopulations.IsSmall(p, minPeople)).ToArray();
                SmallPopulations.WithholdComplements(people.Select(p => new[] { p }).ToList(), withheld, minPeople);
                if (totalWithheld) SmallPopulations.WithholdOneOf(people, withheld);
                return withheld;
            }
            var credits = OneKind(2, creditsTotalWithheld);
            var studio = OneKind(3, studioTotalWithheld);

            return cohorts.Select((c, i) =>
            {
                var cost = evidence[i] || (studioPriced && studio[i]);
                if (!evidence[i] && !cost && !credits[i] && !studio[i]) return c;
                var copy = (CopilotInvestmentCohort)c.MemberwiseClone();
                if (evidence[i])
                {
                    copy.Withheld = true;
                    copy.ActiveDays = null;
                    copy.MedianActiveDays = null;
                    copy.ShareOfActiveDays = null;
                    copy.ValueBase = null;
                }
                if (cost)
                {
                    copy.CostWithheld = true;
                    copy.Cost = null;
                    copy.RoiBase = null;
                }
                if (credits[i])
                {
                    copy.CreditsWithheld = true;
                    copy.Credits = null;
                }
                if (studio[i])
                {
                    copy.StudioWithheld = true;
                    copy.StudioCredits = null;
                }
                return copy;
            }).ToList();
        }
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

        /// <summary>
        /// A copy for a reader without See PII in which no withheld group can be worked out from the others. Every person is
        /// in exactly one row and the rows add up to the report's totals, so with one group withheld, a total less the groups
        /// shown would be that group's figures, under its own name. Further groups are withheld, the fewest people of the
        /// population that falls short first, until the withheld groups together hold no one or at least
        /// <paramref name="minPeople"/> of each population behind the figures (<see cref="SmallPopulations.WithholdComplements"/>).
        /// A withheld group keeps its name, which on its own says nothing about anyone's figures. Rows are copied, never
        /// edited: the cached organisation is the one a reader with See PII is given.
        /// </summary>
        /// <param name="studioPriced">
        /// Whether a group's cost carries its analysed holders' Copilot Studio credits (a Studio price is set). The cost less
        /// the seats, which the holders and the seat price give, is then those holders' Studio charges, and the group's Studio
        /// credits less them everyone else's: a group with one to <paramref name="minPeople"/> - 1 people in either part is
        /// withheld too.
        /// </param>
        /// <param name="totalsWithheld">
        /// Populations (indexes into <see cref="CopilotInvestmentGroupRow.Populations"/>) whose total the groups add up to and
        /// the reader is not given: one more group with some of them is withheld, so the groups do not add up to it
        /// (<see cref="SmallPopulations.WithholdOneOf"/>).
        /// </param>
        internal CopilotInvestmentOrganisation WithComplementsWithheld(int minPeople, bool studioPriced = false, params int[] totalsWithheld)
        {
            // Without a Studio price, the cost is seats only: the split of the Studio credits is behind no figure.
            var kinds = studioPriced ? CopilotInvestmentGroupRow.PopulationCount : CopilotInvestmentGroupRow.HolderStudioPopulation;
            int Size(CopilotInvestmentGroupRow g, int kind) => g.Populations != null && kind < g.Populations.Length ? g.Populations[kind] : 0;
            var populations = Groups.Select(g => Enumerable.Range(0, kinds).Select(k => Size(g, k)).ToArray()).ToList();
            var withheld = Groups.Select(g => g.Withheld || (studioPriced
                && (SmallPopulations.IsSmall(Size(g, CopilotInvestmentGroupRow.HolderStudioPopulation), minPeople)
                    || SmallPopulations.IsSmall(Size(g, CopilotInvestmentGroupRow.OtherStudioPopulation), minPeople)))).ToArray();
            SmallPopulations.WithholdComplements(populations, withheld, minPeople);
            foreach (var kind in totalsWithheld ?? new int[0])
            {
                SmallPopulations.WithholdOneOf(Groups.Select(g => Size(g, kind)).ToList(), withheld);
                SmallPopulations.WithholdComplements(populations, withheld, minPeople);
            }
            if (Groups.Select((g, i) => withheld[i] && !g.Withheld).All(added => !added)) return this;
            var copy = (CopilotInvestmentOrganisation)MemberwiseClone();
            copy.Groups = Groups.Select((g, i) => withheld[i] && !g.Withheld
                ? new CopilotInvestmentGroupRow { Label = g.Label, EmptyKey = g.EmptyKey, Withheld = true, Populations = g.Populations }
                : g).ToList();
            return copy;
        }
    }

    public sealed class CopilotInvestmentGroupRow
    {
        /// <summary>The group's value as tenant data; null when <see cref="EmptyKey"/> names a product-authored bucket.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary><c>notSet</c> or <c>otherSmallGroups</c>; null when <see cref="Label"/> is tenant data.</summary>
        [JsonProperty("emptyKey")]
        public string EmptyKey { get; set; }

        /// <summary>
        /// True when the group or any non-empty contributor population behind its figures is smaller than
        /// <see cref="CopilotInvestmentOrganisation.MinGroupSize"/>; no figures are returned for that row.
        /// </summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }

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

        /// <summary>
        /// The people behind the row's figures, withheld or not: holders, active holders, people with a credit amount, people
        /// with a Studio amount, active people without a seat, analysed holders with a Studio amount and everyone else with a
        /// Studio amount, in that order (<see cref="PopulationCount"/>). What the copy for a reader without See PII needs to
        /// withhold enough groups together (<see cref="CopilotInvestmentOrganisation.WithComplementsWithheld"/>).
        /// </summary>
        [JsonIgnore]
        internal int[] Populations { get; set; }

        /// <summary>Indexes into <see cref="Populations"/>: the people with a balance, and with Copilot Studio credits.</summary>
        internal const int CreditPopulation = 2;
        internal const int StudioPopulation = 3;

        /// <summary>The Studio people split by whether the group's cost carries their credits: analysed holders, and everyone else.</summary>
        internal const int HolderStudioPopulation = 5;
        internal const int OtherStudioPopulation = 6;
        internal const int PopulationCount = 7;
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

        /// <summary>
        /// A copy for a reader without See PII. A curve whose shares single out one to <paramref name="minPeople"/> - 1 people
        /// (<see cref="CopilotInvestmentDistribution.SafeWithoutSeePii"/>) is left out, which the page reads as too few people
        /// to measure: at ten consumers each decile is one person's balance. A quadrant of that few people keeps its count but
        /// not its credits, and the quadrants split the same people, so enough others are withheld with it that no difference
        /// gives them back (<see cref="SmallPopulations.WithholdComplements"/>); the median they are split at is one person's
        /// reading, so it goes when fewer people than that are split at all. When the balances the quadrants add up to (the
        /// analysed holders' and those of people without a seat) may not be worked out, one more quadrant is withheld, so that
        /// adding them up does not give them back (<see cref="SmallPopulations.WithholdOneOf"/>). Never edits this object.
        /// </summary>
        internal CopilotInvestmentConcentration WithoutSmallPopulations(int minPeople, bool quadrantTotalWithheld = false)
        {
            var copy = (CopilotInvestmentConcentration)MemberwiseClone();
            if (ActiveDays != null && !ActiveDays.SafeWithoutSeePii) copy.ActiveDays = null;
            if (Credits != null && !Credits.SafeWithoutSeePii) copy.Credits = null;
            var withheld = Quadrants.Select(q => SmallPopulations.IsSmall(q.People, minPeople)).ToArray();
            SmallPopulations.WithholdComplements(Quadrants.Select(q => new[] { q.People }).ToList(), withheld, minPeople);
            if (quadrantTotalWithheld) SmallPopulations.WithholdOneOf(Quadrants.Select(q => q.People).ToList(), withheld);
            copy.Quadrants = Quadrants.Select((q, i) => withheld[i] ? new CopilotInvestmentQuadrant { Key = q.Key, People = q.People, Withheld = true } : q).ToList();
            if (SmallPopulations.IsSmall(Quadrants.Sum(q => q.People), minPeople)) copy.QuadrantMedianCredits = null;
            return copy;
        }
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

        /// <summary>
        /// False when a share in it, or the difference of two, would be the sum of one to four people's values
        /// (<see cref="CopilotInvestmentStatistics.EveryCutHoldsEnough"/>): a reader without See PII is then not given it.
        /// </summary>
        [JsonIgnore]
        internal bool SafeWithoutSeePii { get; set; }
    }

    public sealed class CopilotInvestmentQuadrant
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("people")]
        public int People { get; set; }

        /// <summary>The quadrant's credits; 0 when <see cref="Withheld"/>, which the page and the workbook never show as a zero.</summary>
        [JsonProperty("credits")]
        public double Credits { get; set; }

        /// <summary>True when, for a reader without See PII, the credits could single out too few people and are not given.</summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }
    }

    /// <summary>People without a seat: metered consumption and the evidence they already show.</summary>
    public sealed class CopilotInvestmentMetered
    {
        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        /// <summary>Null when <see cref="ActiveDaysWithheld"/>.</summary>
        [JsonProperty("activeDays")]
        public long? ActiveDays { get; set; }

        /// <summary>True when, for a reader without See PII, <see cref="ActiveDays"/> is null: one to four of these people were active.</summary>
        [JsonProperty("activeDaysWithheld")]
        public bool ActiveDaysWithheld { get; set; }

        [JsonProperty("studioUsers")]
        public int StudioUsers { get; set; }

        /// <summary>Null when <see cref="StudioWithheld"/>.</summary>
        [JsonProperty("studioCredits")]
        public double? StudioCredits { get; set; }

        [JsonProperty("studioWindowValue")]
        public double? StudioWindowValue { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="StudioCredits"/> and <see cref="StudioWindowValue"/> are null:
        /// one to four people are behind them, or the Studio total less them would be one to four seat holders' charges.
        /// </summary>
        [JsonProperty("studioWithheld")]
        public bool StudioWithheld { get; set; }

        [JsonProperty("creditUsers")]
        public int CreditUsers { get; set; }

        /// <summary>Null when <see cref="CreditsWithheld"/>.</summary>
        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("creditBalanceValue")]
        public double? CreditBalanceValue { get; set; }

        /// <summary>As <see cref="StudioWithheld"/>, for <see cref="Credits"/> and <see cref="CreditBalanceValue"/>.</summary>
        [JsonProperty("creditsWithheld")]
        public bool CreditsWithheld { get; set; }

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

        internal CopilotInvestmentMetered Copy() => (CopilotInvestmentMetered)MemberwiseClone();
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

        /// <summary>The category's consumption; 0 when <see cref="Withheld"/>, which the page and the workbook never show as a zero.</summary>
        [JsonProperty("quantity")]
        public double Quantity { get; set; }

        /// <summary>True when, for a reader without See PII, <see cref="Quantity"/> could single out too few people and is not given.</summary>
        [JsonProperty("withheld")]
        public bool Withheld { get; set; }

        /// <summary>Accounts with an amount other than zero in the category: who <see cref="Quantity"/> describes.</summary>
        [JsonIgnore]
        internal int Contributors { get; set; }

        /// <summary>
        /// Copies of the categories for a reader without See PII. A category's quantity is withheld when one to
        /// <paramref name="minPeople"/> - 1 accounts are behind it - the same people's balance as a withheld total, often. The
        /// categories of one unit add up to that unit's total and one person can consume several, so the smallest category
        /// of at least that many accounts is withheld with them (<see cref="SmallPopulations.WithholdOverlappingComplement"/>).
        /// The counts stay. Rows are copied, never edited.
        /// </summary>
        internal static List<CopilotInvestmentServiceRow> WithoutSmallPopulations(List<CopilotInvestmentServiceRow> rows, int minPeople)
        {
            if (rows == null) return null;
            var withheld = rows.Select(r => SmallPopulations.IsSmall(r.Contributors, minPeople)).ToArray();
            foreach (var unit in rows.Select((r, i) => i).GroupBy(i => rows[i].Unit ?? string.Empty, StringComparer.Ordinal))
            {
                var indexes = unit.ToList();
                var inUnit = indexes.Select(i => withheld[i]).ToArray();
                SmallPopulations.WithholdOverlappingComplement(indexes.Select(i => rows[i].Contributors).ToList(), inUnit, minPeople);
                for (var k = 0; k < indexes.Count; k++) withheld[indexes[k]] = inUnit[k];
            }
            return rows.Select((r, i) =>
            {
                if (!withheld[i]) return r;
                var copy = (CopilotInvestmentServiceRow)r.MemberwiseClone();
                copy.Quantity = 0;
                copy.Withheld = true;
                return copy;
            }).ToList();
        }

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

        /// <summary>
        /// True when, for a reader without See PII, <see cref="MonthlyAmount"/> is null: it would be Copilot Studio charges
        /// that the report withholds (one to four people's, or a part of a withheld modelled cost).
        /// </summary>
        [JsonProperty("monthlyAmountWithheld")]
        public bool MonthlyAmountWithheld { get; set; }

        internal CopilotInvestmentAction Copy() => (CopilotInvestmentAction)MemberwiseClone();
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

        /// <summary>A copy with these values null and, when given, another tone. The cached finding and its values are never edited.</summary>
        internal CopilotInvestmentFinding Without(string tone, params string[] values)
        {
            var copy = (CopilotInvestmentFinding)MemberwiseClone();
            copy.Values = new Dictionary<string, double?>(Values ?? new Dictionary<string, double?>(), StringComparer.Ordinal);
            foreach (var value in values)
                if (copy.Values.ContainsKey(value)) copy.Values[value] = null;
            if (tone != null) copy.Tone = tone;
            return copy;
        }
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

        [JsonProperty("studioEnvironments")]
        public List<CopilotInvestmentStudioEnvironment> StudioEnvironments { get; set; } = new List<CopilotInvestmentStudioEnvironment>();

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

    /// <summary>A person's billed Copilot Studio credits in one Power Platform environment: the finest grain the per-user ledger has.</summary>
    public sealed class CopilotInvestmentStudioEnvironment
    {
        [JsonProperty("environmentId")]
        public string EnvironmentId { get; set; }

        /// <summary>Null when the environment lookup was unavailable at import.</summary>
        [JsonProperty("environmentName")]
        public string EnvironmentName { get; set; }

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
