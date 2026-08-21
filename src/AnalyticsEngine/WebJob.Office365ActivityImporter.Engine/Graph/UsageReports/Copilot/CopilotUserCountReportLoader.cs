using Common.Entities;
using Common.Entities.Entities.UsageReports;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Imports the two tenant-aggregate Copilot reports (user-count summary and user-count trend) into
    /// <see cref="CopilotUserCountLog"/>.
    ///
    /// These are the cheap ones - a few thousand rows regardless of tenant size, no per-user joins, and
    /// completely unaffected by the tenant's concealed-user-information setting - so they are imported first
    /// and independently of the per-user detail. Even on a tenant where the per-user report is unusable
    /// because identities are hashed, these still give an enabled-vs-active adoption picture that matches the
    /// Microsoft 365 admin centre.
    /// </summary>
    public class CopilotUserCountReportLoader
    {
        private readonly ICopilotReportSource _reportSource;
        private readonly ILogger _logger;

        /// <summary>
        /// Rows persisted per SaveChanges. These reports are small (a period's worth of days x a dozen apps),
        /// but a D180 trend backfill across many apps still runs to a few thousand rows, so commit in batches
        /// rather than building one enormous command tree.
        /// </summary>
        public int SaveBatchSize { get; set; } = 500;

        public CopilotUserCountReportLoader(ICopilotReportSource reportSource, ILogger logger)
        {
            _reportSource = reportSource ?? throw new ArgumentNullException(nameof(reportSource));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<int> LoadAndSaveSummaryAsync(AnalyticsEntitiesContext db, string period, string version = CopilotReportVersions.V2)
        {
            return LoadAndSaveAsync(db, new CopilotReportRequest(CopilotReportNames.UserCountSummary, period, version));
        }

        public Task<int> LoadAndSaveTrendAsync(AnalyticsEntitiesContext db, string period, string version = CopilotReportVersions.V2)
        {
            return LoadAndSaveAsync(db, new CopilotReportRequest(CopilotReportNames.UserCountTrend, period, version));
        }

        /// <summary>
        /// Downloads, parses and upserts one aggregate report. Returns the number of rows written to SQL
        /// (0 when everything Graph returned already matched what we hold).
        /// </summary>
        public async Task<int> LoadAndSaveAsync(AnalyticsEntitiesContext db, CopilotReportRequest request)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var isTrend = request.ReportName == CopilotReportNames.UserCountTrend;
            if (!isTrend && request.ReportName != CopilotReportNames.UserCountSummary)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.ReportName,
                    $"{nameof(CopilotUserCountReportLoader)} handles only the Copilot user-count summary and trend reports.");
            }

            var importLog = new CopilotUsageReportImportLog
            {
                ReportName = request.ReportName,
                ReportVersion = request.Version,
                ReportPeriod = request.Period,
                ImportedUtc = DateTime.UtcNow,
            };

            List<CopilotUserCountLog> parsed;
            try
            {
                var reports = await _reportSource.LoadReportAsync(request);

                parsed = isTrend
                    ? CopilotUserCountReportParser.ParseTrend(reports)
                    : CopilotUserCountReportParser.ParseSummary(reports);

                // Graph nests the numbers one level down (adoptionByProduct / adoptionByDate), so "no rows"
                // can mean either "no Copilot licences" or "the shape changed and we understood none of it".
                // Counting the entries we should have produced rows for makes a schema change visible instead
                // of silently importing nothing - which matters most for the one-off D180 history backfill,
                // where recording an empty parse as a clean import would mark the backfill done, switch later
                // runs to D28, and lose the missing history for good.
                var entryCount = CountEntries(reports, isTrend);
                if (entryCount > 0 && parsed.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Copilot report {request} returned {entryCount} entry/entries but none could be parsed - " +
                        "no recognisable '<app>EnabledUsers' / '<app>ActiveUsers' values were found. " +
                        "Microsoft has probably changed the report schema; the import was stopped rather than recording an empty snapshot.");
                }
            }
            catch (GraphResourceNotFoundException ex)
            {
                // 404 is a deliberately-tolerated outcome, not a failure: the report endpoint doesn't exist
                // in this cloud (these reports are global-cloud only - not US Government, not 21Vianet), or
                // Microsoft has retired this report version. Retrying achieves nothing, so this counts as a
                // completed run for cadence purposes - but the reason is recorded on the import log so the
                // Health page shows why the table is empty instead of implying the tenant has no licences.
                importLog.RowsRead = 0;
                importLog.Error = Truncate($"Report not available: {ex.Message}", 1000);
                await SaveImportLog(db, importLog);

                _logger.LogWarning($"Copilot aggregate report {request} is not available on this tenant: {ex.Message} " +
                    "These reports exist only in the global cloud (not US Government or 21Vianet). No rows were imported.");
                return 0;
            }
            catch (Exception ex)
            {
                importLog.Error = Truncate(ex.Message, 1000);
                await SaveImportLog(db, importLog);
                throw;
            }

            importLog.RowsRead = parsed.Count;
            importLog.ReportRefreshDate = parsed.Count > 0 ? parsed.Max(r => r.ReportRefreshDate) : (DateTime?)null;

            if (parsed.Count == 0)
            {
                _logger.LogWarning($"Copilot aggregate report {request} returned no rows. " +
                    "The report downloaded successfully and was genuinely empty, which is expected on a tenant with no Microsoft 365 Copilot licences.");
                await SaveImportLog(db, importLog);
                return 0;
            }

            var written = await UpsertOrRecordFailure(db, parsed, isTrend ? CopilotUserCountReportTypes.Trend : CopilotUserCountReportTypes.Summary, importLog);
            importLog.RowsSaved = written;
            await SaveImportLog(db, importLog);

            _logger.LogInformation($"Copilot aggregate report {request}: parsed {parsed.Count} row(s), wrote {written} to SQL.");
            return written;
        }

        /// <summary>
        /// Number of nested entries the response contained, i.e. how many app-count blocks we expected to
        /// turn into rows.
        /// </summary>
        private static int CountEntries(List<JObject> reports, bool isTrend)
        {
            if (reports == null) return 0;

            var property = isTrend ? "adoptionByDate" : "adoptionByProduct";
            return reports.Where(r => r != null)
                          .Select(r => r[property] as JArray)
                          .Where(a => a != null)
                          .Sum(a => a.Count);
        }

        private async Task<int> UpsertOrRecordFailure(AnalyticsEntitiesContext db, List<CopilotUserCountLog> parsed,
            string reportType, CopilotUsageReportImportLog importLog)
        {
            try
            {
                return await UpsertAsync(db, parsed, reportType);
            }
            catch (Exception ex)
            {
                // Persistence failures must reach the Health page too, and must be written on a FRESH context:
                // the one that just failed a SaveChanges can be left with entities in a broken state.
                importLog.Error = Truncate(ex.Message, 1000);
                using (var freshDb = new AnalyticsEntitiesContext())
                {
                    await SaveImportLog(freshDb, importLog);
                }
                throw;
            }
        }

        private async Task<int> UpsertAsync(AnalyticsEntitiesContext db, List<CopilotUserCountLog> parsed, string reportType)
        {
            var minDate = parsed.Min(r => r.ReportDate);
            var maxDate = parsed.Max(r => r.ReportDate);

            // One query for everything we might collide with. Bounded by the report's own date range, so this
            // stays small even after years of retained history.
            var existing = await db.CopilotUserCountLogs
                .Where(r => r.ReportType == reportType && r.ReportDate >= minDate && r.ReportDate <= maxDate)
                .ToListAsync();

            var existingByKey = new Dictionary<string, CopilotUserCountLog>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in existing)
            {
                existingByKey[KeyOf(row)] = row;
            }

            var written = 0;
            var pending = 0;
            var autoDetectWasEnabled = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
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
                        if (!HasChanged(stored, row)) continue;

                        stored.ReportRefreshDate = row.ReportRefreshDate;
                        stored.EnabledUsers = row.EnabledUsers;
                        stored.ActiveUsers = row.ActiveUsers;
                        stored.PromptsSubmitted = row.PromptsSubmitted;
                        stored.AveragePromptsSubmitted = row.AveragePromptsSubmitted;
                        db.Entry(stored).State = EntityState.Modified;
                    }
                    else
                    {
                        db.CopilotUserCountLogs.Add(row);
                        existingByKey[key] = row;
                    }

                    written++;
                    pending++;
                    if (pending >= SaveBatchSize)
                    {
                        await db.SaveChangesAsync();
                        pending = 0;
                    }
                }

                if (pending > 0) await db.SaveChangesAsync();
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }

            return written;
        }

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

        private static async Task SaveImportLog(AnalyticsEntitiesContext db, CopilotUsageReportImportLog importLog)
        {
            db.CopilotUsageReportImportLogs.Add(importLog);
            await db.SaveChangesAsync();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength);
        }
    }
}
