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
    }
}
