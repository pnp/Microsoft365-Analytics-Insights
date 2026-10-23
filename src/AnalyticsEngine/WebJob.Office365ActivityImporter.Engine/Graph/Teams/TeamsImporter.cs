using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class TeamsImporter : AbstractApiLoader
    {
        private TeamsFinder _teamsFinder;
        private TeamsLoadContext _context;
        private readonly ITeamsCognitiveStatsLoader _cognitiveStatsLoader;

        /// <param name="userScope">The <c>UserGroupsFilter</c> scope for this crawl; null means unfiltered.</param>
        public TeamsImporter(AnalyticsLogger logger, AppConfig settings, GraphServiceClient graphServiceClient,
            Common.Entities.UserScope.UserImportScope userScope = null)
            : this(logger, settings, graphServiceClient, userScope, null)
        {
        }

        public TeamsImporter(AnalyticsLogger logger, AppConfig settings, GraphServiceClient graphServiceClient,
            Common.Entities.UserScope.UserImportScope userScope, ITeamsCognitiveStatsLoader cognitiveStatsLoader) : base(logger, settings)
        {
            if (logger is null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (graphServiceClient is null)
            {
                throw new ArgumentNullException(nameof(graphServiceClient));
            }

            _context = new TeamsLoadContext(graphServiceClient)
            {
                UserScope = userScope ?? Common.Entities.UserScope.UserImportScope.Unfiltered
            };
            _teamsFinder = new TeamsFinder(logger, settings, graphServiceClient);
            _cognitiveStatsLoader = cognitiveStatsLoader ?? new TeamsCognitiveStatsLoader(new AppConfig(), logger);
        }

        /// <summary>
        /// Import with a pre-defined white/blacklist of groups to include/ignore
        /// </summary>
        /// <returns>How many Teams were saved and how many failed; a failed Team is logged and retried next cycle.</returns>
        public async Task<TeamsCrawlSummary> RefreshAndSaveAllTeamsData(TeamsCrawlConfig filterConfig)
        {
            var targetGroups = await _teamsFinder.FindGroupsWithTeamToCrawl(filterConfig);
            var summary = new TeamsCrawlSummary { NoGroupReportedWorkloads = _teamsFinder.NoGroupReportedWorkloads };

            if (targetGroups.Count > 0 && _context.UpnByAzureAdId == null)
            {
                _context.UpnByAzureAdId = await LoadUpnByAzureAdId();
            }

            // Figure out how many threads we'll need
#if DEBUG
            const int MAX_TEAMS_PER_THREAD = 10;
#else
            const int MAX_TEAMS_PER_THREAD = 100;
#endif

            var loader = new ParallelListProcessor<Group>(MAX_TEAMS_PER_THREAD);
            await loader.ProcessListInParallel(targetGroups,
                (threadListChunk, threadIndex) => LoadTeamsChunkThreaded(threadListChunk, summary),
                threads => _logger.LogInformation($"Loading & saving to SQL {targetGroups.Count} groups/teams over {threads} threads..."));

            if (summary.TeamsFailed > 0 && summary.TeamsSaved == 0)
            {
                _logger.LogError($"Teams import: all {summary.TeamsFailed:N0} Team(s) failed to load or save this cycle - most likely an outage " +
                    "rather than a problem with any one Team (the error for each is logged above). The Teams import is not counted as run, " +
                    "so it is tried again next cycle.");
            }
            else if (summary.TeamsFailed > 0)
            {
                _logger.LogWarning($"Teams import: {summary.TeamsFailed:N0} of {targetGroups.Count:N0} Team(s) couldn't be loaded or saved this cycle " +
                    "and will be tried again next cycle; the error for each is logged above. The other Teams were imported.");
            }
            _logger.LogInformation($"Teams import complete.\n");
            return summary;
        }

        /// <summary>
        /// Every user's Entra object id and UPN, read once for the whole crawl in one query - a projection, not 200k
        /// tracked entities - and shared read-only by the parallel chunks.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, string>> LoadUpnByAzureAdId()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var users = await db.users.AsNoTracking()
                    .Where(u => u.AzureAdId != null && u.AzureAdId != string.Empty)
                    .Select(u => new { u.ID, u.AzureAdId, u.UserPrincipalName })
                    .ToListAsync();
                var map = TeamsCrawlRules.BuildUpnByAzureAdId(users.Select(u => (u.ID, u.AzureAdId, u.UserPrincipalName)));
                _logger.LogInformation($"Teams import: resolving members against {map.Count:N0} known user(s).");
                return map;
            }
        }

        Task LoadTeamsChunkThreaded(List<Group> groupsWithTeams, TeamsCrawlSummary summary)
        {
            return TeamsChunkCrawler.CrawlChunk(groupsWithTeams,
                () => new TeamsChunkUnitOfWork(),
                (unitOfWork, group) => LoadAndSaveTeam(unitOfWork.Lookup, group),
                summary, _logger);
        }

        /// <returns>Whether the Team was saved.</returns>
        async Task<bool> LoadAndSaveTeam(TeamsAndCallsDBLookupManager lookupManager, Group parentGroup)
        {
            O365Team team;
            try
            {
                team = await O365Team.LoadTeamFull(parentGroup, _context, _logger, _settings, lookupManager.Database);
            }
            catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                // Anything at all - not only Graph errors - so one bad Team doesn't take the rest of its chunk with it.
                _logger.LogError(ex, $"Couldn't load team from Group {parentGroup.DisplayName}: {ex.Message}");
                return false;
            }

            try
            {
                // Null when the final SQL save failed; SaveToSQL has logged why.
                return await team.SaveToSQL(lookupManager, _settings, _logger, _cognitiveStatsLoader) != null;
            }
            catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                _logger.LogError(ex, $"Couldn't save Group {parentGroup.DisplayName} to SQL: {ex.Message}");
                return false;
            }
        }

        /// <summary>The EF context and lookup caches one chunk of Teams shares.</summary>
        private sealed class TeamsChunkUnitOfWork : IDisposable
        {
            public TeamsChunkUnitOfWork()
            {
                Database = new AnalyticsEntitiesContext();
                Lookup = new TeamsAndCallsDBLookupManager(Database);
            }

            public AnalyticsEntitiesContext Database { get; }

            public TeamsAndCallsDBLookupManager Lookup { get; }

            public void Dispose() => Database.Dispose();
        }
    }

    /// <summary>
    /// One chunk of the Teams crawl: up to 100 Teams, crawled one after another over one shared unit of work (an EF
    /// context and its lookup caches).
    /// </summary>
    internal static class TeamsChunkCrawler
    {
        /// <param name="crawlTeam">Loads and saves one Team; returns whether it was saved.</param>
        /// <remarks>
        /// A Team that fails - Graph, SQL, a deleted user, anything short of cancellation - is logged, counted and
        /// skipped; the rest of the chunk carries on. Before, one failure abandoned up to 99 other Teams and the Graph
        /// import sections after this one, every cycle. The unit of work is replaced after a failure: the failed Team can
        /// leave changes tracked in the shared EF context, which the next Team's save would otherwise try to write again.
        /// </remarks>
        public static async Task CrawlChunk<TUnitOfWork>(IEnumerable<Group> groups, Func<TUnitOfWork> openUnitOfWork,
            Func<TUnitOfWork, Group, Task<bool>> crawlTeam, TeamsCrawlSummary summary, ILogger logger) where TUnitOfWork : class, IDisposable
        {
            var unitOfWork = openUnitOfWork();
            try
            {
                foreach (var group in groups)
                {
                    bool saved;
                    try
                    {
                        saved = await crawlTeam(unitOfWork, group);
                    }
                    catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
                    {
                        logger.LogError(ex, $"Couldn't import Team from Group {group.DisplayName}: {ex.Message}");
                        saved = false;
                    }

                    if (saved)
                    {
                        summary.RecordSaved();
                    }
                    else
                    {
                        summary.RecordFailed();
                        unitOfWork.Dispose();
                        unitOfWork = null;
                        unitOfWork = openUnitOfWork();
                    }
                }
            }
            finally
            {
                unitOfWork?.Dispose();
            }
        }
    }

    /// <summary>The outcome of one Teams crawl. Safe to update from the parallel chunks.</summary>
    public sealed class TeamsCrawlSummary
    {
        private int _saved;
        private int _failed;

        public int TeamsSaved => Volatile.Read(ref _saved);

        public int TeamsFailed => Volatile.Read(ref _failed);

        /// <summary>
        /// Graph returned groups, but not one reported its provisioned workloads, so no Team could be identified - the
        /// importer, not the tenant, is at fault (see <see cref="TeamsFinder"/>).
        /// </summary>
        public bool NoGroupReportedWorkloads { get; internal set; }

        /// <summary>
        /// Whether the Teams section counts as having run, which stamps its cadence gate until the next due run.
        /// </summary>
        /// <remarks>
        /// Not when Teams were attempted and every one failed - that is an outage (Graph, SQL), not a bad Team - nor when
        /// no group reported its workloads: either way stamping the gate would hide it until the next due run. A partial
        /// failure still counts: each failed Team is logged and retried at the next run, whereas failing the section for
        /// it would re-crawl every Team, every cycle, for as long as one Team stays broken.
        /// </remarks>
        public bool Succeeded => !NoGroupReportedWorkloads && !(TeamsFailed > 0 && TeamsSaved == 0);

        internal void RecordSaved() => Interlocked.Increment(ref _saved);

        internal void RecordFailed() => Interlocked.Increment(ref _failed);
    }
}
