using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.UsageReports;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Imports the first-party Cowork usage report into dbo.cowork_usage_user_activity_log.
    /// The report's unit is a Cowork task, not an audit interaction; the two are stored separately so the
    /// adoption report can reconcile them without ever presenting them as comparable counts.
    /// </summary>
    public class CoworkUsageUserDetailLoader
    {
        private readonly ICopilotReportSource _reportSource;
        private readonly ILogger _logger;
        private readonly UserGroupsCache _userGroupsCache;
        private readonly UserGroupsFilterModel _userGroupsFilter;
        private readonly ICoworkUsagePersistenceManager _persistence;

        public int SaveBatchSize { get; set; } = 1000;

        public CoworkUsageUserDetailLoader(ICopilotReportSource reportSource, ILogger logger,
            UserGroupsCache userGroupsCache = null, UserGroupsFilterModel userGroupsFilter = null)
            : this(reportSource, logger, userGroupsCache, userGroupsFilter, null)
        {
        }

        public CoworkUsageUserDetailLoader(ICopilotReportSource reportSource, ILogger logger,
            UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilter,
            ICoworkUsagePersistenceManager persistence)
        {
            _reportSource = reportSource ?? throw new ArgumentNullException(nameof(reportSource));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userGroupsCache = userGroupsCache;
            _userGroupsFilter = userGroupsFilter;
            _persistence = persistence;
        }

        public Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, string period, string version = CopilotReportVersions.V2)
        {
            return LoadAndSaveAsync(db, new CopilotReportRequest(CopilotReportNames.CoworkUsageUserDetail, period, version));
        }

        public Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, CopilotReportRequest request)
        {
            if (_persistence == null && db == null) throw new ArgumentNullException(nameof(db));
            return LoadAndSaveCoreAsync(PersistenceFor(db), request);
        }

        public Task<int> LoadAndSaveAsync(CopilotReportRequest request)
        {
            if (_persistence == null)
            {
                throw new InvalidOperationException($"This overload needs an {nameof(ICoworkUsagePersistenceManager)}.");
            }
            return LoadAndSaveCoreAsync(_persistence, request);
        }

        private ICoworkUsagePersistenceManager PersistenceFor(AnalyticsEntitiesContext db)
        {
            return _persistence ?? new SqlCoworkUsagePersistenceManager(db, _logger) { SaveBatchSize = SaveBatchSize };
        }

        private async Task<int> LoadAndSaveCoreAsync(ICoworkUsagePersistenceManager persistence, CopilotReportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ReportName != CopilotReportNames.CoworkUsageUserDetail)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.ReportName,
                    $"{nameof(CoworkUsageUserDetailLoader)} handles only {CopilotReportNames.CoworkUsageUserDetail}.");
            }

            var importLog = new CopilotUsageReportImportLog
            {
                ReportName = request.ReportName,
                ReportVersion = request.Version,
                ReportPeriod = request.Period,
                ImportedUtc = DateTime.UtcNow,
            };

            List<CoworkUsageUserDetailRow> parsed;
            try
            {
                var reports = await _reportSource.LoadReportAsync(request);
                parsed = CoworkUsageUserDetailParser.Parse(reports);
                if (reports.Count > 0 && parsed.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Cowork report {request} returned {reports.Count} user object(s) but none could be parsed. " +
                        "Microsoft has probably changed the report schema; the import was stopped rather than recording an empty snapshot.");
                }
            }
            catch (GraphResourceNotFoundException ex)
            {
                importLog.RowsRead = 0;
                importLog.Error = Truncate($"Report not available: {GraphHttpException.DescribeForStorage(ex)}", 1000);
                await persistence.RecordReportLoadAsync(importLog);
                _logger.LogWarning($"Cowork usage report {request} is not available on this tenant: {ex.Message}.");
                return 0;
            }
            catch (GraphHttpException ex) when (IsUnknownReportFunctionBadRequest(ex))
            {
                importLog.RowsRead = 0;
                importLog.Error = Truncate($"Report not available: {GraphHttpException.DescribeForStorage(ex)}", 1000);
                await persistence.RecordReportLoadAsync(importLog);
                _logger.LogWarning($"Cowork usage report {request} is not available on this tenant: {ex.Message}.");
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
                await persistence.RecordReportLoadAsync(importLog);
                _logger.LogInformation($"Cowork usage report {request} returned no rows.");
                return 0;
            }

            var concealment = CopilotUsageReportPolicy.EvaluateConcealment(parsed.Select(r => new CopilotUsageUserDetailRow { UserPrincipalName = r.UserPrincipalName }).ToList());
            if (concealment.Outcome == ConcealedIdentityOutcome.AbortImport)
            {
                importLog.IsUpnObfuscated = true;
                await persistence.RecordReportLoadAsync(importLog);
                _logger.LogError($"Cowork usage report {request} came back with concealed user identities for all {parsed.Count} row(s). Per-user Cowork tasks were not imported.");
                return 0;
            }

            var importable = parsed.Where(r => CopilotUsageUserDetailRow.LooksLikeRealUpn(r.UserPrincipalName)).ToList();
            var skipped = parsed.Count - importable.Count;
            if (skipped > 0)
            {
                _logger.LogWarning($"Cowork usage report {request}: skipped {skipped} row(s) with concealed or unusable user identities.");
            }

            importable = await FilterToUsersInScope(importable);
            CoworkUsageReportPolicy.ApplyPeriodKeys(importable, request.PeriodDays);

            int written;
            try
            {
                written = (await persistence.UpsertUserDetailAsync(importable)).Written;
            }
            catch (Exception ex)
            {
                importLog.Error = Truncate(GraphHttpException.DescribeForStorage(ex), 1000);
                await persistence.RecordReportLoadAfterFailureAsync(importLog);
                throw;
            }

            importLog.RowsSaved = written;
            await persistence.RecordReportLoadAsync(importLog);
            _logger.LogInformation($"Cowork usage report {request}: parsed {parsed.Count} row(s), wrote {written} to SQL.");
            return written;
        }

        internal static bool IsUnknownReportFunctionBadRequest(GraphHttpException ex)
        {
            if (ex == null || ex.StatusCode != HttpStatusCode.BadRequest) return false;
            if (string.IsNullOrWhiteSpace(ex.GraphErrorCode)) return false;
            if (ex.GraphErrorCode.IndexOf("badrequest", StringComparison.OrdinalIgnoreCase) < 0) return false;

            var message = ExtractGraphErrorMessage(ex.ResponseBody);
            if (string.IsNullOrWhiteSpace(message)) return false;

            return Contains(message, "resource not found")
                || Contains(message, "resource could not be found")
                || Contains(message, "function not found")
                || Contains(message, "not find a property")
                || Contains(message, "not found for the segment")
                || Contains(message, "unknown segment");
        }

        private static bool Contains(string value, string expected)
        {
            return value?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractGraphErrorMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;

            try
            {
                return JObject.Parse(responseBody)["error"]?["message"]?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<List<CoworkUsageUserDetailRow>> FilterToUsersInScope(List<CoworkUsageUserDetailRow> rows)
        {
            if (_userGroupsCache == null || _userGroupsFilter == null || _userGroupsFilter.Patterns.Count == 0) return rows;

            _logger.LogWarning($"Cowork usage report: a user group filter is configured, so up to {rows.Count:N0} Entra group-membership lookups may be issued.");
            var inScope = new List<CoworkUsageUserDetailRow>(rows.Count);
            foreach (var row in rows)
            {
                if (await _userGroupsCache.IsInGroupsFilter(row.UserPrincipalName, _userGroupsFilter)) inScope.Add(row);
            }
            return inScope;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength);
        }
    }

    public class CoworkUsageUserDetailRow
    {
        public DateTime ReportRefreshDate { get; set; }
        public string UserPrincipalName { get; set; }
        public int? ReportPeriodDays { get; set; }
        public int? TotalTasks { get; set; }
        public int? ScheduledTasks { get; set; }
        public int? UserInitiatedTasks { get; set; }
        public int? ActiveDays { get; set; }
        public DateTime? LastActivityDate { get; set; }
        public bool? RetainedUser { get; set; }
    }

    public static class CoworkUsageUserDetailParser
    {
        private static readonly string[] UserProperties = { "userPrincipalName", "userId", "User ID" };
        private static readonly string[] DetailsByPeriodProperties = { "coworkActivityUserDetailsByPeriod", "coworkUsageUserDetailsByPeriod" };
        private static readonly string[] TotalTaskProperties = { "totalTasks", "Total tasks", "totalCoworkTasks" };
        private static readonly string[] ScheduledTaskProperties = { "scheduledTasks", "Scheduled tasks" };
        private static readonly string[] UserInitiatedTaskProperties = { "userInitiatedTasks", "User-initiated tasks", "user-initiatedTasks" };
        private static readonly string[] ActiveDaysProperties = { "activeDays", "Active days" };
        private static readonly string[] LastActivityProperties = { "lastActivityDate", "Last activity date" };
        private static readonly string[] RetainedProperties = { "retainedCoworkUser", "retainedUser", "Retained Cowork user" };

        public static List<CoworkUsageUserDetailRow> Parse(IEnumerable<JObject> reports)
        {
            var results = new List<CoworkUsageUserDetailRow>();
            if (reports == null) return results;
            foreach (var report in reports) ParseUserInto(report, results);
            return results;
        }

        public static void ParseUserInto(JObject user, List<CoworkUsageUserDetailRow> into)
        {
            if (user == null) return;
            var refreshDate = CopilotUserCountReportParser.GetDate(user, "reportRefreshDate")
                ?? CopilotUserCountReportParser.GetDate(user, "Report Refresh Date")
                ?? DateTime.UtcNow.Date;
            var upn = GetStringAny(user, UserProperties)?.Trim();
            if (string.IsNullOrWhiteSpace(upn)) return;

            var periods = GetArrayAny(user, DetailsByPeriodProperties);
            if (periods == null || periods.Count == 0)
            {
                into.Add(BuildRow(user, refreshDate, upn, null));
                return;
            }

            foreach (var period in periods.OfType<JObject>()) into.Add(BuildRow(user, refreshDate, upn, period));
        }

        private static CoworkUsageUserDetailRow BuildRow(JObject user, DateTime refreshDate, string upn, JObject period)
        {
            var source = period ?? user;
            return new CoworkUsageUserDetailRow
            {
                ReportRefreshDate = refreshDate.Date,
                UserPrincipalName = upn,
                ReportPeriodDays = CopilotUserCountReportParser.GetInt(source, "reportPeriod"),
                TotalTasks = GetIntAny(source, TotalTaskProperties),
                ScheduledTasks = GetIntAny(source, ScheduledTaskProperties),
                UserInitiatedTasks = GetIntAny(source, UserInitiatedTaskProperties),
                ActiveDays = GetIntAny(source, ActiveDaysProperties),
                LastActivityDate = GetDateAny(source, LastActivityProperties),
                RetainedUser = GetBoolAny(source, RetainedProperties),
            };
        }

        private static string GetStringAny(JObject source, IEnumerable<string> properties)
        {
            foreach (var property in properties)
            {
                var value = source[property]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static JArray GetArrayAny(JObject source, IEnumerable<string> properties)
        {
            foreach (var property in properties)
            {
                if (source[property] is JArray arr) return arr;
            }
            return null;
        }

        private static int? GetIntAny(JObject source, IEnumerable<string> properties)
        {
            foreach (var property in properties)
            {
                var value = CopilotUserCountReportParser.GetInt(source, property);
                if (value.HasValue) return value;
            }
            return null;
        }

        private static DateTime? GetDateAny(JObject source, IEnumerable<string> properties)
        {
            foreach (var property in properties)
            {
                var value = CopilotUserCountReportParser.GetDate(source, property);
                if (value.HasValue) return value;
            }
            return null;
        }

        private static bool? GetBoolAny(JObject source, IEnumerable<string> properties)
        {
            foreach (var property in properties)
            {
                var token = source[property];
                if (token == null) continue;
                if (token.Type == JTokenType.Boolean) return token.Value<bool>();
                var text = token.Value<string>();
                if (bool.TryParse(text, out var b)) return b;
                if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return null;
        }
    }

    public static class CoworkUsageReportPolicy
    {
        public static int ApplyPeriodKeys(List<CoworkUsageUserDetailRow> rows, int? requestPeriodDays)
        {
            var removed = rows.RemoveAll(r => !r.ReportPeriodDays.HasValue && !requestPeriodDays.HasValue);
            foreach (var row in rows)
            {
                if (!row.ReportPeriodDays.HasValue) row.ReportPeriodDays = requestPeriodDays.Value;
            }
            return removed;
        }
    }

    public interface ICoworkUsagePersistenceManager
    {
        Task<CopilotUsageUpsertResult> UpsertUserDetailAsync(IReadOnlyList<CoworkUsageUserDetailRow> rows);
        Task RecordReportLoadAsync(CopilotUsageReportImportLog importLog);
        Task RecordReportLoadAfterFailureAsync(CopilotUsageReportImportLog importLog);
    }

    public class SqlCoworkUsagePersistenceManager : ICoworkUsagePersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;
        private readonly IAnalyticsDbContextFactory _contextFactory;
        public int SaveBatchSize { get; set; } = 1000;

        public SqlCoworkUsagePersistenceManager(AnalyticsEntitiesContext db, ILogger logger, IAnalyticsDbContextFactory contextFactory = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
        }

        public async Task<CopilotUsageUpsertResult> UpsertUserDetailAsync(IReadOnlyList<CoworkUsageUserDetailRow> rows)
        {
            var result = new CopilotUsageUpsertResult();
            if (rows == null || rows.Count == 0) return result;

            var resolver = new SqlCopilotUsagePersistenceManager(_db, _logger, _contextFactory) { SaveBatchSize = SaveBatchSize };
            var resolution = await resolver.ResolveUserIdsAsync(rows.Select(r => r.UserPrincipalName));
            var importable = rows.Where(r => resolution.IdsByUpn.ContainsKey(r.UserPrincipalName)).ToList();
            if (importable.Count == 0) return result;

            // EF's connection is a Microsoft.Data.SqlClient connection (SPOInsightsDBConfiguration, #511), so
            // this file must use that namespace too: casting it to System.Data.SqlClient.SqlConnection threw
            // InvalidCastException on every call, and no Cowork usage row was ever saved.
            var con = (SqlConnection)_db.Database.Connection;
            var openedHere = con.State != ConnectionState.Open;
            if (openedHere)
            {
                // Opening EF's connection directly bypasses AzureSqlAccessTokenInterceptor, so attach (or
                // refresh) the Entra token here. Without it an Entra-only Azure SQL install would reopen with no
                // token, or with the expired one left from EF's last open of this long-lived context (#609).
                AzureSqlTokenAuth.ApplyAccessTokenIfNeeded(con);
                await con.OpenAsync();
            }
            try
            {
                using (var create = con.CreateCommand())
                {
                    create.CommandText = @"CREATE TABLE #cowork_usage_import (
    user_id int NOT NULL,
    [date] datetime NOT NULL,
    report_period_days int NOT NULL,
    total_tasks int NULL,
    scheduled_tasks int NULL,
    user_initiated_tasks int NULL,
    active_days int NULL,
    last_activity_date datetime NULL,
    retained_user bit NULL
);";
                    await create.ExecuteNonQueryAsync();
                }

                var table = new DataTable();
                foreach (var name in new[] { "user_id", "report_period_days", "total_tasks", "scheduled_tasks", "user_initiated_tasks", "active_days" }) table.Columns.Add(name, typeof(int));
                table.Columns.Add("date", typeof(DateTime));
                table.Columns.Add("last_activity_date", typeof(DateTime));
                table.Columns.Add("retained_user", typeof(bool));

                foreach (var row in importable)
                {
                    var dr = table.NewRow();
                    dr["user_id"] = resolution.IdsByUpn[row.UserPrincipalName];
                    dr["date"] = row.ReportRefreshDate.Date;
                    dr["report_period_days"] = row.ReportPeriodDays.Value;
                    dr["total_tasks"] = (object)row.TotalTasks ?? DBNull.Value;
                    dr["scheduled_tasks"] = (object)row.ScheduledTasks ?? DBNull.Value;
                    dr["user_initiated_tasks"] = (object)row.UserInitiatedTasks ?? DBNull.Value;
                    dr["active_days"] = (object)row.ActiveDays ?? DBNull.Value;
                    dr["last_activity_date"] = (object)row.LastActivityDate ?? DBNull.Value;
                    dr["retained_user"] = (object)row.RetainedUser ?? DBNull.Value;
                    table.Rows.Add(dr);
                }

                using (var bulk = new SqlBulkCopy(con))
                {
                    bulk.DestinationTableName = "#cowork_usage_import";
                    foreach (DataColumn col in table.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                    await bulk.WriteToServerAsync(table);
                }

                using (var merge = con.CreateCommand())
                {
                    merge.CommandText = @"MERGE dbo.cowork_usage_user_activity_log AS target
USING #cowork_usage_import AS source
ON target.[date] = source.[date]
AND target.user_id = source.user_id
AND target.report_period_days = source.report_period_days
WHEN MATCHED AND (
       ISNULL(target.total_tasks, -1) <> ISNULL(source.total_tasks, -1)
    OR ISNULL(target.scheduled_tasks, -1) <> ISNULL(source.scheduled_tasks, -1)
    OR ISNULL(target.user_initiated_tasks, -1) <> ISNULL(source.user_initiated_tasks, -1)
    OR ISNULL(target.active_days, -1) <> ISNULL(source.active_days, -1)
    OR ISNULL(target.last_activity_date, '19000101') <> ISNULL(source.last_activity_date, '19000101')
    OR ISNULL(CAST(target.retained_user AS int), -1) <> ISNULL(CAST(source.retained_user AS int), -1))
THEN UPDATE SET total_tasks = source.total_tasks,
                scheduled_tasks = source.scheduled_tasks,
                user_initiated_tasks = source.user_initiated_tasks,
                active_days = source.active_days,
                last_activity_date = source.last_activity_date,
                retained_user = source.retained_user
WHEN NOT MATCHED THEN INSERT (user_id, [date], report_period_days, total_tasks, scheduled_tasks, user_initiated_tasks, active_days, last_activity_date, retained_user)
VALUES (source.user_id, source.[date], source.report_period_days, source.total_tasks, source.scheduled_tasks, source.user_initiated_tasks, source.active_days, source.last_activity_date, source.retained_user);";
                    result.Inserted = await merge.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                if (openedHere) con.Close();
            }
            return result;
        }

        public async Task RecordReportLoadAsync(CopilotUsageReportImportLog importLog)
        {
            _db.CopilotUsageReportImportLogs.Add(importLog);
            await _db.SaveChangesAsync();
        }

        public async Task RecordReportLoadAfterFailureAsync(CopilotUsageReportImportLog importLog)
        {
            using (var freshDb = _contextFactory.Create())
            {
                freshDb.CopilotUsageReportImportLogs.Add(importLog);
                await freshDb.SaveChangesAsync();
            }
        }
    }
}
