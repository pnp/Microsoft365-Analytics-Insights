using Common.Entities.CopilotInvestment;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotSpend
{
    /// <summary>Everything one Copilot Spend view is built from. The facts are tenant-wide and shared; the predicate narrows them.</summary>
    public sealed class CopilotSpendInput
    {
        public DateTime GeneratedUtc { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public int WindowDays { get; set; }

        /// <summary>True when a filter narrows the view: tenant-only facts (unmatched accounts, policies, pool balances) are withheld.</summary>
        public bool Filtered { get; set; }

        public Func<int, bool> InScope { get; set; }
        public Func<int, CopilotInvestmentDirectoryEntry> Directory { get; set; }

        public string Dimension { get; set; } = CopilotInvestmentDimensions.Department;
        public string DimensionName { get; set; }
        public Func<int, string> GroupOf { get; set; }
        public int MinGroupSize { get; set; } = CopilotSpendCalculator.DefaultMinGroupSize;

        public CreditFacts Credits { get; set; }
        public bool CreditCollectionEnabled { get; set; }

        /// <summary>Billed Studio credits per person in the window (tenant-wide, cached).</summary>
        public StudioFacts Studio { get; set; }
        public bool StudioImportEnabled { get; set; }

        /// <summary>The scoped per-day and per-agent Studio breakdown, read in SQL; null when it could not be read.</summary>
        public CopilotSpendStudioBreakdown StudioBreakdown { get; set; }
    }

    /// <summary>Builds the descriptive summary. No price, no value, no judgement: counts and quantities as the sources report them.</summary>
    public static class CopilotSpendCalculator
    {
        /// <summary>A group with fewer people than this is folded into one row, so an aggregate never stands in for one person.</summary>
        public const int DefaultMinGroupSize = 5;

        /// <summary>The most groups listed; the rest are folded into one row.</summary>
        public const int MaxGroups = 100;

        public static CopilotSpendSummary Calculate(CopilotSpendInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var inScope = input.InScope ?? (_ => true);
            var credits = input.Credits ?? new CreditFacts();
            var studio = input.Studio ?? new StudioFacts();

            // One latest reading per person in view. A person is one directory object, but the newest reading wins if not.
            var balances = credits.Users.Where(u => u.UserId.HasValue && inScope(u.UserId.Value))
                .GroupBy(u => u.UserId.Value)
                .Select(g => g.OrderByDescending(u => u.ObservationDate).ThenBy(u => u.ObjectId).First())
                .ToDictionary(u => u.UserId.Value);
            var unresolved = input.Filtered ? new List<CreditUserFact>() : credits.Users.Where(u => !u.UserId.HasValue).ToList();
            var studioByUser = studio.Users.Where(u => u.UserId.HasValue && inScope(u.UserId.Value) && u.Credits > 0)
                .GroupBy(u => u.UserId.Value)
                .ToDictionary(g => g.Key, g => g.First());
            var unmatchedStudio = input.Filtered ? (double?)null : Math.Round(studio.Users.Where(u => !u.UserId.HasValue).Sum(u => u.Credits), 6);

            var people = new List<CopilotSpendPersonRow>();
            foreach (var userId in studioByUser.Keys.Union(balances.Where(b => Consumed(b.Value)).Select(b => b.Key)))
            {
                var entry = input.Directory?.Invoke(userId);
                var row = new CopilotSpendPersonRow
                {
                    UserId = userId,
                    UserPrincipalName = entry?.UserPrincipalName,
                    Department = entry?.Department,
                    JobTitle = entry?.JobTitle,
                    Company = entry?.Company,
                    Country = entry?.Country,
                    Manager = entry?.Manager,
                };
                if (studioByUser.TryGetValue(userId, out var used))
                {
                    row.StudioCredits = Math.Round(used.Credits, 6);
                    row.StudioDays = used.ActiveDays;
                    row.StudioAgents = used.Agents;
                }
                if (balances.TryGetValue(userId, out var balance))
                {
                    row.BalanceCredits = balance.Credits.HasValue ? Math.Round(balance.Credits.Value, 6) : (double?)null;
                    row.BalanceDate = balance.ObservationDate;
                    row.BalancePartial = balance.Partial;
                    row.UnknownAmounts = balance.UnknownConsumptionRows > 0;
                    row.OtherUnits = balance.OtherUnitRows > 0;
                    row.LimitExhausted = balance.LimitExhaustedRows > 0;
                    row.NotEntitled = balance.NotEntitledRows > 0;
                    row.CreditObjectId = balance.ObjectId;
                }
                people.Add(row);
            }

            var summary = new CopilotSpendSummary
            {
                GeneratedUtc = input.GeneratedUtc,
                FromUtc = input.FromUtc,
                ToUtc = input.ToUtc,
                WindowDays = input.WindowDays,
                Filtered = input.Filtered,
                People = CopilotSpendPeople.Sort(people, null, true).ToList(),
                PeopleCount = people.Count,
            };
            summary.Studio = Studio(input, studio, studioByUser.Values.ToList(), unmatchedStudio);
            summary.Balances = Balances(input, credits, balances.Values.ToList(), unresolved);
            summary.Organisation = Organisation(input, people);
            return summary;
        }

        /// <summary>
        /// Whether a reading puts a person in the list: credits consumed, or an amount the source could not state (unknown,
        /// another unit) or a limit reached. A plain zero balance is counted as observed, not listed.
        /// </summary>
        internal static bool Consumed(CreditUserFact balance) =>
            (balance.Credits ?? 0) > 0 || balance.UnknownConsumptionRows > 0 || balance.OtherUnitRows > 0 || balance.LimitExhaustedRows > 0;

        private static CopilotSpendStudio Studio(CopilotSpendInput input, StudioFacts studio, List<StudioUserFact> used, double? unmatched)
        {
            var section = new CopilotSpendStudio
            {
                ImportEnabled = input.StudioImportEnabled,
                HasData = studio.HasData,
                FailureCode = studio.FailureCode,
                LastImportUtc = studio.LastImportUtc,
                LastImportFailed = !string.IsNullOrEmpty(studio.LastImportError),
                EarliestDate = studio.EarliestDate,
                CoversWindow = studio.EarliestDate.HasValue && studio.EarliestDate.Value.Date <= input.FromUtc.Date,
                People = used.Count,
                UnmatchedCredits = unmatched,
                Credits = Math.Round(used.Sum(u => u.Credits) + (unmatched ?? 0), 6),
            };
            var breakdown = input.StudioBreakdown;
            if (breakdown == null || breakdown.FailureCode != null)
            {
                section.BreakdownFailureCode = breakdown?.FailureCode ?? CopilotSpendErrorCodes.StudioBreakdownUnavailable;
                return section;
            }
            section.Days = breakdown.Days.OrderBy(d => d.Date).ToList();
            section.Agents = breakdown.Agents.OrderByDescending(a => a.Credits).ThenBy(a => a.AgentId, StringComparer.Ordinal).ToList();
            return section;
        }

        private static CopilotSpendBalances Balances(CopilotSpendInput input, CreditFacts credits, List<CreditUserFact> observed, List<CreditUserFact> unresolved)
        {
            var section = new CopilotSpendBalances
            {
                TablesPresent = credits.TablesPresent,
                CollectionEnabled = input.CreditCollectionEnabled,
                HasData = credits.SnapshotDate.HasValue,
                FailureCode = credits.FailureCode,
                SnapshotDate = credits.SnapshotDate,
                SnapshotBandFrom = credits.SnapshotBandFrom,
                FirstObservationDate = credits.FirstObservationDate,
                PeriodStatus = CopilotInvestmentCalculator.CreditPeriodStatus(observed.Concat(unresolved)),
                OldestFreshness = observed.Concat(unresolved).Where(o => o.OldestFreshness.HasValue).Select(o => o.OldestFreshness).DefaultIfEmpty(null).Min(),
                NewestFreshness = observed.Concat(unresolved).Where(o => o.NewestFreshness.HasValue).Select(o => o.NewestFreshness).DefaultIfEmpty(null).Max(),
                ObservedPeople = observed.Count,
                ConsumingPeople = observed.Count(o => (o.Credits ?? 0) > 0),
                PartialPeople = observed.Count(o => o.Partial),
                UnknownAmountPeople = observed.Count(o => o.UnknownConsumptionRows > 0),
                OtherUnitPeople = observed.Count(o => o.OtherUnitRows > 0),
                LimitExhaustedPeople = observed.Count(o => o.LimitExhaustedRows > 0),
                NotEntitledPeople = observed.Count(o => o.NotEntitledRows > 0),
                Credits = Math.Round(observed.Concat(unresolved).Sum(o => o.Credits ?? 0), 6),
                SweepsInWindow = credits.Sweeps.Count,
                CompletedSweepsInWindow = credits.Sweeps.Count(s => s.LastCompleted.HasValue),
                Services = CopilotInvestmentCalculator.ServiceRows(credits, observed, input.Filtered),
            };
            if (!input.Filtered)
            {
                section.UnresolvedAccounts = unresolved.Count;
                section.UnresolvedCredits = Math.Round(unresolved.Sum(u => u.Credits ?? 0), 6);
                section.Policies = CopilotInvestmentCalculator.PolicyRows(credits);
                section.TenantBalances = CopilotInvestmentCalculator.TenantBalanceRows(credits);
            }
            return section;
        }

        private static CopilotSpendOrganisation Organisation(CopilotSpendInput input, List<CopilotSpendPersonRow> people)
        {
            var organisation = new CopilotSpendOrganisation
            {
                Dimension = input.Dimension,
                DimensionName = input.DimensionName,
                MinGroupSize = Math.Max(1, input.MinGroupSize),
            };
            if (input.GroupOf == null) return organisation;

            var studioTotal = people.Sum(p => p.StudioCredits);
            var balanceTotal = people.Sum(p => p.BalanceCredits ?? 0);
            var groups = people.GroupBy(p => NullIfBlank(input.GroupOf(p.UserId)), StringComparer.Ordinal)
                .Select(g => new { Label = g.Key, People = g.ToList() })
                .ToList();
            var shown = groups.Where(g => g.People.Count >= organisation.MinGroupSize)
                .OrderByDescending(g => g.People.Sum(p => p.StudioCredits))
                .ThenByDescending(g => g.People.Sum(p => p.BalanceCredits ?? 0))
                .ThenBy(g => g.Label, StringComparer.Ordinal)
                .Take(MaxGroups)
                .ToList();
            var folded = groups.Except(shown).ToList();

            foreach (var group in shown)
            {
                var row = GroupRow(group.People, studioTotal, balanceTotal);
                if (group.Label == null) row.EmptyKey = CopilotInvestmentCalculator.GroupKeys.NotSet;
                else row.Label = group.Label;
                organisation.Groups.Add(row);
            }
            if (folded.Count > 0)
            {
                var row = GroupRow(folded.SelectMany(g => g.People).ToList(), studioTotal, balanceTotal);
                row.EmptyKey = CopilotInvestmentCalculator.GroupKeys.OtherSmallGroups;
                organisation.Groups.Add(row);
                organisation.FoldedGroups = folded.Count;
            }
            return organisation;
        }

        private static CopilotSpendGroupRow GroupRow(List<CopilotSpendPersonRow> people, double studioTotal, double balanceTotal)
        {
            var studio = people.Sum(p => p.StudioCredits);
            var balance = people.Sum(p => p.BalanceCredits ?? 0);
            return new CopilotSpendGroupRow
            {
                People = people.Count,
                StudioPeople = people.Count(p => p.StudioCredits > 0),
                StudioCredits = Math.Round(studio, 6),
                ShareOfStudio = studioTotal > 0 ? Math.Round(studio / studioTotal, 4) : (double?)null,
                BalancePeople = people.Count(p => (p.BalanceCredits ?? 0) > 0),
                BalanceCredits = Math.Round(balance, 6),
                ShareOfBalances = balanceTotal > 0 ? Math.Round(balance / balanceTotal, 4) : (double?)null,
                UnknownAmountPeople = people.Count(p => p.UnknownAmounts),
            };
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>One person's drill-down from their own facts: balance by category, balance over time, Studio by agent.</summary>
        public static CopilotSpendPersonDetail Detail(CopilotSpendPersonRow person, PersonDetailFacts facts) => new CopilotSpendPersonDetail
        {
            Person = person,
            Services = CopilotInvestmentPersonDetailBuilder.CreditServices(facts),
            Balances = (facts ?? new PersonDetailFacts()).Balances.OrderBy(b => b.Date).ToList(),
            StudioAgents = CopilotInvestmentPersonDetailBuilder.StudioAgents(facts),
        };
    }

    /// <summary>The scoped Studio breakdown read in SQL.</summary>
    public sealed class CopilotSpendStudioBreakdown
    {
        public List<CopilotSpendDay> Days { get; set; } = new List<CopilotSpendDay>();
        public List<CopilotSpendAgent> Agents { get; set; } = new List<CopilotSpendAgent>();
        public string FailureCode { get; set; }
    }

    /// <summary>Searching and ordering the people list, over every person in view, before it is paged.</summary>
    public static class CopilotSpendPeople
    {
        public static IEnumerable<CopilotSpendPersonRow> Filter(IEnumerable<CopilotSpendPersonRow> rows, string source, string search)
        {
            switch ((source ?? string.Empty).Trim())
            {
                case "studio": rows = rows.Where(r => r.StudioCredits > 0); break;
                case "balances": rows = rows.Where(r => r.BalanceCredits.HasValue || r.UnknownAmounts || r.OtherUnits || r.LimitExhausted); break;
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                rows = rows.Where(r => Contains(r.UserPrincipalName, term) || Contains(r.Department, term) || Contains(r.JobTitle, term)
                    || Contains(r.Company, term) || Contains(r.Manager, term));
            }
            return rows;
        }

        private static bool Contains(string value, string term) => value != null && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Orders the whole list. A value a person does not have (no balance reading) sorts last whichever way the column is
        /// read; ties fall back to the sign-in name and then the user id, so the same request always pages the same way.
        /// </summary>
        public static IEnumerable<CopilotSpendPersonRow> Sort(IEnumerable<CopilotSpendPersonRow> rows, string sortBy, bool descending)
        {
            IOrderedEnumerable<CopilotSpendPersonRow> ordered = null;
            foreach (var key in Keys(sortBy))
            {
                var value = key;
                ordered = ordered == null ? rows.OrderBy(r => value(r) == null) : ordered.ThenBy(r => value(r) == null);
                ordered = descending ? ordered.ThenByDescending(value, ValueOrder) : ordered.ThenBy(value, ValueOrder);
            }
            return ordered.ThenBy(r => r.UserPrincipalName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.UserId);
        }

        private static readonly IComparer<object> ValueOrder = Comparer<object>.Create((a, b) =>
            a is string x && b is string y ? StringComparer.OrdinalIgnoreCase.Compare(x, y) : Comparer<object>.Default.Compare(a, b));

        private static Func<CopilotSpendPersonRow, object>[] Keys(string sortBy)
        {
            switch ((sortBy ?? string.Empty).Trim())
            {
                case "user": return new Func<CopilotSpendPersonRow, object>[] { r => r.UserPrincipalName };
                case "department": return new Func<CopilotSpendPersonRow, object>[] { r => NullIfBlank(r.Department) };
                case "manager": return new Func<CopilotSpendPersonRow, object>[] { r => NullIfBlank(r.Manager) };
                case "studioDays": return new Func<CopilotSpendPersonRow, object>[] { r => r.StudioDays };
                case "studioAgents": return new Func<CopilotSpendPersonRow, object>[] { r => r.StudioAgents };
                case "balanceCredits": return new Func<CopilotSpendPersonRow, object>[] { r => r.BalanceCredits };
                case "balanceDate": return new Func<CopilotSpendPersonRow, object>[] { r => r.BalanceDate };
                case "studioCredits":
                default:
                    // Billed Studio credits, then the latest balance: ranked one after the other, never added.
                    return new Func<CopilotSpendPersonRow, object>[] { r => r.StudioCredits, r => r.BalanceCredits };
            }
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
