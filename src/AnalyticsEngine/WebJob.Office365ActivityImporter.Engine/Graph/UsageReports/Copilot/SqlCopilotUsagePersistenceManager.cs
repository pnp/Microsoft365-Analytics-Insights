using Common.Entities;
using Common.Entities.Entities.UsageReports;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// EF6 implementation of <see cref="ICopilotUsagePersistenceManager"/>. Every query and every batching
    /// decision here was moved verbatim out of <c>CopilotUsageUserDetailLoader</c> and
    /// <c>CopilotUserCountReportLoader</c> by issue #370 - including the
    /// <c>AutoDetectChangesEnabled = false</c> blocks, the change-tracker detaching, and the bounded
    /// min/max-date collision query.
    /// </summary>
    public class SqlCopilotUsagePersistenceManager : ICopilotUsagePersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;
        private readonly IAnalyticsDbContextFactory _contextFactory;

        /// <summary>
        /// Rows per SaveChanges. Small enough that EF6 never builds hundreds of thousands of command trees
        /// at once (which OutOfMemoryExceptions on a small App Service) and that any IN clause stays well
        /// under SQL Server's parameter limit.
        /// </summary>
        public int SaveBatchSize { get; set; } = 1000;

        public SqlCopilotUsagePersistenceManager(AnalyticsEntitiesContext db, ILogger logger,
            IAnalyticsDbContextFactory contextFactory = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
        }

        #region User id resolution

        /// <inheritdoc />
        public async Task<CopilotUserIdResolution> ResolveUserIdsAsync(IEnumerable<string> userPrincipalNames)
        {
            // Comparisons are case-insensitive in memory rather than via SQL LOWER(), which would make the
            // predicate non-SARGable and force a table scan.
            var existingUsers = await _db.users.AsNoTracking()
                .Select(u => new { u.ID, u.UserPrincipalName })
                .ToListAsync();

            var idsByUpn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var knownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in existingUsers)
            {
                if (string.IsNullOrWhiteSpace(user.UserPrincipalName)) continue;
                idsByUpn[user.UserPrincipalName] = user.ID;

                var domain = CopilotUsageReportPolicy.DomainOf(user.UserPrincipalName);
                if (domain != null) knownDomains.Add(domain);
            }

            var plan = CopilotUsageReportPolicy.PlanNewUsers(userPrincipalNames, idsByUpn, knownDomains);

            if (plan.SkippedUnknownDomain > 0)
            {
                _logger.LogWarning($"Copilot per-user report: skipped {plan.SkippedUnknownDomain} identity(ies) on an email domain this database has no users for. " +
                    "They were not created, because an unrecognised domain is how a pseudonymised report would look. " +
                    "If these are genuine users, run the Graph user metadata import so they are known first.");
            }

            if (plan.ToCreate.Count == 0)
            {
                return new CopilotUserIdResolution(idsByUpn, 0, plan.SkippedUnknownDomain);
            }

            _logger.LogInformation($"Copilot per-user report: creating {plan.ToCreate.Count} user record(s) not yet known to the database.");

            var autoDetectWasEnabled = _db.Configuration.AutoDetectChangesEnabled;
            _db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                var newUsers = new List<Common.Entities.User>(Math.Min(plan.ToCreate.Count, SaveBatchSize));
                for (var i = 0; i < plan.ToCreate.Count; i++)
                {
                    // Existing loaders store UPNs lower-cased; matching that avoids creating a second user
                    // record that differs only by case.
                    var user = new Common.Entities.User { UserPrincipalName = plan.ToCreate[i].ToLowerInvariant() };
                    _db.users.Add(user);
                    newUsers.Add(user);

                    if (newUsers.Count >= SaveBatchSize)
                    {
                        await FlushNewUsers(_db, newUsers, idsByUpn);
                    }
                }

                await FlushNewUsers(_db, newUsers, idsByUpn);
            }
            finally
            {
                _db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }

            return new CopilotUserIdResolution(idsByUpn, plan.ToCreate.Count, plan.SkippedUnknownDomain);
        }

        /// <summary>
        /// Commits a batch of new users, records their ids, then DETACHES them.
        /// Detaching matters at scale: auto-detect is off, so every SaveChanges needs an explicit
        /// DetectChanges, and DetectChanges walks every tracked entity. Leaving 200,000 added users tracked
        /// would make the per-batch scan O(total x batches) and hold them all in memory for the rest of the
        /// import. The ids are all we need afterwards.
        /// </summary>
        private static async Task FlushNewUsers(AnalyticsEntitiesContext db, List<Common.Entities.User> newUsers, Dictionary<string, int> idsByUpn)
        {
            if (newUsers.Count == 0) return;

            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();

            foreach (var user in newUsers)
            {
                idsByUpn[user.UserPrincipalName] = user.ID;
                db.Entry(user).State = EntityState.Detached;
            }

            newUsers.Clear();
        }

        #endregion

        #region Per-user detail upsert

        /// <inheritdoc />
        public async Task<CopilotUsageUpsertResult> UpsertUserDetailAsync(IReadOnlyList<CopilotUsageUserDetailRow> rows,
            IReadOnlyDictionary<string, int> userIdsByUpn, bool hasVersion2Data)
        {
            var result = new CopilotUsageUpsertResult();
            if (rows == null || rows.Count == 0) return result;

            var reportDates = rows.Select(r => r.ReportRefreshDate.Date).Distinct().ToList();
            var periods = rows.Select(r => r.ReportPeriodDays.Value).Distinct().ToList();

            var autoDetectWasEnabled = _db.Configuration.AutoDetectChangesEnabled;
            _db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                // Work in batches of users rather than one pass over all of them. Batching bounds not just the
                // SQL command trees but EF's change tracker: the previous shape loaded and kept every existing
                // row for the report date tracked for the whole loop, which at ~200,000 licensed users is the
                // memory profile that caused an OutOfMemoryException in the other usage-report loaders.
                for (var offset = 0; offset < rows.Count; offset += SaveBatchSize)
                {
                    var count = Math.Min(SaveBatchSize, rows.Count - offset);
                    var batch = new List<CopilotUsageUserDetailRow>(count);
                    for (var i = 0; i < count; i++) batch.Add(rows[offset + i]);

                    await SaveBatchAsync(batch, userIdsByUpn, reportDates, periods, hasVersion2Data, result);
                    DetachActivityLogs(_db);
                }
            }
            finally
            {
                _db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
                DetachActivityLogs(_db);
            }

            return result;
        }

        private async Task SaveBatchAsync(List<CopilotUsageUserDetailRow> batch, IReadOnlyDictionary<string, int> userIdsByUpn,
            List<DateTime> reportDates, List<int> periods, bool hasVersion2Data, CopilotUsageUpsertResult result)
        {
            var batchUserIds = new List<int>(batch.Count);
            foreach (var row in batch)
            {
                if (userIdsByUpn.TryGetValue(row.UserPrincipalName, out var id)) batchUserIds.Add(id);
            }
            if (batchUserIds.Count == 0) return;

            var existingByKey = new Dictionary<string, CopilotUsageUserActivityLog>();
            foreach (var existing in await _db.CopilotUsageUserActivityLogs
                .Where(r => reportDates.Contains(r.Date) && periods.Contains(r.ReportPeriodDays) && batchUserIds.Contains(r.UserID))
                .ToListAsync())
            {
                existingByKey[KeyOf(existing.Date, existing.ReportPeriodDays, existing.UserID)] = existing;
            }

            var written = 0;
            foreach (var row in batch)
            {
                if (!userIdsByUpn.TryGetValue(row.UserPrincipalName, out var userId)) continue;

                var date = row.ReportRefreshDate.Date;
                var periodDays = row.ReportPeriodDays.Value;
                var key = KeyOf(date, periodDays, userId);

                var isNew = !existingByKey.TryGetValue(key, out var log);
                if (isNew)
                {
                    log = new CopilotUsageUserActivityLog { Date = date, UserID = userId, ReportPeriodDays = periodDays };
                    existingByKey[key] = log;
                }

                var changed = Populate(log, row, hasVersion2Data);

                if (isNew)
                {
                    _db.CopilotUsageUserActivityLogs.Add(log);
                    result.Inserted++;
                }
                else if (changed)
                {
                    _db.Entry(log).State = EntityState.Modified;
                    result.Updated++;
                }
                else
                {
                    // Graph gap-fills the last few days, so re-imports are mostly identical rows. Skipping
                    // unchanged UPDATEs is the difference between writing a handful of rows and rewriting
                    // every licensed user every cycle.
                    result.Unchanged++;
                    continue;
                }

                written++;
            }

            if (written > 0) await _db.SaveChangesAsync();
        }

        private static void DetachActivityLogs(AnalyticsEntitiesContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries<CopilotUsageUserActivityLog>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        /// <summary>
        /// Copies report values onto the log row, returning true if any stored value actually changed.
        ///
        /// When the response carried no version 2 values at all, those columns are left untouched instead of
        /// being written as NULL: that response can't distinguish "the user submitted no prompts" from
        /// "Graph didn't send prompt data", and blanking a previously captured prompt count is the more
        /// damaging of the two possible mistakes.
        /// </summary>
        internal static bool Populate(CopilotUsageUserActivityLog log, CopilotUsageUserDetailRow row, bool hasVersion2Data)
            => CopilotUsageUserDetailLoader.Populate(log, row, hasVersion2Data);
        /// <summary>Matches the unique index on (date, user_id, report_period_days).</summary>
        private static string KeyOf(DateTime date, int periodDays, int userId)
        {
            return $"{date:yyyy-MM-dd}|{periodDays}|{userId}";
        }

        #endregion

        #region Aggregate user-count upsert

        /// <inheritdoc />
        public async Task<CopilotUsageUpsertResult> UpsertUserCountsAsync(IReadOnlyList<CopilotUserCountLog> parsed, string reportType)
        {
            var result = new CopilotUsageUpsertResult();
            if (parsed == null || parsed.Count == 0) return result;

            var minDate = parsed.Min(r => r.ReportDate);
            var maxDate = parsed.Max(r => r.ReportDate);

            // One query for everything we might collide with. Bounded by the report's own date range, so this
            // stays small even after years of retained history.
            var existing = await _db.CopilotUserCountLogs
                .Where(r => r.ReportType == reportType && r.ReportDate >= minDate && r.ReportDate <= maxDate)
                .ToListAsync();

            var existingByKey = new Dictionary<string, CopilotUserCountLog>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in existing)
            {
                existingByKey[KeyOf(row)] = row;
            }

            var pending = 0;
            var autoDetectWasEnabled = _db.Configuration.AutoDetectChangesEnabled;
            _db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                foreach (var row in parsed)
                {
                    var key = KeyOf(row);
                    if (existingByKey.TryGetValue(key, out var stored))
                    {
                        // Graph gap-fills the most recent ~3 days, so re-importing an overlapping window is
                        // normal and usually changes nothing. Only write when a value actually moved. The
                        // refresh date deliberately does NOT count as a change on its own: it advances every
                        // day, so including it would rewrite every day in the window daily (up to 180 days x
                        // every app) purely to restamp provenance. Report-level freshness lives in
                        // copilot_usage_report_import_log instead.
                        if (!HasChanged(stored, row))
                        {
                            result.Unchanged++;
                            continue;
                        }

                        stored.ReportRefreshDate = row.ReportRefreshDate;
                        stored.EnabledUsers = row.EnabledUsers;
                        stored.ActiveUsers = row.ActiveUsers;
                        stored.PromptsSubmitted = row.PromptsSubmitted;
                        stored.AveragePromptsSubmitted = row.AveragePromptsSubmitted;
                        _db.Entry(stored).State = EntityState.Modified;
                        result.Updated++;
                    }
                    else
                    {
                        _db.CopilotUserCountLogs.Add(row);
                        existingByKey[key] = row;
                        result.Inserted++;
                    }

                    pending++;
                    if (pending >= UserCountSaveBatchSize)
                    {
                        await _db.SaveChangesAsync();
                        pending = 0;
                    }
                }

                if (pending > 0) await _db.SaveChangesAsync();
            }
            finally
            {
                _db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }

            return result;
        }

        /// <summary>
        /// Rows persisted per SaveChanges for the aggregate reports. These are small (a period's worth of
        /// days x a dozen apps), but a D180 trend backfill across many apps still runs to a few thousand
        /// rows, so commit in batches rather than building one enormous command tree.
        /// </summary>
        public int UserCountSaveBatchSize { get; set; } = 500;

        private static bool HasChanged(CopilotUserCountLog stored, CopilotUserCountLog incoming)
        {
            return stored.EnabledUsers != incoming.EnabledUsers
                || stored.ActiveUsers != incoming.ActiveUsers
                || stored.PromptsSubmitted != incoming.PromptsSubmitted
                || stored.AveragePromptsSubmitted != incoming.AveragePromptsSubmitted;
        }

        /// <summary>Matches the unique index on (report_type, report_period_days, report_date, app_name).</summary>
        private static string KeyOf(CopilotUserCountLog row)
        {
            return $"{row.ReportType}|{(row.ReportPeriodDays.HasValue ? row.ReportPeriodDays.Value.ToString() : string.Empty)}|{row.ReportDate:yyyy-MM-dd}|{row.AppName}";
        }

        #endregion

        #region Import log

        /// <inheritdoc />
        public async Task RecordReportLoadAsync(CopilotUsageReportImportLog importLog)
        {
            _db.CopilotUsageReportImportLogs.Add(importLog);
            await _db.SaveChangesAsync();
        }

        /// <inheritdoc />
        public async Task RecordReportLoadAfterFailureAsync(CopilotUsageReportImportLog importLog)
        {
            using (var freshDb = _contextFactory.Create())
            {
                freshDb.CopilotUsageReportImportLogs.Add(importLog);
                await freshDb.SaveChangesAsync();
            }
        }

        #endregion
    }
}
