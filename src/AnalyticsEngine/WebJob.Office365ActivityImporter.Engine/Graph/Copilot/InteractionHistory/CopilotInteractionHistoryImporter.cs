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
    /// target that would be 200k calls per cycle, which is why the import is off by default and why four
    /// independent brakes apply, in this order:
    /// <list type="number">
    /// <item>the <c>CopilotInteractionHistory</c> feature toggle (off by default);</item>
    /// <item>the <c>UserGroupsFilter</c> scope, which must be set - an unscoped run is refused unless
    /// <c>CopilotInteractionHistoryAllowUnscoped</c> is explicitly turned on;</item>
    /// <item><c>CopilotInteractionHistoryMaxUsersPerCycle</c>, a hard per-cycle ceiling. Users are taken
    /// least-recently-run first, so a scope bigger than the cap is still covered - just round-robin over
    /// several cycles rather than all at once;</item>
    /// <item>a per-user back-off list, so users who return nothing (almost always because they have no
    /// <c>M365_COPILOT_BUSINESS_CHAT</c> service plan) stop consuming the budget.</item>
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
        /// run at all (missing permission, or an unscoped configuration).
        /// </summary>
        public async Task<CopilotInteractionImportLog> ImportAsync()
        {
            _logger.LogInformation("Starting Copilot AI interaction history import...");
            var sw = Stopwatch.StartNew();

            if (!ValidateScopeConfiguration())
                return null;

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
                var scopedUsers = await ResolveScopedUsersAsync();
                runLog.UsersInScope = scopedUsers.Count;

                if (scopedUsers.Count == 0)
                {
                    _logger.LogWarning(
                        $"No users matched the UserGroupsFilter ('{_settings.UserGroupsFilter}') for the Copilot " +
                        "interaction history import, so there is nothing to do. Check the group name(s) - the filter " +
                        "matches Entra ID group display names and supports * wildcards.");
                    return await FinishRunAsync(runLog, sw);
                }

                var due = await SelectDueUsersAsync(scopedUsers, runLog);
                if (due.Count == 0)
                {
                    _logger.LogInformation(
                        $"Copilot interaction history: all {scopedUsers.Count} in-scope user(s) are inside their " +
                        "back-off window, so no Graph calls were made this cycle.");
                    return await FinishRunAsync(runLog, sw);
                }

                _logger.LogInformation(
                    $"Copilot interaction history: {scopedUsers.Count} user(s) in scope, calling Graph for {due.Count} " +
                    $"of them this cycle (cap {_settings.CopilotInteractionHistoryMaxUsersPerCycle}, least-recently-run " +
                    $"first). Cognitive enrichment is {(_cognitiveEnricher.IsEnabled ? "enabled" : "disabled")}.");

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
                runLog.Error = Truncate(ex.Message, 1000);
            }

            return await FinishRunAsync(runLog, sw);
        }

        #region Scope and scheduling

        /// <summary>
        /// Refuses to run against an unbounded user set. Without this, an operator who ticks the box but
        /// leaves <c>UserGroupsFilter</c> empty would silently start one Graph call per user in the tenant.
        /// </summary>
        internal bool ValidateScopeConfiguration()
        {
            if (_userGroupsFilter.Patterns.Count > 0)
                return true;

            if (_settings.CopilotInteractionHistoryAllowUnscoped)
            {
                _logger.LogWarning(
                    "Copilot interaction history is running UNSCOPED (no UserGroupsFilter) because " +
                    "CopilotInteractionHistoryAllowUnscoped is true. This costs one Graph call per user in the " +
                    $"database, limited to {_settings.CopilotInteractionHistoryMaxUsersPerCycle} per cycle. " +
                    "Setting UserGroupsFilter to a pilot group is strongly recommended.");
                return true;
            }

            _logger.LogWarning(
                "Skipping the Copilot interaction history import: no UserGroupsFilter is configured. This import " +
                "makes one Microsoft Graph call per user, so it must be pointed at a pilot group. Set the " +
                "UserGroupsFilter app setting to one or more Entra ID group display names (';'-separated, '*' " +
                "wildcards allowed), or set CopilotInteractionHistoryAllowUnscoped=true to accept the cost of " +
                "scanning every user.");
            return false;
        }

        /// <summary>
        /// Users eligible for the import: enabled accounts with a usable identifier that are members of a
        /// group matching the filter.
        /// </summary>
        /// <remarks>
        /// Resolution is deliberately group-first - list the pilot group's members, then intersect with the
        /// users table - rather than asking "is this user in the group?" for every user in the database.
        /// The latter is one Graph call per tenant user, which at the ~200k-user design target would spend
        /// 200,000 calls just working out who to import, before reading a single interaction.
        /// </remarks>
        private async Task<List<Common.Entities.User>> ResolveScopedUsersAsync()
        {
            if (_userGroupsFilter.Patterns.Count == 0)
            {
                // Only reachable when the operator explicitly opted in to an unscoped run, which is the one
                // case where we genuinely do want every enabled user.
                using (var db = _dbContextFactory())
                {
                    return await db.users
                        .Where(u => u.UserPrincipalName != null && u.UserPrincipalName != ""
                                    && (u.AccountEnabled == null || u.AccountEnabled == true))
                        .ToListAsync();
                }
            }

            if (_pilotGroupResolver == null)
            {
                _logger.LogWarning(
                    "A UserGroupsFilter is configured but no pilot-group resolver is available, so membership can't " +
                    "be checked. Skipping the Copilot interaction history import rather than risk scanning every user.");
                return new List<Common.Entities.User>();
            }

            var memberUpns = await _pilotGroupResolver.GetMemberUpnsAsync(_userGroupsFilter);
            if (memberUpns.Count == 0)
                return new List<Common.Entities.User>();

            // Query BY the pilot members rather than loading the directory and filtering in memory. The old
            // shape materialised every enabled user - ~200,000 tracked entities at the design baseline - just
            // to keep the handful in a pilot group, every cycle. Chunked to stay inside SQL Server's
            // 2100-parameter limit.
            //
            // Disabled accounts are still excluded here: they can't generate new Copilot interactions, so
            // calling Graph for them would spend the per-cycle budget on guaranteed-empty results.
            var inScope = new List<Common.Entities.User>();
            using (var db = _dbContextFactory())
            {
                foreach (var upnChunk in ChunkStrings(memberUpns.ToList()))
                {
                    var rows = await db.users
                        .Where(u => upnChunk.Contains(u.UserPrincipalName)
                                    && (u.AccountEnabled == null || u.AccountEnabled == true))
                        .ToListAsync();

                    inScope.AddRange(rows);
                }
            }

            _logger.LogInformation(
                $"Copilot interaction history: the pilot group(s) have {memberUpns.Count} member(s), of which " +
                $"{inScope.Count} are enabled users present in the analytics database.");

            return inScope;
        }

        /// <summary>
        /// Picks which in-scope users to actually call Graph for this cycle: drops those still inside a
        /// back-off window, then takes the least-recently-run first up to the cap.
        /// </summary>
        /// <remarks>
        /// Ordering by last run (nulls first) is what makes the cap safe. A pilot group larger than the cap
        /// still gets fully covered, just spread over consecutive cycles, and never-imported users are always
        /// preferred over ones already up to date.
        /// </remarks>
        private async Task<List<UserImportState>> SelectDueUsersAsync(List<Common.Entities.User> scopedUsers, CopilotInteractionImportLog runLog)
        {
            var now = DateTime.UtcNow;
            var userIds = scopedUsers.Select(u => u.ID).ToList();

            var watermarksByUserId = new Dictionary<int, CopilotInteractionUserWatermark>();
            using (var db = _dbContextFactory())
            {
                foreach (var idChunk in ChunkIds(userIds))
                {
                    var rows = await db.CopilotInteractionUserWatermarks
                        .Where(w => idChunk.Contains(w.UserId))
                        .ToListAsync();

                    foreach (var row in rows)
                        watermarksByUserId[row.UserId] = row;
                }
            }

            var due = new List<UserImportState>();
            foreach (var user in scopedUsers)
            {
                watermarksByUserId.TryGetValue(user.ID, out var watermark);

                if (watermark?.SkipUntilUtc != null && watermark.SkipUntilUtc.Value > now)
                {
                    runLog.UsersSkipped++;
                    continue;
                }

                due.Add(new UserImportState { User = user, Watermark = watermark });
            }

            return due
                .OrderBy(d => d.Watermark?.LastRunUtc ?? DateTime.MinValue)
                .Take(_settings.CopilotInteractionHistoryMaxUsersPerCycle)
                .ToList();
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

            var existing = await LoadExistingInteractionKeysAsync(db, sessionIdByUserAndRef.Values.Distinct().ToList());
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
            var existingKeys = await LoadExistingInteractionKeysAsync(db, sessionIds.Values.ToList());

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
        private async Task<HashSet<string>> LoadExistingInteractionKeysAsync(AnalyticsEntitiesContext db, List<int> sessionIds)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (sessionIds == null || sessionIds.Count == 0)
                return keys;

            var distinct = sessionIds.Distinct().ToList();
            foreach (var idChunk in ChunkIds(distinct))
            {
                var rows = await db.CopilotInteractions
                    .Where(i => idChunk.Contains(i.SessionId))
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
