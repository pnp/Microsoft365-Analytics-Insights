using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    /// <summary>
    /// SQL adapters for the App Insights importer's read/write ports (issue #374). Each wraps the query or
    /// call that previously sat inline in <see cref="AppInsightsImporter"/>, unchanged.
    ///
    /// All of them borrow the caller's <see cref="AnalyticsEntitiesContext"/> rather than creating one, so
    /// the whole import keeps using the single context it always has - creating a context per day would
    /// change both the change-tracker lifetime and the connection churn.
    /// </summary>
    public sealed class SqlHitWatermarkStore : IHitWatermarkStore
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlHitWatermarkStore(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<DateTime?> GetNewestHitTimestampUtcAsync()
        {
            var newestHit = await _db.hits.OrderByDescending(h => h.hit_timestamp).Take(1).FirstOrDefaultAsync();
            return newestHit?.hit_timestamp;
        }
    }

    /// <summary>SQL <see cref="ISiteFilterLoader"/>, delegating to the existing loader.</summary>
    public sealed class SqlSiteFilterLoader : ISiteFilterLoader
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlSiteFilterLoader(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Task<List<FilterUrlConfig>> LoadAsync() => SiteFilterLoader.Load(_db);
    }

    /// <summary>
    /// SQL <see cref="IImportDbMaintenance"/>: the duplicate-hit cleanup and the page-request-id index.
    /// </summary>
    public sealed class SqlImportDbMaintenance : IImportDbMaintenance
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlImportDbMaintenance(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Task RunStartupMaintenanceAsync() => ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(_db);
    }

    /// <summary>
    /// SQL <see cref="IAppInsightsDayPersistenceManager"/>, calling the existing save extensions verbatim.
    /// </summary>
    public sealed class SqlAppInsightsDayPersistenceManager : IAppInsightsDayPersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly AnalyticsLogger _logger;
        private readonly AppConfig _config;

        public SqlAppInsightsDayPersistenceManager(AnalyticsEntitiesContext db, AnalyticsLogger logger, AppConfig config)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config;
        }

        public Task SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
            => pageViews.SaveToSQL(_db, _logger, filterUrls);

        public Task SaveCustomEventsAsync(CustomEventsResultCollection events)
            => events.SaveAllEventTypesToSql(_logger, _config);
    }
}
