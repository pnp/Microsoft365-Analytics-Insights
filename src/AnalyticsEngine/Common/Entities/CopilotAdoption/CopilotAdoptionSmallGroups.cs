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
