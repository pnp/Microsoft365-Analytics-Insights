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

    /// <summary>
    /// Writes a batch of page-views. The one write port of the day persistence manager that has anything
    /// to report back: the staging rules already work out how many rows were dropped and why, and those
    /// numbers previously reached a log line and nothing else. See issue #369.
    /// </summary>
    public interface IPageViewsPersistenceManager
    {
        Task<PageViewSaveResult> SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls);
    }

    /// <summary>
    /// Writes the time-on-page patches carried by page-exit events. Returns the row count the merge
    /// reported, which is what the event save summary prints.
    /// </summary>
    public interface IHitUpdatePersistenceManager
    {
        Task<int> SaveHitUpdatesAsync(CustomEventsResultCollection events);
    }

    /// <summary>
    /// Writes the search events in a batch. Returns the merge script's ADO.NET rows-affected total, which
    /// covers the new <c>search_terms</c> lookup rows as well as the <c>searches</c> rows - "Migrate
    /// Searches Import.sql" runs both inserts in one batch with no <c>SET NOCOUNT ON</c>, so
    /// <c>ExecuteNonQuery</c> sums them. It is not a count of searches alone.
    /// </summary>
    public interface ISearchesPersistenceManager
    {
        Task<int> SaveSearchesAsync(CustomEventsResultCollection events);
    }

    /// <summary>Writes page metadata / comment / like updates. Returns the number of URLs updated.</summary>
    public interface IPageUpdatePersistenceManager
    {
        Task<int> SavePageUpdatesAsync(CustomEventsResultCollection events);
    }

    /// <summary>
    /// Writes the click events in a batch. Like <see cref="ISearchesPersistenceManager"/> this is the
    /// merge script's ADO.NET rows-affected total, not a count of clicks: "Migrate clicks from
    /// staging.sql" inserts into <c>hits_clicked_element_class_names</c>, <c>urls</c> and
    /// <c>hits_clicked_element_titles</c> before <c>hits_clicked_elements</c>, all in one batch with no
    /// <c>SET NOCOUNT ON</c>.
    /// </summary>
    public interface IClicksPersistenceManager
    {
        Task<int> SaveClicksAsync(CustomEventsResultCollection events);
    }
}
