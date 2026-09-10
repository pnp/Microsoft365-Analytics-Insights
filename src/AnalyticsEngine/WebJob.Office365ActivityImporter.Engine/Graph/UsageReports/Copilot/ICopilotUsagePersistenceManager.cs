using Common.Entities.Entities.UsageReports;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// What an upsert actually did. The <b>Unchanged</b> count is the point of this type: both Copilot
    /// usage upserts carry an "only write when a value actually moved" rule, because Graph gap-fills the
    /// most recent few days and re-importing an overlapping window is normal. Before issue #370 that rule
    /// was invisible to tests - a regression that rewrote every row on every cycle (up to 180 days x every
    /// app, or every licensed user) would have produced identical return values and gone unnoticed.
    /// </summary>
    public class CopilotUsageUpsertResult
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }

        /// <summary>Rows actually written to SQL. This is what the loaders return and log.</summary>
        public int Written => Inserted + Updated;
    }

    /// <summary>
    /// The outcome of mapping a report's user principal names onto user ids.
    /// </summary>
    public class CopilotUserIdResolution
    {
        public CopilotUserIdResolution(IReadOnlyDictionary<string, int> idsByUpn, int created, int skippedUnknownDomain)
        {
            IdsByUpn = idsByUpn;
            Created = created;
            SkippedUnknownDomain = skippedUnknownDomain;
        }

        /// <summary>Case-insensitive UPN to user id. Only contains identities that exist after this call.</summary>
        public IReadOnlyDictionary<string, int> IdsByUpn { get; }

        /// <summary>How many user records had to be created.</summary>
        public int Created { get; }

        /// <summary>
        /// How many report identities were skipped because their e-mail domain is not one this database
        /// already holds users for. That is the real boundary that stops a pseudonymised report populating
        /// the users table with junk.
        /// </summary>
        public int SkippedUnknownDomain { get; }
    }

    /// <summary>
    /// Write port for the Copilot usage reports (issue #370). The read side was already abstracted by
    /// <see cref="ICopilotReportSource"/>; this completes the seam, so the whole import - concealment
    /// decision, period keying, user resolution and both upserts - runs in a unit test with zero Graph and
    /// zero SQL Server.
    /// </summary>
    public interface ICopilotUsagePersistenceManager
    {
        /// <summary>
        /// Map report UPNs to user ids, creating the ones that are missing and whose domain is recognised.
        /// </summary>
        Task<CopilotUserIdResolution> ResolveUserIdsAsync(IEnumerable<string> userPrincipalNames);

        /// <summary>
        /// Upsert per-user rows. <paramref name="rows"/> must already carry a report period (see
        /// <c>CopilotUsageReportPolicy.ApplyPeriodKeys</c>); rows whose UPN is not in
        /// <paramref name="userIdsByUpn"/> are skipped.
        /// </summary>
        Task<CopilotUsageUpsertResult> UpsertUserDetailAsync(IReadOnlyList<CopilotUsageUserDetailRow> rows,
            IReadOnlyDictionary<string, int> userIdsByUpn, bool hasVersion2Data);

        /// <summary>Upsert tenant-aggregate user-count rows for one report type.</summary>
        Task<CopilotUsageUpsertResult> UpsertUserCountsAsync(IReadOnlyList<CopilotUserCountLog> rows, string reportType);

        /// <summary>Record the per-report diagnostic the Health page reads.</summary>
        Task RecordReportLoadAsync(CopilotUsageReportImportLog importLog);

        /// <summary>
        /// Record the diagnostic after a persistence failure. Implementations that use EF must do this on a
        /// FRESH context: the one that just failed a SaveChanges can be left holding entities in a broken
        /// state, so reusing it would lose the very diagnostic the operator needs.
        /// </summary>
        Task RecordReportLoadAfterFailureAsync(CopilotUsageReportImportLog importLog);
    }
}
