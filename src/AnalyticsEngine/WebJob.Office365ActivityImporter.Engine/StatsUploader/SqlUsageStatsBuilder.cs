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
        public SqlUsageStatsBuilder(AnalyticsEntitiesContext db, ILogger tracer, Guid tenantId) : base(tracer, tenantId)
        {
            _db = db;
        }

        public async Task<AnonUsageStatsModel> GetLatestSavedDbStats()
        {
            var latestReports = await _db.TelemetryReports.OrderByDescending(s => s.ReportSubmitted).Take(1).ToListAsync();
            if (latestReports.Count == 1 && !string.IsNullOrEmpty(latestReports[0].Report))
            {
                try
                {
                    return JsonConvert.DeserializeObject<AnonUsageStatsModel>(latestReports[0].Report);
                }
                catch (JsonReaderException)
                {
                    // Ignore
                }
            }
            return null;
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
            stats.BuildVersionLabel = System.Configuration.ConfigurationManager.AppSettings["BuildLabel"];
            return stats;
        }

        private Task<List<AnonUsageStatsModel.TableStat>> GetStatsFromSql()
        {
            var sql = @"
SELECT 
    t.NAME as TableName,
    p.rows as [Rows],
    CAST(ROUND(((SUM(a.total_pages) * 8) / 1024.00), 2) AS NUMERIC(36, 2)) AS TotalSpaceMB
FROM 
    sys.tables t
INNER JOIN      
    sys.indexes i ON t.OBJECT_ID = i.object_id
INNER JOIN 
    sys.partitions p ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
INNER JOIN 
    sys.allocation_units a ON p.partition_id = a.container_id
LEFT OUTER JOIN 
    sys.schemas s ON t.schema_id = s.schema_id
WHERE 
    t.is_ms_shipped = 0
GROUP BY 
    t.Name, s.Name, p.Rows
ORDER BY 
    TotalSpaceMB DESC, t.Name
";

            var resuls = _db.Database.SqlQuery<AnonUsageStatsModel.TableStat>(sql);

            return Task.FromResult(resuls.ToList());
        }

        public override async Task SaveUsageStatsModelToDatabase(AnonUsageStatsModel latestStats)
        {
            _db.TelemetryReports.Add(new Common.Entities.Entities.TelemetryReport
            {
                Report = JsonConvert.SerializeObject(latestStats),
                ReportSubmitted = DateTime.Now
            });
            await _db.SaveChangesAsync();
        }
    }
}
