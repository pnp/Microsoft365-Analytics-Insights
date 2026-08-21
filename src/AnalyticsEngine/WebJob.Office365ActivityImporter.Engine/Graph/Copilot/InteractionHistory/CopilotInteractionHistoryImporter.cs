using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.Entities.Copilot;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Imports Microsoft 365 Copilot AI interaction history, one Graph call per in-scope user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cost model.</b> <c>getAllEnterpriseInteractions</c> has no tenant-wide or delta form, so this
    /// import costs at least one HTTP call for every user it looks at. At the product's ~200k-user design
    /// target that would be 200k calls per cycle if it were unbounded, which is why the import is off by
    /// default and why these brakes apply, in this order:
    /// <list type="number">
    /// <item>the <c>CopilotInteractionHistory</c> feature toggle (off by default), and the
    /// <c>AiEnterpriseInteraction.Read.All</c> application permission, which the installer does not grant.
    /// Those two are the real controls: to stop this import, turn the workload off or withhold the
    /// permission;</item>
    /// <item><c>UserGroupsFilter</c>, an <b>optional</b> narrowing to one or more Entra ID groups. Recommended
    /// for a pilot, but not required - without it every enabled user is eligible, still subject to the
    /// ceiling below;</item>
    /// <item><c>CopilotInteractionHistoryMaxUsersPerCycle</c>, a hard per-cycle ceiling. Users are taken
    /// least-recently-run first, so a scope bigger than the cap is still covered - just round-robin over
    /// several cycles rather than all at once;</item>
    /// <item>a per-user back-off list, so users who return nothing (almost always because they have no
    /// <c>M365_COPILOT_BUSINESS_CHAT</c> service plan) stop consuming the budget;</item>
    /// <item>the cadence gate (<c>CopilotInteractionHistoryIntervalHours</c>, daily by default).</item>
    /// </list>
    /// On top of that the import is incremental: each user has a watermark, so a steady-state cycle only asks
    /// for interactions created since the last successful run.
    /// </para>
    /// <para>
    /// <b>Privacy.</b> The Graph payload contains real prompt and response text. It is projected to counts by
    /// <see cref="InteractionStatsExtractor"/> and, for prompts only and only when cognitive services are
    /// configured, to a sentiment score, detected language and key phrases. The text itself is never written
    /// to the database or the log.
    /// </para>
    /// </remarks>
    public class CopilotInteractionHistoryImporter : AbstractApiLoader
    {
        /// <summary>Users per save batch. Keeps IN clauses well inside SQL Server's 2100-parameter limit.</summary>
        private const int DefaultUserChunkSize = 25;

        /// <summary>
        /// Concurrent Graph calls. Deliberately lower than the sent-email import's 8: this endpoint is newer,
        /// its throttling limits are not published, and being throttled here wastes a whole user's call.
        /// </summary>
        private const int DefaultGraphLoadParallelism = 4;

        /// <summary>Max elements per IN clause when checking existing rows.</summary>
        private const int SqlInClauseChunkSize = 1000;

        /// <summary>
        /// How far BEFORE the oldest interaction in the batch the de-duplication read reaches back.
        ///
        /// The de-dup only has to see rows a batch could actually collide with, and a batch's rows carry
        /// their own <c>createdDateTime</c> - so there is no reason to read a thread's entire history. That
        /// unbounded read was the cost that grew for the life of a thread: a persistent BizChat thread never
        /// ends, so the same few minutes of new data got progressively more expensive to de-duplicate
        /// against (measured at synthetic scale: 252,000 rows pulled into the HashSet for 50 long-lived
        /// threads, against 72,000 for the same threads bounded to recent history).
        ///
        /// The margin exists so the bound can never cause a MISS, which would be far worse than a slow read:
        /// a missed duplicate hits the unique index on (session_id, graph_interaction_id) and fails the
        /// batch.
        ///
        /// Note what the margin is NOT protecting against. The window is anchored to the BATCH's own oldest
        /// timestamp, not to wall-clock, so the length of any importer outage is irrelevant - a stored row
        /// that duplicates a batch row carries the same <c>createdDateTime</c> as that batch row, which is
        /// by construction >= the batch minimum. What the margin actually absorbs is narrower: Graph
        /// re-stating a timestamp slightly differently between reads, <c>datetime</c> rounding in SQL
        /// Server (up to 3.33 ms), and clock/precision differences on the boundary. Seven days is far more
        /// than any of those need, and still bounds the read to a fixed window instead of "everything,
        /// forever" - so it is cheap insurance, not a load-bearing outage guard.
        /// </summary>
        internal const int DedupLookbackMarginDays = 7;

        /// <summary>
        /// How far before the stored watermark the next query window starts.
        /// </summary>
        /// <remarks>
        /// Two jobs. First, Graph's <c>createdDateTime</c> filter uses a strict <c>gt</c>, so without any
        /// overlap a second interaction created in the same second as the watermark would be skipped for
        /// ever. Second, the watermark advances to the end of the queried window rather than to the newest
        /// row seen, so this doubles as a safety lag absorbing late-arriving interactions and clock skew
        /// between the producing services. Anything re-read is dropped by the existing-key check before it
        /// costs a database write or a cognitive call.
        /// </remarks>
        internal const int WatermarkOverlapSeconds = 300;

        private readonly IAiInteractionSourceLoader _sourceLoader;
        private readonly IInteractionCognitiveEnricher _cognitiveEnricher;
        private readonly IPilotGroupMemberResolver _pilotGroupResolver;
        private readonly UserGroupsFilterModel _userGroupsFilter;
        private readonly Func<AnalyticsEntitiesContext> _dbContextFactory;
        private readonly int _userChunkSize;
        private readonly int _graphLoadParallelism;

        public CopilotInteractionHistoryImporter(
            AnalyticsLogger logger,
            AppConfig settings,
            IAiInteractionSourceLoader sourceLoader,
            IInteractionCognitiveEnricher cognitiveEnricher,
            IPilotGroupMemberResolver pilotGroupResolver,
            UserGroupsFilterModel userGroupsFilter,
            Func<AnalyticsEntitiesContext> dbContextFactory = null,
            int userChunkSize = DefaultUserChunkSize,
            int graphLoadParallelism = DefaultGraphLoadParallelism)
            : base(logger, settings)
        {
            _sourceLoader = sourceLoader ?? throw new ArgumentNullException(nameof(sourceLoader));
            _cognitiveEnricher = cognitiveEnricher ?? NullInteractionCognitiveEnricher.Instance;
            _pilotGroupResolver = pilotGroupResolver;
            _userGroupsFilter = userGroupsFilter ?? new UserGroupsFilterModel();
            _dbContextFactory = dbContextFactory ?? (() => new AnalyticsEntitiesContext());
            _userChunkSize = userChunkSize > 0 ? userChunkSize : DefaultUserChunkSize;
            _graphLoadParallelism = graphLoadParallelism > 0 ? graphLoadParallelism : DefaultGraphLoadParallelism;
        }

        /// <summary>
        /// Runs one import cycle. Returns the run log that was persisted, or null when the import declined to
        /// run at all (missing permission).
        /// </summary>
        public async Task<CopilotInteractionImportLog> ImportAsync()
        {
            _logger.LogInformation("Starting Copilot AI interaction history import...");
            var sw = Stopwatch.StartNew();

            if (!await _sourceLoader.HasInteractionReadAccessAsync())
            {
                _logger.LogWarning(
                    "Skipping the Copilot interaction history import: the runtime identity does not hold the " +
                    "AiEnterpriseInteraction.Read.All application permission. This permission is not granted by the " +
                    "installer and needs separate admin consent - add it to the app registration and grant admin " +
                    "consent, then this import will start on the next cycle.");
                return null;
            }

            var runLog = new CopilotInteractionImportLog { RunStartedUtc = DateTime.UtcNow };

            try
            {
                var due = await SelectDueUsersAsync(runLog);

                if (runLog.UsersInScope == 0)
                {
                    if (!string.IsNullOrEmpty(runLog.Error))
                    {
                        // Already reported as a failure by the scope resolver - don't follow it with a
                        // reassuring "nothing to do", which is what made this case easy to miss.
                    }
                    else if (_userGroupsFilter.Patterns.Count > 0)
                    {
                        _logger.LogWarning(
                            $"No users matched the UserGroupsFilter ('{_settings.UserGroupsFilter}') for the Copilot " +
                            "interaction history import, so there is nothing to do. Check the group name(s) - the filter " +
                            "matches Entra ID group display names and supports * wildcards.");
                    }
                    else
                    {
                        _logger.LogWarning(
                            "The Copilot interaction history import found no enabled users to look at, so there is " +
                            "nothing to do.");
                    }
                    return await FinishRunAsync(runLog, sw);
                }

                if (due.Count == 0)
                {
                    _logger.LogInformation(
                        $"Copilot interaction history: all {runLog.UsersInScope} in-scope user(s) are inside their " +
                        "back-off window, so no Graph calls were made this cycle.");
                    return await FinishRunAsync(runLog, sw);
                }

                _logger.LogInformation(
                    $"Copilot interaction history: {runLog.UsersInScope} user(s) in scope" +
                    $"{(_userGroupsFilter.Patterns.Count > 0 ? $" (narrowed by UserGroupsFilter '{_settings.UserGroupsFilter}')" : " (no UserGroupsFilter set - every enabled user is eligible)")}, " +
                    $"calling Graph for {due.Count} of them this cycle (cap {_settings.CopilotInteractionHistoryMaxUsersPerCycle}, " +
                    $"least-recently-run first). Cognitive enrichment is {(_cognitiveEnricher.IsEnabled ? "enabled" : "disabled")}.");

                for (int i = 0; i < due.Count; i += _userChunkSize)
                {
                    // GetRange, not Skip().Take(): Skip on a List walks every preceding element, making a
                    // chunking loop quadratic.
                    var chunk = due.GetRange(i, Math.Min(_userChunkSize, due.Count - i));
                    await ProcessUserChunkAsync(chunk, runLog);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Copilot interaction history import failed: {ex.Message}");
                runLog.Error = Truncate(GraphHttpException.DescribeForStorage(ex), 1000);
            }

            return await FinishRunAsync(runLog, sw);
        }

        #region Scope and scheduling


        /// <summary>
        /// Picks the users to call Graph for this cycle: optionally narrowed by <c>UserGroupsFilter</c>, with
        /// backed-off users dropped, ordered least-recently-run first, and capped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>UserGroupsFilter</c> is an <b>optional narrowing</b>, not a precondition. With it set, only that
        /// group's members are eligible; without it, every enabled user is. The controls that decide whether
        /// this import runs at all are the workload toggle and the <c>AiEnterpriseInteraction.Read.All</c>
        /// permission - not the filter.
        /// </para>
        /// <para>
        /// Ordering by last run (nulls first) is what makes the cap safe: a scope bigger than the cap is still
        /// covered, just spread over consecutive cycles, and never-imported users are always preferred over
        /// ones already up to date.
        /// </para>
        /// <para>
        /// The two paths are deliberately different. Narrowed, the member list is small, so users are fetched
        /// by UPN in chunks and ordered in memory. Unnarrowed - now the default - the whole selection is done
        /// in SQL: joined to the watermarks, back-off filtered, ordered and <c>TOP</c>-capped, so the cycle
        /// materialises at most <c>CopilotInteractionHistoryMaxUsersPerCycle</c> users no matter how large the
        /// directory is. Pulling 200,000 users back to filter them in memory would otherwise become the normal
        /// case rather than the exception.
        /// </para>
        /// </remarks>
        private async Task<List<UserImportState>> SelectDueUsersAsync(CopilotInteractionImportLog runLog)
        {
            var now = DateTime.UtcNow;
            var cap = _settings.CopilotInteractionHistoryMaxUsersPerCycle > 0
                ? _settings.CopilotInteractionHistoryMaxUsersPerCycle
                : AppConfig.DefaultCopilotInteractionHistoryMaxUsersPerCycle;

            if (_userGroupsFilter.Patterns.Count > 0)
            {
                // A filter of '*' matches every group, so it narrows nothing - but resolving it the group-first
                // way would enumerate the entire directory and every group's membership just to arrive back at
                // "everyone". Take the unnarrowed path instead, which reaches the same scope in one capped SQL
                // query (issue #297).
                if (_userGroupsFilter.MatchesEverything)
                {
                    _logger.LogWarning(
                        $"Copilot interaction history: UserGroupsFilter ('{_settings.UserGroupsFilter}') matches every " +
                        "group, so it is not narrowing anything. Treating this cycle as unscoped - every enabled user " +
                        "is eligible, capped per cycle as usual. Name the pilot group(s) to actually narrow the import, " +
                        "or clear the filter to make the intent explicit.");
                }
                else
                {
                    return await SelectDueUsersInGroupsAsync(runLog, now, cap);
                }
            }

            return await SelectDueUsersAcrossDirectoryAsync(runLog, now, cap);
        }

        /// <summary>
        /// Narrowed path: resolve the group's members from Graph, then fetch just those users.
        /// </summary>
        /// <remarks>
        /// Resolution is deliberately group-first - list the group's members, then look those up in the users
        /// table - rather than asking "is this user in the group?" for every user in the database. The latter
        /// is one Graph call per tenant user, which at the ~200k-user design target would spend 200,000 calls
        /// just working out who to import, before reading a single interaction.
        /// </remarks>
        private async Task<List<UserImportState>> SelectDueUsersInGroupsAsync(
            CopilotInteractionImportLog runLog, DateTime now, int cap)
        {
            if (_pilotGroupResolver == null)
            {
                _logger.LogWarning(
                    "A UserGroupsFilter is configured but no group resolver is available, so membership can't be " +
                    "checked. Skipping this cycle rather than silently widening the scope to every user.");
                return new List<UserImportState>();
            }

            var resolution = await _pilotGroupResolver.GetMemberUpnsAsync(_userGroupsFilter);

            // An incomplete resolution is the import failing to do its job, not a quiet no-op. Recorded on
            // the run log so it surfaces on the health page rather than only in a log line nobody reads
            // (issue #297) - a pilot group sitting past the discovery cap used to look exactly like an
            // idle, healthy import.
            if (resolution.IsIncomplete)
            {
                var message = "Copilot interaction history: the pilot scope could not be resolved in full - "
                    + resolution.IncompleteReason;
                _logger.LogError(message);
                runLog.Error = Truncate(message, 1000);
            }

            var memberUpns = resolution.MemberUpns;
            if (memberUpns.Count == 0)
            {
                runLog.UsersInScope = 0;
                return new List<UserImportState>();
            }

            var candidates = new List<UserImportState>();
            using (var db = _dbContextFactory())
            {
                foreach (var upnChunk in ChunkStrings(memberUpns.ToList()))
                {
                    var rows = await db.users
                        .Where(u => upnChunk.Contains(u.UserPrincipalName)
                                    && (u.AccountEnabled == null || u.AccountEnabled == true))
                        .GroupJoin(
                            db.CopilotInteractionUserWatermarks,
                            u => u.ID,
                            w => w.UserId,
                            (u, ws) => new { User = u, Watermark = ws.FirstOrDefault() })
                        .ToListAsync();

                    foreach (var row in rows)
                        candidates.Add(new UserImportState { User = row.User, Watermark = row.Watermark });
                }
            }

            runLog.UsersInScope = candidates.Count;

            var due = new List<UserImportState>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (candidate.Watermark?.SkipUntilUtc != null && candidate.Watermark.SkipUntilUtc.Value > now)
                {
                    runLog.UsersSkipped++;
                    continue;
                }
                due.Add(candidate);
            }

            return due
                .OrderBy(d => d.Watermark?.LastRunUtc ?? DateTime.MinValue)
                .Take(cap)
                .ToList();
        }

        /// <summary>
        /// Unnarrowed path: every enabled user is eligible, but the selection happens in SQL so only the
        /// capped number of users is ever materialised.
        /// </summary>
        private async Task<List<UserImportState>> SelectDueUsersAcrossDirectoryAsync(
            CopilotInteractionImportLog runLog, DateTime now, int cap)
        {
            using (var db = _dbContextFactory())
            {
                var eligible = db.users
                    .Where(u => u.UserPrincipalName != null && u.UserPrincipalName != ""
                                && (u.AccountEnabled == null || u.AccountEnabled == true));

                // Reported for the run log so an operator can see the ratio of scope to per-cycle budget.
                // One COUNT per cycle (daily by default) against the Graph calls that follow it.
                runLog.UsersInScope = await eligible.CountAsync();

                if (runLog.UsersInScope == 0)
                    return new List<UserImportState>();

                // Back-off, ordering and the cap all resolve server-side: SQL Server sorts NULL LastRunUtc
                // first ascending, which is exactly the "never imported goes first" rule.
                var rows = await eligible
                    .GroupJoin(
                        db.CopilotInteractionUserWatermarks,
                        u => u.ID,
                        w => w.UserId,
                        (u, ws) => new { User = u, Watermark = ws.FirstOrDefault() })
                    .Where(x => x.Watermark == null
                                || x.Watermark.SkipUntilUtc == null
                                || x.Watermark.SkipUntilUtc <= now)
                    .OrderBy(x => x.Watermark.LastRunUtc)
                    .Take(cap)
                    .ToListAsync();

                return rows
                    .Select(r => new UserImportState { User = r.User, Watermark = r.Watermark })
                    .ToList();
            }
        }

        #endregion

        #region Chunk pipeline

        /// <summary>
        /// Loads a chunk of users from Graph in parallel, then saves the whole chunk on a single connection.
        /// </summary>
        /// <remarks>
        /// Writes are deliberately serialised. The lookup tables (app class, locale, device, keywords...) are
        /// insert-if-missing, so parallel writers would each see "not there" and race to insert the same row,
        /// colliding on the unique index. This is the same shape as the sent-email importer's address handling.
        /// </remarks>
        private async Task ProcessUserChunkAsync(List<UserImportState> chunk, CopilotInteractionImportLog runLog)
        {
            var loaded = await LoadChunkInParallelAsync(chunk);

            var withData = loaded.Where(r => r.Stats.Count > 0).ToList();
            if (withData.Count > 0)
            {
                // Drop anything already imported BEFORE enrichment, not during the save.
                //
                // The query window deliberately overlaps the previous one, so a steady-state cycle always
                // re-reads a few interactions. Enriching those again would re-send the same prompt text to
                // Azure AI Language on every single cycle - a recurring bill, and a needless repeat exposure
                // of the prompt to an external service - for rows we are about to discard anyway.
                using (var db = _dbContextFactory())
                {
                    await RemoveAlreadyImportedAsync(db, withData);
                }

                withData = withData.Where(r => r.Stats.Count > 0).ToList();
            }

            if (withData.Count > 0)
            {
                runLog.CognitiveDocsScored += await EnrichChunkAsync(withData);

                using (var db = _dbContextFactory())
                {
                    runLog.InteractionsSaved += await SaveChunkAsync(db, withData);
                }
            }

            await UpdateWatermarksAsync(loaded, runLog);
        }

        /// <summary>
        /// Strips interactions we already hold, matching on (session, Graph interaction id) - the same
        /// key as the unique index that ultimately protects against duplicates.
        /// </summary>
        /// <remarks>
        /// Keyed on the database <c>session_id</c>, not on the raw Graph <c>sessionId</c> string. Two
        /// reasons, one correctness and one performance:
        /// <list type="number">
        /// <item><description>
        /// <c>copilot_interaction_sessions</c> is unique on <c>(user_id, session_ref)</c> because a Copilot
        /// thread can be shared - a Teams meeting session appears in more than one participant's history.
        /// Filtering on <c>session_ref</c> alone therefore matches *other* users' rows, so a second pilot
        /// user's genuinely-new interactions would be discarded as "already imported". The read is complete,
        /// so the watermark still advances and the under-count is permanent.
        /// </description></item>
        /// <item><description>
        /// <c>session_id</c> is the leading column of the <c>(session_id, graph_interaction_id)</c> unique
        /// index, which covers this query. Filtering through the <c>Session.SessionRef</c> navigation instead
        /// forces a join and an nvarchar(450) predicate. Measured at synthetic scale (50k sessions / 2.25M
        /// interactions): 2,408 logical reads and 486 ms via the navigation, against 1,323 reads and 85 ms
        /// keyed on <c>session_id</c> - 1.8x the reads and 5.7x the time for the same result.
        /// </description></item>
        /// </list>
        /// </remarks>
        private async Task RemoveAlreadyImportedAsync(AnalyticsEntitiesContext db, List<UserLoadResult> withData)
        {
            // The (user, session ref) pairs this batch actually touches.
            var refsByUser = new Dictionary<int, HashSet<string>>();
            foreach (var result in withData)
            {
                var userId = result.State.User.ID;
                foreach (var s in result.Stats)
                {
                    if (string.IsNullOrEmpty(s.SessionRef))
                        continue;

                    if (!refsByUser.TryGetValue(userId, out var refs))
                    {
                        refs = new HashSet<string>(StringComparer.Ordinal);
                        refsByUser[userId] = refs;
                    }
                    refs.Add(Truncate(s.SessionRef, 450));
                }
            }

            if (refsByUser.Count == 0)
                return;

            var allRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var refs in refsByUser.Values)
                allRefs.UnionWith(refs);

            // Resolve each user's OWN session rows. The user-id predicate is what keeps a shared thread
            // belonging to another pilot user out of the result.
            var userIds = refsByUser.Keys.ToList();
            var sessionIdByUserAndRef = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var refChunk in ChunkStrings(allRefs.ToList()))
            {
                var rows = await db.CopilotInteractionSessions
                    .Where(x => refChunk.Contains(x.SessionRef) && userIds.Contains(x.UserId))
                    .Select(x => new { x.ID, x.UserId, x.SessionRef })
                    .ToListAsync();

                foreach (var row in rows)
                {
                    if (refsByUser.TryGetValue(row.UserId, out var wanted) && wanted.Contains(row.SessionRef))
                        sessionIdByUserAndRef[SessionKey(row.UserId, row.SessionRef)] = row.ID;
                }
            }

            if (sessionIdByUserAndRef.Count == 0)
                return;

            var existing = await LoadExistingInteractionKeysAsync(db, sessionIdByUserAndRef.Values.Distinct().ToList(), DedupWindowStart(withData));
            if (existing.Count == 0)
                return;

            foreach (var result in withData)
            {
                var userId = result.State.User.ID;
                var keep = new List<InteractionStats>(result.Stats.Count);
                var keptBodies = result.PromptBodies != null ? new List<string>(result.Stats.Count) : null;

                for (int i = 0; i < result.Stats.Count; i++)
                {
                    var s = result.Stats[i];

                    if (!string.IsNullOrEmpty(s.SessionRef)
                        && sessionIdByUserAndRef.TryGetValue(SessionKey(userId, Truncate(s.SessionRef, 450)), out var sessionId)
                        && existing.Contains(InteractionKey(sessionId, s.GraphInteractionId)))
                    {
                        continue;
                    }

                    keep.Add(s);
                    // Bodies are index-aligned with Stats, so they must be filtered in lock-step.
                    keptBodies?.Add(i < result.PromptBodies.Count ? result.PromptBodies[i] : null);
                }

                result.Stats = keep;
                if (keptBodies != null)
                    result.PromptBodies = keptBodies;
            }
        }

        private async Task<List<UserLoadResult>> LoadChunkInParallelAsync(List<UserImportState> chunk)
        {
            var results = new List<UserLoadResult>(chunk.Count);
            var resultsLock = new object();

            using (var throttle = new System.Threading.SemaphoreSlim(_graphLoadParallelism))
            {
                var tasks = chunk.Select(async state =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        var result = await LoadSingleUserAsync(state);
                        lock (resultsLock)
                        {
                            results.Add(result);
                        }
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }

            return results;
        }

        private async Task<UserLoadResult> LoadSingleUserAsync(UserImportState state)
        {
            var toUtc = DateTime.UtcNow;
            var fromUtc = GetWindowStart(state.Watermark, toUtc, _settings.CopilotInteractionHistoryMaxDaysBackOnFirstRun);

            var loadResult = await _sourceLoader.LoadInteractionsForUserAsync(state.User, fromUtc, toUtc);

            var result = new UserLoadResult
            {
                State = state,
                WindowEndUtc = toUtc,
                UserNotAvailable = loadResult.UserNotAvailable,
                Truncated = loadResult.Truncated,
                Error = loadResult.Error
            };

            if (loadResult.Failed || loadResult.UserNotAvailable)
                return result;

            // Project to content-free stats immediately, keeping the raw bodies only in a local that dies
            // with this method. Everything after this point is counts.
            result.Stats = InteractionStatsExtractor.Extract(loadResult.Interactions);
            result.PromptBodies = BuildPromptBodyLookup(loadResult.Interactions, result.Stats);

            return result;
        }

        /// <summary>
        /// Start of the Graph query window: just before the user's watermark, or a bounded backfill the first
        /// time we see them.
        /// </summary>
        internal static DateTime GetWindowStart(CopilotInteractionUserWatermark watermark, DateTime nowUtc, int maxDaysBackOnFirstRun)
        {
            if (watermark?.LastInteractionUtc != null)
                return watermark.LastInteractionUtc.Value.AddSeconds(-WatermarkOverlapSeconds);

            var days = maxDaysBackOnFirstRun > 0
                ? maxDaysBackOnFirstRun
                : AppConfig.DefaultCopilotInteractionHistoryMaxDaysBackOnFirstRun;

            return nowUtc.AddDays(-days);
        }

        /// <summary>
        /// Plain-text prompt bodies, aligned by index with <paramref name="stats"/>, ready for the cognitive
        /// call. Non-prompt positions are null so responses are never sent for scoring.
        /// </summary>
        private static List<string> BuildPromptBodyLookup(IReadOnlyList<AiInteraction> interactions, List<InteractionStats> stats)
        {
            // Keyed by session + id, not id alone: the Graph interaction id is only unique within a session,
            // so keying on it alone would let one session's prompt overwrite another's and attribute the
            // wrong sentiment and key phrases to it.
            var bodiesByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var interaction in interactions)
            {
                if (interaction?.Id == null || interaction.SessionId == null || !interaction.IsUserPrompt)
                    continue;

                bodiesByKey[interaction.SessionId + "|" + interaction.Id] = StringUtils.StripHtmlToPlainText(interaction.Body?.Content);
            }

            var aligned = new List<string>(stats.Count);
            foreach (var s in stats)
            {
                bodiesByKey.TryGetValue(s.SessionRef + "|" + s.GraphInteractionId, out var body);
                aligned.Add(s.IsUserPrompt ? body : null);
            }
            return aligned;
        }

        private async Task<int> EnrichChunkAsync(List<UserLoadResult> withData)
        {
            if (!_cognitiveEnricher.IsEnabled)
                return 0;

            // Flatten the chunk into one aligned pair of lists so batching happens across users rather than
            // per user - a 10-document Azure batch shouldn't be wasted on a user with two prompts.
            var allStats = new List<InteractionStats>();
            var allBodies = new List<string>();

            foreach (var result in withData)
            {
                for (int i = 0; i < result.Stats.Count; i++)
                {
                    allStats.Add(result.Stats[i]);
                    allBodies.Add(result.PromptBodies != null && i < result.PromptBodies.Count ? result.PromptBodies[i] : null);
                }
            }

            return await _cognitiveEnricher.EnrichAsync(allStats, allBodies);
        }

        #endregion

        #region Persistence

        private async Task<int> SaveChunkAsync(AnalyticsEntitiesContext db, List<UserLoadResult> withData)
        {
            var allStats = withData.SelectMany(r => r.Stats).ToList();

            var lookups = await ResolveLookupsAsync(db, allStats);
            var sessionIds = await ResolveSessionsAsync(db, withData);
            var existingKeys = await LoadExistingInteractionKeysAsync(db, sessionIds.Values.ToList(), DedupWindowStart(withData));

            var newInteractions = new List<CopilotInteraction>();
            var keyPhrasesByInteraction = new Dictionary<CopilotInteraction, List<string>>();

            // EF6 calls DetectChanges on every DbSet.Add by default, and DetectChanges walks the entire
            // change tracker - so adding N entities costs O(N^2) entity examinations. A full chunk can carry
            // 25 users x 50 pages x 100 interactions, where that quadratic term dominates the whole save.
            // Detection is instead done once, explicitly, immediately before SaveChanges.
            //
            // Scoped to this loop only: the lookup/session resolution above and SaveKeyPhrasesAsync below
            // both add and save their own entities, and they rely on automatic detection.
            var autoDetectWasEnabled = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                foreach (var result in withData)
                {
                    foreach (var s in result.Stats)
                    {
                        if (!sessionIds.TryGetValue(SessionKey(result.State.User.ID, s.SessionRef), out var sessionId))
                            continue;

                        // The watermark overlap intentionally re-fetches a few seconds of interactions, so
                        // already-imported rows are expected here and are simply dropped.
                        if (existingKeys.Contains(InteractionKey(sessionId, s.GraphInteractionId)))
                            continue;

                        var interaction = new CopilotInteraction
                        {
                            GraphInteractionId = Truncate(s.GraphInteractionId, 200),
                            SessionId = sessionId,
                            UserId = result.State.User.ID,
                            RequestId = Truncate(s.RequestId, 200),
                            InteractionTypeId = lookups.InteractionTypes.Resolve(s.InteractionType),
                            AppClassId = lookups.AppClasses.Resolve(s.AppClass),
                            ConversationTypeId = lookups.ConversationTypes.Resolve(s.ConversationType),
                            LocaleId = lookups.Locales.Resolve(s.Locale),
                            DeviceId = lookups.Devices.Resolve(s.Device),
                            CreatedUtc = s.CreatedUtc,
                            BodyCharCount = s.BodyCharCount,
                            BodyWordCount = s.BodyWordCount,
                            AttachmentCount = s.AttachmentCount,
                            LinkCount = s.LinkCount,
                            MentionCount = s.MentionCount,
                            ContextCount = s.ContextCount,
                            ResponseLatencyMs = s.ResponseLatencyMs,
                            SentimentScore = s.SentimentScore,
                            LanguageId = lookups.Languages.Resolve(s.LanguageName),
                        };

                        newInteractions.Add(interaction);
                        db.CopilotInteractions.Add(interaction);

                        if (s.KeyPhrases != null && s.KeyPhrases.Count > 0)
                            keyPhrasesByInteraction[interaction] = s.KeyPhrases;

                        // Guard against the same interaction appearing twice inside one batch.
                        existingKeys.Add(InteractionKey(sessionId, s.GraphInteractionId));
                    }
                }

                if (newInteractions.Count == 0)
                    return 0;

                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }

            await SaveKeyPhrasesAsync(db, keyPhrasesByInteraction);

            return newInteractions.Count;
        }

        /// <summary>
        /// Resolves every lookup value the batch needs in one pass per table, inserting the missing ones.
        /// </summary>
        private async Task<LookupSet> ResolveLookupsAsync(AnalyticsEntitiesContext db, List<InteractionStats> allStats)
        {
            var set = new LookupSet
            {
                InteractionTypes = await LookupTable<CopilotInteractionTypeLookup>.BuildAsync(db, db.CopilotInteractionTypes, allStats.Select(s => s.InteractionType)),
                AppClasses = await LookupTable<CopilotInteractionAppClass>.BuildAsync(db, db.CopilotInteractionAppClasses, allStats.Select(s => s.AppClass)),
                ConversationTypes = await LookupTable<CopilotInteractionConversationType>.BuildAsync(db, db.CopilotInteractionConversationTypes, allStats.Select(s => s.ConversationType)),
                Locales = await LookupTable<CopilotInteractionLocale>.BuildAsync(db, db.CopilotInteractionLocales, allStats.Select(s => s.Locale)),
                Devices = await LookupTable<CopilotInteractionDevice>.BuildAsync(db, db.CopilotInteractionDevices, allStats.Select(s => s.Device)),
                Languages = await LookupTable<Language>.BuildAsync(db, db.Languages, allStats.Select(s => s.LanguageName)),
            };

            return set;
        }

        /// <summary>
        /// Maps each (user, sessionRef) to a session row id, creating rows for threads not seen before.
        /// </summary>
        private async Task<Dictionary<string, int>> ResolveSessionsAsync(AnalyticsEntitiesContext db, List<UserLoadResult> withData)
        {
            var wanted = new Dictionary<string, Tuple<int, string>>(StringComparer.Ordinal);
            foreach (var result in withData)
            {
                foreach (var s in result.Stats)
                {
                    var sessionRef = Truncate(s.SessionRef, 450);
                    if (string.IsNullOrEmpty(sessionRef))
                        continue;

                    var key = SessionKey(result.State.User.ID, sessionRef);
                    if (!wanted.ContainsKey(key))
                        wanted[key] = Tuple.Create(result.State.User.ID, sessionRef);
                }
            }

            var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
            if (wanted.Count == 0)
                return resolved;

            var refs = wanted.Values.Select(v => v.Item2).Distinct(StringComparer.Ordinal).ToList();
            foreach (var refChunk in ChunkStrings(refs))
            {
                var existing = await db.CopilotInteractionSessions
                    .Where(s => refChunk.Contains(s.SessionRef))
                    .Select(s => new { s.ID, s.SessionRef, s.UserId })
                    .ToListAsync();

                foreach (var row in existing)
                    resolved[SessionKey(row.UserId, row.SessionRef)] = row.ID;
            }

            var toCreate = new List<CopilotInteractionSession>();
            foreach (var entry in wanted)
            {
                if (resolved.ContainsKey(entry.Key))
                    continue;

                var session = new CopilotInteractionSession
                {
                    UserId = entry.Value.Item1,
                    SessionRef = entry.Value.Item2
                };
                toCreate.Add(session);
                db.CopilotInteractionSessions.Add(session);
            }

            if (toCreate.Count > 0)
            {
                await db.SaveChangesAsync();
                foreach (var session in toCreate)
                    resolved[SessionKey(session.UserId, session.SessionRef)] = session.ID;
            }

            return resolved;
        }

        /// <summary>
        /// Existing (session, interaction) keys, so the overlap re-fetch doesn't produce duplicates. Batched
        /// rather than one query per interaction.
        /// </summary>
        /// <summary>
        /// Oldest interaction in the batch, less <see cref="DedupLookbackMarginDays"/>. Rows older than this
        /// cannot collide with anything in the batch, so there is no point reading them.
        /// </summary>
        private static DateTime DedupWindowStart(List<UserLoadResult> withData)
        {
            return DedupWindowStart(withData.SelectMany(r => r.Stats).Select(s => s.CreatedUtc));
        }

        /// <summary>
        /// The timestamp-only core of <see cref="DedupWindowStart(List{UserLoadResult})"/>, separated so it
        /// can be unit tested without building loader results.
        /// </summary>
        /// <summary>
        /// The "no bound" sentinel for the de-duplication window.
        ///
        /// Deliberately NOT <see cref="DateTime.MinValue"/>. <c>copilot_interactions.created_utc</c> is a
        /// SQL Server <c>datetime</c> (EF6 <c>c.DateTime()</c>), whose floor is 1753-01-01; passing
        /// 0001-01-01 as a parameter against that column risks a <c>SqlDateTime</c> overflow. That would
        /// turn this guard - whose entire purpose is to FAIL OPEN when the window cannot be computed - into
        /// one that fails closed by throwing, in exactly the situation it exists for. No Copilot
        /// interaction can predate 1753, so this is equivalent in meaning and safe as a parameter.
        /// </summary>
        internal static readonly DateTime UnboundedDedupWindowStart = new DateTime(1753, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static DateTime DedupWindowStart(IEnumerable<DateTime> createdUtcs)
        {
            var oldest = DateTime.MaxValue;
            if (createdUtcs != null)
            {
                foreach (var created in createdUtcs)
                {
                    // A default timestamp means we cannot place that row in time. Skipping it would compute
                    // a window that EXCLUDES the stored row it duplicates - a missed duplicate, which hits
                    // the unique index and fails the batch. So any default makes the whole window fall
                    // open. Reachable in practice: Graph returning "0001-01-01T00:00:00Z" deserialises to a
                    // non-null DateTime that IS default, so the null check in InteractionStatsExtractor
                    // does not cover this.
                    if (created == default(DateTime))
                        return UnboundedDedupWindowStart;

                    if (created < oldest)
                        oldest = created;
                }
            }

            // No timestamps at all - fall back to reading everything rather than risking a missed duplicate.
            if (oldest == DateTime.MaxValue)
                return UnboundedDedupWindowStart;

            // Guard the subtraction, and never return a value below the datetime floor.
            if (oldest <= UnboundedDedupWindowStart.AddDays(DedupLookbackMarginDays))
                return UnboundedDedupWindowStart;

            return oldest.AddDays(-DedupLookbackMarginDays);
        }

        private async Task<HashSet<string>> LoadExistingInteractionKeysAsync(AnalyticsEntitiesContext db, List<int> sessionIds, DateTime fromUtc)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (sessionIds == null || sessionIds.Count == 0)
                return keys;

            var distinct = sessionIds.Distinct().ToList();
            foreach (var idChunk in ChunkIds(distinct))
            {
                // Bounded by created_utc so a long-lived thread's whole history isn't re-read every cycle
                // (issue #294). Seeks IX_copilot_interactions_dedup_window (session_id, created_utc)
                // INCLUDE (graph_interaction_id), which serves the range predicate and the projection
                // without touching the base table.
                var rows = await db.CopilotInteractions
                    .Where(i => idChunk.Contains(i.SessionId) && i.CreatedUtc >= fromUtc)
                    .Select(i => new { i.SessionId, i.GraphInteractionId })
                    .ToListAsync();

                foreach (var row in rows)
                    keys.Add(InteractionKey(row.SessionId, row.GraphInteractionId));
            }

            return keys;
        }

        private async Task SaveKeyPhrasesAsync(AnalyticsEntitiesContext db, Dictionary<CopilotInteraction, List<string>> keyPhrasesByInteraction)
        {
            if (keyPhrasesByInteraction.Count == 0)
                return;

            var allPhrases = keyPhrasesByInteraction.Values.SelectMany(p => p);
            var keywordIds = await LookupTable<KeyWord>.BuildAsync(db, db.KeyWords, allPhrases);

            // Same reason as SaveChunkAsync: EF6 runs DetectChanges on every Add, so this loop is O(N^2)
            // in the number of link rows - and there are up to ten key phrases per scored prompt, on top of
            // the interactions already tracked from the same save.
            var autoDetectWasEnabled = db.Configuration.AutoDetectChangesEnabled;
            db.Configuration.AutoDetectChangesEnabled = false;
            try
            {
                foreach (var entry in keyPhrasesByInteraction)
                {
                    foreach (var phrase in entry.Value)
                    {
                        var keywordId = keywordIds.Resolve(phrase);
                        if (keywordId == null)
                            continue;

                        db.CopilotInteractionKeywords.Add(new CopilotInteractionKeyword
                        {
                            InteractionId = entry.Key.ID,
                            KeyWordId = keywordId.Value
                        });
                    }
                }

                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();
            }
            finally
            {
                db.Configuration.AutoDetectChangesEnabled = autoDetectWasEnabled;
            }
        }

        /// <summary>
        /// Persists per-user progress: advances the watermark after a complete read, and applies a back-off
        /// to users who returned nothing so they stop consuming the per-cycle call budget.
        /// </summary>
        private async Task UpdateWatermarksAsync(List<UserLoadResult> loaded, CopilotInteractionImportLog runLog)
        {
            var now = DateTime.UtcNow;

            using (var db = _dbContextFactory())
            {
                // Load every watermark for the chunk in one batched query rather than one query per user -
                // a per-row query inside a loop is exactly the pattern this codebase avoids, and at the
                // per-cycle cap that would be hundreds of extra round trips.
                var userIds = loaded.Select(r => r.State.User.ID).Distinct().ToList();
                var existing = new Dictionary<int, CopilotInteractionUserWatermark>();
                foreach (var idChunk in ChunkIds(userIds))
                {
                    var rows = await db.CopilotInteractionUserWatermarks
                        .Where(w => idChunk.Contains(w.UserId))
                        .ToListAsync();

                    foreach (var row in rows)
                        existing[row.UserId] = row;
                }

                foreach (var result in loaded)
                {
                    if (!existing.TryGetValue(result.State.User.ID, out var watermark))
                    {
                        watermark = new CopilotInteractionUserWatermark { UserId = result.State.User.ID };
                        db.CopilotInteractionUserWatermarks.Add(watermark);
                        existing[result.State.User.ID] = watermark;
                    }

                    watermark.LastRunUtc = now;
                    runLog.UsersScanned++;

                    if (result.Error != null)
                    {
                        // A retryable failure, or a partially-read window. Record it but leave the watermark
                        // alone so the same window is retried in full next cycle - advancing it here would
                        // silently skip whatever we failed to read.
                        //
                        // Deliberately does NOT touch ConsecutiveEmptyOrFailed or apply the back-off. That
                        // back-off exists to stop us re-asking users who have no Copilot licence, and it lasts
                        // CopilotInteractionHistoryEmptyUserBackOffHours (72h by default). Counting a transient
                        // Graph failure towards it means a brief 5xx or throttling blip - which hits every user
                        // in the cycle, not one - parks the entire active pilot group for three days, which is
                        // the opposite of "retried in full next cycle". A user that fails every cycle simply
                        // gets retried daily; the per-cycle ceiling and least-recently-run ordering already
                        // bound what that can cost.
                        runLog.UsersFailed++;
                        watermark.LastError = Truncate(result.Error, 500);
                        continue;
                    }

                    watermark.LastError = null;
                    runLog.InteractionsRead += result.Stats.Count;

                    if (result.UserNotAvailable)
                    {
                        // Terminal for this user (no Copilot licence, no such user). Nothing was read, so
                        // the watermark stays put; the back-off stops us asking again for a while.
                        watermark.ConsecutiveEmptyOrFailed++;
                        ApplyBackOff(watermark, now);
                        continue;
                    }

                    if (result.Truncated)
                    {
                        // We stopped at the page cap, so the window was only partly read. Advance only as far
                        // as the newest interaction actually received; the rest resumes next cycle. Not
                        // counted as empty - this user is clearly active.
                        var newestRead = InteractionStatsExtractor.GetNewestCreatedUtc(result.Stats);
                        if (newestRead != null)
                            AdvanceWatermark(watermark, newestRead.Value);

                        watermark.ConsecutiveEmptyOrFailed = 0;
                        watermark.SkipUntilUtc = null;
                        continue;
                    }

                    // Complete, successful read of the whole window.
                    //
                    // The watermark advances to the END OF THE QUERIED WINDOW, not to the newest interaction
                    // returned. Using the newest interaction would wedge the watermark permanently: the next
                    // window starts slightly before it (the overlap), so that same interaction comes back
                    // every cycle, which looks like a non-empty success and rewrites the identical
                    // watermark. An inactive user would then be re-queried for ever, never reach the empty
                    // back-off, and keep re-processing the same interaction - a standing cost for no data.
                    AdvanceWatermark(watermark, result.WindowEndUtc);

                    if (result.Stats.Count == 0)
                    {
                        watermark.ConsecutiveEmptyOrFailed++;
                        ApplyBackOff(watermark, now);
                    }
                    else
                    {
                        watermark.ConsecutiveEmptyOrFailed = 0;
                        watermark.SkipUntilUtc = null;
                    }
                }

                await db.SaveChangesAsync();
            }
        }

        /// <summary>Moves a watermark forward only - never backwards, whatever order results arrive in.</summary>
        private static void AdvanceWatermark(CopilotInteractionUserWatermark watermark, DateTime toUtc)
        {
            if (watermark.LastInteractionUtc == null || toUtc > watermark.LastInteractionUtc.Value)
                watermark.LastInteractionUtc = toUtc;
        }

        /// <summary>
        /// Backs a user off after repeated empty or failed runs.
        /// </summary>
        /// <remarks>
        /// The most common cause by far is a user without the <c>M365_COPILOT_BUSINESS_CHAT</c> service plan,
        /// which never resolves on its own. One quiet cycle is not enough evidence though - a licensed user
        /// simply may not have used Copilot that day - so the back-off only starts after two consecutive
        /// empty results, and it always expires so a newly-licensed user is picked up again.
        /// </remarks>
        internal void ApplyBackOff(CopilotInteractionUserWatermark watermark, DateTime nowUtc)
        {
            var backOffHours = _settings.CopilotInteractionHistoryEmptyUserBackOffHours;
            if (backOffHours <= 0)
                return;

            if (watermark.ConsecutiveEmptyOrFailed < 2)
                return;

            watermark.SkipUntilUtc = nowUtc.AddHours(backOffHours);
        }

        private async Task<CopilotInteractionImportLog> FinishRunAsync(CopilotInteractionImportLog runLog, Stopwatch sw)
        {
            sw.Stop();
            runLog.RunFinishedUtc = DateTime.UtcNow;

            using (var db = _dbContextFactory())
            {
                db.CopilotInteractionImportLogs.Add(runLog);
                await db.SaveChangesAsync();
            }

            _logger.LogInformation(
                $"Copilot interaction history import finished in {sw.Elapsed.TotalSeconds:N0}s: " +
                $"{runLog.UsersInScope} in scope, {runLog.UsersScanned} called, {runLog.UsersSkipped} backed off, " +
                $"{runLog.UsersFailed} failed; {runLog.InteractionsRead} interaction(s) read, " +
                $"{runLog.InteractionsSaved} saved, {runLog.CognitiveDocsScored} prompt(s) scored.");

            return runLog;
        }

        #endregion

        #region Helpers

        private static string SessionKey(int userId, string sessionRef) => userId + "|" + sessionRef;

        private static string InteractionKey(int sessionId, string graphInteractionId) => sessionId + "|" + graphInteractionId;

        internal static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength);
        }

        private static IEnumerable<List<int>> ChunkIds(List<int> ids)
        {
            for (int i = 0; i < ids.Count; i += SqlInClauseChunkSize)
                yield return ids.GetRange(i, Math.Min(SqlInClauseChunkSize, ids.Count - i));
        }

        private static IEnumerable<List<string>> ChunkStrings(List<string> values)
        {
            for (int i = 0; i < values.Count; i += SqlInClauseChunkSize)
                yield return values.GetRange(i, Math.Min(SqlInClauseChunkSize, values.Count - i));
        }

        private class UserImportState
        {
            public Common.Entities.User User { get; set; }
            public CopilotInteractionUserWatermark Watermark { get; set; }
        }

        private class UserLoadResult
        {
            public UserImportState State { get; set; }
            public DateTime WindowEndUtc { get; set; }
            public List<InteractionStats> Stats { get; set; } = new List<InteractionStats>();

            /// <summary>Plain-text prompt bodies aligned with <see cref="Stats"/>; discarded after enrichment.</summary>
            public List<string> PromptBodies { get; set; }

            public bool UserNotAvailable { get; set; }
            public bool Truncated { get; set; }
            public string Error { get; set; }
        }

        private class LookupSet
        {
            public LookupTable<CopilotInteractionTypeLookup> InteractionTypes { get; set; }
            public LookupTable<CopilotInteractionAppClass> AppClasses { get; set; }
            public LookupTable<CopilotInteractionConversationType> ConversationTypes { get; set; }
            public LookupTable<CopilotInteractionLocale> Locales { get; set; }
            public LookupTable<CopilotInteractionDevice> Devices { get; set; }
            public LookupTable<Language> Languages { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Name-to-id map for a simple lookup table, resolved in one round trip per batch.
    /// </summary>
    /// <remarks>
    /// Deliberately batch-oriented: the naive alternative (query per distinct value inside the row loop)
    /// is the per-row-EF-query anti-pattern this codebase explicitly avoids, and would issue thousands of
    /// round trips for a busy pilot group.
    /// </remarks>
    internal class LookupTable<T> where T : AbstractEFEntityWithName, new()
    {
        /// <summary>Matches the <c>name</c> column width on <see cref="AbstractEFEntityWithName"/>.</summary>
        private const int MaxNameLength = 100;

        private readonly Dictionary<string, int> _idsByName;

        private LookupTable(Dictionary<string, int> idsByName)
        {
            _idsByName = idsByName;
        }

        /// <summary>
        /// Loads the ids for every distinct non-empty name in <paramref name="names"/>, creating rows for any
        /// that don't exist yet.
        /// </summary>
        public static async Task<LookupTable<T>> BuildAsync(AnalyticsEntitiesContext db, IDbSet<T> set, IEnumerable<string> names)
        {
            // OrdinalIgnoreCase matches SQL Server's default case-insensitive collation, so we don't create
            // two rows that the database would consider identical - and no ToLower() allocations either.
            var idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var wanted = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var name = raw.Trim();
                if (name.Length > MaxNameLength)
                    name = name.Substring(0, MaxNameLength);

                if (seen.Add(name))
                    wanted.Add(name);
            }

            if (wanted.Count == 0)
                return new LookupTable<T>(idsByName);

            // Chunked: an unchunked Contains() becomes one SQL parameter per value, and a busy chunk of
            // users can easily produce more distinct key phrases than SQL Server's 2100-parameter limit.
            foreach (var nameChunk in ChunkNames(wanted))
            {
                var existing = await set.Where(x => nameChunk.Contains(x.Name)).ToListAsync();
                foreach (var row in existing)
                {
                    if (row.Name != null)
                        idsByName[row.Name] = row.ID;
                }
            }

            var toCreate = new List<T>();
            foreach (var name in wanted)
            {
                if (idsByName.ContainsKey(name))
                    continue;

                var entity = new T { Name = name };
                toCreate.Add(entity);
                set.Add(entity);
            }

            if (toCreate.Count > 0)
            {
                await db.SaveChangesAsync();
                foreach (var entity in toCreate)
                    idsByName[entity.Name] = entity.ID;
            }

            return new LookupTable<T>(idsByName);
        }

        /// <summary>Id for a name, or null when the value was absent from the source payload.</summary>
        public int? Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var trimmed = name.Trim();
            if (trimmed.Length > MaxNameLength)
                trimmed = trimmed.Substring(0, MaxNameLength);

            return _idsByName.TryGetValue(trimmed, out var id) ? id : (int?)null;
        }

        /// <summary>Keeps each IN clause well inside SQL Server's 2100-parameter limit.</summary>
        private static IEnumerable<List<string>> ChunkNames(List<string> names)
        {
            const int chunkSize = 1000;
            for (int i = 0; i < names.Count; i += chunkSize)
                yield return names.GetRange(i, Math.Min(chunkSize, names.Count - i));
        }
    }
}
