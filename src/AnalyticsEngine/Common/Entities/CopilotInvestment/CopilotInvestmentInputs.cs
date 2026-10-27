using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>Directory attributes for a person the adoption analysis holds no row for (credits or Studio only).</summary>
    public sealed class CopilotInvestmentDirectoryEntry
    {
        public string UserPrincipalName { get; set; }
        public string Department { get; set; }
        public string JobTitle { get; set; }
        public string Country { get; set; }
        public string Company { get; set; }
        public string Manager { get; set; }
        public bool? AccountEnabled { get; set; }
    }

    /// <summary>One person in scope, joined across the adoption evidence, the preview balances and the Studio ledger.</summary>
    public sealed class CopilotInvestmentPerson
    {
        public int UserId { get; set; }
        public string UserPrincipalName { get; set; }
        public string Department { get; set; }
        public string JobTitle { get; set; }
        public string Country { get; set; }
        public string Company { get; set; }
        public string Manager { get; set; }
        public bool? AccountEnabled { get; set; }

        /// <summary>Holds a Copilot seat and was scored by the adoption analysis.</summary>
        public bool Licensed { get; set; }
        public string SeatLicences { get; set; }
        public double? SeatHeldDays { get; set; }

        /// <summary>The adoption band's enum name, for a seat holder.</summary>
        public string Band { get; set; }
        public double? AdoptionScore { get; set; }
        public string SignalSource { get; set; }
        public bool TooNewToJudge { get; set; }
        public string ReclaimEligibility { get; set; }

        /// <summary>Copilot active days in the window: the adoption signal for a seat holder, audit days for anyone else.</summary>
        public int ActiveDays { get; set; }
        public long Interactions { get; set; }
        public int AppsUsed { get; set; }
        public int AgentsUsed { get; set; }
        public long CoworkInteractions { get; set; }
        public DateTime? LastActivityUtc { get; set; }

        /// <summary>Recommended for a seat by the adoption analysis' licence-opportunity ranking.</summary>
        public bool Recommended { get; set; }

        public CreditUserFact Credit { get; set; }
        public StudioUserFact Studio { get; set; }
    }

    /// <summary>Everything one calculation needs. Built per request; nothing in it is shared or edited afterwards.</summary>
    public sealed class CopilotInvestmentInput
    {
        public DateTime GeneratedUtc { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public int WindowDays { get; set; }
        public double WorkingDaysPerWeek { get; set; } = 5;
        public int MinGroupSize { get; set; } = 5;
        public bool Filtered { get; set; }

        public List<CopilotInvestmentPerson> People { get; set; } = new List<CopilotInvestmentPerson>();

        /// <summary>Seat holders in scope the adoption analysis did not score because of its row cap.</summary>
        public int HoldersNotAnalysed { get; set; }
        public int RecommendedCandidates { get; set; }

        public bool AdoptionAvailable { get; set; }
        public bool AuditAvailable { get; set; }
        public bool UsageReportAvailable { get; set; }
        public DateTime? UsageReportDate { get; set; }
        public bool FiguresIncomplete { get; set; }
        public bool LicensedUsersCapped { get; set; }
        public DateTime? AnalysisGeneratedUtc { get; set; }

        /// <summary>Effective assumptions (tenant defaults with any what-if applied). Null when they could not be read.</summary>
        public CopilotInvestmentAssumptions Assumptions { get; set; }
        public string AssumptionsStatusOverride { get; set; }
        public Dictionary<string, string> Provenance { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public long AssumptionsVersion { get; set; }
        public DateTime? AssumptionsUpdatedUtc { get; set; }
        public bool AssumptionsDurable { get; set; }

        public CreditFacts Credits { get; set; }

        /// <summary>
        /// Every latest balance of a person in scope, whether or not it adds them to <see cref="People"/>. Coverage and
        /// totals are counted from this, so a quiet, unlicensed person with a zero balance is counted as observed
        /// without becoming a row of the report.
        /// </summary>
        public List<CreditUserFact> ScopedCredits { get; set; }
        public bool CreditCollectionEnabled { get; set; }
        public StudioFacts Studio { get; set; }
        public bool StudioImportEnabled { get; set; }

        public string Dimension { get; set; } = CopilotInvestmentDimensions.Department;
        public string DimensionName { get; set; }

        /// <summary>The group a person falls in for <see cref="Dimension"/>; null when they hold no value.</summary>
        public Func<int, string> GroupOf { get; set; }
    }

    /// <summary>Joins the adoption analysis' scored rows with the report's own facts, one person per user id.</summary>
    public static class CopilotInvestmentPeopleBuilder
    {
        /// <param name="scoped">The adoption analysis, already narrowed to the reader's scope.</param>
        /// <param name="inScope">Whether a user id is in the reader's scope - for the balances and ledger rows.</param>
        /// <param name="directory">Attributes for someone the analysis has no row for; may return null.</param>
        /// <param name="scopedCredits">Every in-scope balance, including the quiet ones that add no person.</param>
        public static List<CopilotInvestmentPerson> Build(
            CopilotAdoptionAnalysis scoped,
            IEnumerable<CreditUserFact> credits,
            IEnumerable<StudioUserFact> studio,
            Func<int, bool> inScope,
            Func<int, CopilotInvestmentDirectoryEntry> directory,
            out int recommendedCandidates,
            out List<CreditUserFact> scopedCredits)
        {
            var people = new Dictionary<int, CopilotInvestmentPerson>();
            foreach (var row in scoped?.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
            {
                people[row.UserId] = new CopilotInvestmentPerson
                {
                    UserId = row.UserId,
                    UserPrincipalName = row.UserPrincipalName,
                    Department = row.Department,
                    JobTitle = row.JobTitle,
                    Country = row.Country,
                    Company = row.CompanyName,
                    Manager = row.ManagerUserPrincipalName,
                    AccountEnabled = row.AccountEnabled,
                    Licensed = true,
                    SeatLicences = row.SeatLicences,
                    SeatHeldDays = row.SeatHeldDays,
                    Band = row.Band.ToString(),
                    AdoptionScore = row.AdoptionScore,
                    SignalSource = row.SignalSource,
                    TooNewToJudge = row.TooNewToJudge,
                    ReclaimEligibility = row.ReclaimEligibility,
                    ActiveDays = Math.Max(0, row.ActiveDays),
                    Interactions = row.Interactions,
                    AppsUsed = row.AppsUsed,
                    AgentsUsed = row.AgentsUsed,
                    CoworkInteractions = row.CoworkInteractions,
                    LastActivityUtc = row.LastInteractionUtc ?? row.ReportLastActivityUtc,
                };
            }

            foreach (var row in scoped?.UnlicensedUsers ?? new List<UnlicensedUsageQueryRow>())
            {
                if (people.ContainsKey(row.UserId)) continue;
                var entry = directory?.Invoke(row.UserId);
                people[row.UserId] = new CopilotInvestmentPerson
                {
                    UserId = row.UserId,
                    UserPrincipalName = row.UserPrincipalName ?? entry?.UserPrincipalName,
                    Department = row.Department ?? entry?.Department,
                    JobTitle = entry?.JobTitle,
                    Country = entry?.Country,
                    Company = entry?.Company,
                    Manager = entry?.Manager,
                    AccountEnabled = entry?.AccountEnabled,
                    ActiveDays = Math.Max(0, row.ActiveDays),
                    Interactions = row.Interactions,
                    AppsUsed = row.AppsUsed,
                    AgentsUsed = row.AgentsUsed,
                    LastActivityUtc = row.LastInteractionUtc,
                };
            }

            var recommended = 0;
            foreach (var row in scoped?.Opportunities ?? new List<LicenceOpportunityRow>())
            {
                if (!row.Recommended) continue;
                recommended++;
                if (people.TryGetValue(row.UserId, out var person) && !person.Licensed) person.Recommended = true;
            }
            recommendedCandidates = recommended;

            scopedCredits = new List<CreditUserFact>();
            foreach (var fact in credits ?? Enumerable.Empty<CreditUserFact>())
            {
                if (!fact.UserId.HasValue || !inScope(fact.UserId.Value)) continue;
                scopedCredits.Add(fact);
                // A whole directory is swept every day. Someone with no seat, no Copilot activity and a quiet balance
                // is observed (and counted as such) but is not a person this report has anything to say about.
                if (people.TryGetValue(fact.UserId.Value, out var known)) known.Credit = fact;
                else if (Notable(fact)) Attach(people, fact.UserId.Value, directory).Credit = fact;
            }

            foreach (var fact in studio ?? Enumerable.Empty<StudioUserFact>())
            {
                if (!fact.UserId.HasValue || !inScope(fact.UserId.Value)) continue;
                if (fact.Credits <= 0 && fact.PriorCredits <= 0) continue;
                Attach(people, fact.UserId.Value, directory).Studio = fact;
            }

            return people.Values.OrderBy(p => p.UserId).ToList();
        }

        /// <summary>A balance worth a row of its own: spend, an amount the source could not state, a refusal or a gap.</summary>
        internal static bool Notable(CreditUserFact fact) =>
            (fact.Credits ?? 0) > 0 || fact.UnknownConsumptionRows > 0 || fact.OtherUnitRows > 0 || fact.NotAllowedRows > 0
            || fact.LimitExhaustedRows > 0 || fact.NearLimitRows > 0 || fact.NotEntitledRows > 0 || fact.Partial;

        private static CopilotInvestmentPerson Attach(Dictionary<int, CopilotInvestmentPerson> people, int userId,
            Func<int, CopilotInvestmentDirectoryEntry> directory)
        {
            if (people.TryGetValue(userId, out var person)) return person;
            var entry = directory?.Invoke(userId);
            person = new CopilotInvestmentPerson
            {
                UserId = userId,
                UserPrincipalName = entry?.UserPrincipalName,
                Department = entry?.Department,
                JobTitle = entry?.JobTitle,
                Country = entry?.Country,
                Company = entry?.Company,
                Manager = entry?.Manager,
                AccountEnabled = entry?.AccountEnabled,
            };
            people[userId] = person;
            return person;
        }
    }

    /// <summary>Small, exact statistics over a population. Pure functions, so every figure can be checked by hand.</summary>
    public static class CopilotInvestmentStatistics
    {
        /// <summary>Nearest-rank percentile (0..1) of an unsorted list; null when empty.</summary>
        public static double? Percentile(IEnumerable<double> values, double p)
        {
            var sorted = (values ?? Enumerable.Empty<double>()).OrderBy(v => v).ToList();
            if (sorted.Count == 0) return null;
            var rank = (int)Math.Ceiling(Math.Max(0, Math.Min(1, p)) * sorted.Count);
            return sorted[Math.Max(0, rank - 1)];
        }

        public static double? Median(IEnumerable<double> values)
        {
            var sorted = (values ?? Enumerable.Empty<double>()).OrderBy(v => v).ToList();
            if (sorted.Count == 0) return null;
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>Gini coefficient of non-negative values: 0 is perfectly even, values near 1 concentrated in a few.</summary>
        public static double Gini(IEnumerable<double> values)
        {
            var sorted = (values ?? Enumerable.Empty<double>()).Select(v => Math.Max(0, v)).OrderBy(v => v).ToList();
            var n = sorted.Count;
            var total = sorted.Sum();
            if (n == 0 || total <= 0) return 0;
            double weighted = 0;
            for (var i = 0; i < n; i++) weighted += (i + 1) * sorted[i];
            return Math.Max(0, Math.Min(1, 2 * weighted / (n * total) - (double)(n + 1) / n));
        }

        /// <summary>The share of the total held by the largest <paramref name="fraction"/> of the population (at least one member).</summary>
        public static double TopShare(IEnumerable<double> values, double fraction)
        {
            var sorted = (values ?? Enumerable.Empty<double>()).Select(v => Math.Max(0, v)).OrderByDescending(v => v).ToList();
            var total = sorted.Sum();
            if (sorted.Count == 0 || total <= 0) return 0;
            var count = Math.Max(1, (int)Math.Ceiling(sorted.Count * fraction));
            return sorted.Take(count).Sum() / total;
        }

        /// <summary>Cumulative share of the total held by the bottom 0%, 10%, ... 100%: eleven points.</summary>
        public static List<double> Lorenz(IEnumerable<double> values)
        {
            var sorted = (values ?? Enumerable.Empty<double>()).Select(v => Math.Max(0, v)).OrderBy(v => v).ToList();
            var total = sorted.Sum();
            var points = new List<double>();
            for (var decile = 0; decile <= 10; decile++)
            {
                if (sorted.Count == 0 || total <= 0)
                {
                    points.Add(decile / 10d);
                    continue;
                }
                var count = (int)Math.Round(sorted.Count * decile / 10d, MidpointRounding.AwayFromZero);
                points.Add(Math.Round(sorted.Take(count).Sum() / total, 4));
            }
            return points;
        }

        public static CopilotInvestmentDistribution Distribution(IReadOnlyCollection<double> values, double topFraction)
        {
            if (values == null || values.Count == 0) return null;
            return new CopilotInvestmentDistribution
            {
                Population = values.Count,
                TopFraction = topFraction,
                TopShare = Math.Round(TopShare(values, topFraction), 4),
                Gini = Math.Round(Gini(values), 4),
                Lorenz = Lorenz(values),
            };
        }
    }
}
