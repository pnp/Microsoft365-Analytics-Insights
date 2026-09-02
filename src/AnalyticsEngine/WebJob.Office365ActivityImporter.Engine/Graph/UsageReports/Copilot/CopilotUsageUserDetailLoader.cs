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
        private readonly ICopilotUsagePersistenceManager _persistence;

        /// <summary>
        /// Rows per SaveChanges. Matches the value the other usage-report loaders settled on: small enough
        /// that EF6 never builds hundreds of thousands of command trees at once (which OutOfMemoryExceptions
        /// on a small App Service) and that any IN clause stays well under SQL Server's parameter limit.
        /// </summary>
        public int SaveBatchSize { get; set; } = 1000;

        public CopilotUsageUserDetailLoader(ICopilotReportSource reportSource, ILogger logger,
            UserGroupsCache userGroupsCache = null, UserGroupsFilterModel userGroupsFilter = null)
            : this(reportSource, logger, userGroupsCache, userGroupsFilter, null)
        {
        }

        /// <summary>
        /// As above, with the write side supplied (issue #370). The original signature is kept as a
        /// delegating overload rather than gaining an optional parameter, so no already-compiled caller
        /// breaks. When <paramref name="persistence"/> is null the db-taking <c>LoadAndSaveAsync</c>
        /// overloads build a <see cref="SqlCopilotUsagePersistenceManager"/> over the context they are given.
        /// </summary>
        public CopilotUsageUserDetailLoader(ICopilotReportSource reportSource, ILogger logger,
            UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilter,
            ICopilotUsagePersistenceManager persistence)
        {
            _reportSource = reportSource ?? throw new ArgumentNullException(nameof(reportSource));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userGroupsCache = userGroupsCache;
            _userGroupsFilter = userGroupsFilter;
            _persistence = persistence;
        }

        public Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, string period, string version = CopilotReportVersions.V2)
        {
            return LoadAndSaveAsync(db, new CopilotReportRequest(CopilotReportNames.UsageUserDetail, period, version));
        }

        /// <summary>
        /// Downloads, parses and upserts the per-user report against the supplied context. Returns rows
        /// written to SQL; 0 when the tenant conceals user information, when Graph returned nothing, or when
        /// nothing changed.
        /// </summary>
        public Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, CopilotReportRequest request)
        {
            if (_persistence == null && db == null) throw new ArgumentNullException(nameof(db));
            return LoadAndSaveCoreAsync(PersistenceFor(db), request);
        }

        /// <summary>
        /// As above, using the <see cref="ICopilotUsagePersistenceManager"/> this loader was constructed
        /// with. This is the overload that lets the whole import run with no database at all.
        /// </summary>
        public Task<int> LoadAndSaveAsync(CopilotReportRequest request)
        {
            if (_persistence == null)
            {
                throw new InvalidOperationException(
                    $"This overload needs an {nameof(ICopilotUsagePersistenceManager)}; construct the loader with one, or call the overload that takes a database context.");
            }
            return LoadAndSaveCoreAsync(_persistence, request);
        }

        private ICopilotUsagePersistenceManager PersistenceFor(AnalyticsEntitiesContext db)
        {
            return _persistence ?? new SqlCopilotUsagePersistenceManager(db, _logger) { SaveBatchSize = SaveBatchSize };
        }

        private async Task<int> LoadAndSaveCoreAsync(ICopilotUsagePersistenceManager persistence, CopilotReportRequest request)
        {
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
                await persistence.RecordReportLoadAsync(importLog);

                _logger.LogWarning($"Copilot per-user report {request} is not available on this tenant: {ex.Message} " +
                    "These reports exist only in the global cloud (not US Government or 21Vianet). No rows were imported.");
                return 0;
            }
            catch (Exception ex)
            {
                importLog.Error = Truncate(GraphHttpException.DescribeForStorage(ex), 1000);
                await persistence.RecordReportLoadAsync(importLog);
                throw;
            }

            importLog.RowsRead = parsed.Count;
            importLog.ReportRefreshDate = parsed.Count > 0 ? parsed.Max(r => r.ReportRefreshDate) : (DateTime?)null;

            if (parsed.Count == 0)
            {
                _logger.LogWarning($"Copilot per-user report {request} returned no rows. " +
                    "The report downloaded successfully and was genuinely empty, which is expected on a tenant with no Microsoft 365 Copilot licences.");
                await persistence.RecordReportLoadAsync(importLog);
                return 0;
            }

            // Asked for v2 but got v1? Every prompt and active-usage-day column will be NULL, which looks like
            // "nobody prompted" unless we say so out loud.
            // The concealment decision itself is a pure rule - see CopilotUsageReportPolicy (#370).
            var concealment = CopilotUsageReportPolicy.EvaluateConcealment(parsed);
            var concealedCount = concealment.ConcealedCount;
            if (concealment.Outcome == ConcealedIdentityOutcome.AbortImport)
            {
                importLog.IsUpnObfuscated = true;
                await persistence.RecordReportLoadAsync(importLog);

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

            // Same list instance on the ImportAll path - NOT a copy. SaveAsync drops unkeyable rows with
            // an in-place RemoveAll, and the closing log reads parsed.Count, so copying here would both
            // allocate a second 200k-element array and change that operator-facing count.
            var importable = concealment.Importable;

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
                written = await SaveAsync(persistence, importable, request, hasVersion2Data);
            }
            catch (Exception ex)
            {
                // Persistence failures must reach the Health page too, and must be written on a FRESH context:
                // the one that just failed a SaveChanges can be left with entities in a broken state.
                importLog.Error = Truncate(GraphHttpException.DescribeForStorage(ex), 1000);
                await persistence.RecordReportLoadAfterFailureAsync(importLog);
                throw;
            }

            importLog.RowsSaved = written;
            await persistence.RecordReportLoadAsync(importLog);

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

        /// <summary>
        /// Resolves users, keys the rows by report period, and hands the survivors to the persistence port.
        ///
        /// Order matters and is preserved: users are resolved BEFORE the period keying, over the full row
        /// set, so an identity that appears only on rows later dropped as unkeyable is still created exactly
        /// as it always was.
        /// </summary>
        private async Task<int> SaveAsync(ICopilotUsagePersistenceManager persistence, List<CopilotUsageUserDetailRow> rows,
            CopilotReportRequest request, bool hasVersion2Data)
        {
            if (rows.Count == 0) return 0;

            var resolution = await persistence.ResolveUserIdsAsync(rows.Select(r => r.UserPrincipalName));

            // The period is part of the key: D7 and D28 describe the SAME user and date with different prompt
            // counts, active-day counts and last-activity values, so they are different facts, not a conflict.
            // The rule (including the in-place filtering that keeps the caller's "parsed N row(s)" count
            // honest) lives in CopilotUsageReportPolicy so it can be asserted without a database.
            var unkeyable = CopilotUsageReportPolicy.ApplyPeriodKeys(rows, request.PeriodDays);

            if (unkeyable > 0)
            {
                _logger.LogWarning($"Copilot per-user report: dropped {unkeyable} row(s) with no report period. " +
                    "A period is part of the row's identity, so it cannot be stored without one.");
            }

            if (rows.Count == 0) return 0;

            var upsert = await persistence.UpsertUserDetailAsync(rows, resolution.IdsByUpn, hasVersion2Data);
            return upsert.Written;
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

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength);
        }
    }
}
