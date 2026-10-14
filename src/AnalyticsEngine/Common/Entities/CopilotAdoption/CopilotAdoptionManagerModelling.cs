using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Do people managers use Copilot themselves, and how do their direct reports compare (#641)? The
    /// figures for one population: the whole analysis, or one department.
    /// </summary>
    /// <remarks>
    /// <para><b>Definitions.</b> A <i>direct report</i> is a licensed user this analysis scored whose
    /// account is enabled and whose recorded manager is someone else with an enabled account. A
    /// <i>people manager</i> is anyone who directly manages at least one such report, whether or not they
    /// hold a Copilot seat themselves. Disabled accounts are left out on both sides, as the issue's
    /// prototype does: a departed manager whose reports have not been re-parented yet is not a manager
    /// who chose not to use Copilot. A manager is <i>active</i> with any Copilot use in the period,
    /// licensed or through Copilot Chat without a seat; <i>not active</i> when the data shows none; and
    /// <i>unknown</i> when it cannot say - see <see cref="ManagerCopilotStatus.Unknown"/>. A report is
    /// active and habitual by the same rules as the headline adoption and habit rates (a band above
    /// Dormant; Established or Champion), so the split rates can be read against those.</para>
    /// <para><b>Aggregates only.</b> No manager is named, and nothing here is attached to a person's row
    /// or to the accountability roll-up. A group too small to hide an individual in is published blank:
    /// every figure that depends on a manager's own use needs at least
    /// <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/> people managers whose use is known, and
    /// each rate needs at least that many reports behind it.</para>
    /// <para><b>An association, not a cause.</b> Teams whose manager uses Copilot may differ in function,
    /// seniority or seat coverage. The split by whether the manager holds a seat exists because a manager
    /// with a seat is more likely both to use Copilot and to lead a team that was given seats, which on
    /// its own would widen the gap.</para>
    /// <para>A nullable figure is null when it was suppressed or there was nothing to divide by - never 0,
    /// because on Snapshot facts a blank reads as "not reported" and a zero as a measured result.</para>
    /// </remarks>
    public class ManagerModellingFigures
    {
        /// <summary>
        /// Direct reports in the population: the licensed users every report figure below divides.
        /// </summary>
        [JsonProperty("reportsWithManager")]
        public int ReportsWithManager { get; set; }

        /// <summary>People managers whose own Copilot use in the period is known. The denominator of <see cref="ManagersActivePct"/>.</summary>
        [JsonProperty("managersStatusKnown")]
        public int ManagersStatusKnown { get; set; }

        /// <summary>
        /// People managers whose own use the loaded data cannot determine. Kept apart from "not active"
        /// and out of every rate, rather than counted as not using Copilot.
        /// </summary>
        [JsonProperty("managersStatusUnknown")]
        public int ManagersStatusUnknown { get; set; }

        /// <summary>People managers who used Copilot in the period. Null when suppressed.</summary>
        [JsonProperty("managersActive")]
        public int? ManagersActive { get; set; }

        /// <summary>
        /// The share of people managers whose use is known who used Copilot in the period, licensed or
        /// not. Null when fewer than the minimum group size of managers is known.
        /// </summary>
        [JsonProperty("managersActivePct")]
        public double? ManagersActivePct { get; set; }

        /// <summary>Direct reports whose manager used Copilot. Null when suppressed.</summary>
        [JsonProperty("reportsManagerActive")]
        public int? ReportsManagerActive { get; set; }

        /// <summary>Share of those reports active in the period.</summary>
        [JsonProperty("reportsActiveRatePctManagerActive")]
        public double? ReportsActiveRatePctManagerActive { get; set; }

        /// <summary>Share of those reports with a habit (Established or Champion).</summary>
        [JsonProperty("reportsHabitRatePctManagerActive")]
        public double? ReportsHabitRatePctManagerActive { get; set; }

        /// <summary>Direct reports whose manager is known not to have used Copilot. Null when suppressed.</summary>
        [JsonProperty("reportsManagerInactive")]
        public int? ReportsManagerInactive { get; set; }

        [JsonProperty("reportsActiveRatePctManagerInactive")]
        public double? ReportsActiveRatePctManagerInactive { get; set; }

        [JsonProperty("reportsHabitRatePctManagerInactive")]
        public double? ReportsHabitRatePctManagerInactive { get; set; }

        /// <summary>Direct reports whose manager's own use is unknown, and who are therefore in neither split.</summary>
        [JsonProperty("reportsManagerUnknown")]
        public int ReportsManagerUnknown { get; set; }

        #region Split by whether the manager holds a Copilot seat

        [JsonProperty("reportsManagerActiveLicensed")]
        public int? ReportsManagerActiveLicensed { get; set; }

        [JsonProperty("reportsActiveRatePctManagerActiveLicensed")]
        public double? ReportsActiveRatePctManagerActiveLicensed { get; set; }

        [JsonProperty("reportsHabitRatePctManagerActiveLicensed")]
        public double? ReportsHabitRatePctManagerActiveLicensed { get; set; }

        [JsonProperty("reportsManagerActiveUnlicensed")]
        public int? ReportsManagerActiveUnlicensed { get; set; }

        [JsonProperty("reportsActiveRatePctManagerActiveUnlicensed")]
        public double? ReportsActiveRatePctManagerActiveUnlicensed { get; set; }

        [JsonProperty("reportsHabitRatePctManagerActiveUnlicensed")]
        public double? ReportsHabitRatePctManagerActiveUnlicensed { get; set; }

        [JsonProperty("reportsManagerInactiveLicensed")]
        public int? ReportsManagerInactiveLicensed { get; set; }

        [JsonProperty("reportsActiveRatePctManagerInactiveLicensed")]
        public double? ReportsActiveRatePctManagerInactiveLicensed { get; set; }

        [JsonProperty("reportsHabitRatePctManagerInactiveLicensed")]
        public double? ReportsHabitRatePctManagerInactiveLicensed { get; set; }

        [JsonProperty("reportsManagerInactiveUnlicensed")]
        public int? ReportsManagerInactiveUnlicensed { get; set; }

        [JsonProperty("reportsActiveRatePctManagerInactiveUnlicensed")]
        public double? ReportsActiveRatePctManagerInactiveUnlicensed { get; set; }

        [JsonProperty("reportsHabitRatePctManagerInactiveUnlicensed")]
        public double? ReportsHabitRatePctManagerInactiveUnlicensed { get; set; }

        #endregion

        /// <summary>
        /// Writes these figures onto the summary's top-level properties of the same names, where Snapshot
        /// facts and the API read them.
        /// </summary>
        public void CopyTo(CopilotAdoptionSummary summary)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));

            summary.ReportsWithManager = ReportsWithManager;
            summary.ManagersStatusKnown = ManagersStatusKnown;
            summary.ManagersStatusUnknown = ManagersStatusUnknown;
            summary.ManagersActive = ManagersActive;
            summary.ManagersActivePct = ManagersActivePct;
            summary.ReportsManagerActive = ReportsManagerActive;
            summary.ReportsActiveRatePctManagerActive = ReportsActiveRatePctManagerActive;
            summary.ReportsHabitRatePctManagerActive = ReportsHabitRatePctManagerActive;
            summary.ReportsManagerInactive = ReportsManagerInactive;
            summary.ReportsActiveRatePctManagerInactive = ReportsActiveRatePctManagerInactive;
            summary.ReportsHabitRatePctManagerInactive = ReportsHabitRatePctManagerInactive;
            summary.ReportsManagerUnknown = ReportsManagerUnknown;
            summary.ReportsManagerActiveLicensed = ReportsManagerActiveLicensed;
            summary.ReportsActiveRatePctManagerActiveLicensed = ReportsActiveRatePctManagerActiveLicensed;
            summary.ReportsHabitRatePctManagerActiveLicensed = ReportsHabitRatePctManagerActiveLicensed;
            summary.ReportsManagerActiveUnlicensed = ReportsManagerActiveUnlicensed;
            summary.ReportsActiveRatePctManagerActiveUnlicensed = ReportsActiveRatePctManagerActiveUnlicensed;
            summary.ReportsHabitRatePctManagerActiveUnlicensed = ReportsHabitRatePctManagerActiveUnlicensed;
            summary.ReportsManagerInactiveLicensed = ReportsManagerInactiveLicensed;
            summary.ReportsActiveRatePctManagerInactiveLicensed = ReportsActiveRatePctManagerInactiveLicensed;
            summary.ReportsHabitRatePctManagerInactiveLicensed = ReportsHabitRatePctManagerInactiveLicensed;
            summary.ReportsManagerInactiveUnlicensed = ReportsManagerInactiveUnlicensed;
            summary.ReportsActiveRatePctManagerInactiveUnlicensed = ReportsActiveRatePctManagerInactiveUnlicensed;
            summary.ReportsHabitRatePctManagerInactiveUnlicensed = ReportsHabitRatePctManagerInactiveUnlicensed;
        }

        /// <summary>
        /// The reverse of <see cref="CopyTo"/>: reads the summary's top-level figures, so the whole
        /// population can head a table of department rows.
        /// </summary>
        public void CopyFrom(CopilotAdoptionSummary summary)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));

            ReportsWithManager = summary.ReportsWithManager;
            ManagersStatusKnown = summary.ManagersStatusKnown;
            ManagersStatusUnknown = summary.ManagersStatusUnknown;
            ManagersActive = summary.ManagersActive;
            ManagersActivePct = summary.ManagersActivePct;
            ReportsManagerActive = summary.ReportsManagerActive;
            ReportsActiveRatePctManagerActive = summary.ReportsActiveRatePctManagerActive;
            ReportsHabitRatePctManagerActive = summary.ReportsHabitRatePctManagerActive;
            ReportsManagerInactive = summary.ReportsManagerInactive;
            ReportsActiveRatePctManagerInactive = summary.ReportsActiveRatePctManagerInactive;
            ReportsHabitRatePctManagerInactive = summary.ReportsHabitRatePctManagerInactive;
            ReportsManagerUnknown = summary.ReportsManagerUnknown;
            ReportsManagerActiveLicensed = summary.ReportsManagerActiveLicensed;
            ReportsActiveRatePctManagerActiveLicensed = summary.ReportsActiveRatePctManagerActiveLicensed;
            ReportsHabitRatePctManagerActiveLicensed = summary.ReportsHabitRatePctManagerActiveLicensed;
            ReportsManagerActiveUnlicensed = summary.ReportsManagerActiveUnlicensed;
            ReportsActiveRatePctManagerActiveUnlicensed = summary.ReportsActiveRatePctManagerActiveUnlicensed;
            ReportsHabitRatePctManagerActiveUnlicensed = summary.ReportsHabitRatePctManagerActiveUnlicensed;
            ReportsManagerInactiveLicensed = summary.ReportsManagerInactiveLicensed;
            ReportsActiveRatePctManagerInactiveLicensed = summary.ReportsActiveRatePctManagerInactiveLicensed;
            ReportsHabitRatePctManagerInactiveLicensed = summary.ReportsHabitRatePctManagerInactiveLicensed;
            ReportsManagerInactiveUnlicensed = summary.ReportsManagerInactiveUnlicensed;
            ReportsActiveRatePctManagerInactiveUnlicensed = summary.ReportsActiveRatePctManagerInactiveUnlicensed;
            ReportsHabitRatePctManagerInactiveUnlicensed = summary.ReportsHabitRatePctManagerInactiveUnlicensed;
        }
    }

    /// <summary>
    /// The manager-modelling figures for one department, shown as columns beside the department's
    /// adoption row. The department is the REPORT's own: a manager who leads people in two departments
    /// is counted in both.
    /// </summary>
    public class ManagerModellingSegmentRow : ManagerModellingFigures
    {
        /// <summary>The department name, as imported. Tenant data: rendered verbatim and never translated.</summary>
        [JsonProperty("segment")]
        public string Segment { get; set; }

        /// <summary>Licensed users analysed in the department, eligible as a report or not - the seat count the department row shows.</summary>
        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }
    }

    /// <summary>A people manager's own Copilot use in the period, as far as the loaded data can tell.</summary>
    internal enum ManagerCopilotStatus
    {
        /// <summary>Used Copilot in the period, with a seat or through Copilot Chat without one.</summary>
        Active,

        /// <summary>The data shows no Copilot use in the period.</summary>
        NotActive,

        /// <summary>
        /// The data cannot say. The manager holds a seat but is beyond the licensed-user row cap; or holds
        /// no seat and the unlicensed Copilot list was truncated, failed or never ran (no audit import);
        /// or is an external guest, whom the unlicensed list leaves out.
        /// </summary>
        Unknown,

        /// <summary>The manager's account is disabled. Left out, together with their reports.</summary>
        Disabled,
    }

    /// <summary>What the analysis knows about one people manager.</summary>
    internal struct ManagerFacts
    {
        public ManagerFacts(int managerId, ManagerCopilotStatus status, bool holdsSeat)
        {
            ManagerId = managerId;
            Status = status;
            HoldsSeat = holdsSeat;
        }

        public int ManagerId { get; }

        public ManagerCopilotStatus Status { get; }

        /// <summary>Holds a Copilot seat in the period. False for a manager known only as an unlicensed user, or not known at all.</summary>
        public bool HoldsSeat { get; }
    }

    /// <summary>
    /// Every manager of a scored licensed user, with their own Copilot status, resolved once from the
    /// whole tenant's rows.
    /// </summary>
    /// <remarks>
    /// Built once per analysis and then only read. A narrowed view inherits the tenant analysis's
    /// directory (<see cref="CopilotAdoptionScopeFilter.FilterRows"/>), because a report inside the slice
    /// may well have a manager outside it, and that manager's use is no less known for it. Holds ids and
    /// statuses only - no row, and nothing that names anyone.
    /// </remarks>
    internal sealed class CopilotAdoptionManagerDirectory
    {
        private readonly Dictionary<int, ManagerFacts> _managers;

        internal CopilotAdoptionManagerDirectory(Dictionary<int, ManagerFacts> managers, bool inherited)
        {
            _managers = managers ?? new Dictionary<int, ManagerFacts>();
            Inherited = inherited;
        }

        /// <summary>True on a narrowed analysis, whose directory was resolved against the tenant it was narrowed from.</summary>
        internal bool Inherited { get; }

        internal int Count => _managers.Count;

        internal CopilotAdoptionManagerDirectory AsInherited()
        {
            return Inherited ? this : new CopilotAdoptionManagerDirectory(_managers, inherited: true);
        }

        internal bool TryGetManager(int managerId, out ManagerFacts facts)
        {
            return _managers.TryGetValue(managerId, out facts);
        }

        /// <summary>
        /// The manager of <paramref name="user"/>, when the user counts as a direct report: an enabled
        /// account with a recorded manager who is someone else and is not disabled.
        /// </summary>
        /// <remarks>
        /// A user recorded as their own manager is not their own report - Entra allows the loop, and a
        /// directory import can produce one. Only the DIRECT manager is ever read, so a longer cycle
        /// (A manages B, B manages A) is just two report lines and cannot recurse.
        /// </remarks>
        internal bool TryGetManagerOf(LicensedUserAdoptionRow user, out ManagerFacts manager)
        {
            manager = default(ManagerFacts);
            if (user == null || user.AccountEnabled == false) return false;
            if (!user.ManagerUserId.HasValue || user.ManagerUserId.Value == user.UserId) return false;

            if (!_managers.TryGetValue(user.ManagerUserId.Value, out manager))
            {
                // Only possible when the directory was built from other rows. The data cannot say.
                if (user.ManagerAccountEnabled == false) return false;
                manager = new ManagerFacts(user.ManagerUserId.Value, ManagerCopilotStatus.Unknown, holdsSeat: false);
            }

            return manager.Status != ManagerCopilotStatus.Disabled;
        }
    }

    /// <summary>
    /// Builds the manager-modelling figures (#641) from rows the analysis already holds.
    /// </summary>
    /// <remarks>
    /// <para><b>Cost at 200,000 users.</b> No SQL of its own and no new scan of <c>copilot_chats</c>: the
    /// licensed-user query carries each user's <c>manager_id</c> and the manager's
    /// <c>account_enabled</c> from a join it already made, and the unlicensed Copilot list already carries
    /// each user's id. From those, one dictionary build over the licensed rows, one hash set over the
    /// unlicensed rows, and one pass that resolves each report's manager with a dictionary lookup - O(n)
    /// in time, and O(number of managers) in retained memory.</para>
    /// <para>Status and suppression rules are on <see cref="ManagerModellingFigures"/>.</para>
    /// </remarks>
    internal static class CopilotAdoptionManagerModelling
    {
        /// <summary>
        /// Computes the figures for the analysis's licensed users, onto the summary's top-level properties
        /// and, for each department in <see cref="CopilotAdoptionSummary.AdoptionByDepartment"/>, onto
        /// <see cref="CopilotAdoptionSummary.ManagerModellingByDepartment"/>.
        /// </summary>
        /// <param name="analysis">The analysis being finalised - the tenant one, or a narrowed one.</param>
        /// <param name="emptyDepartmentLabel">The label the department breakdown gives users with no department, so the two group identically.</param>
        /// <param name="minGroup">The smallest group any figure may describe: <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/>.</param>
        internal static void Apply(CopilotAdoptionAnalysis analysis, string emptyDepartmentLabel, int minGroup)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var summary = analysis.Summary;
            var users = analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>();
            var directory = DirectoryFor(analysis);
            var segments = summary.AdoptionByDepartment ?? new List<AdoptionSegmentRow>();

            var whole = new Accumulator();
            var byDepartment = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
            foreach (var segment in segments)
            {
                if (segment?.Segment != null && !byDepartment.ContainsKey(segment.Segment))
                {
                    byDepartment.Add(segment.Segment, new Accumulator());
                }
            }

            foreach (var user in users)
            {
                if (user == null || !directory.TryGetManagerOf(user, out var manager)) continue;

                whole.Add(user, manager);

                if (byDepartment.Count > 0
                    && byDepartment.TryGetValue(SegmentKey(user.Department, emptyDepartmentLabel), out var department))
                {
                    department.Add(user, manager);
                }
            }

            var figures = new ManagerModellingFigures();
            whole.WriteTo(figures, minGroup);
            figures.CopyTo(summary);

            // The same departments, in the same order, as the adoption-by-department rows these figures
            // are shown beside - so they inherit that breakdown's minimum seat count and its cap.
            summary.ManagerModellingByDepartment = segments
                .Where(s => s?.Segment != null)
                .Select(s =>
                {
                    var row = new ManagerModellingSegmentRow { Segment = s.Segment, LicensedUsers = s.LicensedUsers };
                    byDepartment[s.Segment].WriteTo(row, minGroup);
                    return row;
                })
                .ToList();
        }

        /// <summary>
        /// The directory a finalise pass resolves managers against: the one a narrowed analysis inherited
        /// from its tenant analysis, or a fresh one from this analysis's own rows - which on the tenant
        /// analysis is the whole tenant. Stored, so the views narrowed from it can inherit it.
        /// </summary>
        internal static CopilotAdoptionManagerDirectory DirectoryFor(CopilotAdoptionAnalysis analysis)
        {
            var existing = analysis.ManagerDirectory;
            if (existing != null && existing.Inherited) return existing;

            var built = BuildDirectory(analysis);
            analysis.ManagerDirectory = built;
            return built;
        }

        /// <summary>
        /// The directory a view narrowed from <paramref name="analysis"/> should resolve managers against:
        /// the tenant analysis's own, built now if it has never been finalised.
        /// </summary>
        internal static CopilotAdoptionManagerDirectory InheritedDirectory(CopilotAdoptionAnalysis analysis)
        {
            return (analysis.ManagerDirectory ?? BuildDirectory(analysis)).AsInherited();
        }

        /// <summary>Resolves every manager of a scored licensed user. One pass; see the class remarks for the cost.</summary>
        internal static CopilotAdoptionManagerDirectory BuildDirectory(CopilotAdoptionAnalysis analysis)
        {
            var licensed = analysis?.LicensedUsers ?? new List<LicensedUserAdoptionRow>();

            var seatHolders = new Dictionary<int, LicensedUserAdoptionRow>(licensed.Count);
            foreach (var row in licensed)
            {
                if (row != null) seatHolders[row.UserId] = row;
            }

            var unlicensedActive = new HashSet<int>();
            foreach (var row in analysis?.UnlicensedUsers ?? new List<UnlicensedUsageQueryRow>())
            {
                if (row != null) unlicensedActive.Add(row.UserId);
            }

            // Absence from the unlicensed list means "no Copilot use" only when the list is the whole
            // population: the query ran, completed, and did not stop at MaxUnlicensedUsersScored.
            var unlicensedComplete = analysis != null
                && analysis.UnlicensedUsageAssessed
                && analysis.Summary?.Unlicensed?.Truncated != true;

            // Seat holders the licensed-user query never returned, because it stopped at its row cap.
            var notScored = analysis?.LicensedUsersNotAnalysed == null
                ? new HashSet<int>()
                : new HashSet<int>(analysis.LicensedUsersNotAnalysed);
            var seatHoldersKnown = analysis == null || !analysis.LicensedUsersCapped || analysis.LicensedUsersNotAnalysed != null;

            var managers = new Dictionary<int, ManagerFacts>();
            foreach (var report in licensed)
            {
                if (report?.ManagerUserId == null) continue;

                var managerId = report.ManagerUserId.Value;
                if (managerId == report.UserId || managers.ContainsKey(managerId)) continue;

                managers.Add(managerId, Resolve(managerId, report, seatHolders, unlicensedActive, unlicensedComplete, notScored, seatHoldersKnown));
            }

            return new CopilotAdoptionManagerDirectory(managers, inherited: false);
        }

        private static ManagerFacts Resolve(
            int managerId,
            LicensedUserAdoptionRow report,
            Dictionary<int, LicensedUserAdoptionRow> seatHolders,
            HashSet<int> unlicensedActive,
            bool unlicensedComplete,
            HashSet<int> notScored,
            bool seatHoldersKnown)
        {
            // A manager who holds a seat and was scored: their own row is the evidence, scored by exactly
            // the rules the adoption rate uses.
            if (seatHolders.TryGetValue(managerId, out var own))
            {
                if (own.AccountEnabled == false) return new ManagerFacts(managerId, ManagerCopilotStatus.Disabled, holdsSeat: true);

                return new ManagerFacts(
                    managerId,
                    own.Band > AdoptionBand.Dormant ? ManagerCopilotStatus.Active : ManagerCopilotStatus.NotActive,
                    holdsSeat: true);
            }

            if (report.ManagerAccountEnabled == false) return new ManagerFacts(managerId, ManagerCopilotStatus.Disabled, holdsSeat: false);

            // A seat holder past the licensed-user row cap: a seat, but no scored row to judge their use by.
            if (notScored.Contains(managerId)) return new ManagerFacts(managerId, ManagerCopilotStatus.Unknown, holdsSeat: true);

            // Presence on the unlicensed list is proof of use whether or not the list is complete. The
            // unlicensed query anti-joins every seat holder, so this manager holds no seat.
            if (unlicensedActive.Contains(managerId)) return new ManagerFacts(managerId, ManagerCopilotStatus.Active, holdsSeat: false);

            // From here on the only evidence is an ABSENCE, which proves nothing unless both lists are
            // whole - and the unlicensed list leaves external guests out by design.
            if (!seatHoldersKnown || !unlicensedComplete || CopilotAdoptionEmailDomain.IsExternalGuest(report.ManagerUserPrincipalName))
            {
                return new ManagerFacts(managerId, ManagerCopilotStatus.Unknown, holdsSeat: false);
            }

            return new ManagerFacts(managerId, ManagerCopilotStatus.NotActive, holdsSeat: false);
        }

        /// <summary>The department key, grouped exactly as the department breakdown groups it.</summary>
        private static string SegmentKey(string value, string emptyLabel)
        {
            return string.IsNullOrWhiteSpace(value) ? emptyLabel : value.Trim();
        }

        /// <summary>
        /// A rate, or null when fewer than <paramref name="minGroup"/> people are behind it - a smaller
        /// group would describe a handful of identifiable reports.
        /// </summary>
        private static double? Rate(int part, int total, int minGroup)
        {
            return total >= minGroup ? (double?)CopilotAdoptionScoring.Percentage(part, total) : null;
        }

        /// <summary>The running counts for one population.</summary>
        private sealed class Accumulator
        {
            private const int ActiveManager = 0;
            private const int InactiveManager = 1;
            private const int Licensed = 0;
            private const int Unlicensed = 1;

            private readonly HashSet<int> _managers = new HashSet<int>();
            private readonly int[,] _reports = new int[2, 2];
            private readonly int[,] _activeReports = new int[2, 2];
            private readonly int[,] _habitualReports = new int[2, 2];
            private int _managersActive;
            private int _managersInactive;
            private int _managersUnknown;
            private int _reportsWithManager;
            private int _reportsManagerUnknown;

            public void Add(LicensedUserAdoptionRow report, ManagerFacts manager)
            {
                _reportsWithManager++;

                if (_managers.Add(manager.ManagerId))
                {
                    switch (manager.Status)
                    {
                        case ManagerCopilotStatus.Active: _managersActive++; break;
                        case ManagerCopilotStatus.NotActive: _managersInactive++; break;
                        default: _managersUnknown++; break;
                    }
                }

                if (manager.Status != ManagerCopilotStatus.Active && manager.Status != ManagerCopilotStatus.NotActive)
                {
                    _reportsManagerUnknown++;
                    return;
                }

                var status = manager.Status == ManagerCopilotStatus.Active ? ActiveManager : InactiveManager;
                var seat = manager.HoldsSeat ? Licensed : Unlicensed;

                _reports[status, seat]++;
                if (report.Band > AdoptionBand.Dormant) _activeReports[status, seat]++;
                if (CopilotAdoptionScoring.IsHabitual(report.Band)) _habitualReports[status, seat]++;
            }

            public void WriteTo(ManagerModellingFigures figures, int minGroup)
            {
                var min = Math.Max(1, minGroup);
                var known = _managersActive + _managersInactive;

                figures.ReportsWithManager = _reportsWithManager;
                figures.ManagersStatusKnown = known;
                figures.ManagersStatusUnknown = _managersUnknown;
                figures.ReportsManagerUnknown = _reportsManagerUnknown;

                // Fewer known managers than the minimum group: every figure below would say something
                // about a handful of identifiable managers' own use, so none of it is published.
                if (known < min) return;

                figures.ManagersActive = _managersActive;
                figures.ManagersActivePct = CopilotAdoptionScoring.Percentage(_managersActive, known);

                figures.ReportsManagerActive = Sum(_reports, ActiveManager);
                figures.ReportsActiveRatePctManagerActive = Rate(Sum(_activeReports, ActiveManager), Sum(_reports, ActiveManager), min);
                figures.ReportsHabitRatePctManagerActive = Rate(Sum(_habitualReports, ActiveManager), Sum(_reports, ActiveManager), min);

                figures.ReportsManagerInactive = Sum(_reports, InactiveManager);
                figures.ReportsActiveRatePctManagerInactive = Rate(Sum(_activeReports, InactiveManager), Sum(_reports, InactiveManager), min);
                figures.ReportsHabitRatePctManagerInactive = Rate(Sum(_habitualReports, InactiveManager), Sum(_reports, InactiveManager), min);

                figures.ReportsManagerActiveLicensed = _reports[ActiveManager, Licensed];
                figures.ReportsActiveRatePctManagerActiveLicensed = Rate(_activeReports[ActiveManager, Licensed], _reports[ActiveManager, Licensed], min);
                figures.ReportsHabitRatePctManagerActiveLicensed = Rate(_habitualReports[ActiveManager, Licensed], _reports[ActiveManager, Licensed], min);

                figures.ReportsManagerActiveUnlicensed = _reports[ActiveManager, Unlicensed];
                figures.ReportsActiveRatePctManagerActiveUnlicensed = Rate(_activeReports[ActiveManager, Unlicensed], _reports[ActiveManager, Unlicensed], min);
                figures.ReportsHabitRatePctManagerActiveUnlicensed = Rate(_habitualReports[ActiveManager, Unlicensed], _reports[ActiveManager, Unlicensed], min);

                figures.ReportsManagerInactiveLicensed = _reports[InactiveManager, Licensed];
                figures.ReportsActiveRatePctManagerInactiveLicensed = Rate(_activeReports[InactiveManager, Licensed], _reports[InactiveManager, Licensed], min);
                figures.ReportsHabitRatePctManagerInactiveLicensed = Rate(_habitualReports[InactiveManager, Licensed], _reports[InactiveManager, Licensed], min);

                figures.ReportsManagerInactiveUnlicensed = _reports[InactiveManager, Unlicensed];
                figures.ReportsActiveRatePctManagerInactiveUnlicensed = Rate(_activeReports[InactiveManager, Unlicensed], _reports[InactiveManager, Unlicensed], min);
                figures.ReportsHabitRatePctManagerInactiveUnlicensed = Rate(_habitualReports[InactiveManager, Unlicensed], _reports[InactiveManager, Unlicensed], min);
            }

            private static int Sum(int[,] counts, int status)
            {
                return counts[status, Licensed] + counts[status, Unlicensed];
            }
        }
    }
}
