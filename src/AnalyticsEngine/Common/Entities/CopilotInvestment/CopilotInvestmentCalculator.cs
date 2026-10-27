using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// Turns observed evidence, observed spend and explicit assumptions into the investment report.
    /// </summary>
    /// <remarks>
    /// <para><b>Matched populations.</b> The return compares like with like: the value modelled for the seat
    /// holders the adoption analysis scored, against those same holders' seats (and their own billed Copilot
    /// Studio credits when a Studio price is set). People without a seat are a separate metered lens.</para>
    /// <para><b>Active days, not prompts.</b> Modelled time is counted per day a person used Copilot, so sending
    /// more prompts on the same day earns nothing extra.</para>
    /// <para><b>Balances are not spend.</b> Preview credit balances are reported, valued and stress-tested as an
    /// explicit what-if, never added into the cost the return is measured against.</para>
    /// </remarks>
    public static partial class CopilotInvestmentCalculator
    {
        /// <summary>Days in an average Gregorian month.</summary>
        public const double AverageMonthDays = 365.2425 / 12;

        /// <summary>Active days a month treated as a strong, habitual pattern (roughly twice a week).</summary>
        public const double StrongEvidenceDaysPerMonth = 8;

        /// <summary>Active days a month below which a pattern is thin (less than every other week).</summary>
        public const double ThinEvidenceDaysPerMonth = 2;

        /// <summary>The fewest credit consumers before spend-concentration flags are raised at all.</summary>
        public const int MinConsumersForSpendFlags = 5;

        public static CopilotInvestmentSummary Calculate(CopilotInvestmentInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var context = new Context(input);
            var summary = new CopilotInvestmentSummary
            {
                GeneratedUtc = input.GeneratedUtc,
                FromUtc = input.FromUtc,
                ToUtc = input.ToUtc,
                WindowDays = input.WindowDays,
                Weekdays = Math.Round(context.Weekdays, 2),
                MonthsInWindow = Math.Round(context.Months, 4),
                Filtered = input.Filtered,
            };

            context.ComputePeople();
            summary.Assumptions = AssumptionsView(input, context);
            summary.Sources = Sources(input, context);
            summary.Investment = Investment(context);
            summary.Evidence = Evidence(input, context);
            summary.Value = Value(context);
            summary.BreakEven = BreakEven(context);
            summary.Sensitivity = Sensitivity(context);
            summary.Cohorts = Cohorts(context);
            summary.Organisation = Organisation(input, context);
            summary.Concentration = Concentration(context);
            summary.Metered = Metered(input, context);
            summary.Services = Services(input, context);
            summary.Policies = Policies(input);
            summary.TenantBalances = TenantBalances(input);
            summary.Actions = Actions(input, context, summary);
            summary.Findings = Findings(input, context, summary);
            summary.Trust = Trust(input, context, summary);
            summary.People = context.Rows;
            return summary;
        }

        /// <summary>The per-request working set: effective prices, the window and each person's computed row.</summary>
        internal sealed class Context
        {
            public Context(CopilotInvestmentInput input)
            {
                Input = input;
                Months = Math.Max(1, input.WindowDays) / AverageMonthDays;
                Weekdays = Math.Max(1, input.WindowDays) * (input.WorkingDaysPerWeek <= 0 ? 5 : input.WorkingDaysPerWeek) / 7d;
                WeekdaysPerMonth = AverageMonthDays * (input.WorkingDaysPerWeek <= 0 ? 5 : input.WorkingDaysPerWeek) / 7d;
                var a = input.Assumptions;
                var money = a?.Currency != null;
                Currency = a?.Currency;
                SeatPrice = money ? a.SeatPricePerMonth : null;
                CreditPrice = money ? a.CreditPrice : null;
                StudioPrice = money ? a.StudioCreditPrice : null;
                Hourly = money ? a.HourlyCost : null;
                Minutes = a?.MinutesPerActiveDay != null && a.MinutesPerActiveDay.IsComplete ? a.MinutesPerActiveDay : null;
                Realisation = a?.Realisation != null && a.Realisation.IsComplete ? a.Realisation : null;
                ValueReady = SeatPrice.HasValue && Hourly.HasValue && Minutes != null && Realisation != null;
                if (ValueReady && Minutes.Base.Value > 0 && Realisation.Base.Value > 0)
                    BreakEvenDaysPerMonth = SeatPrice.Value / (Minutes.Base.Value / 60 * Realisation.Base.Value * Hourly.Value);
            }

            public CopilotInvestmentInput Input { get; }
            public double Months { get; }
            public double Weekdays { get; }
            public double WeekdaysPerMonth { get; }
            public string Currency { get; }
            public double? SeatPrice { get; }
            public double? CreditPrice { get; }
            public double? StudioPrice { get; }
            public double? Hourly { get; }
            public CopilotInvestmentRange Minutes { get; }
            public CopilotInvestmentRange Realisation { get; }
            public bool ValueReady { get; }
            public double? BreakEvenDaysPerMonth { get; }

            public List<CopilotInvestmentPerson> Holders { get; private set; } = new List<CopilotInvestmentPerson>();
            public List<CopilotInvestmentPerson> NonHolders { get; private set; } = new List<CopilotInvestmentPerson>();
            public List<CopilotInvestmentPersonRow> Rows { get; } = new List<CopilotInvestmentPersonRow>();
            public Dictionary<int, CopilotInvestmentPersonRow> RowsById { get; } = new Dictionary<int, CopilotInvestmentPersonRow>();
            public double? HighSpendThreshold { get; private set; }

            public double Value(CopilotInvestmentPerson person, string minutesScenario, string realisationScenario)
            {
                if (!ValueReady || !person.Licensed) return 0;
                return person.ActiveDays * Minutes.Value(minutesScenario).Value / 60 * Realisation.Value(realisationScenario).Value * Hourly.Value;
            }

            public double SeatDays(CopilotInvestmentPerson person) =>
                person.Licensed ? Math.Min(person.SeatHeldDays ?? Input.WindowDays, Input.WindowDays) : 0;

            public double? SeatCost(CopilotInvestmentPerson person) =>
                person.Licensed && SeatPrice.HasValue ? SeatPrice.Value * SeatDays(person) / AverageMonthDays : (double?)null;

            public double? StudioCost(CopilotInvestmentPerson person) =>
                StudioPrice.HasValue ? (person.Studio?.Credits ?? 0) * StudioPrice.Value : (double?)null;

            /// <summary>A seat holder's own modelled cost: their seat, and their billed Studio credits when priced.</summary>
            public double? Cost(CopilotInvestmentPerson person)
            {
                var seat = SeatCost(person);
                if (!seat.HasValue) return null;
                return seat.Value + (StudioCost(person) ?? 0);
            }

            public double ActiveDaysPerMonth(CopilotInvestmentPerson person) => person.ActiveDays / Months;

            public bool StrongEvidence(CopilotInvestmentPerson person) =>
                person.Licensed
                    ? BandAtLeast(person.Band, AdoptionBand.Established) || ActiveDaysPerMonth(person) >= StrongEvidenceDaysPerMonth
                    : ActiveDaysPerMonth(person) >= StrongEvidenceDaysPerMonth;

            public bool ThinEvidence(CopilotInvestmentPerson person) =>
                person.Licensed
                    ? !BandAtLeast(person.Band, AdoptionBand.Developing) || ActiveDaysPerMonth(person) < ThinEvidenceDaysPerMonth
                    : ActiveDaysPerMonth(person) < ThinEvidenceDaysPerMonth;

            public void ComputePeople()
            {
                var people = Input.People ?? new List<CopilotInvestmentPerson>();
                Holders = people.Where(p => p.Licensed).ToList();
                NonHolders = people.Where(p => !p.Licensed).ToList();

                var consumers = people.Where(p => (p.Credit?.Credits ?? 0) > 0).Select(p => p.Credit.Credits.Value).ToList();
                if (consumers.Count >= MinConsumersForSpendFlags) HighSpendThreshold = CopilotInvestmentStatistics.Percentile(consumers, 0.9);

                foreach (var person in people)
                {
                    var row = Row(person);
                    Rows.Add(row);
                    RowsById[person.UserId] = row;
                }
            }

            private CopilotInvestmentPersonRow Row(CopilotInvestmentPerson person)
            {
                var credit = person.Credit;
                var row = new CopilotInvestmentPersonRow
                {
                    UserId = person.UserId,
                    UserPrincipalName = person.UserPrincipalName,
                    Department = person.Department,
                    JobTitle = person.JobTitle,
                    Country = person.Country,
                    Company = person.Company,
                    Manager = person.Manager,
                    Licensed = person.Licensed,
                    SeatLicences = person.SeatLicences,
                    SeatDays = person.Licensed ? Math.Round(SeatDays(person), 2) : (double?)null,
                    SeatBasis = person.Licensed ? (person.SeatHeldDays.HasValue ? SeatBases.LicenceHistory : SeatBases.CurrentAssignments) : null,
                    Band = person.Band,
                    AdoptionScore = person.AdoptionScore,
                    SignalSource = person.SignalSource,
                    ActiveDays = person.ActiveDays,
                    ActiveDaysPerMonth = Math.Round(ActiveDaysPerMonth(person), 2),
                    HabitShare = Weekdays > 0 ? Math.Round(Math.Min(1, person.ActiveDays / Weekdays), 4) : (double?)null,
                    AppsUsed = person.AppsUsed,
                    AgentsUsed = person.AgentsUsed,
                    CoworkInteractions = person.CoworkInteractions,
                    Interactions = person.Interactions,
                    LastActivityUtc = person.LastActivityUtc,
                    TooNewToJudge = person.TooNewToJudge,
                    ReclaimEligibility = person.ReclaimEligibility,
                    AccountEnabled = person.AccountEnabled,
                    RecommendedForLicence = person.Recommended,
                    Credits = credit?.Credits,
                    CreditsValue = credit?.Credits.HasValue == true && CreditPrice.HasValue ? Money(credit.Credits.Value * CreditPrice.Value) : (double?)null,
                    CreditObservationDate = credit?.ObservationDate,
                    CreditObjectId = credit?.ObjectId,
                    StudioCredits = Math.Round(person.Studio?.Credits ?? 0, 6),
                    StudioCost = Money(StudioCost(person)),
                    SeatCost = Money(SeatCost(person)),
                    Cost = Money(person.Licensed ? Cost(person) : null),
                };

                if (ValueReady && person.Licensed)
                {
                    row.HoursBase = Math.Round(person.ActiveDays * Minutes.Base.Value / 60 * Realisation.Base.Value, 2);
                    row.ValueConservative = Money(Value(person, CopilotInvestmentScenarios.Conservative, CopilotInvestmentScenarios.Conservative));
                    row.ValueBase = Money(Value(person, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base));
                    row.ValueUpside = Money(Value(person, CopilotInvestmentScenarios.Upside, CopilotInvestmentScenarios.Upside));
                    var cost = Cost(person);
                    row.ValueToCostBase = cost > 0 ? Math.Round(Value(person, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base) / cost.Value, 4) : (double?)null;
                    if (BreakEvenDaysPerMonth.HasValue)
                        row.BreakEvenGapDaysPerMonth = Math.Round(Math.Max(0, BreakEvenDaysPerMonth.Value - ActiveDaysPerMonth(person)), 2);
                }

                row.Signal = SignalFor(person);
                row.Flags = FlagsFor(person);
                return row;
            }

            private string SignalFor(CopilotInvestmentPerson person)
            {
                if (person.Licensed)
                {
                    if (person.ActiveDays <= 0)
                        return person.TooNewToJudge ? CopilotInvestmentSignals.TooNewToJudge : CopilotInvestmentSignals.NoEvidence;
                    if (ValueReady)
                    {
                        var cost = Cost(person) ?? 0;
                        if (Value(person, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base) >= cost) return CopilotInvestmentSignals.Supported;
                        if (Value(person, CopilotInvestmentScenarios.Upside, CopilotInvestmentScenarios.Upside) >= cost) return CopilotInvestmentSignals.Plausible;
                        return person.TooNewToJudge ? CopilotInvestmentSignals.TooNewToJudge : CopilotInvestmentSignals.BelowBreakEven;
                    }
                    if (BandAtLeast(person.Band, AdoptionBand.Established)) return CopilotInvestmentSignals.StrongEvidence;
                    if (BandAtLeast(person.Band, AdoptionBand.Developing)) return CopilotInvestmentSignals.Developing;
                    return CopilotInvestmentSignals.LightEvidence;
                }

                if (person.ActiveDays > 0 && BreakEvenDaysPerMonth.HasValue && ActiveDaysPerMonth(person) >= BreakEvenDaysPerMonth.Value)
                    return CopilotInvestmentSignals.ExpandCandidate;
                if (person.Recommended) return CopilotInvestmentSignals.ExpandCandidate;
                if ((person.Studio?.Credits ?? 0) > 0 || (person.Credit?.Credits ?? 0) > 0) return CopilotInvestmentSignals.Metered;
                return CopilotInvestmentSignals.ObservedUnlicensed;
            }

            private List<string> FlagsFor(CopilotInvestmentPerson person)
            {
                var flags = new List<string>();
                var credit = person.Credit;
                if (credit != null)
                {
                    if (credit.LimitExhaustedRows > 0) flags.Add(CopilotInvestmentFlags.LimitExhausted);
                    else if (credit.NearLimitRows > 0) flags.Add(CopilotInvestmentFlags.NearLimit);
                    if (credit.NotEntitledRows > 0) flags.Add(CopilotInvestmentFlags.NotEntitled);
                    if (credit.Partial) flags.Add(CopilotInvestmentFlags.CreditsPartial);
                    if (credit.OtherUnitRows > 0) flags.Add(CopilotInvestmentFlags.OtherUnit);
                    if (HighSpendThreshold.HasValue && (credit.Credits ?? 0) > 0 && credit.Credits.Value >= HighSpendThreshold.Value && ThinEvidence(person))
                        flags.Add(CopilotInvestmentFlags.HighSpendThinEvidence);
                }
                if (person.Licensed && person.ReclaimEligibility == CopilotAdoptionScoring.ReclaimEligibilityTiers.Excluded)
                    flags.Add(CopilotInvestmentFlags.ReclaimExcluded);
                if (person.AccountEnabled == false) flags.Add(CopilotInvestmentFlags.AccountDisabled);
                if (IsReclaimCandidate(person)) flags.Add(CopilotInvestmentFlags.ReclaimCandidate);
                return flags;
            }

            public bool IsReclaimCandidate(CopilotInvestmentPerson person) =>
                person.Licensed && person.ActiveDays <= 0 && !person.TooNewToJudge
                && (person.ReclaimEligibility == CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain
                    || person.ReclaimEligibility == CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable);
        }

        public static class SeatBases
        {
            public const string LicenceHistory = "licenceHistory";
            public const string CurrentAssignments = "currentAssignments";
            public const string Mixed = "mixed";
        }

        internal static bool BandAtLeast(string band, AdoptionBand minimum) =>
            Enum.TryParse(band, out AdoptionBand parsed) && parsed >= minimum;

        internal static double? Money(double? value) => value.HasValue ? Math.Round(value.Value, 2) : (double?)null;

        internal static double? Ratio(double numerator, double? denominator) =>
            denominator.HasValue && denominator.Value > 0 ? Math.Round(numerator / denominator.Value, 4) : (double?)null;

        private static CopilotInvestmentAssumptionsView AssumptionsView(CopilotInvestmentInput input, Context context)
        {
            var view = new CopilotInvestmentAssumptionsView
            {
                Version = input.AssumptionsVersion,
                UpdatedUtc = input.AssumptionsUpdatedUtc,
                StorageDurable = input.AssumptionsDurable,
                Provenance = new Dictionary<string, string>(input.Provenance ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                ScenarioApplied = (input.Provenance ?? new Dictionary<string, string>()).Values.Any(v => v == CopilotInvestmentProvenance.Scenario),
            };
            if (input.Assumptions == null)
            {
                view.Status = input.AssumptionsStatusOverride ?? CopilotInvestmentAssumptionStatus.Unavailable;
                view.MissingForValue = CopilotInvestmentFields.RequiredForValue.ToList();
                return view;
            }

            view.Values = input.Assumptions.Clone();
            view.Note = input.Assumptions.Note;
            var values = input.Assumptions.Fields().ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            view.MissingForValue = CopilotInvestmentFields.RequiredForValue.Where(f => values[f] == null).ToList();
            var anySet = CopilotInvestmentFields.Inputs.Any(f => values[f] != null);
            view.Status = view.MissingForValue.Count == 0 ? CopilotInvestmentAssumptionStatus.Complete
                : anySet ? CopilotInvestmentAssumptionStatus.Partial : CopilotInvestmentAssumptionStatus.Missing;
            return view;
        }

        private static CopilotInvestmentSources Sources(CopilotInvestmentInput input, Context context)
        {
            var sources = new CopilotInvestmentSources
            {
                Adoption = new CopilotInvestmentAdoptionSource
                {
                    Available = input.AdoptionAvailable,
                    AuditAvailable = input.AuditAvailable,
                    UsageReportAvailable = input.UsageReportAvailable,
                    UsageReportDate = input.UsageReportDate,
                    FiguresIncomplete = input.FiguresIncomplete,
                    LicensedUsersCapped = input.LicensedUsersCapped,
                    HoldersNotAnalysed = input.HoldersNotAnalysed,
                    AuditSignalHolders = context.Holders.Count(h => h.SignalSource == CopilotAdoptionScoring.SignalSourceAudit),
                    UsageReportSignalHolders = context.Holders.Count(h => h.SignalSource == CopilotAdoptionScoring.SignalSourceUsageReport),
                    AnalysisGeneratedUtc = input.AnalysisGeneratedUtc,
                },
            };

            var credits = input.Credits ?? new CreditFacts();
            var observed = (input.People ?? new List<CopilotInvestmentPerson>()).Where(p => p.Credit != null).Select(p => p.Credit).ToList();
            var unresolved = credits.Users.Where(u => !u.UserId.HasValue).ToList();
            var source = new CopilotInvestmentCreditSource
            {
                TablesPresent = credits.TablesPresent,
                CollectionEnabled = input.CreditCollectionEnabled,
                HasData = credits.Users.Count > 0,
                FirstObservationDate = credits.FirstObservationDate,
                SnapshotDate = credits.SnapshotDate,
                SnapshotBandFrom = credits.SnapshotBandFrom,
                PeriodStatus = PeriodStatus(observed.Concat(input.Filtered ? Enumerable.Empty<CreditUserFact>() : unresolved)),
                ObservedUsers = observed.Count,
                PartialUsers = observed.Count(o => o.Partial),
                StaleUsers = credits.SnapshotDate.HasValue ? observed.Count(o => o.ObservationDate < credits.SnapshotDate.Value) : 0,
                OldestFreshness = observed.Where(o => o.OldestFreshness.HasValue).Select(o => o.OldestFreshness).DefaultIfEmpty(null).Min(),
                NewestFreshness = observed.Where(o => o.NewestFreshness.HasValue).Select(o => o.NewestFreshness).DefaultIfEmpty(null).Max(),
                SweepsInWindow = credits.Sweeps.Count,
                CompletedSweepsInWindow = credits.Sweeps.Count(s => s.LastCompleted.HasValue),
                ContextObservedAt = credits.Context?.ObservedAt,
                FailureCode = credits.FailureCode,
            };
            // A directory object nobody imported cannot be shown to be in a filtered view, and its existence is a
            // tenant-wide fact, so the unresolved figures are only reported for an unfiltered one.
            if (!input.Filtered)
            {
                source.UnresolvedObjects = unresolved.Count;
                source.UnresolvedCredits = Math.Round(unresolved.Sum(u => u.Credits ?? 0), 6);
            }
            if (credits.Collector != null)
            {
                source.DirectoryUsers = credits.Collector.DirectoryUsers;
                source.CollectorObserved = credits.Collector.Observed;
                source.CollectorFailed = credits.Collector.Failed;
                source.CollectorDenied = credits.Collector.Denied;
                source.CollectorPending = credits.Collector.Pending + credits.Collector.Retrying;
                source.CollectorPhase = credits.Collector.Phase;
                source.CollectorLastCompleted = credits.Collector.LastCompleted;
            }
            sources.Credits = source;

            var studio = input.Studio ?? new StudioFacts();
            sources.Studio = new CopilotInvestmentStudioSource
            {
                HasData = studio.HasData,
                ImportEnabled = input.StudioImportEnabled,
                LastImportUtc = studio.LastImportUtc,
                LastImportFailed = !string.IsNullOrEmpty(studio.LastImportError),
                UnresolvedCredits = input.Filtered ? (double?)null : Math.Round(studio.Users.Where(u => !u.UserId.HasValue).Sum(u => u.Credits), 6),
                FailureCode = studio.FailureCode,
            };
            return sources;
        }

        private static string PeriodStatus(IEnumerable<CreditUserFact> facts)
        {
            var statuses = facts.Select(f => string.Equals(f.PeriodStatus, "verified", StringComparison.Ordinal) && f.SourceMonth.HasValue ? "verified" : "unverified")
                .Distinct(StringComparer.Ordinal).ToList();
            if (statuses.Count == 0) return null;
            return statuses.Count == 1 ? statuses[0] : "mixed";
        }

        private static CopilotInvestmentSpend Investment(Context context)
        {
            var input = context.Input;
            var holders = context.Holders;
            var people = input.People ?? new List<CopilotInvestmentPerson>();
            var spend = new CopilotInvestmentSpend { Currency = context.Currency };

            var allHolders = holders.Count + input.HoldersNotAnalysed;
            var seatDays = holders.Sum(h => context.SeatDays(h)) + input.HoldersNotAnalysed * (double)input.WindowDays;
            var activeHolders = holders.Count(h => h.ActiveDays > 0);
            spend.Seats = new CopilotInvestmentSeats
            {
                Holders = allHolders,
                AnalysedHolders = holders.Count,
                SeatDays = Math.Round(seatDays, 2),
                Basis = holders.Count == 0 ? null
                    : holders.All(h => h.SeatHeldDays.HasValue) ? SeatBases.LicenceHistory
                    : holders.Any(h => h.SeatHeldDays.HasValue) ? SeatBases.Mixed : SeatBases.CurrentAssignments,
            };
            if (context.SeatPrice.HasValue)
            {
                spend.Seats.MonthlyRunRate = Money(allHolders * context.SeatPrice.Value);
                spend.Seats.WindowCost = Money(context.SeatPrice.Value * seatDays / AverageMonthDays);
                spend.Seats.AnalysedWindowCost = Money(holders.Sum(h => context.SeatCost(h) ?? 0));
                spend.Seats.CostPerActiveHolderMonthly = activeHolders > 0 ? Money(holders.Count * context.SeatPrice.Value / activeHolders) : null;
            }

            var studioPeople = people.Where(p => p.Studio != null).ToList();
            spend.Studio = new CopilotInvestmentStudioSpend
            {
                Users = studioPeople.Count(p => p.Studio.Credits > 0),
                Credits = Math.Round(studioPeople.Sum(p => p.Studio.Credits), 6),
                PriorCredits = Math.Round(studioPeople.Sum(p => p.Studio.PriorCredits), 6),
                PriorUsers = studioPeople.Count(p => p.Studio.PriorCredits > 0),
                HolderCredits = Math.Round(studioPeople.Where(p => p.Licensed).Sum(p => p.Studio.Credits), 6),
            };
            if (context.StudioPrice.HasValue)
            {
                spend.Studio.WindowValue = Money(spend.Studio.Credits * context.StudioPrice.Value);
                spend.Studio.HolderWindowValue = Money(spend.Studio.HolderCredits * context.StudioPrice.Value);
            }

            var creditPeople = people.Where(p => p.Credit != null).ToList();
            spend.Credits = new CopilotInvestmentCreditBalances
            {
                ObservedUsers = creditPeople.Count,
                ConsumingUsers = creditPeople.Count(p => (p.Credit.Credits ?? 0) > 0),
                Credits = Math.Round(creditPeople.Sum(p => p.Credit.Credits ?? 0), 6),
                HolderCredits = Math.Round(creditPeople.Where(p => p.Licensed).Sum(p => p.Credit.Credits ?? 0), 6),
                NonHolderCredits = Math.Round(creditPeople.Where(p => !p.Licensed).Sum(p => p.Credit.Credits ?? 0), 6),
                PeriodStatus = PeriodStatus(creditPeople.Select(p => p.Credit)),
                OtherUnitUsers = creditPeople.Count(p => p.Credit.OtherUnitRows > 0),
                UnknownAmountUsers = creditPeople.Count(p => p.Credit.UnknownConsumptionRows > 0),
                LimitExhaustedUsers = creditPeople.Count(p => p.Credit.LimitExhaustedRows > 0),
                NearLimitUsers = creditPeople.Count(p => p.Credit.LimitExhaustedRows == 0 && p.Credit.NearLimitRows > 0),
                NotEntitledUsers = creditPeople.Count(p => p.Credit.NotEntitledRows > 0),
            };
            if (context.CreditPrice.HasValue) spend.Credits.BalanceValue = Money(spend.Credits.Credits * context.CreditPrice.Value);

            if (spend.Seats.AnalysedWindowCost.HasValue)
            {
                spend.ModelledWindowCost = Money(spend.Seats.AnalysedWindowCost.Value + (spend.Studio.HolderWindowValue ?? 0));
                spend.ModelledMonthlyCost = Money(spend.ModelledWindowCost.Value / context.Months);
            }
            return spend;
        }

        private static CopilotInvestmentEvidence Evidence(CopilotInvestmentInput input, Context context)
        {
            var holders = context.Holders;
            var active = holders.Where(h => h.ActiveDays > 0).ToList();
            var activeDays = active.Select(h => (double)h.ActiveDays).ToList();
            var evidence = new CopilotInvestmentEvidence
            {
                Holders = holders.Count + input.HoldersNotAnalysed,
                AnalysedHolders = holders.Count,
                ActiveHolders = active.Count,
                ActiveShare = holders.Count > 0 ? Math.Round(active.Count / (double)holders.Count, 4) : (double?)null,
                IdleHolders = holders.Count - active.Count,
                IdleTooNewToJudge = holders.Count(h => h.ActiveDays <= 0 && h.TooNewToJudge),
                TotalActiveDays = holders.Sum(h => (long)h.ActiveDays),
                MedianActiveDays = CopilotInvestmentStatistics.Median(activeDays),
                P25ActiveDays = CopilotInvestmentStatistics.Percentile(activeDays, 0.25),
                P75ActiveDays = CopilotInvestmentStatistics.Percentile(activeDays, 0.75),
                ActiveDaysPerActiveHolderMonthly = active.Count > 0 ? Math.Round(activeDays.Sum() / active.Count / context.Months, 2) : (double?)null,
                HabitShare = active.Count > 0 ? Math.Round(active.Average(h => Math.Min(1, h.ActiveDays / context.Weekdays)), 4) : (double?)null,
                MultiAppHolders = holders.Count(h => h.AppsUsed >= 3),
                AgentHolders = holders.Count(h => h.AgentsUsed > 0),
                CoworkHolders = holders.Count(h => h.CoworkInteractions > 0),
                UnlicensedActiveUsers = context.NonHolders.Count(p => p.ActiveDays > 0),
                UnlicensedActiveDays = context.NonHolders.Sum(p => (long)p.ActiveDays),
                RecommendedCandidates = input.RecommendedCandidates,
            };
            foreach (var band in Enum.GetValues(typeof(AdoptionBand)).Cast<AdoptionBand>().OrderByDescending(b => b))
                evidence.Bands.Add(new CopilotInvestmentBandCount { Band = band.ToString(), Holders = holders.Count(h => h.Band == band.ToString()) });

            var edges = new[] { 0d, 2d, 5d, 10d, 15d };
            var keys = new[] { "1to2", "2to5", "5to10", "10to15", "15plus" };
            evidence.Distribution.Add(new CopilotInvestmentDistributionBucket
            {
                Key = "none", MinDaysPerMonth = 0, MaxDaysPerMonth = 0, Holders = holders.Count(h => h.ActiveDays <= 0),
            });
            for (var i = 0; i < edges.Length; i++)
            {
                var min = edges[i];
                double? max = i + 1 < edges.Length ? edges[i + 1] : (double?)null;
                evidence.Distribution.Add(new CopilotInvestmentDistributionBucket
                {
                    Key = keys[i],
                    MinDaysPerMonth = min,
                    MaxDaysPerMonth = max,
                    Holders = holders.Count(h => h.ActiveDays > 0 && context.ActiveDaysPerMonth(h) > min
                        && (!max.HasValue || context.ActiveDaysPerMonth(h) <= max.Value)),
                    BreakEvenBucket = context.BreakEvenDaysPerMonth.HasValue && context.BreakEvenDaysPerMonth.Value > min
                        && (!max.HasValue || context.BreakEvenDaysPerMonth.Value <= max.Value),
                });
            }
            return evidence;
        }

        private static CopilotInvestmentValue Value(Context context)
        {
            var result = new CopilotInvestmentValue();
            if (!context.ValueReady)
            {
                result.Missing = MissingForValue(context.Input.Assumptions);
                return result;
            }
            result.Available = true;
            var cost = ModelledCost(context);
            var totalDays = context.Holders.Sum(h => (double)h.ActiveDays);
            foreach (var scenario in CopilotInvestmentScenarios.All)
            {
                var minutes = context.Minutes.Value(scenario).Value;
                var realisation = context.Realisation.Value(scenario).Value;
                var assisted = totalDays * minutes / 60;
                var value = assisted * realisation * context.Hourly.Value;
                result.Scenarios.Add(new CopilotInvestmentScenarioResult
                {
                    Scenario = scenario,
                    MinutesPerActiveDay = minutes,
                    Realisation = realisation,
                    AssistedHours = Math.Round(assisted, 1),
                    RealisedHours = Math.Round(assisted * realisation, 1),
                    Value = Money(value).Value,
                    MonthlyValue = Money(value / context.Months).Value,
                    RoiMultiple = Ratio(value, cost),
                    Net = cost.HasValue ? Money(value - cost.Value) : null,
                    HoldersAtOrAboveBreakEven = context.Holders.Count(h => context.Value(h, scenario, scenario) >= (context.Cost(h) ?? 0) && h.ActiveDays > 0),
                });
            }
            var active = context.Holders.Count(h => h.ActiveDays > 0);
            var baseValue = result.Scenarios.Single(s => s.Scenario == CopilotInvestmentScenarios.Base).Value;
            result.ValuePerActiveHolderMonthly = active > 0 ? Money(baseValue / active / context.Months) : null;
            return result;
        }

        internal static List<string> MissingForValue(CopilotInvestmentAssumptions assumptions)
        {
            if (assumptions == null) return CopilotInvestmentFields.RequiredForValue.ToList();
            var values = assumptions.Fields().ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            return CopilotInvestmentFields.RequiredForValue.Where(f => values[f] == null).ToList();
        }

        /// <summary>The cost the return is measured against: the analysed holders' own modelled cost.</summary>
        internal static double? ModelledCost(Context context)
        {
            if (!context.SeatPrice.HasValue) return null;
            return context.Holders.Sum(h => context.Cost(h) ?? 0);
        }

        private static CopilotInvestmentBreakEven BreakEven(Context context)
        {
            var result = new CopilotInvestmentBreakEven();
            if (!context.ValueReady) return result;
            result.Available = true;
            var cost = ModelledCost(context) ?? 0;
            var totalDays = context.Holders.Sum(h => (double)h.ActiveDays);
            var realisation = context.Realisation.Base.Value;
            if (totalDays > 0 && realisation > 0 && context.Hourly.Value > 0)
            {
                var minutes = cost / (totalDays * realisation * context.Hourly.Value / 60);
                result.MinutesPerActiveDay = Math.Round(minutes, 2);
                if (context.Minutes.Base.Value > 0) result.MarginOfSafety = Math.Round(1 - minutes / context.Minutes.Base.Value, 4);
            }
            if (context.BreakEvenDaysPerMonth.HasValue) result.ActiveDaysPerMonthPerSeat = Math.Round(context.BreakEvenDaysPerMonth.Value, 2);
            if (realisation > 0 && context.Hourly.Value > 0)
                result.MinutesPerWeekdayPerSeat = Math.Round(context.SeatPrice.Value / (context.Hourly.Value / 60 * realisation * context.WeekdaysPerMonth), 2);

            foreach (var holder in context.Holders)
            {
                if (holder.ActiveDays <= 0) result.HoldersNoEvidence++;
                else if (context.Value(holder, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base) >= (context.Cost(holder) ?? 0)) result.HoldersAtOrAbove++;
                else result.HoldersBelow++;
            }
            result.ShareAtOrAbove = context.Holders.Count > 0 ? Math.Round(result.HoldersAtOrAbove / (double)context.Holders.Count, 4) : (double?)null;
            return result;
        }

        private static CopilotInvestmentSensitivity Sensitivity(Context context)
        {
            var result = new CopilotInvestmentSensitivity();
            if (!context.ValueReady) return result;
            var cost = ModelledCost(context);
            foreach (var minutes in CopilotInvestmentScenarios.All)
                foreach (var realisation in CopilotInvestmentScenarios.All)
                {
                    var value = context.Holders.Sum(h => context.Value(h, minutes, realisation));
                    result.Grid.Add(new CopilotInvestmentSensitivityCell
                    {
                        MinutesScenario = minutes,
                        RealisationScenario = realisation,
                        Value = Money(value).Value,
                        RoiMultiple = Ratio(value, cost),
                    });
                }

            var baseValue = context.Holders.Sum(h => context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base));
            result.RoiBaseHourlyMinus20 = Ratio(baseValue * 0.8, cost);
            result.RoiBaseHourlyPlus20 = Ratio(baseValue * 1.2, cost);

            if (context.CreditPrice.HasValue && cost.HasValue)
            {
                var monthlyBalances = context.Holders.Sum(h => h.Credit?.Credits ?? 0) * context.CreditPrice.Value;
                result.RoiBaseIfBalancesMonthly = Ratio(baseValue, cost.Value + monthlyBalances * context.Months);
            }

            var reclaim = context.Holders.Where(context.IsReclaimCandidate).Sum(h => context.Cost(h) ?? 0);
            if (cost.HasValue && reclaim > 0) result.RoiBaseAfterReclaim = Ratio(baseValue, cost.Value - reclaim);
            return result;
        }
    }
}
