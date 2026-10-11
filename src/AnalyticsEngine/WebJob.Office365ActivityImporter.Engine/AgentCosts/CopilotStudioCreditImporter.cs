using Common.Entities.Config;
using Common.Entities.Entities.AgentCosts;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>The two log outcomes one consumption import produces.</summary>
    public sealed class ConsumptionImportOutcomes
    {
        public ConsumptionImportOutcomes(AgentCostImportOutcome agents, AgentCostImportOutcome users)
        {
            Agents = agents;
            Users = users;
        }

        /// <summary>Per-agent figures (<c>CopilotStudioCredits</c>).</summary>
        public AgentCostImportOutcome Agents { get; }

        /// <summary>Per-user figures (<c>CopilotStudioUserCredits</c>).</summary>
        public AgentCostImportOutcome Users { get; }
    }

    /// <summary>
    /// Imports billed Microsoft Copilot Studio consumption ("Copilot Credits") into SQL.
    ///
    /// <para>This is <b>what Microsoft charged</b>, per agent and per day. It is deliberately not reconciled
    /// with the per-conversation <c>CopilotCreditEstimation</c> this product derives from audit events: that
    /// one is an inference over audit data and the two will legitimately disagree.</para>
    ///
    /// <para><b>Where the figures come from.</b> Microsoft restricted the tenant-wide per-agent route to its own
    /// admin center, so consumption is read with a delegated administrator connection: <c>/users</c> lists who
    /// consumed, and <c>/users/{id}/resources</c> gives each active user's consumption per agent. Per-agent
    /// rows are the sum of those, with the distinct contributing users counted. They therefore cover
    /// consumption Microsoft attributes to a user; anything it does not attribute is not visible via any
    /// permitted API, and the capacity route's consumed total remains the authoritative tenant total.</para>
    /// </summary>
    public class CopilotStudioCreditImporter
    {
        /// <summary>Parallel per-user reads. Modest on purpose: the throttle-aware client retries a 429.</summary>
        private const int MaxConcurrentUserReads = 6;
        private readonly ILogger _logger;
        private readonly ICopilotStudioCreditSource _source;
        private readonly IAgentCostStore _store;
        private readonly IClock _clock;
        private readonly int _trailingWindowDays;

        /// <summary>
        /// Resolves the Entra object ids on per-user rows to <c>dbo.users</c>. Optional: when absent the
        /// rows are stored unlinked, which is exactly what happens for a user who cannot be resolved anyway.
        /// </summary>
        private readonly AgentCostUserResolver _userResolver;

        /// <summary>
        /// The <c>UserGroupsFilter</c> scope. Per-user credit rows for anyone outside it are not stored; the per-agent
        /// and capacity figures, which carry no identities, stay tenant-wide. Null means unfiltered.
        /// </summary>
        private readonly IUserImportScopeProvider _userScopeProvider;

        /// <summary>
        /// Stops a broken or looping continuation token from paging for ever. A tenant's daily consumption is
        /// a few thousand rows at most, and the page size is 5000, so this is far above any real response.
        /// </summary>
        private const int MaxPages = 500;

        public CopilotStudioCreditImporter(
            ILogger logger,
            ICopilotStudioCreditSource source,
            IAgentCostStore store,
            int trailingWindowDays = AppConfig.DefaultCopilotStudioCreditsTrailingWindowDays,
            IClock clock = null,
            AgentCostUserResolver userResolver = null,
            IUserImportScopeProvider userScopeProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? SystemClock.Instance;
            _userResolver = userResolver;
            _userScopeProvider = userScopeProvider;
            _trailingWindowDays = trailingWindowDays > 0
                ? Math.Min(trailingWindowDays, AppConfig.MaxCopilotStudioCreditsTrailingWindowDays)
                : AppConfig.DefaultCopilotStudioCreditsTrailingWindowDays;
        }

        /// <summary>
        /// Runs one import. Returns the log row it wrote, which carries the outcome; never throws, so a
        /// failure here cannot take down the import sections that run after it.
        /// </summary>
        public async Task<ConsumptionImportOutcomes> ImportConsumptionAsync()
        {
            // Microsoft's consumption figures lag, and a partially-settled day is re-read on the next run
            // anyway, so the window ends today rather than trying to guess how far behind the API is.
            var today = _clock.UtcNow.Date;
            var from = today.AddDays(-(_trailingWindowDays - 1));

            var agentLog = NewLog(AgentCostImportNames.CopilotStudioCredits, from, today);
            var userLog = NewLog(AgentCostImportNames.CopilotStudioUserCredits, from, today);

            if (!_source.CanReadConsumption)
            {
                // Every consumption route refuses the application identity, so don't send requests that are
                // known to fail. A named state the portal can act on, not an error.
                agentLog.Error = userLog.Error = AgentCostImportNames.ConnectionRequired;
                _logger.LogInformation("Copilot Studio consumption was not imported: no delegated administrator connection "
                    + "exists. Connect one in Administration > Copilot Studio billing connection. Capacity is unaffected.");
                await SafeSaveLogAsync(agentLog);
                await SafeSaveLogAsync(userLog);
                return new ConsumptionImportOutcomes(
                    new AgentCostImportOutcome(agentLog, isConnectionRequired: true),
                    new AgentCostImportOutcome(userLog, isConnectionRequired: true));
            }

            var activeUsersByDay = new List<KeyValuePair<DateTime, List<string>>>();
            var userPhaseCompleted = false;

            // Phase A: one /users read per day, shared by the per-user table and the per-agent derivation.
            try
            {
                var environmentNames = await _source.GetEnvironmentNamesAsync();
                var userScope = _userScopeProvider == null ? UserImportScope.Unfiltered : await _userScopeProvider.GetScopeAsync();

                var mapped = new List<CopilotStudioCreditUserDaily>();
                var rowsRead = 0;
                var rowsOutOfScope = 0;

                // One request per day: the usage date is then a fact about the request, not an inference
                // about the response.
                for (var day = from; day <= today; day = day.AddDays(1))
                {
                    var dayRows = await ReadAllUserPagesAsync(day);
                    rowsRead += dayRows.Count;

                    // Tenant-wide on purpose: the per-agent figures carry no identities, so they include
                    // people outside UserGroupsFilter. Only the stored per-user rows are filtered.
                    var active = dayRows.Where(r => r.HasActivity && !string.IsNullOrWhiteSpace(r.UserId))
                        .Select(r => r.UserId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    activeUsersByDay.Add(new KeyValuePair<DateTime, List<string>>(day, active));

                    var inScopeRows = SelectRowsInScope(dayRows, userScope);
                    rowsOutOfScope += dayRows.Count - inScopeRows.Count;
                    mapped.AddRange(MapUserRows(inScopeRows, day, environmentNames, _clock.UtcNow));
                }

                if (rowsOutOfScope > 0)
                {
                    _logger.LogInformation($"Copilot Studio per-user credits: {rowsOutOfScope:N0} row(s) for people outside UserGroupsFilter were not stored.");
                }

                userLog.RowsRead = rowsRead;
                await LinkUsersAsync(mapped);
                userLog.RowsSaved = await _store.UpsertCopilotStudioUserCreditsAsync(mapped);
                userPhaseCompleted = true;

                _logger.LogInformation($"Copilot Studio per-user credits: read {userLog.RowsRead:N0} row(s) across "
                    + $"{(today - from).Days + 1} day(s), stored {userLog.RowsSaved:N0}.");
                await SafeSaveLogAsync(userLog);

                // Phase B: the per-agent figures. See ReadDayResourcesAsync for the call volume.
                var agentRows = new List<CopilotStudioCreditDaily>();
                var resourceRowsRead = 0;
                foreach (var entry in activeUsersByDay)
                {
                    var rows = await ReadDayResourcesAsync(entry.Key, entry.Value);
                    resourceRowsRead += rows.Count;
                    agentRows.AddRange(MapAndAggregate(rows, entry.Key, entry.Key, environmentNames, _clock.UtcNow));
                }

                // Stored only once every day has been read, so a failed window never leaves a partial figure.
                agentLog.RowsRead = resourceRowsRead;
                agentLog.RowsSaved = await _store.UpsertCopilotStudioCreditsAsync(agentRows);

                _logger.LogInformation(
                    $"Copilot Studio credits: read {agentLog.RowsRead:N0} per-user agent row(s) for "
                    + $"{activeUsersByDay.Sum(e => e.Value.Count):N0} active user-day(s) ({from:yyyy-MM-dd}..{today:yyyy-MM-dd}), "
                    + $"stored {agentLog.RowsSaved:N0} per-agent row(s).");
                await SafeSaveLogAsync(agentLog);
                return new ConsumptionImportOutcomes(new AgentCostImportOutcome(agentLog), new AgentCostImportOutcome(userLog));
            }
            catch (AgentCostAuthorisationException ex)
            {
                _logger.LogError(ex, "Copilot Studio credit import failed: " + ex.Message);
                return await FailAsync(agentLog, userLog, ex.Message, true, userPhaseCompleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Copilot Studio credit import failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
                return await FailAsync(agentLog, userLog, ex.Message, false, userPhaseCompleted);
            }
        }

        private AgentCostImportLog NewLog(string name, DateTime from, DateTime to) => new AgentCostImportLog
        {
            ImportName = name,
            ImportedUtc = _clock.UtcNow,
            WindowFrom = from,
            WindowTo = to,
        };

        /// <summary>
        /// Records the failure on whichever logs were not already completed (the per-user log is saved as soon
        /// as the per-user rows are stored, so a later per-agent failure does not overwrite it).
        /// </summary>
        private async Task<ConsumptionImportOutcomes> FailAsync(
            AgentCostImportLog agentLog, AgentCostImportLog userLog, string error, bool authorisation, bool userPhaseCompleted)
        {
            agentLog.Error = error;
            await SafeSaveLogAsync(agentLog);

            var userFailed = !userPhaseCompleted;
            if (userFailed)
            {
                userLog.Error = error;
                await SafeSaveLogAsync(userLog);
            }

            return new ConsumptionImportOutcomes(
                new AgentCostImportOutcome(agentLog, isAuthorisationFailure: authorisation),
                new AgentCostImportOutcome(userLog, isAuthorisationFailure: userFailed && authorisation));
        }

        /// <summary>
        /// Every page of one day of <c>/users</c>. Read in full before anything is mapped: rows for the same
        /// person that straddle a page boundary must be combined, not overwritten by the later page.
        /// </summary>
        private async Task<List<CopilotStudioUserCreditRow>> ReadAllUserPagesAsync(DateTime day)
        {
            var all = new List<CopilotStudioUserCreditRow>();
            string continuationToken = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;

            do
            {
                if (++pages > MaxPages)
                {
                    throw new AgentCostIncompleteReadException(
                        $"Copilot Studio per-user credit paging for {day:yyyy-MM-dd} exceeded the {MaxPages}-page "
                        + "safety limit, so the day could not be read completely. Nothing was stored.");
                }

                var page = await _source.GetUserConsumptionPageAsync(day, day, continuationToken);
                if (page == null)
                {
                    throw new AgentCostIncompleteReadException(
                        "The Power Platform licensing API does not offer the per-user consumption route on this tenant, so "
                        + "no Copilot Studio consumption could be read. It will be retried on the next cycle.");
                }

                all.AddRange(page.Rows);
                if (!page.HasMore) break;

                if (!seenTokens.Add(page.ContinuationToken))
                {
                    throw new AgentCostIncompleteReadException(
                        "The Power Platform licensing API returned a per-user continuation token it had already "
                        + "returned, so paging could not complete. Nothing was stored for this window.");
                }

                continuationToken = page.ContinuationToken;
            }
            while (!string.IsNullOrEmpty(continuationToken));

            return all;
        }

        /// <summary>
        /// Reads every active user's per-agent consumption for one day, with bounded concurrency.
        /// </summary>
        /// <remarks>
        /// <para><b>What this covers.</b> Microsoft restricted the tenant-wide per-agent route to its own admin
        /// center, so per-agent figures are rebuilt from <c>/users/{userId}/resources</c>. They therefore cover
        /// consumption Microsoft attributes to a user; anything it does not attribute to a user is not
        /// visible through any permitted API. The capacity route's consumed total remains the authoritative
        /// tenant total.</para>
        /// <para><b>Cost at 200,000 users.</b> One call per active Copilot Studio user per day in the window:
        /// calls ~ window days (default 7) x daily active users, plus a few <c>/users</c> pages. With 10,000
        /// daily active users that is ~70,000 calls per import (a few minutes at 6-way concurrency), which is
        /// why only users with consumption are fanned out and why the import runs on its own cadence.</para>
        /// <para>Any failed read fails the day (and so the window): a figure silently missing a user would
        /// look like a drop in spend.</para>
        /// </remarks>
        private async Task<List<CopilotStudioCreditRow>> ReadDayResourcesAsync(DateTime day, IReadOnlyList<string> userIds)
        {
            var all = new List<CopilotStudioCreditRow>();
            if (userIds.Count == 0) return all;

            var next = -1;
            var failed = 0;
            Exception firstError = null;

            var workers = Enumerable.Range(0, Math.Min(MaxConcurrentUserReads, userIds.Count)).Select(_ => Task.Run(async () =>
            {
                while (Volatile.Read(ref failed) == 0)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= userIds.Count) return;

                    try
                    {
                        var rows = await ReadUserResourcesAsync(userIds[i], day);
                        lock (all) all.AddRange(rows);
                    }
                    catch (Exception ex)
                    {
                        lock (all) { if (firstError == null) firstError = ex; }
                        Interlocked.Exchange(ref failed, 1);
                        return;
                    }
                }
            })).ToList();

            await Task.WhenAll(workers);

            if (firstError != null) ExceptionDispatchInfo.Capture(firstError).Throw();
            return all;
        }

        private async Task<List<CopilotStudioCreditRow>> ReadUserResourcesAsync(string userId, DateTime day)
        {
            var all = new List<CopilotStudioCreditRow>();
            string continuationToken = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;

            do
            {
                if (++pages > MaxPages)
                {
                    throw new AgentCostIncompleteReadException(
                        $"Copilot Studio per-agent paging for one user on {day:yyyy-MM-dd} exceeded the {MaxPages}-page safety "
                        + "limit, so the day could not be read completely. Nothing was stored.");
                }

                var page = await _source.GetUserResourceConsumptionPageAsync(userId, day, continuationToken);
                foreach (var row in page.Rows)
                {
                    row.UserId = userId;
                    all.Add(row);
                }

                if (!page.HasMore) break;

                if (!seenTokens.Add(page.ContinuationToken))
                {
                    throw new AgentCostIncompleteReadException(
                        "The Power Platform licensing API returned a continuation token it had already returned, so "
                        + "paging could not complete. Nothing was stored for this window; it will be retried on the next "
                        + "cycle.");
                }

                continuationToken = page.ContinuationToken;
            }
            while (!string.IsNullOrEmpty(continuationToken));

            return all;
        }

        /// <summary>
        /// Maps API rows onto entities, and combines any that land on the same dimension slice.
        /// </summary>
        /// <remarks>
        /// Aggregation is expected here, not defensive: each active user contributes a row per agent and
        /// feature, so many users land on the same dimension slice. Credits are summed, and the distinct-user
        /// count is the number of different users who contributed to the slice.
        /// </remarks>
        internal static IReadOnlyList<CopilotStudioCreditDaily> MapAndAggregate(
            IEnumerable<CopilotStudioCreditRow> rows,
            DateTime windowFrom,
            DateTime windowTo,
            IReadOnlyDictionary<string, string> environmentNames,
            DateTime importedUtc)
        {
            var byHash = new Dictionary<string, CopilotStudioCreditDaily>(StringComparer.Ordinal);
            var usersByHash = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var row in rows ?? Enumerable.Empty<CopilotStudioCreditRow>())
            {
                if (row == null) continue;

                var usageDate = ResolveUsageDate(row, windowFrom, windowTo);

                var hash = AgentCostRowHasher.Hash(
                    // Invariant: part of the row's identity, so it must not change with the host's calendar.
                    usageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    row.EnvironmentId,
                    row.ResourceId,
                    row.FeatureName);

                if (!string.IsNullOrWhiteSpace(row.UserId))
                {
                    if (!usersByHash.TryGetValue(hash, out var users))
                    {
                        usersByHash[hash] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    users.Add(row.UserId);
                }

                if (byHash.TryGetValue(hash, out var existing))
                {
                    existing.BilledCredits += row.Consumed;

                    if (row.NonBillableQuantity.HasValue)
                    {
                        existing.NonBilledCredits = (existing.NonBilledCredits ?? 0m) + row.NonBillableQuantity.Value;
                    }

                    continue;
                }

                string environmentName = null;
                if (!string.IsNullOrEmpty(row.EnvironmentId) && environmentNames != null)
                {
                    environmentNames.TryGetValue(row.EnvironmentId, out environmentName);
                }

                byHash.Add(hash, new CopilotStudioCreditDaily
                {
                    UsageDate = usageDate,
                    EnvironmentId = row.EnvironmentId,
                    EnvironmentName = environmentName,
                    AgentId = row.ResourceId,
                    AgentName = row.ResourceName,
                    Harness = CopilotStudioHarnessClassifier.Classify(row.FeatureName),
                    FeatureName = row.FeatureName,
                    BilledCredits = row.Consumed,
                    NonBilledCredits = row.NonBillableQuantity,
                    DimensionHash = hash,
                    ImportedUtc = importedUtc,
                });
            }

            foreach (var entry in usersByHash)
            {
                byHash[entry.Key].DistinctUsers = entry.Value.Count;
            }

            return byHash.Values.ToList();
        }

        /// <summary>
        /// The usage day for a row.
        /// </summary>
        /// <remarks>
        /// <para>Because the importer asks for exactly one day per request, the <b>requested</b> day is the
        /// authoritative answer and is used unconditionally. The API's own <c>asOfDate</c> is deliberately
        /// not preferred over it: that field is absent from the documented response model, so relying on it
        /// would make the usage date - which is part of the upsert key - depend on an undocumented field
        /// continuing to be returned.</para>
        /// <para>It is also never clamped or rewritten. The date being a fact about the request means it is
        /// stable across runs, so a re-read of the same day updates the row in place rather than inserting a
        /// second copy under a shifted key as the trailing window advances.</para>
        /// </remarks>
        private static DateTime ResolveUsageDate(CopilotStudioCreditRow row, DateTime windowFrom, DateTime windowTo)
        {
            // windowFrom == windowTo for every production call; the parameters remain so the mapping stays
            // testable with a wider window.
            return windowFrom == windowTo ? windowFrom.Date : (row.AsOfDate?.Date ?? windowTo.Date);
        }

        private async Task SafeSaveLogAsync(AgentCostImportLog log)
        {
            try
            {
                await _store.SaveImportLogAsync(log);
            }
            catch (Exception ex)
            {
                // The log row is a diagnostic. Failing to write it must not turn a successful import into a
                // failed one, nor mask the error it was recording.
                _logger.LogWarning($"Couldn't write the Copilot Studio credit import log row: {ex.Message}");
            }
        }

        /// <summary>
        /// Takes a snapshot of the tenant's overall Copilot Credits entitlement - entitled, consumed,
        /// available and overage status.
        ///
        /// Separate from <see cref="ImportConsumptionAsync"/> because it answers a different question ("are we about to
        /// run out?" rather than "where did the spend go?") and because it is cheap: one call, one row. Never
        /// throws, for the same reason as the consumption import.
        /// </summary>
        public async Task<AgentCostImportOutcome> ImportCapacityAsync()
        {
            var log = new AgentCostImportLog
            {
                ImportName = AgentCostImportNames.CopilotStudioCapacity,
                ImportedUtc = _clock.UtcNow,
            };

            try
            {
                var snapshot = await _source.GetCapacityAsync();
                if (snapshot == null)
                {
                    // Not an error: a tenant with no Copilot Studio entitlement legitimately has nothing to
                    // report. Recorded as zero rows so it can be told apart from a failure.
                    _logger.LogInformation("Copilot Studio credit entitlement: the API returned no entitlement for this tenant.");
                }
                else
                {
                    await _store.SaveCapacitySnapshotAsync(new CopilotStudioCreditCapacity
                    {
                        SnapshotUtc = _clock.UtcNow,
                        ConsumptionAsOf = snapshot.ConsumedLastUpdatedOn,
                        Entitled = snapshot.Entitled,
                        Consumed = snapshot.Consumed,
                        ConsumptionType = snapshot.ConsumptionType,
                        Allocated = snapshot.Allocated,
                        Available = snapshot.Available,
                        PayAsYouGoConsumed = snapshot.PayAsYouGoConsumed,
                        Status = snapshot.Status,
                    });

                    log.RowsRead = 1;
                    log.RowsSaved = 1;

                    _logger.LogInformation($"Copilot Studio credit entitlement: consumed {snapshot.Consumed} of "
                        + $"{snapshot.Entitled} ({snapshot.Status}).");
                }
            }
            catch (AgentCostAuthorisationException ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, "Copilot Studio credit entitlement read failed: " + ex.Message);
                await SafeSaveLogAsync(log);
                return new AgentCostImportOutcome(log, isAuthorisationFailure: true);
            }
            catch (Exception ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, $"Copilot Studio credit entitlement read failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
            }

            await SafeSaveLogAsync(log);
            return new AgentCostImportOutcome(log);
        }

        /// <summary>
        /// Points the mapped rows at their <c>dbo.users</c> row, so the stored credits carry a real foreign
        /// key rather than an opaque identifier.
        /// </summary>
        /// <remarks>
        /// Deliberately swallows everything. Attribution is a decoration on a billing figure: if resolving
        /// it fails, the right outcome is a row that reports its spend against the raw object id and gets
        /// linked on a later cycle - not a lost import. The rows are linked <b>before</b> the upsert so a
        /// row is never written unlinked when it could have been linked.
        /// </remarks>
        private async Task LinkUsersAsync(IReadOnlyList<CopilotStudioCreditUserDaily> rows)
        {
            if (_userResolver == null || rows == null || rows.Count == 0) return;

            try
            {
                var resolution = await _userResolver.ResolveAsync(rows.Select(r => r.EntraObjectId));

                foreach (var row in rows)
                {
                    if (!string.IsNullOrWhiteSpace(row.EntraObjectId)
                        && resolution.UserIdsByObjectId.TryGetValue(row.EntraObjectId, out var userId))
                    {
                        row.UserId = userId;
                    }
                }

                if (resolution.NotInDirectory > 0)
                {
                    _logger.LogInformation(
                        $"Copilot Studio per-user credits: {resolution.NotInDirectory:N0} identifier(s) are not in the "
                        + "directory - normally people who have since been deleted. Their credits are still reported, "
                        + "against the raw identifier.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Copilot Studio per-user credits: could not attribute rows to users ({ex.Message}). "
                    + "The credits themselves are unaffected and will be linked on a later cycle.");
            }
        }

        /// <summary>
        /// Makes a bounded attempt at rows earlier runs could not attribute.
        /// </summary>
        /// <remarks>
        /// Separate from the import because it covers rows <b>outside</b> the trailing window, which the
        /// import will never re-read. Without it a row whose user was created five minutes too late would
        /// stay unattributed for ever.
        /// </remarks>
        public async Task LinkOutstandingUsersAsync()
        {
            if (_userResolver == null) return;

            try
            {
                await _userResolver.ResolveOutstandingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Copilot Studio per-user credits: could not re-attribute older rows ({ex.Message}).");
            }
        }

        /// <summary>
        /// The per-user rows that may be stored under <c>UserGroupsFilter</c>: those whose Entra object id belongs to
        /// someone in the scope.
        /// </summary>
        internal static List<CopilotStudioUserCreditRow> SelectRowsInScope(List<CopilotStudioUserCreditRow> rows, UserImportScope userScope)
        {
            if (rows == null || userScope == null || !userScope.IsFiltered)
            {
                return rows ?? new List<CopilotStudioUserCreditRow>();
            }
            return rows.Where(r => r != null && userScope.IsInScope(r.UserId)).ToList();
        }

        /// <summary>
        /// Maps per-user API rows onto entities for one day, combining any that land on the same identity.
        /// </summary>
        internal static IReadOnlyList<CopilotStudioCreditUserDaily> MapUserRows(
            IEnumerable<CopilotStudioUserCreditRow> rows,
            DateTime usageDate,
            IReadOnlyDictionary<string, string> environmentNames,
            DateTime importedUtc)
        {
            var byHash = new Dictionary<string, CopilotStudioCreditUserDaily>(StringComparer.Ordinal);

            foreach (var row in rows ?? Enumerable.Empty<CopilotStudioUserCreditRow>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.UserId)) continue;

                var hash = AgentCostRowHasher.Hash(
                    usageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.UserId, row.EnvironmentId);

                if (byHash.TryGetValue(hash, out var existing))
                {
                    existing.BilledCredits += row.Consumed;
                    continue;
                }

                string environmentName = null;
                if (!string.IsNullOrEmpty(row.EnvironmentId) && environmentNames != null)
                {
                    environmentNames.TryGetValue(row.EnvironmentId, out environmentName);
                }

                byHash.Add(hash, new CopilotStudioCreditUserDaily
                {
                    UsageDate = usageDate.Date,
                    EntraObjectId = row.UserId,
                    EnvironmentId = row.EnvironmentId,
                    EnvironmentName = environmentName,
                    BilledCredits = row.Consumed,
                    Unit = row.Unit,
                    DimensionHash = hash,
                    ImportedUtc = importedUtc,
                });
            }

            return byHash.Values.ToList();
        }
    }
}
