using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Runs the four custom-event save sections - hit-updates, searches, page-updates, clicks - in order,
    /// each inside its own isolation boundary, and prints the summary line. Lifted verbatim out of
    /// <see cref="CustomEventsResultCollection.SaveAllEventTypesToSql"/> so it sits above the write ports
    /// rather than above <c>AnalyticsEntitiesContext</c>: with in-memory ports the section order, the
    /// per-section failure isolation and the FinishedSectionImport gating are all assertable with no SQL
    /// Server at all. See issue #369.
    ///
    /// Section order is behaviour, not incidental: page-updates run before clicks, and hit-updates first,
    /// exactly as they always have.
    /// </summary>
    public class CustomEventSectionSaver
    {
        private readonly AnalyticsLogger _logger;
        private readonly IHitUpdatePersistenceManager _hitUpdates;
        private readonly ISearchesPersistenceManager _searches;
        private readonly IPageUpdatePersistenceManager _pageUpdates;
        private readonly IClicksPersistenceManager _clicks;

        public CustomEventSectionSaver(AnalyticsLogger logger,
            IHitUpdatePersistenceManager hitUpdates,
            ISearchesPersistenceManager searches,
            IPageUpdatePersistenceManager pageUpdates,
            IClicksPersistenceManager clicks)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hitUpdates = hitUpdates ?? throw new ArgumentNullException(nameof(hitUpdates));
            _searches = searches ?? throw new ArgumentNullException(nameof(searches));
            _pageUpdates = pageUpdates ?? throw new ArgumentNullException(nameof(pageUpdates));
            _clicks = clicks ?? throw new ArgumentNullException(nameof(clicks));
        }

        public async Task SaveAllSectionsAsync(CustomEventsResultCollection events)
        {
            // Each section runs inside its own isolation boundary (see SaveSectionSafe). A failure
            // in one section (e.g. a page-update that trips a DbUpdateException) is logged in full
            // but never aborts the sibling sections nor escapes to stall the whole importer.
            var hitUpdatesCount = await SaveSectionSafe(_logger, "Hit updates",
                () => _hitUpdates.SaveHitUpdatesAsync(events));

            var searchesInserted = await SaveSectionSafe(_logger, "Searches",
                () => _searches.SaveSearchesAsync(events));

            var pagesUpdated = await SaveSectionSafe(_logger, "Page updates",
                () => _pageUpdates.SavePageUpdatesAsync(events));

            var clicks = await SaveSectionSafe(_logger, "Clicks",
                () => _clicks.SaveClicksAsync(events));

            _logger.LogInformation($"Event save summary: {hitUpdatesCount:n0} hit-updates, {searchesInserted:n0} searches, {pagesUpdated:n0} page-updates, {clicks:n0} clicks");
        }

        /// <summary>
        /// Runs one event-save section (hit-updates / searches / page-updates / clicks) inside its own
        /// timer and isolation boundary. A failure in one section is logged in full and reported via
        /// TrackException, but never propagates - so a single bad section can neither abort its sibling
        /// sections nor escape up the call stack and stall the whole importer.
        /// </summary>
        private static async Task<int> SaveSectionSafe(AnalyticsLogger logger, string sectionName, Func<Task<int>> saveAction)
        {
            var timer = new JobTimer(logger, sectionName);
            timer.Start();
            try
            {
                var count = await saveAction();
                timer.PrintElapsed();
                if (count > 0)
                {
                    timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
                }
                return count;
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                logger.LogError($"Failed importing '{sectionName}' section: {CommonExceptionHandler.GetErrorText(ex)}. Skipping this section and continuing.");
                logger.LogError($"Exception detail: {ex}");
                return 0;
            }
        }
    }
}
