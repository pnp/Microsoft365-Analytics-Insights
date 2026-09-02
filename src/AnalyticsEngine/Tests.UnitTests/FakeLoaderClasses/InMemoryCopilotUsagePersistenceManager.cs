using Common.Entities.Entities.UsageReports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ICopilotUsagePersistenceManager"/> - the Copilot usage tables replaced by
    /// dictionaries, so the whole import runs with zero Graph and zero SQL Server. See issue #370.
    ///
    /// It reproduces the two behaviours the real adapter is judged on: the "only write when a value actually
    /// moved" rule (so <see cref="CopilotUsageUpsertResult.Unchanged"/> is meaningful), and the rule that a
    /// user is only created when its e-mail domain is one already known.
    /// </summary>
    public class InMemoryCopilotUsagePersistenceManager : ICopilotUsagePersistenceManager
    {
        private int _nextUserId = 1;

        /// <summary>Users the database already knows. Seed this to control which domains are recognised.</summary>
        public Dictionary<string, int> Users { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-user activity rows, keyed exactly as the unique index is: date | period | user id.</summary>
        public Dictionary<string, CopilotUsageUserActivityLog> UserDetail { get; } = new Dictionary<string, CopilotUsageUserActivityLog>();

        /// <summary>Aggregate rows, keyed as the unique index is: type | period | date | app.</summary>
        public Dictionary<string, CopilotUserCountLog> UserCounts { get; } = new Dictionary<string, CopilotUserCountLog>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every import-log row recorded, in order.</summary>
        public List<CopilotUsageReportImportLog> ImportLogs { get; } = new List<CopilotUsageReportImportLog>();

        /// <summary>Import-log rows recorded through the after-a-failure path (a fresh context in production).</summary>
        public List<CopilotUsageReportImportLog> ImportLogsRecordedAfterFailure { get; } = new List<CopilotUsageReportImportLog>();

        /// <summary>The result of the most recent upsert, so a test can assert Inserted/Updated/Unchanged.</summary>
        public CopilotUsageUpsertResult LastUserCountUpsert { get; private set; }

        /// <summary>Set to make the next per-user upsert throw, to exercise the failure diagnostic path.</summary>
        public Exception FailUserDetailUpsertWith { get; set; }

        /// <summary>Set to make the next aggregate upsert throw.</summary>
        public Exception FailUserCountUpsertWith { get; set; }

        /// <summary>The UPNs the loader asked to resolve, most recent call last.</summary>
        public List<string> LastResolveRequest { get; } = new List<string>();

        public void SeedUser(string upn) => Users[upn] = _nextUserId++;

        public Task<CopilotUserIdResolution> ResolveUserIdsAsync(IEnumerable<string> userPrincipalNames)
        {
            var requested = (userPrincipalNames ?? Enumerable.Empty<string>()).ToList();
            LastResolveRequest.Clear();
            LastResolveRequest.AddRange(requested);

            var knownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in Users.Keys)
            {
                var domain = CopilotUsageReportPolicy.DomainOf(existing);
                if (domain != null) knownDomains.Add(domain);
            }

            var plan = CopilotUsageReportPolicy.PlanNewUsers(requested, Users, knownDomains);
            foreach (var upn in plan.ToCreate)
            {
                Users[upn.ToLowerInvariant()] = _nextUserId++;
            }

            return Task.FromResult(new CopilotUserIdResolution(
                new Dictionary<string, int>(Users, StringComparer.OrdinalIgnoreCase), plan.ToCreate.Count, plan.SkippedUnknownDomain));
        }

        public Task<CopilotUsageUpsertResult> UpsertUserDetailAsync(IReadOnlyList<CopilotUsageUserDetailRow> rows,
            IReadOnlyDictionary<string, int> userIdsByUpn, bool hasVersion2Data)
        {
            if (FailUserDetailUpsertWith != null) throw FailUserDetailUpsertWith;

            var result = new CopilotUsageUpsertResult();
            foreach (var row in rows)
            {
                if (!userIdsByUpn.TryGetValue(row.UserPrincipalName, out var userId)) continue;

                var date = row.ReportRefreshDate.Date;
                var period = row.ReportPeriodDays.Value;
                var key = $"{date:yyyy-MM-dd}|{period}|{userId}";

                var isNew = !UserDetail.TryGetValue(key, out var log);
                if (isNew)
                {
                    log = new CopilotUsageUserActivityLog { Date = date, UserID = userId, ReportPeriodDays = period };
                }

                var changed = CopilotUsageUserDetailLoader.Populate(log, row, hasVersion2Data);

                if (isNew)
                {
                    UserDetail[key] = log;
                    result.Inserted++;
                }
                else if (changed) result.Updated++;
                else result.Unchanged++;
            }

            return Task.FromResult(result);
        }

        public Task<CopilotUsageUpsertResult> UpsertUserCountsAsync(IReadOnlyList<CopilotUserCountLog> rows, string reportType)
        {
            if (FailUserCountUpsertWith != null) throw FailUserCountUpsertWith;

            var result = new CopilotUsageUpsertResult();            foreach (var row in rows)
            {
                var key = $"{row.ReportType}|{(row.ReportPeriodDays.HasValue ? row.ReportPeriodDays.Value.ToString() : string.Empty)}|{row.ReportDate:yyyy-MM-dd}|{row.AppName}";
                if (UserCounts.TryGetValue(key, out var stored))
                {
                    // The production rule, not a copy of it - so a change here (e.g. letting the refresh
                    // date count as a change again) fails these tests rather than only the DB-backed ones.
                    if (!CopilotUsageReportPolicy.UserCountValueChanged(stored, row))
                    {
                        result.Unchanged++;
                        continue;
                    }

                    stored.ReportRefreshDate = row.ReportRefreshDate;
                    stored.EnabledUsers = row.EnabledUsers;
                    stored.ActiveUsers = row.ActiveUsers;
                    stored.PromptsSubmitted = row.PromptsSubmitted;
                    stored.AveragePromptsSubmitted = row.AveragePromptsSubmitted;
                    result.Updated++;
                }
                else
                {
                    UserCounts[key] = row;
                    result.Inserted++;
                }
            }

            LastUserCountUpsert = result;
            return Task.FromResult(result);
        }

        public Task RecordReportLoadAsync(CopilotUsageReportImportLog importLog)
        {
            ImportLogs.Add(importLog);
            return Task.CompletedTask;
        }

        public Task RecordReportLoadAfterFailureAsync(CopilotUsageReportImportLog importLog)
        {
            ImportLogs.Add(importLog);
            ImportLogsRecordedAfterFailure.Add(importLog);
            return Task.CompletedTask;
        }
    }
}
