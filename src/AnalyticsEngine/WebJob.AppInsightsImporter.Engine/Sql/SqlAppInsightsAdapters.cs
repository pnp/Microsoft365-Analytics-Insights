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
        private readonly AnalyticsLogger _logger;
        private readonly AppConfig _config;
        private readonly IPageViewsPersistenceManager _pageViews;
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public SqlAppInsightsDayPersistenceManager(AnalyticsEntitiesContext db, AnalyticsLogger logger, AppConfig config)
            : this(db, logger, config, DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        /// <summary>
        /// <paramref name="contextFactory"/> is used only for the custom-event save, which has always
        /// created its own context rather than borrowing <paramref name="db"/>. A separate overload rather
        /// than a trailing optional parameter, per #381's convention.
        /// </summary>
        public SqlAppInsightsDayPersistenceManager(AnalyticsEntitiesContext db, AnalyticsLogger logger, AppConfig config, IAnalyticsDbContextFactory contextFactory)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config;
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

            // Built after the guards above so the ParamName a caller sees is unchanged (the adapter has
            // guards of its own, and a constructor initialiser would run them first).
            _pageViews = new SqlPageViewsPersistenceManager(db, logger);
        }

        /// <summary>
        /// The page-view write port supplied directly, so the day manager's forwarding can be exercised
        /// without a database. Internal because it exists for that test seam rather than for production
        /// wiring - production always goes through the context-taking constructors above.
        /// <paramref name="contextFactory"/> is still required because the custom-event save creates its
        /// own context.
        /// </summary>
        internal SqlAppInsightsDayPersistenceManager(IPageViewsPersistenceManager pageViews, AnalyticsLogger logger, AppConfig config, IAnalyticsDbContextFactory contextFactory)
        {
            _pageViews = pageViews ?? throw new ArgumentNullException(nameof(pageViews));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config;
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public Task SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
            => _pageViews.SavePageViewsAsync(pageViews, filterUrls);

        public Task SaveCustomEventsAsync(CustomEventsResultCollection events)
            => events.SaveAllEventTypesToSql(_logger, _config, _contextFactory);
    }

    /// <summary>
    /// The per-section SQL write ports named by issue #369. Each is a one-line wrapper over the save
    /// extension that already existed, so the SQL itself is untouched; the point is that the section
    /// orchestration above them (<see cref="CustomEventSectionSaver"/>) no longer needs a database.
    ///
    /// Like the #374 adapters above they borrow the caller's <see cref="AnalyticsEntitiesContext"/> rather
    /// than creating one - the one exception being <see cref="SqlPageUpdatePersistenceManager"/>, whose
    /// path has always built its own contexts.
    /// </summary>
    public sealed class SqlPageViewsPersistenceManager : IPageViewsPersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;

        public SqlPageViewsPersistenceManager(AnalyticsEntitiesContext db, ILogger logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<PageViewSaveResult> SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
            => pageViews.SaveToSQL(_db, _logger, filterUrls);
    }

    /// <summary>SQL <see cref="IHitUpdatePersistenceManager"/>.</summary>
    public sealed class SqlHitUpdatePersistenceManager : IHitUpdatePersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;

        public SqlHitUpdatePersistenceManager(AnalyticsEntitiesContext db, ILogger logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<int> SaveHitUpdatesAsync(CustomEventsResultCollection events)
            => events.SaveHitsUpdatesToSQL(_logger, _db);
    }

    /// <summary>SQL <see cref="ISearchesPersistenceManager"/>.</summary>
    public sealed class SqlSearchesPersistenceManager : ISearchesPersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;

        public SqlSearchesPersistenceManager(AnalyticsEntitiesContext db, ILogger logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<int> SaveSearchesAsync(CustomEventsResultCollection events)
            => events.SaveSearchesToSQL(_logger, _db);
    }

    /// <summary>
    /// SQL <see cref="IPageUpdatePersistenceManager"/>. Takes no <see cref="AnalyticsEntitiesContext"/>:
    /// <see cref="PageUpdateManager"/> creates and disposes one context per chunk (plus one for the
    /// URL-timestamp pass), so it cannot borrow the single context the other three sections are given.
    /// It gets the factory instead, which is why this adapter carries one.
    /// </summary>
    public sealed class SqlPageUpdatePersistenceManager : IPageUpdatePersistenceManager
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public SqlPageUpdatePersistenceManager(ILogger logger, AppConfig config)
            : this(logger, config, DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        public SqlPageUpdatePersistenceManager(ILogger logger, AppConfig config, IAnalyticsDbContextFactory contextFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config;
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public Task<int> SavePageUpdatesAsync(CustomEventsResultCollection events)
            => events.SavePageUpdatesToSQL(_logger, _config, _contextFactory);
    }

    /// <summary>SQL <see cref="IClicksPersistenceManager"/>.</summary>
    public sealed class SqlClicksPersistenceManager : IClicksPersistenceManager
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _logger;

        public SqlClicksPersistenceManager(AnalyticsEntitiesContext db, ILogger logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<int> SaveClicksAsync(CustomEventsResultCollection events)
            => events.SaveClicksToSQL(_logger, _db);
    }
}
