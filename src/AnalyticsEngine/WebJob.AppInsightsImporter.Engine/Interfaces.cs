using Common.Entities.Config;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Reads a day of telemetry from Application Insights. Implemented in production by
    /// <see cref="AppInsightsAPIClient"/>; a fake lets the importer's orchestration - the day loop, the
    /// per-day failure isolation, the window - be tested with no HTTP at all. See issue #374.
    /// </summary>
    public interface IAppInsightsSourceLoader
    {
        Task<PageViewCollection> GetPageViewsAsync(DateTime forDateUtc, bool saveRestResponses);
        Task<CustomEventsResultCollection> GetCustomEventsAsync(DateTime forDateUtc, bool saveRestResponses);
    }

    /// <summary>
    /// The high-water mark the import resumes from: the newest hit already stored. Returns <c>null</c> when
    /// nothing has been imported yet, which the window rule treats as "start 31 days ago".
    /// </summary>
    public interface IHitWatermarkStore
    {
        Task<DateTime?> GetNewestHitTimestampUtcAsync();
    }

    /// <summary>
    /// Loads the org-URL whitelist that decides which page-views are in scope.
    /// </summary>
    public interface ISiteFilterLoader
    {
        Task<List<FilterUrlConfig>> LoadAsync();
    }

    /// <summary>
    /// One-off schema maintenance run at importer startup. This is deployment-time work, not import logic -
    /// it is behind a port so the importer can be exercised without it, and so it has an obvious home if it
    /// ever moves to a migration where it belongs. Also named in issue #369.
    /// </summary>
    public interface IImportDbMaintenance
    {
        Task RunStartupMaintenanceAsync();
    }

    /// <summary>
    /// Writes one day's telemetry. Deliberately coarse - one method per thing the importer saves - because
    /// this is the seam the day loop needs. Issue #369 decomposes the SQL side further (page-views,
    /// searches, clicks, page-updates, hit-updates); those become collaborators of the SQL adapter rather
    /// than changes to this port.
    /// </summary>
    public interface IAppInsightsDayPersistenceManager
    {
        Task SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls);
        Task SaveCustomEventsAsync(CustomEventsResultCollection events);
    }
}
