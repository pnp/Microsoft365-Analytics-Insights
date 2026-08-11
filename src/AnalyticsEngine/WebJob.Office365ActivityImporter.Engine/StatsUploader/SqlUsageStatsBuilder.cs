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
        public SqlUsageStatsBuilder(AnalyticsEntitiesContext db, ILogger logger, Guid tenantId) : base(logger, tenantId)
        {
            _db = db;
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
            var stats = AnonUsageStatsModelLoader.Load(_tenantId, lastSettings);
            stats.TableStats = await GetStatsFromSql();
            stats.DataPointsFromAITotal = await _db.TeamChannelStats.Where(s => s.SentimentScore.HasValue).CountAsync();
            stats.BuildVersionLabel = Common.Entities.BuildConstants.BuildLabel;
            return stats;
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
