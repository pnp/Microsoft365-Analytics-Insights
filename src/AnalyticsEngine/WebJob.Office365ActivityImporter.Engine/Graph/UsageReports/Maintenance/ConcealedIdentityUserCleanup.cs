using Common.Entities.Config;
using Common.Entities.UserDiagnostics;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Maintenance
{
    /// <summary>How a call to <see cref="ConcealedIdentityUserCleanup.RunIfDueAsync"/> ended.</summary>
    public enum ConcealedIdentityUserCleanupOutcome
    {
        /// <summary>Turned off by the <c>CleanUpConcealedIdentityUsers</c> app setting, or no database configured.</summary>
        Disabled,

        /// <summary>The last look found nothing to delete; the next is not due yet.</summary>
        NotDue,

        /// <summary>The usage-report import is running in the background, so the cleanup waits for it.</summary>
        WaitingForUsageReports,

        /// <summary>No user matches the rule.</summary>
        NothingToDo,

        /// <summary>Another writer of users and licences holds the users-and-licences lock.</summary>
        LockBusy,

        /// <summary>Candidates were examined, in one or more batches. See the counts for what happened.</summary>
        Ran,

        /// <summary>Failed before any batch ran - the first look, the catalog or the lock. Nothing was changed.</summary>
        Failed,
    }

    /// <summary>What one run did.</summary>
    public sealed class ConcealedIdentityUserCleanupResult
    {
        internal ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome outcome)
        {
            Outcome = outcome;
        }

        public ConcealedIdentityUserCleanupOutcome Outcome { get; }

        /// <summary>Users matching the rule when the run started.</summary>
        public int Candidates { get; internal set; }

        /// <summary>
        /// Candidates kept before any batch, because the run's one look at the tables with no index on their user column found
        /// a reference to them.
        /// </summary>
        public int KeptByRunSnapshot { get; internal set; }

        /// <summary>Candidates the run's batches examined. The rest are left for the next run.</summary>
        public int Examined { get; internal set; }

        public int UsersDeleted { get; internal set; }

        /// <summary>Kept: something other than a usage-report table refers to them, or their row changed.</summary>
        public int UsersKept => KeptByRunSnapshot + Examined - UsersDeleted;

        public int Remaining => Candidates - KeptByRunSnapshot - Examined;

        public int Batches { get; internal set; }

        /// <summary>Usage-report rows deleted with the users, by table.</summary>
        public IDictionary<string, long> RowsDeletedByTable { get; } = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public long ActivityRowsDeleted => RowsDeletedByTable.Values.Sum();

        /// <summary>Why the run stopped before examining every candidate, when it was not the time budget.</summary>
        public Exception Error { get; internal set; }

        public bool LostLock { get; internal set; }

        public TimeSpan Elapsed { get; internal set; }

        /// <summary>How long each batch took, in order. For diagnostics and the benchmark.</summary>
        internal List<TimeSpan> BatchDurations { get; } = new List<TimeSpan>();

        /// <summary>How many users each batch took, in order.</summary>
        internal List<int> BatchSizes { get; } = new List<int>();

        /// <summary>The step that failed, when <see cref="Error"/> is set: <c>batch</c>, or a step of the preflight.</summary>
        public string FailedStep { get; internal set; }

        /// <summary>Users in the batch that failed, and how long it ran before it did.</summary>
        internal int FailedBatchSize { get; set; }
        internal TimeSpan FailedBatchElapsed { get; set; }

        /// <summary>The SQL Server error number behind <see cref="Error"/>, when it is a SQL error (-2 is a command timeout).</summary>
        public int? SqlErrorNumber => FindSqlException(Error)?.Number;

        /// <summary>
        /// Before the first batch: finding the tables that refer to users, the candidates, and the one read of each table with
        /// no index on its user column.
        /// </summary>
        public TimeSpan PreflightElapsed { get; internal set; }

        /// <summary>The part of <see cref="PreflightElapsed"/> spent on those reads.</summary>
        public TimeSpan ReferenceReadElapsed { get; internal set; }

        /// <summary>How long the read of each table took, in the order they ran.</summary>
        internal List<KeyValuePair<string, TimeSpan>> ReferenceReads { get; } = new List<KeyValuePair<string, TimeSpan>>();

        /// <summary>
        /// The reads ran out of <see cref="ConcealedIdentityUserCleanup.DefaultPreflightBudget"/> before every table was read, so
        /// no batch ran: nothing was changed, and the next attempt waits <see cref="ConcealedIdentityUserCleanup.FailureRetryInterval"/>.
        /// </summary>
        public bool PreflightExceeded { get; internal set; }

        /// <summary>The users the next batch will take, after this run's batches.</summary>
        public int NextBatchSize { get; internal set; }

        /// <summary>The most users a batch may take for now: below the maximum once a size-dependent failure has set a limit.</summary>
        public int BatchSizeLimit { get; internal set; }

        /// <summary>Seconds every batch costs however few users it takes, when that is above what a batch is planned to take.</summary>
        public double? FixedCostSeconds { get; internal set; }

        internal static SqlException FindSqlException(Exception ex)
        {
            for (; ex != null; ex = ex.InnerException)
            {
                if (ex is SqlException sql) return sql;
            }
            return null;
        }
    }

    /// <summary>
    /// Deletes the placeholder users that older builds created from concealed (hashed) usage-report identities, with their
    /// usage-report rows, a bounded slice per import cycle. See <see cref="ConcealedIdentityPlaceholderRule"/> for what a
    /// placeholder is, and the wiki's Office 365 Advanced Analytics Engine prerequisites for the tenant setting behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is deleted.</b> A user is deleted only when all of these hold, re-checked on the row under its locks:
    /// its <c>user_name</c> is exactly the stored form of a concealed identity (32 lower-case hexadecimal characters, so
    /// no <c>@</c>); its <c>azure_ad_id</c> and <c>mail</c> are empty, so the user import has never matched it; and nothing
    /// but the user-keyed usage-report tables (<see cref="ConcealedIdentityUserCleanupSql.UsageReportActivityTables"/>)
    /// refers to it. "Refers" means every foreign key to <c>dbo.users</c>, read from the catalog on each run, plus the
    /// user-id columns that have none (<see cref="ConcealedIdentityUserCleanupSql.KnownUserIdColumnsWithoutForeignKey"/>).
    /// An audit event, a Copilot row, a licence, a Teams record, an organisation value, being someone's manager - any of
    /// them keeps the user, and its usage-report rows with it.
    /// </para>
    /// <para>
    /// <b>How.</b> In batches, each one transaction: a batch deletes its users and all of their usage-report rows, or nothing
    /// (<see cref="ConcealedIdentityUserCleanupSql.BuildBatchSql"/>). The batch size is chosen from what batches cost
    /// (<see cref="ConcealedIdentityCleanupBatchSizer"/>): it starts at <see cref="DefaultBatchSize"/>, never takes more users
    /// than recent batches show should finish well inside <see cref="MaxBatchDuration"/>, prefers large batches within that
    /// while a batch's fixed cost dominates, settles on a smaller size - one that keeps a batch under
    /// <see cref="TargetBatchDuration"/> - when that costs little throughput, and halves the next batch after any batch that
    /// fails. A batch that timed out (<see cref="BatchCommandTimeoutSeconds"/>), or ran past the cap on new ground, also caps
    /// later batches at half its size, a cap that doubles back once a day; a deadlock, a lock timeout, a late reference or a
    /// batch past the cap at a size close to one that recently fitted - the database being slow, unless it happens three
    /// times in a row - does not. A
    /// reference from a table with no index on its user column is looked for once per run, not in every batch
    /// (<see cref="ConcealedIdentityUserCleanupSql.ReferencesCheckedOncePerRun"/>), one timed read per table and within
    /// <see cref="DefaultPreflightBudget"/>: a run whose reads do not finish in it changes nothing and waits
    /// <see cref="FailureRetryInterval"/>, and what the reads found is never kept for a later run.
    /// Every run starts again from the rule, so an interrupted run resumes by itself and a finished one is a no-op. A run
    /// stops starting batches after <see cref="DefaultTimeBudget"/>, so a large backlog is spread over several cycles; when a
    /// run finds nothing to delete it does not look again for <see cref="RecheckInterval"/>, so an idle database costs one
    /// scan of <c>IX_users</c> a day.
    /// </para>
    /// <para>
    /// <b>Concurrency.</b> Runs on the import cycle's own thread, after the user import, the one-person refreshes and the
    /// audit import of that cycle have finished. Two things in the process can still write meanwhile: the usage-report
    /// import, which runs in the background and is the only writer of the tables the cleanup deletes from - the caller
    /// passes whether it is running, and the cleanup waits for it - and the Teams call-notification listener, which only
    /// resolves real sign-in names (they contain an <c>@</c>), never a placeholder. The batches run under the
    /// users-and-licences <see cref="DirectoryWriterLock"/>, on its own connection, taken without waiting: on any instance,
    /// they can never overlap the user import or a one-person refresh, and a busy lock means "next cycle". Anything else that
    /// might add a reference while a batch runs - another instance, the web portal - is held off by the batch's own locks,
    /// except in the tables read once per run, whose writers cannot reach a placeholder
    /// (<see cref="ConcealedIdentityUserCleanupSql.ReferencesCheckedOncePerRun"/>).
    /// </para>
    /// <para>
    /// <b>Kill switch.</b> The <c>CleanUpConcealedIdentityUsers</c> app setting (<see cref="AppConfig.CleanUpConcealedIdentityUsers"/>),
    /// on unless set to <c>false</c>; and also off, with an Error naming the value, when it is present but is neither <c>true</c> nor
    /// <c>false</c> (<c>0</c>, <c>no</c> and <c>off</c> are such values): an irreversible delete never stays on for a value that
    /// was meant to turn it off. Never throws, and writes at most one log line a run.
    /// </para>
    /// </remarks>
    public sealed class ConcealedIdentityUserCleanup
    {
        /// <summary>The app setting that turns the cleanup off when set to <c>false</c>.</summary>
        public const string AppSettingName = AppConfig.CleanUpConcealedIdentityUsersSettingName;

        /// <summary>
        /// Users in the process's first batch: small enough to stay inside <see cref="MaxBatchDuration"/> even on a slow
        /// database, before anything is known. Later batches are sized by <see cref="ConcealedIdentityCleanupBatchSizer"/>.
        /// </summary>
        public const int DefaultBatchSize = 25;

        /// <summary>Most users in one batch - one transaction: the most one batch's <c>VALUES</c> list can carry.</summary>
        public const int MaxBatchSize = ConcealedIdentityUserCleanupSql.MaxBatchSize;

        /// <summary>
        /// Fewest users in one batch. One: when a single user's rows take most of <see cref="MaxBatchDuration"/>, only a
        /// smaller batch helps.
        /// </summary>
        internal const int MinBatchSize = 1;

        /// <summary>
        /// How long one batch - one transaction, holding its locks - should take, when keeping to it costs little throughput.
        /// A placeholder has anything from a few to nine tables x 90 days of rows, and the database may be a small Standard
        /// tier or Business Critical, so the number of users per batch is not fixed (see <see cref="ConcealedIdentityCleanupBatchSizer"/>).
        /// </summary>
        public static readonly TimeSpan TargetBatchDuration = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The cap the batch size is planned against: how long one batch - one transaction, holding its locks - should take at
        /// most. A planning threshold, not a runtime guarantee: no size is chosen that recent batches do not show should finish
        /// well inside it, but blocking, I/O or log throttling can still push a batch past it (see
        /// <see cref="ConcealedIdentityCleanupBatchSizer"/>); <see cref="BatchCommandTimeoutSeconds"/> is the hard stop.
        /// </summary>
        public static readonly TimeSpan MaxBatchDuration = TimeSpan.FromSeconds(30);

        /// <summary>No batch starts once a run has taken this long, so a backlog is spread over cycles.</summary>
        public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromMinutes(2);

        /// <summary>
        /// The most the preflight - finding the tables that refer to users, the candidates, and one read of each table with no
        /// index on its user column - may take while the users-and-licences lock is held. Past it the run stops before any
        /// batch, changes nothing, and waits <see cref="FailureRetryInterval"/>, so a read that cannot finish is paid for once an
        /// hour instead of every cycle. Not part of <see cref="DefaultTimeBudget"/>, which only stops new batches.
        /// </summary>
        public static readonly TimeSpan DefaultPreflightBudget = TimeSpan.FromMinutes(10);

        /// <summary>After a run that found nothing to delete, how long before the database is looked at again.</summary>
        public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);

        /// <summary>After a failure, how long before the next attempt, so a persistent one is not logged every cycle.</summary>
        public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromHours(1);

        /// <summary>How long a batch waits for a lock before giving up until the next cycle.</summary>
        internal const int DefaultLockTimeoutMilliseconds = 30000;

        /// <summary>
        /// The safety net: a batch still running after this is cancelled and rolled back, rather than holding its locks and the
        /// cycle. Three times <see cref="MaxBatchDuration"/>, so a batch the sizer planned well inside the cap reaches it only
        /// when blocking or I/O slows it badly, and far above what a first, <see cref="DefaultBatchSize"/>-user batch takes even
        /// on a slow database. A batch that does time out is not tried at its size again for at least a day (see
        /// <see cref="IsSizeDependentFailure"/>).
        /// </summary>
        internal const int BatchCommandTimeoutSeconds = 90;

        /// <summary>
        /// Whether a failed batch failed because of its size: a command timeout - SqlClient's error -2 - or anything that ran as
        /// long as one. A deadlock (1205), a lock timeout (1222), a reference that appeared mid-batch (547) or any other error is
        /// transient: it halves the next batch, but does not bound the size.
        /// </summary>
        internal static bool IsSizeDependentFailure(Exception ex, TimeSpan took) =>
            (ex is SqlException sql && sql.Number == -2) || took.TotalSeconds >= BatchCommandTimeoutSeconds * 0.95;

        private readonly string _connectionString;
        private readonly bool _enabled;
        private readonly string _unparseableSettingValue;
        private readonly ILogger _logger;
        private readonly IClock _clock;

        // Process-lifetime state: the instance outlives the cycle (see Program.cs).
        private DateTime _notDueBeforeUtc = DateTime.MinValue;
        private bool _workPending;
        private bool _reportedDisabled;
        private bool _reportedUnparseableSetting;
        private bool _reportedNothingToDo;
        private long _totalUsersDeleted;
        private long _totalRowsDeleted;

        // What the run in progress is doing, so a failure before any batch can say where.
        private string _step = "start";

        /// <param name="unparseableSettingValue">
        /// The value of the app setting when it is present but is not <c>true</c> or <c>false</c>: the cleanup is then off
        /// (<paramref name="enabled"/> is false) and the first run says so, naming the value.
        /// </param>
        public ConcealedIdentityUserCleanup(string connectionString, bool enabled, ILogger logger, IClock clock = null, string unparseableSettingValue = null)
        {
            _connectionString = connectionString;
            _enabled = enabled;
            _unparseableSettingValue = unparseableSettingValue;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _sizer = NewSizer(DefaultBatchSize);
        }

        /// <summary>The cleanup for this deployment's database and app settings.</summary>
        public static ConcealedIdentityUserCleanup ForConfig(AppConfig config, ILogger logger)
            => new ConcealedIdentityUserCleanup(config?.ConnectionStrings?.DatabaseConnectionString, config?.CleanUpConcealedIdentityUsers ?? true, logger,
                unparseableSettingValue: config?.CleanUpConcealedIdentityUsersUnparseableValue);

        /// <summary>Test hook: users in the next batch, at most <see cref="MaxBatchSize"/>; later ones are sized from there.</summary>
        internal int BatchSize
        {
            get => _sizer.Next;
            set => _sizer = NewSizer(value);
        }

        // Carried from run to run, so each cycle starts at the size the last one settled on.
        private ConcealedIdentityCleanupBatchSizer _sizer;

        private ConcealedIdentityCleanupBatchSizer NewSizer(int initial)
            => new ConcealedIdentityCleanupBatchSizer(initial, MinBatchSize, MaxBatchSize, TargetBatchDuration, MaxBatchDuration, _clock,
                TimeSpan.FromSeconds(BatchCommandTimeoutSeconds));

        /// <summary>Test hook: overrides <see cref="DefaultTimeBudget"/>.</summary>
        internal TimeSpan TimeBudget { get; set; } = DefaultTimeBudget;

        /// <summary>Test hook: overrides <see cref="DefaultPreflightBudget"/>.</summary>
        internal TimeSpan PreflightBudget { get; set; } = DefaultPreflightBudget;

        /// <summary>Test hook: runs before each table is read in the preflight; the time it takes counts against that read.</summary>
        internal Func<Task> BeforeEachReferenceRead { get; set; }

        /// <summary>Test hook: stop after this many batches, as an interrupted run would.</summary>
        internal int? MaxBatchesPerRun { get; set; }

        /// <summary>Test hook: overrides <see cref="DefaultLockTimeoutMilliseconds"/>.</summary>
        internal int LockTimeoutMilliseconds { get; set; } = DefaultLockTimeoutMilliseconds;

        /// <summary>Test hook: overrides <see cref="BatchCommandTimeoutSeconds"/>.</summary>
        internal int CommandTimeoutSeconds { get; set; } = BatchCommandTimeoutSeconds;

        /// <summary>Test hook: the most users any later batch may take, after the size-dependent failures so far.</summary>
        internal int BatchSizeLimit => _sizer.SizeLimit;

        /// <summary>Test hook: only users with a larger id are considered, so a test sees nothing but its own rows.</summary>
        internal int MinUserIdExclusive { get; set; }

        /// <summary>Test hook: runs after the once-per-run read of the unindexed references, before the first batch.</summary>
        internal Func<Task> AfterReferenceRead { get; set; }

        /// <summary>Test hook: takes the users-and-licences lock. Null takes it without waiting.</summary>
        internal Func<Task<DirectoryWriterLock>> WriterLockFactory { get; set; }

        /// <summary>
        /// Runs one bounded pass when one is due. Never throws, and logs at most one line.
        /// </summary>
        /// <param name="usageReportImportRunning">
        /// Whether the usage-report import - the only other writer of the rows deleted here - is running in the background.
        /// The cleanup then waits for a later cycle.
        /// </param>
        public async Task<ConcealedIdentityUserCleanupResult> RunIfDueAsync(bool usageReportImportRunning = false)
        {
            try
            {
                return await RunCoreAsync(usageReportImportRunning).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Only reached before any batch ran - a batch's own failure is caught and reported with the run's progress.
                _notDueBeforeUtc = _clock.UtcNow + FailureRetryInterval;
                var sqlNumber = ConcealedIdentityUserCleanupResult.FindSqlException(ex)?.Number;
                _logger.LogError($"Concealed-identity user cleanup: could not run - failed at step '{_step}' ({ex.GetType().Name}" +
                    $"{(sqlNumber.HasValue ? $", SQL error {sqlNumber.Value}" : string.Empty)}): {ex.Message} " +
                    $"Nothing was changed; it tries again in {FailureRetryInterval.TotalMinutes:N0} minutes.");
                return new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.Failed) { Error = ex, FailedStep = _step };
            }
        }

        private async Task<ConcealedIdentityUserCleanupResult> RunCoreAsync(bool usageReportImportRunning)
        {
            _step = "start";
            if (!_enabled || string.IsNullOrWhiteSpace(_connectionString))
            {
                if (!_enabled && _unparseableSettingValue != null)
                {
                    if (!_reportedUnparseableSetting)
                    {
                        _reportedUnparseableSetting = true;
                        var shown = _unparseableSettingValue.Length > 50 ? _unparseableSettingValue.Substring(0, 50) + "..." : _unparseableSettingValue;
                        _logger.LogError($"Concealed-identity user cleanup is turned OFF: the {AppSettingName} app setting is '{shown}', which is neither true nor " +
                            "false, so it is ignored and the irreversible delete fails closed. Set it to true to run the cleanup, or to false to switch it off " +
                            "for good (and silence this message).");
                    }
                }
                else if (!_enabled && !_reportedDisabled)
                {
                    _reportedDisabled = true;
                    _logger.LogInformation($"Concealed-identity user cleanup is turned off by the {AppSettingName}=false app setting: placeholder users " +
                        "that older versions created from concealed usage-report identities are left in the database.");
                }
                return new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.Disabled);
            }

            var startedUtc = _clock.UtcNow;
            if (startedUtc < _notDueBeforeUtc)
            {
                return new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.NotDue);
            }

            if (usageReportImportRunning)
            {
                if (_workPending)
                {
                    _logger.LogInformation("Concealed-identity user cleanup is waiting for the usage-report import running in the background to finish; " +
                        "it carries on in a later cycle.");
                }
                return new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.WaitingForUsageReports);
            }

            _step = "first look";
            if (!await AnyCandidateAsync().ConfigureAwait(false))
            {
                _workPending = false;
                _notDueBeforeUtc = startedUtc + RecheckInterval;
                if (!_reportedNothingToDo)
                {
                    _reportedNothingToDo = true;
                    _logger.LogInformation("Concealed-identity user cleanup: no placeholder users from concealed usage-report identities in the database. " +
                        $"Checked again every {RecheckInterval.TotalHours:N0} hours.");
                }
                return new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.NothingToDo);
            }

            using (var writerLock = await AcquireWriterLockAsync().ConfigureAwait(false))
            {
                if (writerLock == null)
                {
                    _workPending = true;
                    _logger.LogInformation("Concealed-identity user cleanup skipped this cycle: another writer of users and licences (the user import, " +
                        "possibly on another instance, or a one-person refresh) holds the users-and-licences lock. It tries again next cycle.");
                    return new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.LockBusy);
                }

                var result = await RunBatchesAsync(writerLock).ConfigureAwait(false);
                RecordAndLog(result, startedUtc);
                return result;
            }
        }

        private async Task<ConcealedIdentityUserCleanupResult> RunBatchesAsync(DirectoryWriterLock writerLock)
        {
            var watch = Stopwatch.StartNew();
            var result = new ConcealedIdentityUserCleanupResult(ConcealedIdentityUserCleanupOutcome.Ran);
            var connection = writerLock.Connection;

            _step = "discovery";
            var (references, activityTables) = await DiscoverAsync(connection).ConfigureAwait(false);
            var keep = ConcealedIdentityUserCleanupSql.KeepReferences(references);
            var oncePerRun = keep.Where(ConcealedIdentityUserCleanupSql.IsCheckedOncePerRun).ToList();
            var everyBatch = keep.Where(r => !ConcealedIdentityUserCleanupSql.IsCheckedOncePerRun(r)).ToList();

            _step = "candidates";
            var candidates = await LoadCandidatesAsync(connection).ConfigureAwait(false);
            result.Candidates = candidates.Count;

            // The tables with no index on their user column are read once, here, rather than scanned in every batch. Bounded by
            // PreflightBudget: the users-and-licences lock is held meanwhile.
            if (candidates.Count > 0 && oncePerRun.Count > 0)
            {
                var referenced = await LoadReferencedCandidatesAsync(connection, oncePerRun, result, watch).ConfigureAwait(false);
                if (referenced == null)
                {
                    result.PreflightExceeded = true;
                    result.PreflightElapsed = watch.Elapsed;
                    result.Elapsed = watch.Elapsed;
                    FillSizing(result);
                    return result;
                }
                result.KeptByRunSnapshot = candidates.RemoveAll(referenced.Contains);
            }

            if (AfterReferenceRead != null) await AfterReferenceRead().ConfigureAwait(false);
            result.PreflightElapsed = watch.Elapsed;

            _step = "batch";
            var next = 0;
            while (next < candidates.Count)
            {
                if (result.Batches > 0 && watch.Elapsed >= TimeBudget) break;
                if (MaxBatchesPerRun.HasValue && result.Batches >= MaxBatchesPerRun.Value) break;
                if (!await writerLock.IsStillHeldAsync().ConfigureAwait(false))
                {
                    result.LostLock = true;
                    break;
                }

                var batch = candidates.GetRange(next, Math.Min(_sizer.Next, candidates.Count - next));
                var batchWatch = Stopwatch.StartNew();
                try
                {
                    await RunBatchAsync(connection, batch, activityTables, everyBatch, result).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The batch rolled back as a whole; the next run starts again from the rule, with a batch half the size. A
                    // timeout also keeps later batches below that size for a day, so it is paid for at most once a day.
                    _sizer.ObserveFailure(batch.Count, batchWatch.Elapsed, IsSizeDependentFailure(ex, batchWatch.Elapsed));
                    result.Error = ex;
                    result.FailedStep = "batch";
                    result.FailedBatchSize = batch.Count;
                    result.FailedBatchElapsed = batchWatch.Elapsed;
                    break;
                }
                _sizer.Observe(batch.Count, batchWatch.Elapsed);
                result.BatchDurations.Add(batchWatch.Elapsed);
                result.BatchSizes.Add(batch.Count);
                next += batch.Count;
                result.Examined = next;
                result.Batches++;
            }

            result.Elapsed = watch.Elapsed;
            FillSizing(result);
            return result;
        }

        private void FillSizing(ConcealedIdentityUserCleanupResult result)
        {
            result.NextBatchSize = _sizer.Next;
            result.BatchSizeLimit = _sizer.SizeLimit;
            result.FixedCostSeconds = _sizer.FixedCostSeconds;
        }

        /// <summary>
        /// The candidates that any of <paramref name="references"/> refers to: one plain read of each table, with no locks
        /// held afterwards. See <see cref="ConcealedIdentityUserCleanupSql.ReferencesCheckedOncePerRun"/> for why a reference
        /// added to them later in the run is not a risk. Null when the preflight ran out of <see cref="PreflightBudget"/> first.
        /// </summary>
        /// <remarks>
        /// One command per table, each timed and each allowed only what is left of the budget - at most 30 minutes - so the
        /// time they take is reported by table, and a read that cannot finish stops the run rather than the cycle. The result is
        /// never kept for a later run: a reference may be added to any of these tables after the read, and the guarantees in
        /// <see cref="ConcealedIdentityUserCleanupSql.ReferencesCheckedOncePerRun"/> hold only for the run that did the read,
        /// under the users-and-licences lock.
        /// </remarks>
        private async Task<HashSet<int>> LoadReferencedCandidatesAsync(SqlConnection connection, IReadOnlyList<UserReferenceColumn> references,
            ConcealedIdentityUserCleanupResult result, Stopwatch preflightWatch)
        {
            const int GenerousSeconds = 1800;
            var referenced = new HashSet<int>();
            var readsWatch = Stopwatch.StartNew();
            try
            {
                foreach (var reference in references)
                {
                    _step = $"reference read ({reference})";
                    var readWatch = Stopwatch.StartNew();
                    if (BeforeEachReferenceRead != null) await BeforeEachReferenceRead().ConfigureAwait(false);

                    var remaining = PreflightBudget - preflightWatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        result.ReferenceReads.Add(new KeyValuePair<string, TimeSpan>(reference.ToString() + " (not started)", readWatch.Elapsed));
                        return null;
                    }

                    // A plain read that holds no lock afterwards, so a long one only delays this run: finite, and within the budget.
                    var seconds = (int)Math.Max(1, Math.Min(GenerousSeconds, Math.Ceiling(remaining.TotalSeconds)));
                    try
                    {
                        using (var command = new SqlCommand(ConcealedIdentityUserCleanupSql.BuildReferencedCandidatesSql(new[] { reference }), connection) { CommandTimeout = seconds })
                        {
                            command.Parameters.Add("@minUserId", SqlDbType.Int).Value = MinUserIdExclusive;
                            using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                            {
                                while (await reader.ReadAsync().ConfigureAwait(false))
                                {
                                    referenced.Add(reader.GetInt32(0));
                                }
                            }
                        }
                    }
                    catch (SqlException ex) when (ex.Number == -2 && seconds < GenerousSeconds)
                    {
                        // The budget, not the generous limit, stopped it.
                        result.ReferenceReads.Add(new KeyValuePair<string, TimeSpan>(reference.ToString() + " (timed out)", readWatch.Elapsed));
                        return null;
                    }
                    result.ReferenceReads.Add(new KeyValuePair<string, TimeSpan>(reference.ToString(), readWatch.Elapsed));
                }
                return referenced;
            }
            finally
            {
                result.ReferenceReadElapsed = readsWatch.Elapsed;
            }
        }

        private async Task RunBatchAsync(SqlConnection connection, List<int> batch, IReadOnlyList<string> activityTables,
            IReadOnlyList<UserReferenceColumn> keep, ConcealedIdentityUserCleanupResult result)
        {
            var sql = ConcealedIdentityUserCleanupSql.BuildBatchSql(batch, activityTables, keep, LockTimeoutMilliseconds);
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                // A parameter makes this an sp_executesql call, so its temporary tables go with it even if it fails part-way.
                command.Parameters.Add("@minUserId", SqlDbType.Int).Value = MinUserIdExclusive;
                using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync().ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("A cleanup batch returned no result.");
                    }

                    result.UsersDeleted += reader.GetInt32(reader.GetOrdinal("users_deleted"));
                    foreach (var table in activityTables)
                    {
                        var rows = reader.GetInt32(reader.GetOrdinal(table));
                        if (rows == 0) continue;
                        result.RowsDeletedByTable.TryGetValue(table, out var soFar);
                        result.RowsDeletedByTable[table] = soFar + rows;
                    }
                }
            }
        }

        private async Task<bool> AnyCandidateAsync()
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var command = new SqlCommand(ConcealedIdentityUserCleanupSql.BuildAnyCandidateSql(), connection) { CommandTimeout = 300 })
                {
                    command.Parameters.Add("@minUserId", SqlDbType.Int).Value = MinUserIdExclusive;
                    var found = await command.ExecuteScalarAsync().ConfigureAwait(false);
                    return found != null && found != DBNull.Value;
                }
            }
        }

        private static async Task<(List<UserReferenceColumn> References, List<string> ActivityTables)> DiscoverAsync(SqlConnection connection)
        {
            var references = new List<UserReferenceColumn>();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = new SqlCommand(ConcealedIdentityUserCleanupSql.BuildReferenceDiscoverySql(), connection) { CommandTimeout = 300 })
            using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    references.Add(new UserReferenceColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
                }
                await reader.NextResultAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    existing.Add(reader.GetString(0));
                }
            }

            // In the declared order, so each batch deletes from the tables in the same order.
            var activityTables = ConcealedIdentityUserCleanupSql.UsageReportActivityTables.Where(existing.Contains).ToList();
            return (references, activityTables);
        }

        private async Task<List<int>> LoadCandidatesAsync(SqlConnection connection)
        {
            var candidates = new List<int>();
            using (var command = new SqlCommand(ConcealedIdentityUserCleanupSql.BuildCandidatesSql(), connection) { CommandTimeout = 600 })
            {
                command.Parameters.Add("@minUserId", SqlDbType.Int).Value = MinUserIdExclusive;
                using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        // The rule again, in C#: a row deleted has passed two independent implementations of it.
                        if (ConcealedIdentityPlaceholderRule.IsPlaceholderUserName(reader.GetString(1)))
                        {
                            candidates.Add(reader.GetInt32(0));
                        }
                    }
                }
            }
            return candidates;
        }

        private Task<DirectoryWriterLock> AcquireWriterLockAsync()
        {
            var factory = WriterLockFactory;
            return factory != null ? factory() : DirectoryWriterLock.TryAcquireAsync(_connectionString, TimeSpan.Zero);
        }

        private void RecordAndLog(ConcealedIdentityUserCleanupResult result, DateTime startedUtc)
        {
            _totalUsersDeleted += result.UsersDeleted;
            _totalRowsDeleted += result.ActivityRowsDeleted;

            var finished = result.Remaining == 0 && result.Error == null && !result.LostLock && !result.PreflightExceeded;
            _workPending = !finished;
            if (finished && result.UsersDeleted == 0)
            {
                // Only users that are kept are left: look again tomorrow, not every cycle.
                _notDueBeforeUtc = startedUtc + RecheckInterval;
            }
            else if (result.Error != null || result.PreflightExceeded)
            {
                _notDueBeforeUtc = startedUtc + FailureRetryInterval;
            }

            var message = DescribeRun(result, finished, _totalUsersDeleted, _totalRowsDeleted);
            if (result.Error != null)
            {
                _logger.LogError(message);
            }
            else if (result.LostLock || result.PreflightExceeded)
            {
                _logger.LogWarning(message);
            }
            else
            {
                _logger.LogInformation(message);
            }
        }

        /// <summary>The one line a run logs.</summary>
        internal static string DescribeRun(ConcealedIdentityUserCleanupResult result, bool finished, long totalUsersDeleted, long totalRowsDeleted)
        {
            var text = new System.Text.StringBuilder("Concealed-identity user cleanup: ");
            if (result.UsersDeleted > 0)
            {
                text.Append($"deleted {result.UsersDeleted:N0} placeholder user(s) that an older version created from concealed (hashed) usage-report " +
                    $"identities, with their {result.ActivityRowsDeleted:N0} usage-report row(s)");
                if (result.RowsDeletedByTable.Count > 0)
                {
                    text.Append(" (" + string.Join(", ", result.RowsDeletedByTable.Select(t => $"{t.Key} {t.Value:N0}")) + ")");
                }
                text.Append($", in {result.Batches:N0} batch(es) taking {result.Elapsed.TotalSeconds:N0} s. ");
            }
            else
            {
                text.Append($"nothing deleted in {result.Batches:N0} batch(es). ");
            }

            if (result.UsersKept > 0)
            {
                text.Append($"{result.UsersKept:N0} user(s) that look like concealed identities were kept, because other data (audit, Copilot, licence, Teams " +
                    "or similar) refers to them or the user import has matched them. ");
            }

            if (result.PreflightExceeded)
            {
                text.Append($"Stopped before any batch: reading the tables that refer to users ran out of its time budget after {result.PreflightElapsed.TotalMinutes:N1} minutes, " +
                    $"so nothing was changed; the cleanup tries again in {FailureRetryInterval.TotalMinutes:N0} minutes. ");
            }
            else if (result.Error != null)
            {
                var sqlNumber = result.SqlErrorNumber;
                text.Append($"Stopped by an error at step '{result.FailedStep ?? "batch"}' ({result.Error.GetType().Name}" +
                    $"{(sqlNumber.HasValue ? $", SQL error {sqlNumber.Value}" : string.Empty)}): {result.Error.Message} " +
                    $"{(result.FailedBatchSize > 0 ? $"That batch ({result.FailedBatchSize:N0} users, {result.FailedBatchElapsed.TotalSeconds:N1} s) was rolled back as a whole; " : string.Empty)}" +
                    $"the cleanup tries again in {FailureRetryInterval.TotalMinutes:N0} minutes. ");
            }
            else if (result.LostLock)
            {
                text.Append("Stopped: the users-and-licences lock was lost part-way (its database connection dropped). ");
            }

            if (finished)
            {
                text.Append(result.UsersDeleted > 0
                    ? "None left to check; the next cycle confirms it. "
                    : $"None left to delete; checked again in {RecheckInterval.TotalHours:N0} hours. ");
            }
            else
            {
                text.Append(result.Error != null || result.PreflightExceeded
                    ? $"{result.Remaining:N0} still to check. "
                    : $"{result.Remaining:N0} still to check, in the next cycle(s). ");
            }

            text.Append($"Preflight {result.PreflightElapsed.TotalMilliseconds:N0} ms (reference reads {result.ReferenceReadElapsed.TotalMilliseconds:N0} ms" +
                (result.ReferenceReads.Count > 0 ? ": " + string.Join(", ", result.ReferenceReads.Select(r => $"{r.Key} {r.Value.TotalMilliseconds:N0} ms")) : string.Empty) + "). ");

            if (result.BatchSizes.Count > 0)
            {
                var seconds = result.BatchDurations.Select(d => d.TotalSeconds).OrderBy(s => s).ToList();
                var sizes = result.BatchSizes.OrderBy(s => s).ToList();
                text.Append($"Batches {result.Batches:N0}: users min/median/max {sizes[0]:N0}/{sizes[sizes.Count / 2]:N0}/{sizes[sizes.Count - 1]:N0}, " +
                    $"seconds min/median/max {seconds[0]:N1}/{seconds[seconds.Count / 2]:N1}/{seconds[seconds.Count - 1]:N1}. ");
            }
            text.Append($"Next batch {result.NextBatchSize:N0} user(s), size limit {result.BatchSizeLimit:N0}" +
                (result.FixedCostSeconds.HasValue ? $", fixed cost about {result.FixedCostSeconds.Value:N0} s per batch whatever its size" : string.Empty) + ". ");

            text.Append($"Deleted since the importer started: {totalUsersDeleted:N0} user(s), {totalRowsDeleted:N0} usage-report row(s). " +
                $"Set the {AppSettingName} app setting to false to turn this off.");
            return text.ToString();
        }
    }
}
