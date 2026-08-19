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
        private readonly ICopilotReportCsvSource _csvSource;
        private readonly ILogger _logger;

        /// <summary>
        /// Rows persisted per SaveChanges. These reports are small (a period's worth of days x a dozen apps),
        /// but a D180 trend backfill across many apps still runs to a few thousand rows, so commit in batches
        /// rather than building one enormous command tree.
        /// </summary>
        public int SaveBatchSize { get; set; } = 500;

        public CopilotUserCountReportLoader(ICopilotReportCsvSource csvSource, ILogger logger)
        {
            _csvSource = csvSource ?? throw new ArgumentNullException(nameof(csvSource));
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
                _logger.LogInformation($"Loading Copilot aggregate report {request}...");
                var csv = await _csvSource.GetReportCsvAsync(request);
                var table = CsvReportTable.Parse(csv);

                // A renamed Microsoft column would otherwise yield zero rows, which is indistinguishable from
                // the perfectly normal "this tenant has no Copilot licences" case.
                CsvReportTable.RequireHeaders(table.Headers, request,
                    isTrend
                        ? new[] { "Report Refresh Date", "Report Date" }
                        : new[] { "Report Refresh Date", "Report Period" });

                // The metadata columns alone aren't enough: a CSV carrying them but no "<app> Enabled Users" /
                // "<app> Active Users" pair parses to zero rows and would be recorded as a successful
                // "this tenant has no Copilot licences" response.
                var apps = CopilotUserCountReportParser.DiscoverAppNames(table.Headers);
                if (apps.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Copilot report {request} contains no '<app> Enabled Users' / '<app> Active Users' columns. " +
                        $"Columns returned: {string.Join(", ", table.Headers)}. " +
                        "Microsoft has probably changed the report schema; the import was stopped rather than recording an empty snapshot.");
                }

                parsed = isTrend
                    ? CopilotUserCountReportParser.ParseTrend(table)
                    : CopilotUserCountReportParser.ParseSummary(table);
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
                    "That is expected on a tenant with no Microsoft 365 Copilot licences, and also happens outside the global cloud, where these reports aren't available.");
                await SaveImportLog(db, importLog);
                return 0;
            }

            var written = await UpsertOrRecordFailure(db, parsed, isTrend ? CopilotUserCountReportTypes.Trend : CopilotUserCountReportTypes.Summary, importLog);
            importLog.RowsSaved = written;
            await SaveImportLog(db, importLog);

            _logger.LogInformation($"Copilot aggregate report {request}: parsed {parsed.Count} row(s), wrote {written} to SQL.");
            return written;
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
