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

        /// <summary>The window's last calendar day: the Studio days run from <see cref="FromUtc"/> to it, inclusive.</summary>
        public DateTime? LastDay { get; set; }
        public int WindowDays { get; set; }

        /// <summary>True when a filter narrows the view: tenant-only facts (unmatched accounts, policies, pool balances) are withheld.</summary>
        public bool Filtered { get; set; }

        public Func<int, bool> InScope { get; set; }
        public Func<int, CopilotInvestmentDirectoryEntry> Directory { get; set; }

        public string Dimension { get; set; } = CopilotInvestmentDimensions.Department;
        public string DimensionName { get; set; }
        public Func<int, string> GroupOf { get; set; }

        /// <summary>Why the people cannot be grouped (<see cref="CopilotSpendGroupingUnavailable"/>), or null.</summary>
        public string GroupingUnavailable { get; set; }
        public int MinGroupSize { get; set; } = CopilotSpendCalculator.DefaultMinGroupSize;
        public int MaxGroups { get; set; } = CopilotSpendCalculator.MaxGroups;

        public CreditFacts Credits { get; set; }
        public bool CreditCollectionEnabled { get; set; }

        /// <summary>Billed Studio credits per person in the window (tenant-wide, cached).</summary>
        public StudioFacts Studio { get; set; }
        public bool StudioImportEnabled { get; set; }

        /// <summary>The scoped per-day and per-environment Studio breakdown (and, unfiltered, the agent ledger), read in SQL; null when it could not be read.</summary>
        public CopilotSpendStudioBreakdown StudioBreakdown { get; set; }
    }

    /// <summary>Builds the descriptive summary. No price, no value, no judgement: counts and quantities as the sources report them.</summary>
    public static class CopilotSpendCalculator
    {
        /// <summary>
        /// A group with fewer people than this is combined with the other small ones, and a combination still that small
        /// shows no figures, so no row's figures describe fewer people than this. A grouping that names people (manager) is
        /// served only to a reader with See PII and shows every group.
        /// </summary>
        public const int DefaultMinGroupSize = 5;

        /// <summary>The most groups the page lists; the rest are combined into one labelled row and listed in full in the export.</summary>
        public const int MaxGroups = 100;

        public static CopilotSpendSummary Calculate(CopilotSpendInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var inScope = input.InScope ?? (_ => true);
            var credits = input.Credits ?? new CreditFacts();
            var studio = input.Studio ?? new StudioFacts();

            // A source counts as zero only when it was read and holds data; otherwise its figures are not available (null).
            var studioAvailable = studio.FailureCode == null && studio.EarliestDate.HasValue;
            var balancesAvailable = credits.FailureCode == null && credits.SnapshotDate.HasValue;

            // One latest reading per person in view. A person is one directory object, but the newest reading wins if not.
            var balances = credits.Users.Where(u => u.UserId.HasValue && inScope(u.UserId.Value))
                .GroupBy(u => u.UserId.Value)
                .Select(g => g.OrderByDescending(u => u.ObservationDate).ThenBy(u => u.ObjectId).First())
                .ToDictionary(u => u.UserId.Value);
            var unresolved = input.Filtered ? new List<CreditUserFact>() : credits.Users.Where(u => !u.UserId.HasValue).ToList();
            var studioByUser = studio.Users.Where(u => u.UserId.HasValue && inScope(u.UserId.Value) && u.Credits > 0)
                .GroupBy(u => u.UserId.Value)
                .ToDictionary(g => g.Key, g => g.First());
            var unmatchedStudio = input.Filtered || !studioAvailable ? (double?)null : Math.Round(studio.Users.Where(u => !u.UserId.HasValue).Sum(u => u.Credits), 6);

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
                    // A readable ledger with no charges for this person is a real zero; an unreadable or absent one is unknown.
                    StudioCredits = studioAvailable ? 0 : (double?)null,
                    StudioDays = studioAvailable ? 0 : (int?)null,
                };
                if (studioByUser.TryGetValue(userId, out var used))
                {
                    row.StudioCredits = Math.Round(used.Credits, 6);
                    row.StudioDays = used.ActiveDays;
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
            summary.Studio = Studio(input, studio, studioAvailable, studioByUser.Values.ToList(), unmatchedStudio);
            summary.Balances = Balances(input, credits, balancesAvailable, balances.Values.ToList(), unresolved);
            summary.Organisation = Organisation(input, people, studioAvailable, balancesAvailable);
            return summary;
        }

        /// <summary>
        /// Whether a reading puts a person in the list: credits consumed, or an amount the source could not state (unknown,
        /// another unit) or a limit reached. A plain zero balance is counted as observed, not listed.
        /// </summary>
        internal static bool Consumed(CreditUserFact balance) =>
            (balance.Credits ?? 0) > 0 || balance.UnknownConsumptionRows > 0 || balance.OtherUnitRows > 0 || balance.LimitExhaustedRows > 0;

        private static CopilotSpendStudio Studio(CopilotSpendInput input, StudioFacts studio, bool available, List<StudioUserFact> used, double? unmatched)
        {
            var section = new CopilotSpendStudio
            {
                ImportEnabled = input.StudioImportEnabled,
                HasData = studio.HasData,
                FailureCode = studio.FailureCode,
                LastImportUtc = studio.LastImportUtc,
                LastImportFailed = !string.IsNullOrEmpty(studio.LastImportError),
                EarliestDate = studio.EarliestDate,
                LatestDate = studio.LatestDate,
                CoversWindow = studio.EarliestDate.HasValue && studio.EarliestDate.Value.Date <= input.FromUtc.Date,
                Available = available,
                People = available ? used.Count : (int?)null,
                UnmatchedCredits = unmatched,
                Credits = available ? Math.Round(used.Sum(u => u.Credits) + (unmatched ?? 0), 6) : (double?)null,
            };
            if (!available) return section;

            var breakdown = input.StudioBreakdown;
            if (breakdown == null || breakdown.FailureCode != null)
            {
                section.BreakdownFailureCode = breakdown?.FailureCode ?? CopilotSpendErrorCodes.StudioBreakdownUnavailable;
                return section;
            }
            section.Days = CalendarDays(input, studio, breakdown.Days);
            section.Environments = breakdown.Environments.OrderByDescending(e => e.Credits).ThenBy(e => e.EnvironmentId, StringComparer.Ordinal).ToList();
            if (input.Filtered || breakdown.Agents == null)
            {
                // Recorded per agent, not per person: a filtered view cannot be shown its share of it.
                section.Agents = null;
                section.AgentLedgerCredits = null;
            }
            else
            {
                section.Agents = breakdown.Agents.OrderByDescending(a => a.Credits).ThenBy(a => a.AgentId, StringComparer.Ordinal).ToList();
                section.AgentLedgerCredits = Math.Round(section.Agents.Sum(a => a.Credits), 6);
            }
            return section;
        }

        /// <summary>
        /// Every calendar day of the window, so a weekend or a quiet day keeps its place on the axis. A day the ledger holds
        /// rows for is <c>recorded</c>; a day between the ledger's first and latest stored days with no rows for the people
        /// in view is <c>noneRecorded</c> (the ledger has nothing, which is all it can say); a day outside those - before it
        /// starts, or after its latest stored day - is <c>notReported</c>, which is not zero.
        /// </summary>
        internal static List<CopilotSpendDay> CalendarDays(CopilotSpendInput input, StudioFacts studio, IEnumerable<CopilotSpendDay> rows)
        {
            var recorded = (rows ?? Enumerable.Empty<CopilotSpendDay>()).GroupBy(d => d.Date.Date).ToDictionary(g => g.Key, g => g.First());
            var first = input.FromUtc.Date;
            var last = (input.LastDay ?? input.ToUtc).Date;
            var days = new List<CopilotSpendDay>();
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (recorded.TryGetValue(day, out var row))
                {
                    days.Add(new CopilotSpendDay { Date = day, Status = CopilotSpendDayStatus.Recorded, Credits = row.Credits, People = row.People });
                    continue;
                }
                var stored = studio.EarliestDate.HasValue && studio.LatestDate.HasValue
                    && day >= studio.EarliestDate.Value.Date && day <= studio.LatestDate.Value.Date;
                days.Add(new CopilotSpendDay { Date = day, Status = stored ? CopilotSpendDayStatus.NoneRecorded : CopilotSpendDayStatus.NotReported });
            }
            return days;
        }

        private static CopilotSpendBalances Balances(CopilotSpendInput input, CreditFacts credits, bool available, List<CreditUserFact> observed,
            List<CreditUserFact> unresolved)
        {
            var section = new CopilotSpendBalances
            {
                TablesPresent = credits.TablesPresent,
                CollectionEnabled = input.CreditCollectionEnabled,
                HasData = credits.SnapshotDate.HasValue,
                Available = available,
                FailureCode = credits.FailureCode,
                SnapshotDate = credits.SnapshotDate,
                SnapshotBeforeWindow = credits.SnapshotDate.HasValue && credits.SnapshotDate.Value.Date < input.FromUtc.Date,
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
                Credits = available ? Math.Round(observed.Concat(unresolved).Sum(o => o.Credits ?? 0), 6) : (double?)null,
                SweepsInWindow = credits.Sweeps.Count,
                CompletedSweepsInWindow = credits.Sweeps.Count(s => s.LastCompleted.HasValue),
                Services = CopilotInvestmentCalculator.ServiceRows(credits, observed, input.Filtered),
            };
            if (!input.Filtered && available)
            {
                section.UnresolvedAccounts = unresolved.Count;
                section.UnresolvedCredits = Math.Round(unresolved.Sum(u => u.Credits ?? 0), 6);
            }
            if (!input.Filtered)
            {
                section.Policies = CopilotInvestmentCalculator.PolicyRows(credits);
                section.TenantBalances = CopilotInvestmentCalculator.TenantBalanceRows(credits);
            }
            return section;
        }

        /// <summary>
        /// Both sources side by side per group. Every group with at least <see cref="CopilotSpendOrganisation.MinGroupSize"/>
        /// people is kept; smaller ones are combined (with no figures if the combination is still that small). The page lists
        /// the largest <see cref="CopilotSpendInput.MaxGroups"/> and combines the rest into a labelled row; the export
        /// (<see cref="CopilotSpendOrganisation.AllGroups"/>) lists every group.
        /// </summary>
        private static CopilotSpendOrganisation Organisation(CopilotSpendInput input, List<CopilotSpendPersonRow> people, bool studioAvailable,
            bool balancesAvailable)
        {
            var organisation = new CopilotSpendOrganisation
            {
                Dimension = input.Dimension,
                DimensionName = input.DimensionName,
                // A grouping that names people is only ever served to a reader with See PII: every manager is shown.
                MinGroupSize = CopilotInvestmentDimensions.NamesPeople(input.Dimension) ? 1 : Math.Max(1, input.MinGroupSize),
                UnavailableCode = input.GroupingUnavailable,
            };
            if (input.GroupOf == null)
            {
                organisation.UnavailableCode = organisation.UnavailableCode ?? CopilotSpendGroupingUnavailable.Dimension;
                return organisation;
            }

            var totals = new GroupTotals
            {
                Studio = people.Sum(p => p.StudioCredits ?? 0),
                Balances = people.Sum(p => p.BalanceCredits ?? 0),
                StudioAvailable = studioAvailable,
                BalancesAvailable = balancesAvailable,
            };
            var groups = people.GroupBy(p => NullIfBlank(input.GroupOf(p.UserId)), StringComparer.Ordinal)
                .Select(g => new { Label = g.Key, People = g.ToList() })
                .ToList();
            var kept = groups.Where(g => g.People.Count >= organisation.MinGroupSize)
                .OrderByDescending(g => g.People.Sum(p => p.StudioCredits ?? 0))
                .ThenByDescending(g => g.People.Sum(p => p.BalanceCredits ?? 0))
                .ThenBy(g => g.Label, StringComparer.Ordinal)
                .ToList();
            var small = groups.Where(g => g.People.Count < organisation.MinGroupSize).ToList();
            var maxGroups = Math.Max(1, input.MaxGroups);

            foreach (var group in kept)
            {
                var row = GroupRow(group.People, totals, organisation.MinGroupSize);
                if (group.Label == null) row.EmptyKey = CopilotInvestmentCalculator.GroupKeys.NotSet;
                else row.Label = group.Label;
                organisation.AllGroups.Add(row);
            }
            organisation.Groups.AddRange(organisation.AllGroups.Take(maxGroups));

            var more = kept.Skip(maxGroups).ToList();
            if (more.Count > 0)
            {
                // Large groups beyond what the page lists: one labelled row on the page, every one of them in the export.
                var row = GroupRow(more.SelectMany(g => g.People).ToList(), totals, organisation.MinGroupSize);
                row.EmptyKey = CopilotSpendGroupKeys.MoreGroups;
                row.Folded = more.Count;
                organisation.Groups.Add(row);
                organisation.MoreGroups = more.Count;
            }
            if (small.Count > 0)
            {
                var row = GroupRow(small.SelectMany(g => g.People).ToList(), totals, organisation.MinGroupSize);
                row.EmptyKey = CopilotInvestmentCalculator.GroupKeys.OtherSmallGroups;
                row.Folded = small.Count;
                organisation.Groups.Add(row);
                organisation.AllGroups.Add(row);
                organisation.FoldedGroups = small.Count;
            }
            return organisation;
        }

        private sealed class GroupTotals
        {
            public double Studio;
            public double Balances;
            public bool StudioAvailable;
            public bool BalancesAvailable;
        }

        /// <summary>
        /// One group's figures: a source that is not available leaves its figures null, and a row with fewer people than the
        /// smallest group shown (only ever a combination of small groups) shows none at all.
        /// </summary>
        private static CopilotSpendGroupRow GroupRow(List<CopilotSpendPersonRow> people, GroupTotals totals, int minGroupSize)
        {
            var row = new CopilotSpendGroupRow { People = people.Count };
            if (people.Count < minGroupSize)
            {
                row.Withheld = true;
                return row;
            }
            if (totals.StudioAvailable)
            {
                var studio = people.Sum(p => p.StudioCredits ?? 0);
                row.StudioPeople = people.Count(p => p.StudioCredits > 0);
                row.StudioCredits = Math.Round(studio, 6);
                row.ShareOfStudio = totals.Studio > 0 ? Math.Round(studio / totals.Studio, 4) : (double?)null;
            }
            if (totals.BalancesAvailable)
            {
                var balance = people.Sum(p => p.BalanceCredits ?? 0);
                row.BalancePeople = people.Count(p => (p.BalanceCredits ?? 0) > 0);
                row.BalanceCredits = Math.Round(balance, 6);
                row.ShareOfBalances = totals.Balances > 0 ? Math.Round(balance / totals.Balances, 4) : (double?)null;
                row.UnknownAmountPeople = people.Count(p => p.UnknownAmounts);
            }
            return row;
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>One person's drill-down from their own facts: balance by category, balance over time, Studio by environment.</summary>
        public static CopilotSpendPersonDetail Detail(CopilotSpendPersonRow person, PersonDetailFacts facts) => new CopilotSpendPersonDetail
        {
            Person = person,
            Services = CopilotInvestmentPersonDetailBuilder.CreditServices(facts),
            Balances = (facts ?? new PersonDetailFacts()).Balances.OrderBy(b => b.Date).ToList(),
            StudioEnvironments = CopilotInvestmentPersonDetailBuilder.StudioEnvironments(facts),
        };
    }

    /// <summary>The scoped Studio breakdown read in SQL.</summary>
    public sealed class CopilotSpendStudioBreakdown
    {
        public List<CopilotSpendDay> Days { get; set; } = new List<CopilotSpendDay>();
        public List<CopilotSpendEnvironment> Environments { get; set; } = new List<CopilotSpendEnvironment>();

        /// <summary>The tenant agent ledger by agent; null when not read, as for a filtered view.</summary>
        public List<CopilotSpendAgent> Agents { get; set; }
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
