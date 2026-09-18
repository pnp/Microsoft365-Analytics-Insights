using Common.Entities;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// Stats builder for SQL 
    /// </summary>
    public class SqlUsageStatsBuilder : BaseUsageStatsBuilder
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly IAnonAdoptionStatsProvider _adoptionStatsProvider;

        public SqlUsageStatsBuilder(AnalyticsEntitiesContext db, ILogger logger, Guid tenantId)
            : this(db, logger, tenantId, null)
        {
        }

        /// <param name="adoptionStatsProvider">
        /// Optional. Null keeps the payload to the deployment stats it has always carried.
        /// </param>
        public SqlUsageStatsBuilder(
            AnalyticsEntitiesContext db, ILogger logger, Guid tenantId, IAnonAdoptionStatsProvider adoptionStatsProvider)
            : base(logger, tenantId)
        {
            _db = db;
            _adoptionStatsProvider = adoptionStatsProvider;
        }

        public override async Task<BaseSolutionInstallConfig> GetLastAppliedSolutionConfig()
        {
            var latestConfig = await _db.ConfigStates.OrderByDescending(s => s.DateApplied).Take(1).ToListAsync();
            if (latestConfig.Count == 1 && !string.IsNullOrEmpty(latestConfig[0].ConfigJson))
            {
                try
                {
                    return JsonConvert.DeserializeObject<BaseSolutionInstallConfig>(latestConfig[0].ConfigJson);
                }
                catch (JsonReaderException)
                {
                    // Ignore
                }
            }
            return null;
        }

        /// <summary>
        /// Build stats
        /// </summary>
        public override async Task<AnonUsageStatsModel> LoadUsageStatsModel(BaseSolutionInstallConfig lastSettings)
        {
            // The previous report is the install's own memory: it carries the anonymous client id
            // forward (so this install stays one client across reports), and the last adoption block
            // (so the daily payload still describes adoption on the six days a week the analysis does
            // not run).
            var previous = await LoadPreviousReport();

            var stats = AnonUsageStatsModelLoader.Load(_tenantId, lastSettings, previous?.AnonClientId);
            LogClientIdentity(stats, previous);

            stats.TableStats = await GetStatsFromSql();
            stats.DataPointsFromAITotal = await _db.TeamChannelStats.Where(s => s.SentimentScore.HasValue).CountAsync();
            stats.BuildVersionLabel = Common.Entities.BuildConstants.BuildLabel;
            stats.Adoption = await LoadAdoptionStats(lastSettings, previous);
            return stats;
        }

        /// <summary>
        /// The freshly computed adoption block, or the last one we stored.
        /// </summary>
        /// <remarks>
        /// Re-sending the stored block keeps every payload self-describing, which matters because the
        /// server's merge would otherwise leave a reader unable to tell a genuinely unchanged figure
        /// from one the client simply stopped sending. The block keeps its ORIGINAL
        /// <see cref="AnonAdoptionStats.GeneratedUtc"/>, so a consumer can see it is up to a week old
        /// and de-duplicate the history container on it.
        /// </remarks>
        private async Task<AnonAdoptionStats> LoadAdoptionStats(
            BaseSolutionInstallConfig lastSettings, AnonUsageStatsModel previous)
        {
            if (_adoptionStatsProvider == null) return null;

            var fresh = await _adoptionStatsProvider.GetAdoptionStats(lastSettings);
            if (fresh != null) return fresh;

            var cached = previous?.Adoption;
            if (cached != null)
            {
                _logger?.LogInformation(
                    $"{UsageStatsManager.LOG_PREFIX}re-sending the adoption metrics computed {cached.GeneratedUtc} "
                    + "(they are recalculated weekly, not daily).");
            }

            return cached;
        }

        /// <summary>
        /// Logs the anonymous client id so an operator can find it in their own Application Insights.
        /// </summary>
        /// <remarks>
        /// This is the only way a customer who WANTS to be identified can tell us which anonymous
        /// record is theirs - the id is random by design and we have no way to work it out. See
        /// <see cref="AnonUsageStatsModelLoader.ResolveAnonClientId"/>.
        /// </remarks>
        private void LogClientIdentity(AnonUsageStatsModel stats, AnonUsageStatsModel previous)
        {
            if (_logger == null) return;

            var previousId = previous?.AnonClientId;
            var rotated = !string.IsNullOrEmpty(previousId)
                          && !string.Equals(previousId, stats.AnonClientId, StringComparison.OrdinalIgnoreCase);

            if (rotated)
            {
                // Worth calling out: from the service's point of view this install becomes a brand new
                // client, which would otherwise look like a fault rather than a deliberate one-off.
                _logger.LogInformation(
                    $"{UsageStatsManager.LOG_PREFIX}this install's anonymous telemetry id has been replaced with a new "
                    + "random one, because the previous id was derived from the tenant id and could be traced back to "
                    + "this tenant. Reporting history before now belongs to the old id.");
            }

            _logger.LogInformation(
                $"{UsageStatsManager.LOG_PREFIX}anonymous telemetry client id for this install is "
                + $"'{stats.AnonClientId}'. It is random and contains nothing about this tenant. Quote it to the "
                + "project team if you would like your reports associated with your organisation.");
        }

        /// <summary>The most recent report this install stored, or null if it has never uploaded one.</summary>
        private async Task<AnonUsageStatsModel> LoadPreviousReport()
        {
            try
            {
                var latest = await _db.TelemetryReports
                    .OrderByDescending(r => r.ReportSubmitted)
                    .Take(1)
                    .ToListAsync();

                if (latest.Count != 1 || string.IsNullOrEmpty(latest[0].Report)) return null;

                return JsonConvert.DeserializeObject<AnonUsageStatsModel>(latest[0].Report);
            }
            catch (JsonException ex)
            {
                // An unreadable stored report must not stop a new one being built. The consequence is
                // a new client id, which is the same outcome as a first-ever report.
                _logger?.LogWarning(
                    $"{UsageStatsManager.LOG_PREFIX}could not read the last stored telemetry report ({ex.Message}); "
                    + "continuing with a new anonymous client id.");
                return null;
            }
        }

        private async Task<List<AnonUsageStatsModel.TableStat>> GetStatsFromSql()
        {
            // Metadata-only (sys.*) so this stays cheap on a large tenant - no scan of customer data.
            // Row counts come from the heap/clustered index only (index_id 0 or 1) via the CROSS APPLY, while
            // size sums every allocation unit. Grouping on schema+table alone means a table can never be split
            // into several output rows just because it gained an extra (e.g. filtered) index.
            var sql = @"
SELECT 
    s.name AS SchemaName,
    t.name AS TableName,
    MAX(tableRows.[Rows]) AS [Rows],
    CAST(ROUND(((SUM(a.total_pages) * 8) / 1024.00), 2) AS NUMERIC(36, 2)) AS TotalSpaceMB
FROM 
    sys.tables t
INNER JOIN      
    sys.indexes i ON t.object_id = i.object_id
INNER JOIN 
    sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN 
    sys.allocation_units a ON p.partition_id = a.container_id
INNER JOIN 
    sys.schemas s ON t.schema_id = s.schema_id
CROSS APPLY 
    (SELECT SUM(pRows.rows) FROM sys.partitions pRows WHERE pRows.object_id = t.object_id AND pRows.index_id IN (0, 1)) AS tableRows([Rows])
WHERE 
    t.is_ms_shipped = 0
GROUP BY 
    s.name, t.name
ORDER BY 
    TotalSpaceMB DESC, t.name
";

            return await _db.Database.SqlQuery<AnonUsageStatsModel.TableStat>(sql).ToListAsync();
        }

        public override async Task SaveUsageStatsModelToDatabase(AnonUsageStatsModel latestStats)
        {
            _db.TelemetryReports.Add(new Common.Entities.Entities.TelemetryReport
            {
                Report = JsonConvert.SerializeObject(latestStats),
                ReportSubmitted = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
    }
}
