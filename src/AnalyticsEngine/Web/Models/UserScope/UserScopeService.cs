using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserScope;
using Common.Entities.UserScope.Purge;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace Web.AnalyticsWeb.Models.UserScope
{
    /// <summary>A request the service refuses, with the HTTP status and stable code the controller answers with.</summary>
    public sealed class UserScopeRequestException : Exception
    {
        public UserScopeRequestException(HttpStatusCode status, string code) : base(code)
        {
            Status = status;
            Code = code;
        }

        public HttpStatusCode Status { get; }
        public string Code { get; }
    }

    /// <summary>Starts purges in the background.</summary>
    internal interface IUserScopePurgeRunner
    {
        /// <summary>Whether this process is running purge <paramref name="jobId"/>.</summary>
        bool IsRunning(int jobId);

        /// <param name="preResolved">The fresh resolution the purge was confirmed against, if still in hand.</param>
        /// <param name="lockedSession">
        /// The session holding the purge lock when the caller took it, which the run takes over. Without one, the run takes
        /// the lock itself and does nothing while another purge holds it.
        /// </param>
        void Start(int jobId, UserImportScopeResolution preResolved, UserScopePurgeSession lockedSession);
    }

    /// <summary>
    /// Runs purges on ASP.NET's background work queue, so a purge outlives the request that started it and is told when
    /// the host shuts down.
    /// </summary>
    internal sealed class UserScopePurgeRunner : IUserScopePurgeRunner
    {
        private readonly ConcurrentDictionary<int, byte> _running = new ConcurrentDictionary<int, byte>();
        private readonly Func<UserScopePurgeDatabase> _database;
        private readonly Func<UserScopePurgeStateStore> _state;
        private readonly Func<IUserImportScopeProvider> _provider;
        private readonly ILogger _logger;

        internal UserScopePurgeRunner(Func<UserScopePurgeDatabase> database, Func<UserScopePurgeStateStore> state,
            Func<IUserImportScopeProvider> provider, ILogger logger)
        {
            _database = database;
            _state = state;
            _provider = provider;
            _logger = logger;
        }

        public static UserScopePurgeRunner Instance { get; } = new UserScopePurgeRunner(
            () => UserScopeService.ProductionDatabase.Value,
            () => UserScopeService.ProductionState.Value,
            () => UserScopeService.ProductionProvider.Value,
            UserScopeService.ProductionLogger.Value);

        public bool IsRunning(int jobId) => _running.ContainsKey(jobId);

        public void Start(int jobId, UserImportScopeResolution preResolved, UserScopePurgeSession lockedSession)
        {
            if (!_running.TryAdd(jobId, 0))
            {
                lockedSession?.Dispose();
                return;
            }

            Func<CancellationToken, Task> work = async stopToken =>
            {
                try
                {
                    var state = _state();
                    var engine = new UserScopePurgeEngine(_database(), state, _logger);
                    var outcome = await engine.RunAsync(jobId, _provider(), preResolved, stopToken, lockedSession).ConfigureAwait(false);
                    if (outcome != UserScopePurgeRunOutcome.NotClaimed)
                    {
                        UserScopePurgeTelemetry.Track(_logger, outcome == UserScopePurgeRunOutcome.Paused ? "paused" : "ended",
                            engine.Job, outcome.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"User scope purge {jobId}: the background run stopped unexpectedly ({ex.GetType().Name}).");
                }
                finally
                {
                    _running.TryRemove(jobId, out _);
                }
            };

            try
            {
                if (HostingEnvironment.IsHosted)
                {
                    HostingEnvironment.QueueBackgroundWorkItem(work);
                }
                else
                {
                    Task.Run(() => work(CancellationToken.None));
                }
            }
            catch
            {
                _running.TryRemove(jobId, out _);
                lockedSession?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Starts again a purge that a previous run of the web app left unfinished - which only a durable store of purge
        /// records can remember. Never blocks or fails start-up: if the store can't be read yet, the next status request
        /// picks the purge up instead.
        /// </summary>
        public void ResumeInterrupted()
        {
            Task.Run(async () =>
            {
                try
                {
                    var state = _state();
                    if (!state.IsDurable)
                    {
                        return;
                    }

                    var latest = await state.GetLatestAsync().ConfigureAwait(false);
                    if (latest != null && latest.IsActive)
                    {
                        _logger.LogInformation($"User scope purge {latest.Id}: starting again after the web app restarted.");
                        Start(latest.Id, null, null);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"User scope purge: could not check for an unfinished purge at start-up ({ex.GetType().Name}).");
                }
            });
        }
    }

    /// <summary>
    /// A <c>UserScopePurge</c> Application Insights event at each milestone of a purge: an audit trail of who removed
    /// what, and when, that outlives the purge's record - including in a deployment that keeps records in memory. It names
    /// the administrator who started the purge, never anyone the purge removes.
    /// </summary>
    internal static class UserScopePurgeTelemetry
    {
        internal static void Track(ILogger logger, string stage, UserScopePurgeJob job, string outcome)
        {
            if (!(logger is AnalyticsLogger analytics) || job == null)
            {
                return;
            }

            var context = new Dictionary<string, string>
            {
                ["Stage"] = stage,
                ["JobId"] = job.Id.ToString(CultureInfo.InvariantCulture),
                ["State"] = job.State ?? string.Empty,
                ["Phase"] = job.Phase ?? string.Empty,
                ["Outcome"] = outcome ?? string.Empty,
                ["ErrorCode"] = job.ErrorCode ?? string.Empty,
                ["RequestedBy"] = job.RequestedBy ?? string.Empty,
                ["FilterFingerprint"] = job.FilterFingerprint ?? string.Empty,
            };
            var metrics = new Dictionary<string, double>
            {
                ["CandidateCount"] = job.CandidateCount,
                ["UsersDeleted"] = job.UsersDeleted,
                ["UsersSkipped"] = job.UsersSkipped,
                ["RowsAffected"] = job.RowsAffected?.Values.Sum() ?? 0,
                ["ScopeMemberCount"] = job.ScopeMemberCount ?? 0,
            };

            try
            {
                analytics.TrackEvent(AnalyticsLogger.AnalyticsEvent.UserScopePurge, context, metrics);
            }
            catch (Exception ex)
            {
                // An audit event that can't be sent must never stop or fail a purge.
                logger.LogWarning($"User scope purge {job.Id}: could not send its {stage} event ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>
    /// What the portal's Administration &gt; User scope page shows and does: the <c>UserGroupsFilter</c> scope, how it
    /// resolved, and purging everything stored about people outside it.
    /// </summary>
    internal sealed class UserScopeService
    {
        private const string LogContext = "UserScope";

        internal static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, LogContext));

        /// <summary>
        /// The web app's own scope provider, process-lifetime like the web jobs' - so the page shows a resolution at most
        /// an hour old without reading the groups on every request, and Refresh forces a new one.
        /// </summary>
        internal static readonly Lazy<IUserImportScopeProvider> ProductionProvider = new Lazy<IUserImportScopeProvider>(
            () => UserImportScopeProvider.CreateForGraph(new AppConfig(), ProductionLogger.Value));

        /// <summary>The analytics database, for the purge's work on it. The purge keeps nothing of its own there.</summary>
        internal static readonly Lazy<UserScopePurgeDatabase> ProductionDatabase = new Lazy<UserScopePurgeDatabase>(
            () => new UserScopePurgeDatabase(new AppConfig().ConnectionStrings.DatabaseConnectionString));

        /// <summary>
        /// Where purge records are kept: the solution's runtime state store - Azure Table storage when it is configured, so
        /// that a purge starts again by itself after a restart and every instance shows its progress - otherwise the web
        /// app's memory.
        /// </summary>
        internal static readonly Lazy<UserScopePurgeStateStore> ProductionState = new Lazy<UserScopePurgeStateStore>(() =>
        {
            var durable = StateStore.TryOpen(new AppConfig(), StatePartitions.UserScopePurge, ProductionLogger.Value);
            return durable != null
                ? new UserScopePurgeStateStore(durable, isDurable: true)
                : new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false);
        });

        private readonly IUserImportScopeProvider _provider;
        private readonly UserScopePurgeDatabase _database;
        private readonly UserScopePurgeStateStore _state;
        private readonly IUserScopePurgeRunner _runner;
        private readonly ILogger _logger;

        internal UserScopeService(IUserImportScopeProvider provider, UserScopePurgeDatabase database, UserScopePurgeStateStore state,
            IUserScopePurgeRunner runner, ILogger logger)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _logger = logger;
        }

        internal static UserScopeService ForThisDeployment()
            => new UserScopeService(ProductionProvider.Value, ProductionDatabase.Value, ProductionState.Value,
                UserScopePurgeRunner.Instance, ProductionLogger.Value);

        internal async Task<UserScopeStatusModel> GetStatusAsync()
            => await BuildStatusAsync(await _provider.GetResolutionAsync().ConfigureAwait(false)).ConfigureAwait(false);

        /// <summary>Reads the groups from Microsoft Graph again now, rather than waiting for the hourly refresh.</summary>
        internal async Task<UserScopeStatusModel> RefreshAsync()
            => await BuildStatusAsync(await _provider.RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);

        /// <summary>
        /// Starts a purge. Only ever against a resolution read from Graph moments ago, complete and with at least one
        /// member: a cached, partial or fail-open scope could make people who are in scope look outside it.
        /// </summary>
        internal async Task<UserScopePurgeJobModel> StartPurgeAsync(bool acknowledged, string requestedBy)
        {
            if (!acknowledged)
            {
                throw new UserScopeRequestException(HttpStatusCode.BadRequest, UserScopeErrorCodes.AcknowledgementRequired);
            }
            if (!_provider.Filter.IsNarrowing)
            {
                throw new UserScopeRequestException(HttpStatusCode.Conflict, UserScopeErrorCodes.ScopeNotFiltered);
            }

            var latest = await State(() => _state.GetLatestAsync()).ConfigureAwait(false);
            if (latest != null && latest.IsActive)
            {
                throw new UserScopeRequestException(HttpStatusCode.Conflict, UserScopeErrorCodes.PurgeAlreadyRunning);
            }

            // The purge lock: one purge at a time, whichever web app instance the requests land on. It is taken before
            // anything else, so two administrators starting a purge at once get one purge between them.
            var session = await Database(() => _database.TryOpenPurgeSessionAsync(TimeSpan.Zero)).ConfigureAwait(false);
            if (session == null)
            {
                throw new UserScopeRequestException(HttpStatusCode.Conflict, UserScopeErrorCodes.PurgeAlreadyRunning);
            }

            try
            {
                var resolution = await _provider.RefreshAsync().ConfigureAwait(false);
                if (resolution.Status != UserImportScopeStatus.Resolved)
                {
                    throw new UserScopeRequestException(HttpStatusCode.ServiceUnavailable, UserScopeErrorCodes.ScopeUnavailable);
                }
                if (resolution.Members == null || resolution.Members.Count == 0)
                {
                    throw new UserScopeRequestException(HttpStatusCode.Conflict, UserScopeErrorCodes.ScopeEmpty);
                }

                var scope = UserImportScope.ForMembers(resolution.Members, "purge");
                var counts = await Database(() => _database.CountUsersAsync(scope)).ConfigureAwait(false);
                if (counts.OutsideScope == 0)
                {
                    throw new UserScopeRequestException(HttpStatusCode.Conflict, UserScopeErrorCodes.NothingToPurge);
                }

                var job = await State(() => _state.CreateAsync(requestedBy, _provider.Filter.Fingerprint, resolution.Members.Count)).ConfigureAwait(false);
                _logger?.LogWarning($"User scope purge {job.Id}: started from the web portal (Administration > User scope). " +
                    $"About {counts.OutsideScope:N0} of {counts.Total:N0} user(s) are outside the scope of {resolution.Members.Count:N0} member(s). " +
                    $"Its progress is kept in {_state.Description}.");
                UserScopePurgeTelemetry.Track(_logger, "started", job, null);

                _runner.Start(job.Id, resolution, session);
                session = null;
                return ToModel(job);
            }
            finally
            {
                // Refused: let go of the lock. Started: the run owns the session now.
                session?.Dispose();
            }
        }

        /// <summary>
        /// A purge's progress. Also the purge's safety net: if it is active but nothing is running it - the web app
        /// instance that was has gone - this starts it again.
        /// </summary>
        internal async Task<UserScopePurgeJobModel> GetPurgeAsync(int id)
        {
            var job = await State(() => _state.GetAsync(id)).ConfigureAwait(false);
            if (job == null)
            {
                throw new UserScopeRequestException(HttpStatusCode.NotFound, UserScopeErrorCodes.JobNotFound);
            }

            await ResumeIfAbandonedAsync(job, null).ConfigureAwait(false);
            return ToModel(job);
        }

        /// <summary>Asks a purge to stop after the batch it is on. What it has already removed stays removed.</summary>
        internal async Task<UserScopePurgeJobModel> CancelPurgeAsync(int id)
        {
            if (!await State(() => _state.RequestCancelAsync(id)).ConfigureAwait(false))
            {
                var existing = await State(() => _state.GetAsync(id)).ConfigureAwait(false);
                throw new UserScopeRequestException(
                    existing == null ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
                    existing == null ? UserScopeErrorCodes.JobNotFound : UserScopeErrorCodes.JobNotActive);
            }

            _logger?.LogWarning($"User scope purge {id}: stop requested from the web portal.");
            var job = await State(() => _state.GetAsync(id)).ConfigureAwait(false);
            await ResumeIfAbandonedAsync(job, null).ConfigureAwait(false);
            return ToModel(job);
        }

        /// <summary>
        /// Starts again a purge whose record says it is active while no session holds the purge lock - the instance that
        /// ran it has gone. The run takes the lock itself, so two instances doing this at once still get one run.
        /// </summary>
        private async Task ResumeIfAbandonedAsync(UserScopePurgeJob job, bool? purgeRunning)
        {
            if (job == null || !job.IsActive || _runner.IsRunning(job.Id))
            {
                return;
            }

            var running = purgeRunning ?? await Database(() => _database.IsPurgeRunningAsync()).ConfigureAwait(false);
            if (!running)
            {
                _runner.Start(job.Id, null, null);
            }
        }

        private async Task<UserScopeStatusModel> BuildStatusAsync(UserImportScopeResolution resolution)
        {
            var status = new UserScopeStatusModel
            {
                Filtered = _provider.Filter.IsNarrowing,
                FilterPatterns = _provider.Filter.Patterns.ToList(),
                Resolution = ToModel(resolution),
                PurgeStateDurable = _state.IsDurable,
            };

            // Where purge records are kept can be out of reach - Azure Table storage behind a private endpoint, a rotated
            // key - while the scope and the database are fine. The page still shows those; only purging waits for it.
            UserScopePurgeJob latest = null;
            var stateUnavailable = false;
            try
            {
                latest = await _state.GetLatestAsync().ConfigureAwait(false);
            }
            catch (UserScopePurgeStateUnavailableException ex)
            {
                LogStateUnavailable(ex);
                stateUnavailable = true;
            }
            status.LatestJob = latest == null ? null : ToModel(latest);

            // True for a purge on any instance - including one this instance has no record of, when records are in memory.
            var purgeRunning = await Database(() => _database.IsPurgeRunningAsync()).ConfigureAwait(false);
            if (latest != null)
            {
                await ResumeIfAbandonedAsync(latest, purgeRunning).ConfigureAwait(false);
            }

            if (resolution.Status == UserImportScopeStatus.Resolved)
            {
                var scope = UserImportScope.ForMembers(resolution.Members, "status");
                var counts = await Database(() => _database.CountUsersAsync(scope)).ConfigureAwait(false);
                status.Database = new UserScopeDatabaseCountsModel
                {
                    TotalUsers = counts.Total,
                    InScopeUsers = counts.Total - counts.OutsideScope,
                    OutOfScopeUsers = counts.OutsideScope,
                };
            }

            status.PurgeUnavailableReason =
                !status.Filtered ? UserScopePurgeUnavailableReasons.NotFiltered
                : purgeRunning || (latest != null && latest.IsActive) ? UserScopePurgeUnavailableReasons.JobActive
                : stateUnavailable ? UserScopePurgeUnavailableReasons.StorageUnavailable
                : resolution.Status != UserImportScopeStatus.Resolved ? UserScopePurgeUnavailableReasons.ScopeUnavailable
                : resolution.Members == null || resolution.Members.Count == 0 ? UserScopePurgeUnavailableReasons.ScopeEmpty
                : status.Database == null || status.Database.OutOfScopeUsers == 0 ? UserScopePurgeUnavailableReasons.NothingToPurge
                : null;

            return status;
        }

        internal static UserScopeResolutionModel ToModel(UserImportScopeResolution resolution)
        {
            var model = new UserScopeResolutionModel
            {
                Status = resolution.Status == UserImportScopeStatus.Resolved ? "resolved"
                    : resolution.Status == UserImportScopeStatus.Unavailable ? "unavailable"
                    : "unfiltered",
                ResolvedUtc = resolution.Status == UserImportScopeStatus.Unfiltered ? (DateTime?)null : DateTime.SpecifyKind(resolution.ResolvedUtc, DateTimeKind.Utc),
                MemberCount = resolution.Members?.Count ?? 0,
                MatchedNoGroup = resolution.MatchedNoGroup,
                Groups = resolution.Groups.Select(g => new UserScopeGroupModel
                {
                    Id = g.Id,
                    DisplayName = g.DisplayName,
                    UserMemberCount = g.UserMemberCount,
                    MatchedPatterns = g.MatchedPatterns.ToList(),
                }).ToList(),
                UnmatchedPatterns = resolution.UnmatchedPatterns.ToList(),
            };

            if (resolution.Status == UserImportScopeStatus.Unavailable)
            {
                model.FailureKind = FailureKindKey(resolution.FailureKind ?? UserImportScopeFailureKind.Unexpected);
                model.HttpStatus = resolution.FailureHttpStatus;
            }
            return model;
        }

        internal static string FailureKindKey(UserImportScopeFailureKind kind)
        {
            switch (kind)
            {
                case UserImportScopeFailureKind.DirectoryRead: return "directoryRead";
                case UserImportScopeFailureKind.BudgetExhausted: return "budgetExhausted";
                case UserImportScopeFailureKind.ClientUnavailable: return "clientUnavailable";
                default: return "unexpected";
            }
        }

        internal static UserScopePurgeJobModel ToModel(UserScopePurgeJob job) => new UserScopePurgeJobModel
        {
            Id = job.Id,
            State = job.State,
            Phase = job.Phase,
            StepIndex = job.StepIndex,
            StepCount = job.StepCount,
            CandidateCount = job.CandidateCount,
            UsersDeleted = job.UsersDeleted,
            UsersSkipped = job.UsersSkipped,
            RowsAffected = job.RowsAffected
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new UserScopePurgeTableCountModel { Table = pair.Key, Rows = pair.Value })
                .ToList(),
            CancelRequested = job.CancelRequested,
            RequestedBy = job.RequestedBy,
            CreatedUtc = job.CreatedUtc,
            StartedUtc = job.StartedUtc,
            UpdatedUtc = job.UpdatedUtc,
            CompletedUtc = job.CompletedUtc,
            ErrorCode = job.ErrorCode,
        };

        /// <summary>Runs a database call, turning a database that can't be reached into the API's stable code.</summary>
        private async Task<T> Database<T>(Func<Task<T>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                _logger?.LogError(ex, $"User scope - database call failed (SQL error {ex.Number}).");
                throw new UserScopeRequestException(HttpStatusCode.ServiceUnavailable, UserScopeErrorCodes.DatabaseUnavailable);
            }
        }

        /// <summary>Runs a call on the purge records' store, turning a store that can't be reached into the API's stable code.</summary>
        private async Task<T> State<T>(Func<Task<T>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (UserScopePurgeStateUnavailableException ex)
            {
                LogStateUnavailable(ex);
                throw new UserScopeRequestException(HttpStatusCode.ServiceUnavailable, UserScopeErrorCodes.StorageUnavailable);
            }
        }

        private void LogStateUnavailable(UserScopePurgeStateUnavailableException ex)
            => _logger?.LogError(ex, $"User scope - couldn't reach where purge records are kept ({_state.Description}), so purges can't " +
                $"be started, followed or stopped until it can be reached. {ex.Message}");
    }
}
