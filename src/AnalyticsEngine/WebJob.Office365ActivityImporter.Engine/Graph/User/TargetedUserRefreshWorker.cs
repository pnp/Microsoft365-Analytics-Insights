using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Licences;
using Common.Entities.State;
using Common.Entities.UserDiagnostics;
using Common.Entities.UserScope;
using DataUtils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Carries out administrators' one-person refreshes from the portal's User lookup page: re-reads one person's
    /// profile and licences from Microsoft Graph and writes only the difference for that person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not the scheduled import run for one user. That path ends with tenant-wide steps - renamed-type
    /// carry-forward, the global history reconcile and a <c>license_refresh_runs</c> row that tells every history
    /// reader the whole tenant was checked - none of which a one-person refresh may do. This writes the person's
    /// assignment rows and their history (as <see cref="LicenceHistorySources.UserRefresh"/>, with no earlier bound)
    /// and nothing else. It never touches the directory delta token or the import's completion stamps either.
    /// </para>
    /// <para>
    /// Runs under the <see cref="DirectoryWriterLock"/>, on the lock's own connection, so it and the scheduled import
    /// take turns: neither can write an older reading over the other's newer one. A request waits in the queue while
    /// another writer holds the lock.
    /// </para>
    /// <para>
    /// Fail-closed throughout: a person outside the import scope, or whose scope cannot be established, is not read
    /// from Graph at all; an incomplete directory answer removes nothing; and a licence that may be another SKU's
    /// renamed record is kept rather than guessed at.
    /// </para>
    /// </remarks>
    public sealed class TargetedUserRefreshWorker
    {
        /// <summary>A one-person refresh takes seconds; a claim older than this belongs to a worker that died.</summary>
        public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

        /// <summary>Requests handled per drain, so a full queue cannot hold the import cycle for long.</summary>
        public const int MaxPerDrain = 10;

        /// <summary>How long to wait for the writer lock. Short: a busy lock means "try next cycle", not "wait".</summary>
        public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

        private readonly AnalyticsLogger _logger;
        private readonly UserRefreshStore _store;
        private readonly ILiveDirectoryReader _reader;
        private readonly IUserImportScopeProvider _scope;
        private readonly Func<bool> _userImportEnabled;
        private readonly Func<Task<DirectoryWriterLock>> _tryAcquireLock;
        private readonly IOfficeLicenseNameResolver _names;

        public TargetedUserRefreshWorker(
            AnalyticsLogger logger,
            UserRefreshStore store,
            ILiveDirectoryReader reader,
            IUserImportScopeProvider scope,
            Func<bool> userImportEnabled,
            Func<Task<DirectoryWriterLock>> tryAcquireLock,
            IOfficeLicenseNameResolver names = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            _userImportEnabled = userImportEnabled ?? throw new ArgumentNullException(nameof(userImportEnabled));
            _tryAcquireLock = tryAcquireLock ?? throw new ArgumentNullException(nameof(tryAcquireLock));
            _names = names ?? new OfficeLicenseNameResolver();
        }

        /// <summary>The worker for this deployment, or null when there is no Storage account to queue requests in.</summary>
        public static TargetedUserRefreshWorker ForConfig(AppConfig config, AnalyticsLogger logger, IUserImportScopeProvider scope)
        {
            var store = UserRefreshStore.TryOpen(config, logger);
            var connectionString = config?.ConnectionStrings?.DatabaseConnectionString;
            if (store == null || string.IsNullOrWhiteSpace(connectionString)) return null;

            return new TargetedUserRefreshWorker(logger, store,
                GraphLiveDirectoryReader.ForConfig(config, logger),
                scope ?? UserImportScopeProvider.CreateForGraph(config, logger),
                () => config.ImportJobSettings?.GraphUsersMetadata == true,
                () => DirectoryWriterLock.TryAcquireAsync(connectionString, LockWait));
        }

        /// <summary>
        /// Handles up to <see cref="MaxPerDrain"/> queued requests. Returns how many finished. Never throws: a failure
        /// leaves the request to its lease, so a later drain retries it (up to <see cref="UserRefreshStore.MaxAttempts"/>).
        /// </summary>
        public async Task<int> DrainAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await PurgeIfDueAsync();

                // One Table read when idle - the common case - rather than a SQL lock every cycle.
                if (!await _store.HasQueuedAsync()) return 0;

                using (var writerLock = await _tryAcquireLock())
                {
                    if (writerLock == null)
                    {
                        _logger.LogInformation("User refresh - another writer of users and licences holds the lock; queued one-person refreshes wait for the next cycle.");
                        return 0;
                    }

                    // The scheduled import marks itself running. If its lock's connection dropped, the lock came free but a
                    // batch of its older reading may still be landing: wait until it has stopped.
                    if (await _store.IsScheduledImportActiveAsync())
                    {
                        _logger.LogInformation("User refresh - the scheduled user import is still running; queued one-person refreshes wait for the next cycle.");
                        return 0;
                    }

                    var finished = 0;
                    for (var i = 0; i < MaxPerDrain && !cancellationToken.IsCancellationRequested; i++)
                    {
                        var request = await _store.TryClaimNextAsync(Lease);
                        if (request == null) break;

                        var result = await ProcessAsync(request, writerLock, cancellationToken);
                        if (result == null) break; // Lock lost: left to its lease, retried later.

                        if (await _store.CompleteAsync(result))
                        {
                            finished++;
                            _logger.LogInformation($"User refresh - request {request.Id} finished: {result.Outcome} ({result.Added.Count} licence(s) added, {result.Removed.Count} removed, {result.HeldBack.Count} kept for lack of evidence, {result.ProfileChanges.Count} profile field(s) changed).");
                        }
                        else
                        {
                            _logger.LogWarning($"User refresh - request {request.Id} was taken over by another attempt before this one could record its result.");
                        }
                    }
                    return finished;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning($"User refresh - queued one-person refreshes skipped this cycle ({ex.GetType().Name}): {ex.Message}");
                return 0;
            }
        }

        /// <summary>How often expired refresh records are deleted (see <see cref="UserRefreshStore.PurgeExpiredAsync"/>).</summary>
        public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

        private DateTime _lastPurgeUtc = DateTime.MinValue;

        /// <summary>Once a day per process; best effort, so it never holds up a refresh.</summary>
        private async Task PurgeIfDueAsync()
        {
            var now = DateTime.UtcNow;
            if (now - _lastPurgeUtc < PurgeInterval) return;
            _lastPurgeUtc = now;
            try
            {
                var purged = await _store.PurgeExpiredAsync();
                if (purged > 0) _logger.LogInformation($"User refresh - deleted {purged.ToString("N0")} expired refresh record(s).");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogInformation($"User refresh - couldn't delete expired refresh records ({ex.GetType().Name}); retried tomorrow.");
            }
        }

        /// <summary>
        /// Carries out one claimed request. Null when the writer lock was lost part-way: the request is then left to its
        /// lease and retried, because writing after losing the lock could overwrite a newer answer.
        /// </summary>
        internal async Task<UserRefreshRequest> ProcessAsync(UserRefreshRequest request, DirectoryWriterLock writerLock, CancellationToken cancellationToken)
        {
            var result = Reset(request);
            try
            {
                if (!_userImportEnabled())
                {
                    return Finish(result, UserRefreshOutcomes.ImportDisabled);
                }

                using (var db = new AnalyticsEntitiesContext(writerLock.Connection, contextOwnsConnection: false))
                {
                    db.Configuration.ProxyCreationEnabled = false;
                    var dbUser = await db.users.FirstOrDefaultAsync(u => u.ID == request.UserId, cancellationToken);
                    if (dbUser == null)
                    {
                        return Finish(result, UserRefreshOutcomes.NotStored);
                    }

                    // Before any Graph call: someone outside the import scope is not read at all.
                    var resolution = await _scope.GetResolutionAsync();
                    var decision = UserScopeDecider.Decide(resolution, dbUser.UserPrincipalName, dbUser.AzureAdId);
                    if (decision.Decision == UserScopeDecisions.Excluded)
                    {
                        return Finish(result, UserRefreshOutcomes.ScopeExcluded);
                    }
                    if (decision.Decision == UserScopeDecisions.Unknown)
                    {
                        return Finish(result, UserRefreshOutcomes.ScopeUnknown);
                    }

                    var key = string.IsNullOrWhiteSpace(dbUser.AzureAdId) ? dbUser.UserPrincipalName : dbUser.AzureAdId;
                    var read = await _reader.ReadAsync(key, cancellationToken, GraphUserDeltaQuery.Select);
                    if (read.Observation == null)
                    {
                        return Finish(result, OutcomeForReadFailure(read.FailureReason));
                    }

                    var live = read.Observation;
                    if (!string.IsNullOrWhiteSpace(dbUser.AzureAdId) && !string.Equals(dbUser.AzureAdId.Trim(), live.ObjectId?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return Finish(result, UserRefreshOutcomes.IdentityMismatch);
                    }
                    if (!string.Equals(dbUser.UserPrincipalName?.Trim(), live.UserPrincipalName?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        AddNote(result, UserRefreshNotes.UserPrincipalNameChanged);
                    }
                    result.ObservedUtc = DateTime.SpecifyKind(live.ObservedAtUtc, DateTimeKind.Utc);

                    var licencesComplete = await RefreshLicencesAsync(db, dbUser.ID, live, writerLock, result);

                    var profileChanged = false;
                    try
                    {
                        profileChanged = await RefreshProfileAsync(db, dbUser, read, resolution, writerLock, result);
                    }
                    catch (WriterLockLostException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _logger.LogWarning($"User refresh - request {request.Id}: licences were refreshed but the profile update failed ({ex.GetType().Name}): {ex.Message}");
                        AddNote(result, UserRefreshNotes.ProfileNotUpdated);
                    }

                    var changed = result.Added.Count + result.Removed.Count > 0 || profileChanged;
                    if (changed || result.Notes.Contains(UserRefreshNotes.ReconciledAfterInterruption))
                    {
                        // A recovered attempt cannot tell whether its predecessor changed SQL, so it always tells reports.
                        await BumpGenerationAsync(result);
                    }

                    var outcome = result.HeldBack.Count > 0 || result.Notes.Contains(UserRefreshNotes.ProfileNotUpdated)
                        ? UserRefreshOutcomes.Partial
                        : changed ? UserRefreshOutcomes.Updated : UserRefreshOutcomes.Unchanged;

                    if (licencesComplete && result.HeldBack.Count == 0)
                    {
                        // Only a complete answer, fully applied, says this person's licences are right.
                        await RecordVerificationAsync(result, outcome);
                    }

                    return Finish(result, outcome);
                }
            }
            catch (WriterLockLostException)
            {
                _logger.LogWarning($"User refresh - request {request.Id}: lost the users-and-licences lock before writing; it is retried on a later cycle.");
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError($"User refresh - request {request.Id} failed ({ex.GetType().Name}): {ex.Message}");
                return Finish(result, UserRefreshOutcomes.Error);
            }
        }

        /// <summary>
        /// Brings the person's licence rows to the directory's answer. Returns whether that answer was complete enough to
        /// remove licences on. A lost lock throws.
        /// </summary>
        private async Task<bool> RefreshLicencesAsync(AnalyticsEntitiesContext db, int userId, LiveDirectoryObservation live, DirectoryWriterLock writerLock, UserRefreshRequest result)
        {
            // Graph's own part numbers first; this build's catalogue for any it did not give.
            var detailsBySku = (live.LicenceDetails ?? new List<LiveLicenceDetail>())
                .GroupBy(d => d.SkuId).ToDictionary(g => g.Key, g => g.First().SkuPartNumber);
            var livePartNumbers = new List<string>();
            var allIdentified = true;
            foreach (var skuId in live.AssignedSkuIds.Distinct())
            {
                detailsBySku.TryGetValue(skuId, out var partNumber);
                partNumber = string.IsNullOrWhiteSpace(partNumber) ? LicensingCatalogue.Embedded.FindByGuid(skuId)?.PartNumber : partNumber.Trim();
                if (string.IsNullOrWhiteSpace(partNumber))
                {
                    allIdentified = false;
                    continue;
                }
                livePartNumbers.Add(partNumber);
            }
            var complete = live.AssignedLicencesPresent && allIdentified;

            // File each live SKU exactly where the scheduled import would, creating the licence type if it is new.
            var cache = new UserMetadataCache(db).LicenseTypeCache;
            var desiredByPart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var partNumber in livePartNumbers.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var type = await UserLicenseProcessor.GetOrCreateLicenseType(cache, _names, partNumber);
                if (type.ID == 0)
                {
                    await EnsureLockAsync(writerLock);
                    await db.SaveChangesAsync();
                }
                desiredByPart[partNumber] = type.ID;
            }
            var desired = new HashSet<int>(desiredByPart.Values);

            var store = new SqlUserLicenseStore(db, _logger);
            var current = new HashSet<int>((await store.LoadAssignmentsFor(new[] { userId })).Select(a => a.LicenseTypeId));
            var currentTypes = await db.LicenseTypes.Where(t => current.Contains(t.ID)).ToListAsync();
            var plan = PlanLicenceChanges(current, currentTypes.ToDictionary(t => t.ID, t => t.SKUID), desiredByPart, complete);

            if (plan.ToAdd.Count + plan.ToRemove.Count > 0)
            {
                var probe = await store.StartRefresh(live.ObservedAtUtc);
                var refresh = new LicenseRefreshRunInfo(probe.CompletedUtc, (DateTime?)null, probe.HistoryTablesAvailable, LicenceHistorySources.UserRefresh);
                if (!refresh.HistoryTablesAvailable) AddNote(result, UserRefreshNotes.NoLicenceHistory);

                await EnsureLockAsync(writerLock);
                using (var transaction = db.Database.BeginTransaction())
                {
                    await store.AddAssignments(plan.ToAdd.Select(id => new UserLicenseAssignment(userId, id)).ToList(), refresh);
                    await store.RemoveAssignments(plan.ToRemove.Select(id => new UserLicenseAssignment(userId, id)).ToList(), refresh);
                    transaction.Commit();
                }
            }

            var names = await db.LicenseTypes.Where(t => desired.Contains(t.ID) || current.Contains(t.ID)).ToDictionaryAsync(t => t.ID, t => t.Name);
            result.Added.AddRange(plan.ToAdd.Select(id => NameOf(names, id)));
            result.Removed.AddRange(plan.ToRemove.Select(id => NameOf(names, id)));
            result.HeldBack.AddRange(plan.HeldBack.Select(id => NameOf(names, id)));
            foreach (var note in plan.Notes) AddNote(result, note);
            return complete;
        }

        /// <summary>The licence changes one complete-or-not directory answer justifies. Pure; unit tested.</summary>
        /// <param name="desiredTypeByLivePartNumber">Each SKU the directory assigns, and the licence type it is filed under now.</param>
        internal static LicenceChangePlan PlanLicenceChanges(
            ICollection<int> currentTypeIds,
            IReadOnlyDictionary<int, string> currentTypeSkuPartNumbers,
            IReadOnlyDictionary<string, int> desiredTypeByLivePartNumber,
            bool complete)
        {
            var plan = new LicenceChangePlan();
            var current = new HashSet<int>(currentTypeIds);
            var desired = new HashSet<int>(desiredTypeByLivePartNumber.Values);
            var liveParts = new HashSet<string>(desiredTypeByLivePartNumber.Keys.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);

            // Records filed under an older product name for a SKU the person still holds. They belong to the scheduled
            // import's rename carry-forward, which moves the record - and its history - to the new name for everyone at
            // once. Adding the new name here as well would leave two records for one SKU and stop that carry-forward
            // keeping this person's original start date.
            var renamedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renamed = new HashSet<int>();
            foreach (var id in current.Where(id => !desired.Contains(id)))
            {
                if (currentTypeSkuPartNumbers.TryGetValue(id, out var sku) && !string.IsNullOrWhiteSpace(sku) && liveParts.Contains(sku.Trim()))
                {
                    renamed.Add(id);
                    renamedParts.Add(sku.Trim());
                }
            }

            foreach (var id in desired.Where(id => !current.Contains(id)).OrderBy(id => id))
            {
                // Added unless every SKU behind it is one an older record already stands for.
                var parts = desiredTypeByLivePartNumber.Where(p => p.Value == id).Select(p => p.Key.Trim());
                if (parts.Any(p => !renamedParts.Contains(p)))
                {
                    plan.ToAdd.Add(id);
                }
            }

            foreach (var id in current.Where(id => !desired.Contains(id)).OrderBy(id => id))
            {
                if (!complete)
                {
                    // The answer may be missing this very licence: removing on it is how licences "vanish".
                    plan.HeldBack.Add(id);
                    if (!plan.Notes.Contains(UserRefreshNotes.IncompleteDirectoryAnswer)) plan.Notes.Add(UserRefreshNotes.IncompleteDirectoryAnswer);
                }
                else if (renamed.Contains(id))
                {
                    plan.HeldBack.Add(id);
                    if (!plan.Notes.Contains(UserRefreshNotes.AmbiguousLicenceKept)) plan.Notes.Add(UserRefreshNotes.AmbiguousLicenceKept);
                }
                else
                {
                    plan.ToRemove.Add(id);
                }
            }
            return plan;
        }

        internal sealed class LicenceChangePlan
        {
            public List<int> ToAdd { get; } = new List<int>();
            public List<int> ToRemove { get; } = new List<int>();
            public List<int> HeldBack { get; } = new List<int>();
            public List<string> Notes { get; } = new List<string>();
        }

        /// <summary>
        /// Applies the standard profile fields with the scheduled import's own mapper. Organisation attributes are left
        /// to the scheduled import, which reads them for everyone. Returns whether anything changed.
        /// </summary>
        private async Task<bool> RefreshProfileAsync(AnalyticsEntitiesContext db, User dbUser, LiveDirectoryReadResult read, UserImportScopeResolution resolution, DirectoryWriterLock writerLock, UserRefreshRequest result)
        {
            if (read.UserJson == null) return false;

            var graphUser = read.UserJson.ToObject<GraphUser>();
            var managerId = (read.UserJson["manager"] as JObject)?["id"]?.ToString();
            graphUser.ManagerInfo = string.IsNullOrWhiteSpace(managerId)
                ? new List<ManagerInfo>()
                : new List<ManagerInfo> { new ManagerInfo { Id = managerId } };

            var before = ProfileSnapshot.Of(dbUser);
            var cache = new UserMetadataCache(db);
            var mapper = new UserDataMapper(_logger, cache)
            {
                UserScope = resolution.Status == UserImportScopeStatus.Unfiltered
                    ? UserImportScope.Unfiltered
                    : UserImportScope.ForMembers(resolution.Members, "user refresh"),
            };

            // The manager must already be stored: a one-person refresh never adds people.
            var managers = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(managerId))
            {
                var manager = await db.users.FirstOrDefaultAsync(u => u.AzureAdId == managerId);
                if (manager != null) managers[managerId] = manager;
            }

            var lastUpdated = dbUser.LastUpdated;
            await mapper.UpdateUserMetadata(db, graphUser, new List<GraphUser> { graphUser }, dbUser, managers, null, DateTime.UtcNow);
            var changes = ProfileSnapshot.Of(dbUser).ChangesFrom(before);
            if (changes.Count == 0)
            {
                // "Metadata last written" means a change was written; a check that found none does not move it.
                dbUser.LastUpdated = lastUpdated;
            }

            await EnsureLockAsync(writerLock);
            await db.SaveChangesAsync();
            result.ProfileChanges.AddRange(changes);
            return changes.Count > 0;
        }

        /// <summary>The profile fields a refresh can change, compared by value or by lookup id.</summary>
        internal sealed class ProfileSnapshot
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

            public static ProfileSnapshot Of(User user)
            {
                var s = new ProfileSnapshot();
                s._values[UserRefreshProfileFields.AccountEnabled] = user.AccountEnabled?.ToString();
                s._values[UserRefreshProfileFields.Mail] = Normalise(user.Mail);
                s._values[UserRefreshProfileFields.PostalCode] = Normalise(user.PostalCode);
                s._values[UserRefreshProfileFields.ObjectId] = Normalise(user.AzureAdId)?.ToLowerInvariant();
                s._values[UserRefreshProfileFields.Department] = Lookup(user.DepartmentId, user.Department);
                s._values[UserRefreshProfileFields.JobTitle] = Lookup(user.JobTitleId, user.JobTitle);
                s._values[UserRefreshProfileFields.OfficeLocation] = Lookup(user.OfficeLocationId, user.OfficeLocation);
                s._values[UserRefreshProfileFields.UsageLocation] = Lookup(user.UsageLocationId, user.UsageLocation);
                s._values[UserRefreshProfileFields.Country] = Lookup(user.UserCountryId, user.UserCountry);
                s._values[UserRefreshProfileFields.StateOrProvince] = Lookup(user.StateOrProvinceId, user.StateOrProvince);
                s._values[UserRefreshProfileFields.CompanyName] = Lookup(user.CompanyNameId, user.CompanyName);
                s._values[UserRefreshProfileFields.Manager] = Lookup(user.ManagerId, user.Manager);
                return s;
            }

            public List<string> ChangesFrom(ProfileSnapshot before)
                => _values.Where(v => !string.Equals(v.Value, before._values[v.Key], StringComparison.Ordinal)).Select(v => v.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

            private static string Normalise(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            // A navigation set by the mapper wins over the foreign key it has not yet updated; a new lookup row has no id.
            private static string Lookup(int? foreignKey, AbstractEFEntity navigation)
                => navigation == null ? foreignKey?.ToString() : navigation.ID == 0 ? "new" : navigation.ID.ToString();
        }

        private async Task BumpGenerationAsync(UserRefreshRequest result)
        {
            try
            {
                await _store.BumpLicenceDataGenerationAsync();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning($"User refresh - request {result.Id}: data changed but the report-cache generation could not be advanced ({ex.GetType().Name}); reports catch up when their caches expire.");
                AddNote(result, UserRefreshNotes.ReportCachesNotRefreshed);
            }
        }

        private async Task RecordVerificationAsync(UserRefreshRequest result, string outcome)
        {
            try
            {
                await _store.RecordVerificationAsync(result.UserId, new UserVerificationRecord
                {
                    VerifiedUtc = result.ObservedUtc ?? DateTime.UtcNow,
                    Outcome = outcome,
                    RequestId = result.Id,
                });
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // The licences are right either way; only the "last verified" fact is lost.
                _logger.LogWarning($"User refresh - request {result.Id}: couldn't record the verification ({ex.GetType().Name}).");
            }
        }

        private static async Task EnsureLockAsync(DirectoryWriterLock writerLock)
        {
            if (!await writerLock.IsStillHeldAsync()) throw new WriterLockLostException();
        }

        private sealed class WriterLockLostException : Exception { }

        internal static string OutcomeForReadFailure(string reason)
        {
            switch (reason)
            {
                case LiveComparisonReasons.UserNotFoundInDirectory: return UserRefreshOutcomes.UserNotFoundInDirectory;
                case LiveComparisonReasons.PermissionDenied: return UserRefreshOutcomes.PermissionDenied;
                default: return UserRefreshOutcomes.GraphUnavailable;
            }
        }

        /// <summary>A fresh result for this attempt: lists from an interrupted attempt are not this attempt's work.</summary>
        private static UserRefreshRequest Reset(UserRefreshRequest request)
        {
            request.Added = new List<string>();
            request.Removed = new List<string>();
            request.HeldBack = new List<string>();
            request.ProfileChanges = new List<string>();
            request.Notes = request.Notes?.Where(n => n == UserRefreshNotes.ReconciledAfterInterruption).ToList() ?? new List<string>();
            request.Outcome = null;
            request.ObservedUtc = null;
            return request;
        }

        /// <summary>Records the outcome: a failure when something went wrong, otherwise completed (refusals included).</summary>
        private static UserRefreshRequest Finish(UserRefreshRequest result, string outcome)
        {
            result.State = UserRefreshOutcomes.IsFailure(outcome) ? UserRefreshStates.Failed : UserRefreshStates.Completed;
            result.Outcome = outcome;
            return result;
        }

        private static void AddNote(UserRefreshRequest result, string note)
        {
            if (!result.Notes.Contains(note)) result.Notes.Add(note);
        }

        private static string NameOf(IReadOnlyDictionary<int, string> names, int id)
            => names.TryGetValue(id, out var name) ? name : "#" + id;
    }
}
