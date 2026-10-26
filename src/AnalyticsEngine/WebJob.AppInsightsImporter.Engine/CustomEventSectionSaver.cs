using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Saves every section, in order, each isolated from the others' failures. Returns which failed, if any,
        /// so the caller can treat the batch's day as not saved - see <see cref="CustomEventSaveResult"/>.
        /// </summary>
        public async Task<CustomEventSaveResult> SaveAllSectionsAsync(CustomEventsResultCollection events)
        {
            // Each section runs inside its own isolation boundary (see SaveSectionSafe). A failure
            // in one section (e.g. a page-update that trips a DbUpdateException) is logged in full
            // but never aborts the sibling sections nor escapes to stall the whole importer. It is
            // reported in the result instead, so the day is retried rather than silently incomplete.
            var failedSections = new List<string>();

            var hitUpdatesCount = await SaveSectionSafe(_logger, "Hit updates",
                () => _hitUpdates.SaveHitUpdatesAsync(events), failedSections);

            var searchesInserted = await SaveSectionSafe(_logger, "Searches",
                () => _searches.SaveSearchesAsync(events), failedSections);

            var pagesUpdated = await SaveSectionSafe(_logger, "Page updates",
                () => _pageUpdates.SavePageUpdatesAsync(events), failedSections);

            var clicks = await SaveSectionSafe(_logger, "Clicks",
                () => _clicks.SaveClicksAsync(events), failedSections);

            _logger.LogInformation($"Event save summary: {hitUpdatesCount:n0} hit-updates, {searchesInserted:n0} searches, {pagesUpdated:n0} page-updates, {clicks:n0} clicks");

            return new CustomEventSaveResult(failedSections);
        }

        /// <summary>
        /// Runs one event-save section (hit-updates / searches / page-updates / clicks) inside its own
        /// timer and isolation boundary. A failure in one section is logged in full, reported via
        /// TrackException and added to <paramref name="failedSections"/>, but never propagates - so a single
        /// bad section can neither abort its sibling sections nor escape up the call stack and stall the whole
        /// importer.
        /// </summary>
        private static async Task<int> SaveSectionSafe(AnalyticsLogger logger, string sectionName, Func<Task<int>> saveAction, List<string> failedSections)
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
                failedSections.Add(sectionName);
                logger.TrackException(ex);
                logger.LogError($"Failed importing '{sectionName}' section: {CommonExceptionHandler.GetErrorText(ex)}. Continuing with the other sections; the day will be retried.");
                logger.LogError($"Exception detail: {ex}");
                return 0;
            }
        }
    }
}
