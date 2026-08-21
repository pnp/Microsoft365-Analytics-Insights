using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.UsageReports;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Imports getMicrosoft365CopilotUsageUserDetail into <see cref="CopilotUsageUserActivityLog"/>.
    ///
    /// Two behaviours here are deliberate and worth knowing before changing anything:
    ///
    /// <b>Concealed identities abort the import rather than degrade it.</b> When a tenant enables "concealed
    /// user information", Graph still returns 200 OK with one row per licensed user - it just replaces the UPN
    /// and display name with hashes. Feeding those through the usual get-or-create-user path would create one
    /// junk user per licensed account (200,000 of them on a large tenant), permanently polluting the users
    /// table and every report built on it, while producing joins that are wrong rather than missing. So the
    /// report is not imported at all, and the reason is recorded in
    /// <see cref="CopilotUsageReportImportLog"/> for the Health page. The audit-log Copilot import is not
    /// affected by that tenant setting, so Copilot reporting still works - it just comes from the audit source.
    ///
    /// <b>Users are resolved in bulk, not per row.</b> At the ~200k-user scale this solution targets, a
    /// per-row user lookup is 200,000 round trips. Existing users are read once into a dictionary and missing
    /// ones inserted in batches.
    /// </summary>
    public class CopilotUsageUserDetailLoader
    {
        private readonly ICopilotReportSource _reportSource;
        private readonly ILogger _logger;
        private readonly UserGroupsCache _userGroupsCache;
        private readonly UserGroupsFilterModel _userGroupsFilter;

        /// <summary>
        /// Rows per SaveChanges. Matches the value the other usage-report loaders settled on: small enough
        /// that EF6 never builds hundreds of thousands of command trees at once (which OutOfMemoryExceptions
        /// on a small App Service) and that any IN clause stays well under SQL Server's parameter limit.
        /// </summary>
        public int SaveBatchSize { get; set; } = 1000;

        public CopilotUsageUserDetailLoader(ICopilotReportSource reportSource, ILogger logger,
            UserGroupsCache userGroupsCache = null, UserGroupsFilterModel userGroupsFilter = null)
        {
            _reportSource = reportSource ?? throw new ArgumentNullException(nameof(reportSource));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userGroupsCache = userGroupsCache;
            _userGroupsFilter = userGroupsFilter;
        }

        public Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, string period, string version = CopilotReportVersions.V2)
        {
            return LoadAndSaveAsync(db, new CopilotReportRequest(CopilotReportNames.UsageUserDetail, period, version));
        }

        /// <summary>
        /// Downloads, parses and upserts the per-user report. Returns rows written to SQL; 0 when the tenant
        /// conceals user information, when Graph returned nothing, or when nothing changed.
        /// </summary>
        public async Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, CopilotReportRequest request)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ReportName != CopilotReportNames.UsageUserDetail)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.ReportName,
                    $"{nameof(CopilotUsageUserDetailLoader)} handles only {CopilotReportNames.UsageUserDetail}.");
            }

            var importLog = new CopilotUsageReportImportLog
            {
                ReportName = request.ReportName,
                ReportVersion = request.Version,
                ReportPeriod = request.Period,
                ImportedUtc = DateTime.UtcNow,
            };

            List<CopilotUsageUserDetailRow> parsed;
            try
            {
                var reports = await _reportSource.LoadReportAsync(request);
                parsed = CopilotUsageUserDetailParser.Parse(reports);

                // The response nests the counters under copilotActivityUserDetailsByPeriod, so "no rows"
                // can mean either "no Copilot licences" or "the shape changed and we understood none of it".
                if (reports.Count > 0 && parsed.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Copilot report {request} returned {reports.Count} user object(s) but none could be parsed. " +
                        "Microsoft has probably changed the report schema; the import was stopped rather than recording an empty snapshot.");
                }
            }
            catch (GraphResourceNotFoundException ex)
            {
                // Tolerated, not a failure - see CopilotUserCountReportLoader for the full reasoning. The
                // report endpoint doesn't exist in this cloud, so there is nothing to retry; record why.
                importLog.RowsRead = 0;
                importLog.Error = Truncate($"Report not available: {GraphHttpException.DescribeForStorage(ex)}", 1000);
                await SaveImportLog(db, importLog);

                _logger.LogWarning($"Copilot per-user report {request} is not available on this tenant: {ex.Message} " +
                    "These reports exist only in the global cloud (not US Government or 21Vianet). No rows were imported.");
                return 0;
            }
            catch (Exception ex)
            {
                importLog.Error = Truncate(GraphHttpException.DescribeForStorage(ex), 1000);
                await SaveImportLog(db, importLog);
                throw;
            }

            importLog.RowsRead = parsed.Count;
            importLog.ReportRefreshDate = parsed.Count > 0 ? parsed.Max(r => r.ReportRefreshDate) : (DateTime?)null;

            if (parsed.Count == 0)
            {
                _logger.LogWarning($"Copilot per-user report {request} returned no rows. " +
                    "The report downloaded successfully and was genuinely empty, which is expected on a tenant with no Microsoft 365 Copilot licences.");
                await SaveImportLog(db, importLog);
                return 0;
            }

            // Asked for v2 but got v1? Every prompt and active-usage-day column will be NULL, which looks like
            // "nobody prompted" unless we say so out loud.
            var concealedCount = parsed.Count(r => r.IsIdentityConcealed);
            if (concealedCount == parsed.Count)
            {
                importLog.IsUpnObfuscated = true;
                await SaveImportLog(db, importLog);

                _logger.LogError($"Copilot per-user usage report {request} came back with concealed user identities for all {parsed.Count} row(s): " +
                    "the tenant has 'concealed user information' enabled for Microsoft 365 usage reports, so Graph replaced each user principal name with a hash. " +
                    "Per-user Copilot usage from Graph cannot be linked to users and has NOT been imported (importing it would create one placeholder user per licensed account). " +
                    "The tenant-level Copilot user counts are unaffected and still import, and the audit-log Copilot import is not affected by this setting at all. " +
                    "To enable per-user Graph usage data, turn off 'Display concealed user, group and site names in all reports' in the Microsoft 365 admin centre (Settings > Org settings > Reports).");
                return 0;
            }

            if (concealedCount > 0)
            {
                _logger.LogWarning($"Copilot per-user usage report {request}: {concealedCount} of {parsed.Count} row(s) had a concealed (hashed) user identity and were skipped; the rest imported normally.");
            }

            var importable = concealedCount == 0 ? parsed : parsed.Where(r => !r.IsIdentityConcealed).ToList();

            // Checked after concealment, so a concealed tenant still gets its diagnostic rather than this
            // warning. Microsoft hasn't published the beta JSON schema for version 2, so an absent set of v2
            // values could mean either "Graph answered v1" or "the field names differ from the ones we look
            // for". Either way the safe response is the same: import the version 1 values and leave the
            // stored version 2 columns alone rather than blanking prompt counts a previous import captured.
            var hasVersion2Data = CopilotUsageUserDetailParser.HasVersion2Data(importable);
            if (request.Version == CopilotReportVersions.V2 && !hasVersion2Data && importable.Count > 0)
            {
                _logger.LogWarning($"Copilot report {request} was requested as {CopilotReportVersions.V2} but no version 2 values " +
                    "(prompt counts, active usage days) were found in the response. Those columns will be left as they are rather than overwritten.");
            }

            int written;
            try
            {
                importable = await FilterToUsersInScope(importable);
                written = await SaveAsync(db, importable, request, hasVersion2Data);
            }
            catch (Exception ex)
            {
                // Persistence failures must reach the Health page too, and must be written on a FRESH context:
                // the one that just failed a SaveChanges can be left with entities in a broken state.
                importLog.Error = Truncate(GraphHttpException.DescribeForStorage(ex), 1000);
                await SaveImportLogOnNewContext(importLog);
                throw;
            }

            importLog.RowsSaved = written;
            await SaveImportLog(db, importLog);

            _logger.LogInformation($"Copilot per-user report {request}: parsed {parsed.Count} row(s), wrote {written} to SQL.");
            return written;
        }

        /// <summary>
        /// Honours the configured Entra group filter, the same way the other per-user usage-report loaders do,
        /// so a customer scoping analytics to a pilot group doesn't silently get the whole tenant here.
        /// </summary>
        private async Task<List<CopilotUsageUserDetailRow>> FilterToUsersInScope(List<CopilotUsageUserDetailRow> rows)
        {
            if (_userGroupsCache == null || _userGroupsFilter == null || _userGroupsFilter.Patterns.Count == 0)
            {
                return rows;
            }

            // With no filter configured this method isn't reached, so the cost below only lands on tenants that
            // scoped analytics to specific groups. Those checks are one Graph /memberOf call per user the first
            // time each is seen - the same thing every other per-user usage-report loader does, but this report
            // is a single pass over every licensed user, so say out loud how many calls that is rather than
            // letting an import quietly take hours.
            _logger.LogWarning($"Copilot per-user report: a user group filter is configured, so up to {rows.Count.ToString("N0")} Entra group-membership lookups may be issued " +
                "(one per user, cached for an hour). On a very large tenant this dominates the import time.");

            var inScope = new List<CopilotUsageUserDetailRow>(rows.Count);
            var skipped = 0;
            foreach (var row in rows)
            {
                if (await _userGroupsCache.IsInGroupsFilter(row.UserPrincipalName, _userGroupsFilter))
                {
                    inScope.Add(row);
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
            {
                _logger.LogInformation($"Copilot per-user report: skipped {skipped} user(s) outside the configured group filter.");
            }
            return inScope;
        }

        private async Task<int> SaveAsync(AnalyticsEntitiesContext db, List<CopilotUsageUserDetailRow> rows,
            CopilotReportRequest request, bool hasVersion2Data)
        {
            if (rows.Count == 0) return 0;

            var userIdsByUpn = await ResolveUserIds(db, rows);

            // The period is part of the key: D7 and D28 describe the SAME user and date with different prompt
            // counts, active-day counts and last-activity values, so they are different facts, not a conflict.
            // A row that states no period and a request that can't supply one (ALL) has no key, so it is
            // dropped rather than stored under the meaningless period 0. Filtered in place: at ~200k licensed
            // users a second full-size list is a pointless copy of the whole report.
            //
            // RemoveAll rather than a reverse loop calling RemoveAt: each RemoveAt shifts every surviving
            // element after it, so dropping a scattered subset of a 200k-row report costs O(N^2) element
            // moves (~5 billion when half the rows are unkeyable). RemoveAll is a single O(N) compaction.
            var requestedPeriodDays = request.PeriodDays;
            var unkeyable = rows.RemoveAll(row =>
            {
                row.ReportPeriodDays = row.ReportPeriodDays ?? requestedPeriodDays;
                return !row.ReportPeriodDays.HasValue;
            });

            if (unkeyable > 0)
            {
                _logger.LogWarning($"Copilot per-user report: dropped {unkeyable} row(s) with no report period. " +
                    "A period is part of the row's identity, so it cannot be stored without one.");
            }

            if (rows.Count == 0) return 0;

            var reportDates = rows.Select(r => r.ReportRefreshDate.Date).Distinct().ToList();
            var periods = rows.Select(r => r.ReportPeriodDays.Value).Distinct().ToList();

            var written = 0;
            var autoDetectWasEnabled = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                // Work in batches of users rather than one pass over all of them. Batching bounds not just the
                // SQL command trees but EF's change tracker: the previous shape loaded and kept every existing
                // row for the report date tracked for the whole loop, which at ~200,000 licensed users is the
                // memory profile that caused an OutOfMemoryException in the other usage-report loaders.
                for (var offset = 0; offset < rows.Count; offset += SaveBatchSize)
                {
                    var batch = rows.GetRange(offset, Math.Min(SaveBatchSize, rows.Count - offset));
                    written += await SaveBatchAsync(db, batch, userIdsByUpn, reportDates, periods, hasVersion2Data);
                    DetachActivityLogs(db);
                }
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
                DetachActivityLogs(db);
            }

            return written;
        }

        private async Task<int> SaveBatchAsync(AnalyticsEntitiesContext db, List<CopilotUsageUserDetailRow> batch,
            Dictionary<string, int> userIdsByUpn, List<DateTime> reportDates, List<int> periods, bool hasVersion2Data)
        {
            var batchUserIds = new List<int>(batch.Count);
            foreach (var row in batch)
            {
                if (userIdsByUpn.TryGetValue(row.UserPrincipalName, out var id)) batchUserIds.Add(id);
            }
            if (batchUserIds.Count == 0) return 0;

            var existingByKey = new Dictionary<string, CopilotUsageUserActivityLog>();
            foreach (var existing in await db.CopilotUsageUserActivityLogs
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
                    db.CopilotUsageUserActivityLogs.Add(log);
                }
                else if (changed)
                {
                    db.Entry(log).State = EntityState.Modified;
                }
                else
                {
                    // Graph gap-fills the last few days, so re-imports are mostly identical rows. Skipping
                    // unchanged UPDATEs is the difference between writing a handful of rows and rewriting
                    // every licensed user every cycle.
                    continue;
                }

                written++;
            }

            if (written > 0) await db.SaveChangesAsync();
            return written;
        }

        private static void DetachActivityLogs(AnalyticsEntitiesContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries<CopilotUsageUserActivityLog>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        /// <summary>
        /// Maps every report UPN to a user id, reading the existing users once and inserting only the ones we
        /// have never seen. Comparisons are case-insensitive in memory rather than via SQL <c>LOWER()</c>,
        /// which would make the predicate non-SARGable and force a table scan.
        ///
        /// A new user is only ever created when its email domain is one we already hold users for. Syntax
        /// alone cannot prove an identity belongs to the tenant, so this - not the UPN shape check - is the
        /// real boundary that stops a pseudonymised report from populating the users table with junk. An
        /// identity on an unrecognised domain is skipped and counted, not invented.
        /// </summary>
        private async Task<Dictionary<string, int>> ResolveUserIds(AnalyticsEntitiesContext db, List<CopilotUsageUserDetailRow> rows)
        {
            var existingUsers = await db.users.AsNoTracking()
                .Select(u => new { u.ID, u.UserPrincipalName })
                .ToListAsync();

            var idsByUpn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var knownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in existingUsers)
            {
                if (string.IsNullOrWhiteSpace(user.UserPrincipalName)) continue;
                idsByUpn[user.UserPrincipalName] = user.ID;

                var domain = DomainOf(user.UserPrincipalName);
                if (domain != null) knownDomains.Add(domain);
            }

            var missing = new List<string>();
            var unknownDomain = 0;
            foreach (var upn in rows.Select(r => r.UserPrincipalName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (idsByUpn.ContainsKey(upn)) continue;

                // An empty users table means there is nothing to validate against yet (a brand-new install
                // where the user-metadata import hasn't run). Creating is then the only way to make progress,
                // and Microsoft's concealed identities are bare hashes with no domain at all, so they are
                // already rejected before reaching here.
                if (knownDomains.Count > 0 && !knownDomains.Contains(DomainOf(upn) ?? string.Empty))
                {
                    unknownDomain++;
                    continue;
                }

                missing.Add(upn);
            }

            if (unknownDomain > 0)
            {
                _logger.LogWarning($"Copilot per-user report: skipped {unknownDomain} identity(ies) on an email domain this database has no users for. " +
                    "They were not created, because an unrecognised domain is how a pseudonymised report would look. " +
                    "If these are genuine users, run the Graph user metadata import so they are known first.");
            }

            if (missing.Count == 0) return idsByUpn;

            _logger.LogInformation($"Copilot per-user report: creating {missing.Count} user record(s) not yet known to the database.");

            var autoDetectWasEnabled = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                var newUsers = new List<Common.Entities.User>(Math.Min(missing.Count, SaveBatchSize));
                for (var i = 0; i < missing.Count; i++)
                {
                    // Existing loaders store UPNs lower-cased; matching that avoids creating a second user
                    // record that differs only by case.
                    var user = new Common.Entities.User { UserPrincipalName = missing[i].ToLowerInvariant() };
                    db.users.Add(user);
                    newUsers.Add(user);

                    if (newUsers.Count >= SaveBatchSize)
                    {
                        await FlushNewUsers(db, newUsers, idsByUpn);
                    }
                }

                await FlushNewUsers(db, newUsers, idsByUpn);
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }

            return idsByUpn;
        }

        /// <summary>
        /// Commits a batch of new users, records their ids, then DETACHES them.
        /// Detaching matters at scale: auto-detect is off, so every SaveChanges needs an explicit
        /// DetectChanges, and DetectChanges walks every tracked entity. Leaving 200,000 added users tracked
        /// would make the per-batch scan O(total x batches) and hold them all in memory for the rest of the
        /// import. The ids are all we need afterwards.
        /// </summary>
        private static async Task FlushNewUsers(AnalyticsEntitiesContext db, List<Common.Entities.User> newUsers,
            Dictionary<string, int> idsByUpn)
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

        /// <summary>
        /// Copies report values onto the log row, returning true if any stored value actually changed.
        ///
        /// When the response carried no version 2 values at all, those columns are left untouched instead of
        /// being written as NULL: that response can't distinguish "the user submitted no prompts" from
        /// "Graph didn't send prompt data", and blanking a previously captured prompt count is the more
        /// damaging of the two possible mistakes.
        /// </summary>
        private static bool Populate(CopilotUsageUserActivityLog log, CopilotUsageUserDetailRow row, bool hasVersion2Data)
        {
            var changed = false;

            changed |= Set(log.LastActivityDate, row.LastActivityDate, v => log.LastActivityDate = v);

            if (hasVersion2Data)
            {
                changed |= Set(log.PromptsAllApps, row.PromptsAllApps, v => log.PromptsAllApps = v);
                changed |= Set(log.PromptsChatWork, row.PromptsChatWork, v => log.PromptsChatWork = v);
                changed |= Set(log.PromptsChatWeb, row.PromptsChatWeb, v => log.PromptsChatWeb = v);
                changed |= Set(log.ActiveUsageDays, row.ActiveUsageDays, v => log.ActiveUsageDays = v);
                changed |= Set(log.ChatWorkLastActivityDate, row.ChatWorkLastActivityDate, v => log.ChatWorkLastActivityDate = v);
                changed |= Set(log.ChatWebLastActivityDate, row.ChatWebLastActivityDate, v => log.ChatWebLastActivityDate = v);
                changed |= Set(log.Microsoft365CopilotLastActivityDate, row.Microsoft365CopilotLastActivityDate, v => log.Microsoft365CopilotLastActivityDate = v);
                changed |= Set(log.EdgeLastActivityDate, row.EdgeLastActivityDate, v => log.EdgeLastActivityDate = v);
                changed |= Set(log.AgentLastActivityDate, row.AgentLastActivityDate, v => log.AgentLastActivityDate = v);
            }

            changed |= Set(log.ChatLastActivityDate, row.ChatLastActivityDate, v => log.ChatLastActivityDate = v);
            changed |= Set(log.TeamsLastActivityDate, row.TeamsLastActivityDate, v => log.TeamsLastActivityDate = v);
            changed |= Set(log.WordLastActivityDate, row.WordLastActivityDate, v => log.WordLastActivityDate = v);
            changed |= Set(log.ExcelLastActivityDate, row.ExcelLastActivityDate, v => log.ExcelLastActivityDate = v);
            changed |= Set(log.PowerPointLastActivityDate, row.PowerPointLastActivityDate, v => log.PowerPointLastActivityDate = v);
            changed |= Set(log.OutlookLastActivityDate, row.OutlookLastActivityDate, v => log.OutlookLastActivityDate = v);
            changed |= Set(log.OneNoteLastActivityDate, row.OneNoteLastActivityDate, v => log.OneNoteLastActivityDate = v);
            changed |= Set(log.LoopLastActivityDate, row.LoopLastActivityDate, v => log.LoopLastActivityDate = v);

            changed |= Set(log.IsUpnObfuscated, false, v => log.IsUpnObfuscated = v);

            return changed;
        }

        private static bool Set<T>(T current, T incoming, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, incoming)) return false;
            assign(incoming);
            return true;
        }

        /// <summary>Matches the unique index on (date, user_id, report_period_days).</summary>
        private static string KeyOf(DateTime date, int periodDays, int userId)
        {
            return $"{date:yyyy-MM-dd}|{periodDays}|{userId}";
        }

        private static string DomainOf(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn)) return null;
            var at = upn.LastIndexOf('@');
            if (at <= 0 || at == upn.Length - 1) return null;
            return upn.Substring(at + 1);
        }

        private static async Task SaveImportLog(AnalyticsEntitiesContext db, CopilotUsageReportImportLog importLog)
        {
            db.CopilotUsageReportImportLogs.Add(importLog);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Records the import diagnostic on a brand-new context. Used on the failure path, where the context
        /// that raised the error may itself be the reason a save can't succeed.
        /// </summary>
        private static async Task SaveImportLogOnNewContext(CopilotUsageReportImportLog importLog)
        {
            using (var freshDb = new AnalyticsEntitiesContext())
            {
                await SaveImportLog(freshDb, importLog);
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength);
        }
    }
}
