using Common.Entities.CopilotInvestment;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// The privacy floor (#844) on the Copilot Adoption rows a reader without See PII is given beside a total: the email-domain
    /// table, the departments' licence-candidate counts and the departments' manager figures. Each method reads the cached
    /// summary and returns new rows, or the same list when nothing is withheld; the cached rows are never edited.
    /// </summary>
    /// <remarks>
    /// <para>Two rules. A population of one to four people has its figures withheld. And wherever a total is published beside
    /// rows, the total less the rows shown is the withheld rows together with any the list leaves out, so when that is itself
    /// one to four people, more rows are withheld - the smallest shown first - until it is nobody or at least the floor. The
    /// total stays: it is the headline, and withholding it would take every figure that adds up to it with it.</para>
    /// <para>A reader with See PII is given the rows as they are; they can read the people behind them anyway.</para>
    /// </remarks>
    internal static class CopilotAdoptionSmallGroups
    {
        #region Breakdowns that leave a group out

        /// <summary>
        /// The department, country and accountability breakdowns for a reader without See PII: whenever one leaves a group
        /// out, it also withholds the smallest group it lists - chosen by people, then by name - from every view of it, so
        /// that its total less the groups shown always holds a group of five or more as well as the one left out, and no
        /// view shows a group another view hides (#844). The licensed and unlicensed sides of the side-by-side table, its
        /// agent figures and its agent builders, which are floored first, follow the same rule.
        /// </summary>
        /// <remarks>
        /// <para><b>What leaves a group out.</b> Every reader is spared a group of fewer than
        /// <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/> people, and each view lists at most
        /// <see cref="CopilotAdoptionOptions.TopSegments"/>. A breakdown leaves a group out when the groups its views list
        /// between them hold fewer people than the total published beside them - the scored seat holders, the unlicensed
        /// users counted, the active users seen in the audit log, the builders. That total less the groups shown is then
        /// the groups left out together, and with only one left out, that group's own figures.</para>
        /// <para><b>The breakdowns, and the views each must agree in.</b></para>
        /// <list type="bullet">
        /// <item>Seat holders by department: adoption and habit by department (the league table), the licensed side of the
        /// side-by-side table, the manager columns and an accountability roll-up by department. Intensity and time saved
        /// by department count the seat holders scored from the audit log, and Cowork rollout by department those scored
        /// for Cowork - subsets of the same people, each against its own total - so each is checked as well, and a
        /// department withheld for any of them is withheld from all of them: a subset's count can equal the whole's, and
        /// would give a withheld department's back.</item>
        /// <item>Seat holders by country: adoption by country and an accountability roll-up by country.</item>
        /// <item>An accountability roll-up by office or company.</item>
        /// <item>Unlicensed Copilot Chat users by department: the side-by-side table's unlicensed side and unlicensed use
        /// by department.</item>
        /// <item>The side-by-side table's agent figures, over the active users seen in the audit log.</item>
        /// <item>Agent builders, whose count of 1-4 is withheld first.</item>
        /// </list>
        /// <para>Every group is one person's only group - a person has one department, country, office or company - so the
        /// smallest-shown rule is enough: the overlapping variant is for the manager figures
        /// (<see cref="ManagerDepartmentsWithoutComplements"/>), where a manager is counted in each department they lead.
        /// The totals stay. A withheld side or agent figure on the side-by-side table is null with
        /// <see cref="SmallGroupWithheldReasons.RevealsSmallerGroup"/>; a withheld row of any other view is left out, as
        /// the groups below the floor already are. <see cref="CopilotAdoptionSummary.SmallestGroupsWithheld"/> names the
        /// breakdowns that withheld one more, so the page and the workbook can say so.</para>
        /// </remarks>
        /// <param name="summary">The reader's summary so far, read only.</param>
        /// <param name="writable">The reader's own copy, made on first call, which the replaced lists are written to.</param>
        internal static void WithholdSmallestListedGroups(CopilotAdoptionSummary summary, Func<CopilotAdoptionSummary> writable, int minPeople)
        {
            if (summary == null) return;

            var dimension = CopilotAdoptionService.NormaliseAccountabilityDimension(summary.AccountabilityDimension);
            var rollupByDepartment = dimension == CopilotAdoptionAccountabilityDimensions.Department;
            var rollupByCountry = dimension == CopilotAdoptionAccountabilityDimensions.Country;
            var rollup = summary.AccountabilityRollup ?? new List<AccountabilityRollupRow>();
            var combined = (summary.CombinedByDepartment ?? new List<AdoptionCombinedSegmentRow>()).ToList();
            var timeSaved = summary.SeatHolderTimeSavedEstimate;
            var unlicensed = summary.Unlicensed;
            var named = new List<string>();

            // Seat holders by department, with the two subsets of them that count their own totals: those scored from the
            // audit log rather than Microsoft's usage report (intensity, and time saved), and those scored for Cowork.
            var seatViews = Groups(summary.AdoptionByDepartment, r => r.Segment, r => r.LicensedUsers)
                .Concat(Groups(summary.HabitByDepartment, r => r.Segment, r => r.LicensedUsers))
                .Concat(Groups(combined, r => r.Segment, r => r.LicensedUsers ?? 0))
                .Concat(Groups(summary.ManagerModellingByDepartment, r => r.Segment, r => r.LicensedUsers))
                .Concat(rollupByDepartment ? Groups(rollup, r => r.Segment, r => r.LicensedUsers) : Enumerable.Empty<(string, int)>());
            var departments = SmallestListedGroups(
                minPeople,
                (summary.ScoredUsers, seatViews),
                (summary.ScoredUsers - summary.UsageReportSourcedUsers,
                    Groups(summary.IntensityByDepartment, r => r.Segment, r => r.LicensedUsers)
                        .Concat(Groups(timeSaved?.ByDepartment, r => r.Segment, r => r.CohortUsers))),
                (summary.CoworkScoredUsers, Groups(summary.CoworkByDepartment, r => r.Segment, r => r.LicensedUsers)
                    .Concat(Groups(summary.CoworkQuadrant, r => r.Segment, r => r.LicensedUsers))));

            var countries = SmallestListedGroups(minPeople, (summary.ScoredUsers,
                Groups(summary.AdoptionByCountry, r => r.Segment, r => r.LicensedUsers)
                    .Concat(rollupByCountry ? Groups(rollup, r => r.Segment, r => r.LicensedUsers) : Enumerable.Empty<(string, int)>())));

            var units = rollupByDepartment ? departments
                : rollupByCountry ? countries
                : dimension == CopilotAdoptionAccountabilityDimensions.DirectManager ? Withheld()
                : SmallestListedGroups(minPeople, (summary.ScoredUsers, Groups(rollup, r => r.Segment, r => r.LicensedUsers)));

            if (departments.Count > 0)
            {
                named.Add(CopilotAdoptionWithheldBreakdowns.Departments);
                var copy = writable();
                copy.AdoptionByDepartment = Without(summary.AdoptionByDepartment, r => r.Segment, departments);
                copy.HabitByDepartment = Without(summary.HabitByDepartment, r => r.Segment, departments);
                copy.IntensityByDepartment = Without(summary.IntensityByDepartment, r => r.Segment, departments);
                copy.ManagerModellingByDepartment = Without(summary.ManagerModellingByDepartment, r => r.Segment, departments);
                copy.CoworkByDepartment = Without(summary.CoworkByDepartment, r => r.Segment, departments);
                copy.CoworkQuadrant = Without(summary.CoworkQuadrant, r => r.Segment, departments);
                if (timeSaved?.ByDepartment != null && timeSaved.ByDepartment.Any(r => departments.Contains(Key(r.Segment))))
                {
                    var estimate = timeSaved.ShallowCopy();
                    estimate.ByDepartment = Without(timeSaved.ByDepartment, r => r.Segment, departments);
                    copy.SeatHolderTimeSavedEstimate = estimate;
                }

                for (var i = 0; i < combined.Count; i++)
                {
                    if (combined[i]?.LicensedUsers.HasValue == true && departments.Contains(Key(combined[i].Segment)))
                    {
                        combined[i] = WithoutLicensedSide(combined[i], minPeople);
                    }
                }
            }

            if (countries.Count > 0)
            {
                named.Add(CopilotAdoptionWithheldBreakdowns.Countries);
                writable().AdoptionByCountry = Without(summary.AdoptionByCountry, r => r.Segment, countries);
            }

            if (units.Count > 0 && rollup.Any(r => units.Contains(Key(r.Segment))))
            {
                named.Add(CopilotAdoptionWithheldBreakdowns.Accountability);
                writable().AccountabilityRollup = Without(rollup, r => r.Segment, units);
            }

            // Unlicensed Copilot Chat users by department: the unlicensed side, and unlicensed use by department, whose
            // bars sum interactions and so carry their people unpublished.
            var chatTotal = (unlicensed?.ActiveUsers ?? 0) > 0 ? unlicensed.ActiveUsers : summary.UnlicensedActiveUsers;
            var chatDepartments = SmallestListedGroups(minPeople, (chatTotal,
                Groups(combined, r => r.Segment, r => r.UnlicensedActiveUsers ?? 0)
                    .Concat(Groups(unlicensed?.UsageByDepartment, r => r.Label, r => r.People))));
            if (chatDepartments.Count > 0)
            {
                named.Add(CopilotAdoptionWithheldBreakdowns.UnlicensedDepartments);
                for (var i = 0; i < combined.Count; i++)
                {
                    if (combined[i]?.UnlicensedActiveUsers.HasValue == true && chatDepartments.Contains(Key(combined[i].Segment)))
                    {
                        combined[i] = WithoutUnlicensedSide(combined[i], minPeople);
                    }
                }

                if (unlicensed?.UsageByDepartment != null && unlicensed.UsageByDepartment.Any(c => chatDepartments.Contains(Key(c.Label))))
                {
                    var copy = unlicensed.ShallowCopy();
                    copy.UsageByDepartment = Without(unlicensed.UsageByDepartment, c => c.Label, chatDepartments);
                    writable().Unlicensed = copy;
                }
            }

            // The agent figures, over the department's active users seen in the audit log - read after the sides, whose
            // withholding takes them too.
            if (summary.AgentActiveUsers.HasValue)
            {
                var agentDepartments = SmallestListedGroups(minPeople, (summary.AgentActiveUsers.Value,
                    Groups(combined.Where(r => r != null && !r.AgentFiguresWithheld), r => r.Segment, r => r.AgentActiveUsers ?? 0)));
                if (agentDepartments.Count > 0)
                {
                    named.Add(CopilotAdoptionWithheldBreakdowns.AgentDepartments);
                    for (var i = 0; i < combined.Count; i++)
                    {
                        if (combined[i] != null && !combined[i].AgentFiguresWithheld && agentDepartments.Contains(Key(combined[i].Segment)))
                        {
                            combined[i] = WithoutAgentFigures(combined[i].Clone(), minPeople);
                        }
                    }
                }
            }

            // Agent builders: a count of one to four is withheld; then, beside the tenant's builders, the smallest shown.
            if (summary.AgentBuilders.HasValue)
            {
                for (var i = 0; i < combined.Count; i++)
                {
                    if (combined[i]?.AgentBuilders is int builders && SmallPopulations.IsSmall(builders, minPeople))
                    {
                        combined[i] = WithoutBuilders(combined[i], SmallGroupWithheldReasons.FewPeople, minPeople);
                    }
                }

                var builderDepartments = SmallestListedGroups(minPeople, (summary.AgentBuilders.Value,
                    Groups(combined, r => r.Segment, r => r.AgentBuilders ?? 0)));
                if (builderDepartments.Count > 0)
                {
                    named.Add(CopilotAdoptionWithheldBreakdowns.BuilderDepartments);
                    for (var i = 0; i < combined.Count; i++)
                    {
                        if (combined[i]?.AgentBuilders.HasValue == true && builderDepartments.Contains(Key(combined[i].Segment)))
                        {
                            combined[i] = WithoutBuilders(combined[i], SmallGroupWithheldReasons.RevealsSmallerGroup, minPeople);
                        }
                    }
                }
            }

            if (summary.CombinedByDepartment != null
                && combined.Where((row, i) => !ReferenceEquals(row, summary.CombinedByDepartment[i])).Any())
            {
                writable().CombinedByDepartment = combined;
            }

            if (named.Count > 0) writable().SmallestGroupsWithheld = named;
        }

        /// <summary>
        /// The groups to withhold from every view of one breakdown. For each population in turn - the breakdown's own, then
        /// any subset of it counted against its own total - whose views list fewer people than that total, the group they
        /// list with the fewest people is withheld too (the first by name on a tie), unless one they list is withheld
        /// already; and while the total less the groups still shown is one to <paramref name="minPeople"/> - 1 people, the
        /// next smallest goes as well. With every listed group at least <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/>
        /// (five or more), one is always enough; the loop keeps the rule true for a smaller setting.
        /// </summary>
        internal static HashSet<string> SmallestListedGroups(int minPeople, params (int Total, IEnumerable<(string Name, int People)> Listed)[] populations)
        {
            var withheld = Withheld();
            foreach (var (total, listed) in populations)
            {
                var groups = new Dictionary<string, (string Name, int People)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, people) in listed ?? Enumerable.Empty<(string, int)>())
                {
                    if (name == null || people <= 0) continue;
                    var key = Key(name);
                    if (!groups.TryGetValue(key, out var group) || people > group.People) groups[key] = (key, people);
                }

                var shown = groups.Values
                    .Where(g => !withheld.Contains(g.Name))
                    .OrderBy(g => g.People)
                    .ThenBy(g => g.Name, StringComparer.Ordinal)
                    .ToList();
                var leftOver = total - shown.Sum(g => g.People);
                if (shown.Count == 0 || leftOver <= 0) continue;

                var next = 0;
                if (shown.Count == groups.Count)
                {
                    withheld.Add(shown[next].Name);
                    leftOver += shown[next++].People;
                }

                while (leftOver < minPeople && next < shown.Count)
                {
                    withheld.Add(shown[next].Name);
                    leftOver += shown[next++].People;
                }
            }

            return withheld;
        }

        private static HashSet<string> Withheld() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A group's name as the breakdowns match it: trimmed, and compared without case.</summary>
        private static string Key(string name) => (name ?? string.Empty).Trim();

        private static IEnumerable<(string Name, int People)> Groups<T>(IEnumerable<T> rows, Func<T, string> name, Func<T, int> people)
        {
            return (rows ?? Enumerable.Empty<T>()).Where(r => r != null).Select(r => (name(r), people(r)));
        }

        private static List<T> Without<T>(List<T> rows, Func<T, string> name, HashSet<string> withheld)
        {
            if (rows == null || !rows.Any(r => r != null && withheld.Contains(Key(name(r))))) return rows;
            return rows.Where(r => r == null || !withheld.Contains(Key(name(r)))).ToList();
        }

        private static AdoptionCombinedSegmentRow WithoutLicensedSide(AdoptionCombinedSegmentRow row, int minPeople)
        {
            var copy = row.Clone();
            copy.LicensedUsers = null;
            copy.LicensedActiveUsers = null;
            copy.InteractionsPerLicensedUser = null;
            copy.LicensedAgentUserPct = null;
            copy.LicensedWithheld = SmallGroupWithheldReasons.RevealsSmallerGroup;
            return WithoutAgentFigures(copy, minPeople);
        }

        private static AdoptionCombinedSegmentRow WithoutUnlicensedSide(AdoptionCombinedSegmentRow row, int minPeople)
        {
            var copy = row.Clone();
            copy.UnlicensedActiveUsers = null;
            copy.InteractionsPerUnlicensedUser = null;
            copy.UnlicensedAgentUserPct = null;
            copy.UnlicensedWithheld = SmallGroupWithheldReasons.RevealsSmallerGroup;
            return WithoutAgentFigures(copy, minPeople);
        }

        /// <summary>
        /// The agent figures withheld on a copy already made: they count both sides together, so withholding either side
        /// takes them too, as it does for every reader. Figures that were never measured stay unmeasured.
        /// </summary>
        private static AdoptionCombinedSegmentRow WithoutAgentFigures(AdoptionCombinedSegmentRow copy, int minPeople)
        {
            if (copy.AgentFiguresWithheld || !copy.AgentActiveUsers.HasValue) return copy;

            copy.AgentFiguresWithheld = true;
            copy.AgentFiguresWithheldReason = SmallGroupWithheldReasons.RevealsSmallerGroup;
            copy.MinimumPeople = minPeople;
            copy.AgentActiveUsers = null;
            copy.AgentUsers = null;
            copy.AgentUserPct = null;
            copy.DistinctAgents = null;
            copy.AgentsPer100ActiveUsers = null;
            copy.AgentInteractions = null;
            copy.InteractionsPerActiveAgent = null;
            return copy;
        }

        private static AdoptionCombinedSegmentRow WithoutBuilders(AdoptionCombinedSegmentRow row, string reason, int minPeople)
        {
            var copy = row.Clone();
            copy.AgentBuilders = null;
            copy.AgentBuildersWithheld = reason;
            copy.MinimumPeople = minPeople;
            return copy;
        }

        #endregion

        #region Email domains

        /// <summary>
        /// The email-domain table for a reader without See PII: each of a row's three populations - its seat holders, its
        /// unlicensed Copilot Chat users and its licence candidates - floored on its own, rather than the row on all of them
        /// together, and each column's total kept from giving a withheld cell back.
        /// </summary>
        /// <remarks>
        /// <para>A domain of 20 seat holders and 3 Chat users is listed, because it has enough people, but its Chat count
        /// is about 3 people: withheld (<see cref="SmallGroupWithheldReasons.FewPeople"/>). Each column then answers to its
        /// own total - the scored seat holders, the unlicensed users counted, the recommended candidates - with
        /// <see cref="WithholdWhatATotalWouldGiveBack"/>. The people of a domain are in exactly one row, so the total less the
        /// rows shown counts the withheld rows and the domains left out of the table, and nobody twice.</para>
        /// <para>Every row stays, in the order of what is shown: withheld seat holders have no adoption rate to rank on, so
        /// their rows join the domains without seats, after those with a rate. Which ten rows are listed is decided before
        /// this, on every figure, as it is for a reader with See PII.</para>
        /// </remarks>
        internal static List<AdoptionDomainRow> EmailDomainsWithoutSmallPopulations(CopilotAdoptionSummary summary, int minPeople)
        {
            var rows = summary?.EmailDomains;
            if (rows == null || rows.Count == 0) return rows;

            var seats = rows.Select(r => r?.LicensedUsers ?? 0).ToList();
            var chat = rows.Select(r => r?.UnlicensedActiveUsers ?? 0).ToList();
            var candidates = rows.Select(r => r?.RecommendedForLicence ?? 0).ToList();

            var seatsSmall = seats.Select(n => SmallPopulations.IsSmall(n, minPeople)).ToArray();
            var chatSmall = chat.Select(n => SmallPopulations.IsSmall(n, minPeople)).ToArray();
            var candidatesSmall = candidates.Select(n => SmallPopulations.IsSmall(n, minPeople)).ToArray();

            var seatsWithheld = (bool[])seatsSmall.Clone();
            var chatWithheld = (bool[])chatSmall.Clone();
            var candidatesWithheld = (bool[])candidatesSmall.Clone();

            WithholdWhatATotalWouldGiveBack(seats, seatsWithheld, summary.ScoredUsers, minPeople);
            WithholdWhatATotalWouldGiveBack(chat, chatWithheld, UnlicensedUsersCounted(summary), minPeople);
            WithholdWhatATotalWouldGiveBack(candidates, candidatesWithheld, summary.RecommendedForLicence, minPeople);

            if (!seatsWithheld.Any(w => w) && !chatWithheld.Any(w => w) && !candidatesWithheld.Any(w => w)) return rows;

            var floored = new List<AdoptionDomainRow>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || (!seatsWithheld[i] && !chatWithheld[i] && !candidatesWithheld[i]))
                {
                    floored.Add(row);
                    continue;
                }

                var copy = row.Clone();
                copy.MinimumPeople = minPeople;
                if (seatsWithheld[i])
                {
                    copy.SeatHoldersWithheld = Reason(seatsSmall[i]);
                    copy.LicensedUsers = null;
                    copy.ActiveUsers = null;
                    copy.HabitualUsers = null;
                    copy.NeverUsedUsers = null;
                    copy.AdoptionRatePct = null;
                    copy.AverageAdoptionScore = null;
                    copy.ReclaimableSeats = null;
                    copy.InteractionsPerLicensedUser = null;
                    copy.CoworkPrimeCandidates = null;
                }

                if (chatWithheld[i])
                {
                    copy.UnlicensedUsersWithheld = Reason(chatSmall[i]);
                    copy.UnlicensedActiveUsers = null;
                }

                if (candidatesWithheld[i])
                {
                    copy.LicenceCandidatesWithheld = Reason(candidatesSmall[i]);
                    copy.RecommendedForLicence = null;
                }

                floored.Add(copy);
            }

            // Worst adoption first among the rows whose seat holders are shown, as for every reader; then the rows with no
            // adoption rate to show - no seats, or seat holders withheld - by the Chat use shown. Never on a withheld figure.
            return floored
                .Select((row, index) => (Row: row, Index: index))
                .OrderBy(r => r.Row != null && HasShownRate(r.Row) ? 0 : 1)
                .ThenBy(r => r.Row != null && HasShownRate(r.Row) ? r.Row.AdoptionRatePct.GetValueOrDefault() : 0)
                .ThenByDescending(r => r.Row?.LicensedUsers ?? 0)
                .ThenByDescending(r => r.Row?.UnlicensedActiveUsers ?? -1)
                .ThenBy(r => r.Row?.Segment, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Index)
                .Select(r => r.Row)
                .ToList();
        }

        private static bool HasShownRate(AdoptionDomainRow row) => row.LicensedUsers.GetValueOrDefault() > 0;

        /// <summary>
        /// The unlicensed Copilot Chat users the domain rows split between them: the scored list behind
        /// <see cref="CopilotAdoptionSummary.Unlicensed"/>, or the headline count when no list was summarised.
        /// </summary>
        private static int UnlicensedUsersCounted(CopilotAdoptionSummary summary)
        {
            var listed = summary.Unlicensed?.ActiveUsers ?? 0;
            return listed > 0 ? listed : summary.UnlicensedActiveUsers;
        }

        private static string Reason(bool small) =>
            small ? SmallGroupWithheldReasons.FewPeople : SmallGroupWithheldReasons.RevealsSmallerGroup;

        #endregion

        #region Licence candidates by department

        /// <summary>
        /// The departments' licence-candidate counts for a reader without See PII, held so that the tenant's recommended
        /// candidates less the counts shown never give back a department withheld for having one to four.
        /// </summary>
        /// <remarks>
        /// <para>Three lists show a department's count: the adoption and habit rows
        /// (<see cref="AdoptionSegmentRow.RecommendedCandidates"/>, the executive league table) and the chart of the
        /// departments with the most candidates. Every reader is given a count only when it is 0 or at least
        /// <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/>; between the two it is null. But the counts shown in any
        /// of the lists, taken from <see cref="CopilotAdoptionSummary.RecommendedForLicence"/>, leave the withheld departments
        /// together with every department no list shows - in a tenant whose departments are all listed, exactly the
        /// withheld ones. So while that difference is one to four people, the department with the fewest candidates shown
        /// is withheld from every list too (<see cref="SmallGroupWithheldReasons.RevealsSmallerGroup"/>). A candidate is in
        /// one department only.</para>
        /// </remarks>
        /// <returns>False, with the lists as they are, when nothing more is withheld.</returns>
        internal static bool CandidateCountsWithoutComplements(
            CopilotAdoptionSummary summary,
            int minPeople,
            out List<AdoptionSegmentRow> adoptionByDepartment,
            out List<AdoptionSegmentRow> habitByDepartment,
            out List<AdoptionCategory> opportunityByDepartment)
        {
            adoptionByDepartment = summary?.AdoptionByDepartment;
            habitByDepartment = summary?.HabitByDepartment;
            opportunityByDepartment = summary?.OpportunityByDepartment;
            if (summary == null) return false;

            var shown = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in (adoptionByDepartment ?? new List<AdoptionSegmentRow>()).Concat(habitByDepartment ?? new List<AdoptionSegmentRow>()))
            {
                if (row?.Segment != null && row.RecommendedCandidates.GetValueOrDefault() > 0) shown[row.Segment] = row.RecommendedCandidates.Value;
            }

            foreach (var bar in opportunityByDepartment ?? new List<AdoptionCategory>())
            {
                if (bar?.Label != null && bar.Value > 0) shown[bar.Label] = (int)Math.Round(bar.Value);
            }

            var withheld = new HashSet<string>(StringComparer.Ordinal);
            while (SmallPopulations.IsSmall(summary.RecommendedForLicence - shown.Where(s => !withheld.Contains(s.Key)).Sum(s => s.Value), minPeople))
            {
                var next = shown
                    .Where(s => !withheld.Contains(s.Key))
                    .OrderBy(s => s.Value)
                    .ThenBy(s => s.Key, StringComparer.Ordinal)
                    .Select(s => s.Key)
                    .FirstOrDefault();
                if (next == null) break;
                withheld.Add(next);
            }

            if (withheld.Count == 0) return false;

            adoptionByDepartment = WithCandidatesWithheld(adoptionByDepartment, withheld);
            habitByDepartment = WithCandidatesWithheld(habitByDepartment, withheld);
            opportunityByDepartment = opportunityByDepartment?.Where(c => c == null || !withheld.Contains(c.Label ?? string.Empty)).ToList();
            return true;
        }

        private static List<AdoptionSegmentRow> WithCandidatesWithheld(List<AdoptionSegmentRow> rows, HashSet<string> withheld)
        {
            return rows?.Select(row =>
            {
                if (row?.Segment == null || !withheld.Contains(row.Segment)) return row;

                var copy = row.CloneSegment();
                copy.RecommendedCandidates = null;
                copy.RecommendedCandidatesWithheld = SmallGroupWithheldReasons.RevealsSmallerGroup;
                return copy;
            }).ToList();
        }

        #endregion

        #region Manager figures by department

        /// <summary>
        /// The departments' manager figures (#641) for a reader without See PII, held so that the whole population's figures,
        /// published beside them, less the departments shown never give back a department's withheld ones.
        /// </summary>
        /// <remarks>
        /// <para>A department's figures are published in three steps, each withheld for every reader when it would describe
        /// too few people managers (<see cref="ManagerModellingFigures"/>): the share of its managers who use Copilot, then the
        /// comparison of the teams whose manager does and does not, then that comparison split by the manager's seat. The
        /// same figures for everyone head the table, so for each step, the whole population's less the departments shown is
        /// the departments withheld together with the managers of the departments no row lists.</para>
        /// <para>A manager can lead people in two departments and is counted in both, so the rows overlap and adding up their
        /// managers proves nothing about how many different people the difference holds. So for each group a step describes
        /// - the managers whose use is known; those who do and do not use Copilot; those four by seat -
        /// <see cref="SmallPopulations.WithholdOverlappingComplement"/> withholds the step for the department with the fewest
        /// of them that has at least <paramref name="minPeople"/>, whenever a department withheld for having one to four of
        /// them, or the unlisted remainder, would otherwise be all the difference held. The whole population's figures stay,
        /// as the headline. A step withheld this way takes the steps after it too, and says why
        /// (<see cref="SmallGroupWithheldReasons.RevealsSmallerGroup"/>).</para>
        /// <para>The group sizes are known to the analysis but not published (<see cref="ManagerModellingFigures.Groups"/>).
        /// A row or summary without them - never one the analysis built - has the comparison and its split withheld for
        /// every department.</para>
        /// </remarks>
        internal static List<ManagerModellingSegmentRow> ManagerDepartmentsWithoutComplements(CopilotAdoptionSummary summary, int minPeople)
        {
            var listed = summary?.ManagerModellingByDepartment;
            if (listed == null || listed.Count == 0) return listed;

            var rows = listed.Where(r => r != null).ToList();
            var count = rows.Count;
            var groupsKnown = summary.ManagerGroups != null && rows.All(r => r.Groups != null);

            // Step 1: managers using Copilot. Who is behind it - the managers whose use is known - is published, so this
            // step needs no hidden figure.
            var managersWithheld = rows.Select(r => !r.ManagersActive.HasValue).ToArray();
            if (summary.ManagersActive.HasValue)
            {
                Protect(rows.Select(r => r.ManagersStatusKnown).ToList(), summary.ManagersStatusKnown, managersWithheld, minPeople);
            }

            // Step 2: the comparison of teams whose manager does and does not use Copilot.
            var comparisonWithheld = rows.Select((r, i) => managersWithheld[i] || !r.ReportsManagerActive.HasValue).ToArray();
            if (summary.ReportsManagerActive.HasValue)
            {
                ProtectGroups(rows, summary, comparisonWithheld, minPeople, groupsKnown, g => g.Active, g => g.Inactive);
            }

            // Step 3: the same comparison split by whether the manager holds a seat.
            var seatSplitWithheld = rows.Select((r, i) => comparisonWithheld[i] || !r.ReportsManagerActiveLicensed.HasValue).ToArray();
            if (summary.ReportsManagerActiveLicensed.HasValue)
            {
                ProtectGroups(rows, summary, seatSplitWithheld, minPeople, groupsKnown,
                    g => g.ActiveLicensed, g => g.ActiveUnlicensed, g => g.InactiveLicensed, g => g.InactiveUnlicensed);
            }

            var changed = false;
            var floored = new List<ManagerModellingSegmentRow>(count);
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                var newlyManagers = managersWithheld[i] && row.ManagersActive.HasValue;
                var newlyComparison = comparisonWithheld[i] && row.ReportsManagerActive.HasValue;
                var newlySeatSplit = seatSplitWithheld[i] && row.ReportsManagerActiveLicensed.HasValue;
                if (!newlyManagers && !newlyComparison && !newlySeatSplit)
                {
                    floored.Add(row);
                    continue;
                }

                changed = true;
                var copy = row.CloneRow();
                if (newlyManagers)
                {
                    copy.ManagersWithheld = SmallGroupWithheldReasons.RevealsSmallerGroup;
                    copy.ManagersActive = null;
                    copy.ManagersActivePct = null;
                }

                if (newlyManagers || newlyComparison)
                {
                    copy.ComparisonWithheld = SmallGroupWithheldReasons.RevealsSmallerGroup;
                    copy.ReportsManagerActive = null;
                    copy.ReportsActiveRatePctManagerActive = null;
                    copy.ReportsHabitRatePctManagerActive = null;
                    copy.ReportsManagerInactive = null;
                    copy.ReportsActiveRatePctManagerInactive = null;
                    copy.ReportsHabitRatePctManagerInactive = null;
                }

                copy.SeatSplitWithheld = SmallGroupWithheldReasons.RevealsSmallerGroup;
                copy.ReportsManagerActiveLicensed = null;
                copy.ReportsActiveRatePctManagerActiveLicensed = null;
                copy.ReportsHabitRatePctManagerActiveLicensed = null;
                copy.ReportsManagerActiveUnlicensed = null;
                copy.ReportsActiveRatePctManagerActiveUnlicensed = null;
                copy.ReportsHabitRatePctManagerActiveUnlicensed = null;
                copy.ReportsManagerInactiveLicensed = null;
                copy.ReportsActiveRatePctManagerInactiveLicensed = null;
                copy.ReportsHabitRatePctManagerInactiveLicensed = null;
                copy.ReportsManagerInactiveUnlicensed = null;
                copy.ReportsActiveRatePctManagerInactiveUnlicensed = null;
                copy.ReportsHabitRatePctManagerInactiveUnlicensed = null;
                floored.Add(copy);
            }

            return changed || count != listed.Count ? floored : listed;
        }

        /// <summary>
        /// One step's groups: each answers to the whole population's group of the same managers. Withholding a department for
        /// one group can only add people to another's difference, so one pass per group, repeated until none adds a row.
        /// </summary>
        private static void ProtectGroups(
            List<ManagerModellingSegmentRow> rows,
            CopilotAdoptionSummary summary,
            bool[] withheld,
            int minPeople,
            bool groupsKnown,
            params Func<ManagerGroupSizes, int>[] groups)
        {
            if (!groupsKnown)
            {
                // Without the group sizes nothing can say which departments, or how many managers no row lists, a total
                // would give back: withhold the step for every department.
                for (var i = 0; i < withheld.Length; i++) withheld[i] = true;
                return;
            }

            bool added;
            do
            {
                added = false;
                foreach (var group in groups)
                {
                    var before = withheld.Count(w => w);
                    Protect(rows.Select(r => group(r.Groups)).ToList(), group(summary.ManagerGroups), withheld, minPeople);
                    added |= withheld.Count(w => w) > before;
                }
            } while (added);
        }

        /// <summary>
        /// <see cref="SmallPopulations.WithholdOverlappingComplement"/> over the rows and what no row lists: the whole
        /// population's group less the rows' (never below none), which the reader can work out the same way and which is
        /// never shown on its own.
        /// </summary>
        private static void Protect(IReadOnlyList<int> people, int whole, bool[] withheld, int minPeople)
        {
            var unlisted = Math.Max(0, whole - people.Sum());
            var withRemainder = people.Concat(new[] { unlisted }).ToList();
            var flags = withheld.Concat(new[] { true }).ToArray();

            SmallPopulations.WithholdOverlappingComplement(withRemainder, flags, minPeople);
            Array.Copy(flags, withheld, withheld.Length);
        }

        #endregion

        #region Shared

        /// <summary>
        /// Complementary withholding over rows that split one population between them, each person in one row, beside a
        /// total of them all: while the total less the rows shown - the withheld rows together with the people no row lists -
        /// is one to four people, the shown row with the fewest people (more than none; the last of them on a tie) is
        /// withheld too. When no such row is left, the total is the population's own figure.
        /// </summary>
        /// <param name="people">For each row, the people behind its figures.</param>
        /// <param name="withheld">For each row, whether it is withheld; a row withheld to protect another is set here.</param>
        /// <param name="total">The total published beside the rows: the people of every row and of none.</param>
        internal static void WithholdWhatATotalWouldGiveBack(IReadOnlyList<int> people, bool[] withheld, int total, int minPeople)
        {
            while (true)
            {
                var shown = 0;
                for (var i = 0; i < people.Count; i++)
                    if (!withheld[i]) shown += Math.Max(0, people[i]);
                if (!SmallPopulations.IsSmall(total - shown, minPeople)) return;

                var next = -1;
                for (var i = 0; i < people.Count; i++)
                    if (!withheld[i] && people[i] > 0 && (next < 0 || people[i] <= people[next])) next = i;
                if (next < 0) return;
                withheld[next] = true;
            }
        }

        #endregion
    }
}
