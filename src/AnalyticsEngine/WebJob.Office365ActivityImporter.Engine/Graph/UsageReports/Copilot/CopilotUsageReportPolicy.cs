using Common.Entities.Entities.UsageReports;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// What the importer should do with a parsed per-user Copilot usage report, given how many of its
    /// rows came back with a concealed (hashed) identity.
    /// </summary>
    public enum ConcealedIdentityOutcome
    {
        /// <summary>No concealed rows - import everything.</summary>
        ImportAll,

        /// <summary>Some rows concealed - skip those, import the rest, and warn.</summary>
        SkipConcealedRows,

        /// <summary>
        /// Every row concealed - do not import at all. Importing would create one placeholder user per
        /// licensed account.
        /// </summary>
        AbortImport,
    }

    /// <summary>
    /// The decision and the rows that survive it.
    /// </summary>
    public class ConcealedIdentityDecision
    {
        public ConcealedIdentityOutcome Outcome { get; set; }

        /// <summary>How many rows carried a concealed identity.</summary>
        public int ConcealedCount { get; set; }

        /// <summary>Total rows in the parsed report.</summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// The rows to import. Empty when <see cref="Outcome"/> is <see cref="ConcealedIdentityOutcome.AbortImport"/>.
        ///
        /// Typed as <see cref="List{T}"/>, not <c>IReadOnlyList</c>, and deliberately so. On the
        /// <see cref="ConcealedIdentityOutcome.ImportAll"/> path this IS the caller's list, and that
        /// aliasing is load-bearing: <c>SaveAsync</c> drops unkeyable rows with an in-place
        /// <c>RemoveAll</c>, and the importer's closing "parsed N row(s)" log reads the original list.
        /// Handing back a copy would both allocate a second 200k-element array on a normal tenant and
        /// silently change that operator-facing count.
        /// </summary>
        public List<CopilotUsageUserDetailRow> Importable { get; set; }
    }

    /// <summary>
    /// Pure policy for the Copilot per-user usage report - no Graph, no SQL, no logging. See issue #370.
    ///
    /// The concealment rule is the highest-stakes decision in this import and had no unit test. When a
    /// tenant enables "concealed user information", Graph still answers 200 OK with one row per licensed
    /// user, but replaces the UPN and display name with hashes. Feeding those through the usual
    /// get-or-create-user path would create one junk user per licensed account - 200,000 of them on a
    /// large tenant - permanently polluting the users table and every report built on it, and producing
    /// joins that are wrong rather than missing. So the report is not imported at all.
    /// </summary>
    public static class CopilotUsageReportPolicy
    {
        /// <summary>
        /// Decide what to do with a parsed report. Mirrors the importer exactly: all-concealed aborts,
        /// partially-concealed drops the concealed rows, none-concealed returns the caller's own list
        /// instance - see the remarks on <see cref="ConcealedIdentityDecision.Importable"/> for why that
        /// aliasing must be preserved rather than copied.
        ///
        /// An empty report is <see cref="ConcealedIdentityOutcome.ImportAll"/>, not
        /// <see cref="ConcealedIdentityOutcome.AbortImport"/> - "no rows" is not "every row concealed",
        /// and the importer has already handled the empty case separately by this point.
        /// </summary>
        public static ConcealedIdentityDecision EvaluateConcealment(List<CopilotUsageUserDetailRow> parsed)
        {
            var rows = parsed ?? new List<CopilotUsageUserDetailRow>();
            var concealedCount = rows.Count(r => r.IsIdentityConcealed);

            if (rows.Count > 0 && concealedCount == rows.Count)
            {
                return new ConcealedIdentityDecision
                {
                    Outcome = ConcealedIdentityOutcome.AbortImport,
                    ConcealedCount = concealedCount,
                    TotalCount = rows.Count,
                    Importable = new List<CopilotUsageUserDetailRow>(),
                };
            }

            if (concealedCount > 0)
            {
                return new ConcealedIdentityDecision
                {
                    Outcome = ConcealedIdentityOutcome.SkipConcealedRows,
                    ConcealedCount = concealedCount,
                    TotalCount = rows.Count,
                    Importable = rows.Where(r => !r.IsIdentityConcealed).ToList(),
                };
            }

            return new ConcealedIdentityDecision
            {
                Outcome = ConcealedIdentityOutcome.ImportAll,
                ConcealedCount = 0,
                TotalCount = rows.Count,
                Importable = rows,
            };
        }

        /// <summary>
        /// Stamp each row with the report period that forms part of its identity, and drop the rows that
        /// still have none. D7 and D28 describe the SAME user and date with different prompt counts,
        /// active-day counts and last-activity values, so they are different facts, not a conflict - a row
        /// that states no period, from a request that cannot supply one (period ALL), has no key at all and
        /// would otherwise be stored under the meaningless period 0.
        ///
        /// Filtered <b>in place</b>, deliberately. At ~200k licensed users a second full-size list is a
        /// pointless copy of the whole report, and the caller's closing "parsed N row(s)" log reads the
        /// original list. <c>RemoveAll</c> rather than a reverse loop of <c>RemoveAt</c>: each
        /// <c>RemoveAt</c> shifts every surviving element after it, so dropping a scattered subset of a
        /// 200k-row report costs O(N^2) element moves (~5 billion when half the rows are unkeyable);
        /// <c>RemoveAll</c> is a single O(N) compaction.
        /// </summary>
        /// <returns>How many rows were dropped for having no usable period.</returns>
        public static int ApplyPeriodKeys(List<CopilotUsageUserDetailRow> rows, int? requestedPeriodDays)
        {
            if (rows == null) return 0;

            return rows.RemoveAll(row =>
            {
                row.ReportPeriodDays = row.ReportPeriodDays ?? requestedPeriodDays;
                return !row.ReportPeriodDays.HasValue;
            });
        }

        /// <summary>
        /// Which of a report's identities may be created as new users.
        ///
        /// A new user is only ever created when its e-mail domain is one the database already holds users
        /// for. Syntax alone cannot prove an identity belongs to the tenant, so this - not a UPN shape check
        /// - is the real boundary that stops a pseudonymised report populating the users table with junk. An
        /// identity on an unrecognised domain is skipped and counted, not invented.
        ///
        /// An empty <paramref name="knownDomains"/> means there is nothing to validate against yet (a
        /// brand-new install where the user-metadata import has not run), so creating is the only way to
        /// make progress. Microsoft's concealed identities are bare hashes with no domain at all and are
        /// rejected long before this point.
        /// </summary>
        public static CopilotNewUserPlan PlanNewUsers(IEnumerable<string> reportUpns,
            IReadOnlyDictionary<string, int> existingIdsByUpn, ISet<string> knownDomains)
        {
            var plan = new CopilotNewUserPlan();
            if (reportUpns == null) return plan;

            foreach (var upn in reportUpns.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (existingIdsByUpn != null && existingIdsByUpn.ContainsKey(upn)) continue;

                if (knownDomains != null && knownDomains.Count > 0 && !knownDomains.Contains(DomainOf(upn) ?? string.Empty))
                {
                    plan.SkippedUnknownDomain++;
                    continue;
                }

                plan.ToCreate.Add(upn);
            }

            return plan;
        }

        /// <summary>The domain part of a UPN, or null when there isn't a usable one.</summary>
        public static string DomainOf(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn)) return null;
            var at = upn.LastIndexOf('@');
            if (at <= 0 || at == upn.Length - 1) return null;
            return upn.Substring(at + 1);
        }

        /// <summary>
        /// Has anything worth writing actually moved on an aggregate user-count row?
        ///
        /// Graph gap-fills the most recent ~3 days, so re-importing an overlapping window is normal and
        /// usually changes nothing. The refresh date deliberately does <b>not</b> count as a change on its
        /// own: it advances every day, so including it would rewrite every day in the window daily (up to
        /// 180 days x every app) purely to restamp provenance. Report-level freshness lives in
        /// <c>copilot_usage_report_import_log</c> instead.
        ///
        /// Lives here rather than privately in the SQL adapter so the in-memory test double asserts the
        /// production rule instead of a second copy of it that could silently drift.
        /// </summary>
        public static bool UserCountValueChanged(CopilotUserCountLog stored, CopilotUserCountLog incoming)
        {
            return stored.EnabledUsers != incoming.EnabledUsers
                || stored.ActiveUsers != incoming.ActiveUsers
                || stored.PromptsSubmitted != incoming.PromptsSubmitted
                || stored.AveragePromptsSubmitted != incoming.AveragePromptsSubmitted;
        }
    }

    /// <summary>Which report identities may be created, and how many were rejected.</summary>
    public class CopilotNewUserPlan
    {
        public List<string> ToCreate { get; } = new List<string>();
        public int SkippedUnknownDomain { get; set; }
    }
}
