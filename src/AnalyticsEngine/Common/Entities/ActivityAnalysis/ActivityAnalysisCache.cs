using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>Every load slot stayed taken for the whole wait budget. Answered as a 503 with Retry-After.</summary>
    public sealed class ActivityAnalysisBusyException : Exception
    {
        public ActivityAnalysisBusyException()
            : base("Another activity analysis is being prepared right now. Try again in a few seconds.") { }
    }

    /// <summary>
    /// A small cache of expensive, shared results - one load per key however many readers ask, a bounded number of
    /// loads at once, and a bounded number of results kept.
    /// </summary>
    /// <remarks>
    /// <para><b>Single flight.</b> Concurrent requests for the same key share one load. The load runs on its own worker
    /// and is never tied to a request: a reader who navigates away must not cancel what others are waiting for. Each
    /// caller's wait is still cancellable.</para>
    /// <para><b>Bounded loads.</b> At most <c>concurrentLoads</c> run at once; a load that cannot start within the wait
    /// budget fails every caller waiting on it with <see cref="ActivityAnalysisBusyException"/>, and is forgotten, so the
    /// next request tries again. A failed load is never cached.</para>
    /// <para><b>Bounded memory.</b> At most <c>capacity</c> results are kept, least recently used evicted first - room is
    /// made before a load starts, so a large result is never held three times over - and each expires after
    /// <c>idleLifetime</c> unused and/or <c>maximumAge</c> after it was read. An optional timer prunes expired results
    /// when nobody is asking, so an idle site does not hold a 46 MB read model until its next visitor.</para>
    /// </remarks>
    public sealed class ActivityAnalysisLoadCache<T> where T : class
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _loads;
        private readonly int _capacity;
        private readonly TimeSpan? _idleLifetime;
        private readonly TimeSpan? _maximumAge;
        private readonly TimeSpan _loadWait;
        private readonly Func<DateTime> _utcNow;
        private readonly Timer _pruner;

        public ActivityAnalysisLoadCache(
            int capacity, TimeSpan? idleLifetime, TimeSpan? maximumAge, int concurrentLoads, TimeSpan loadWait,
            Func<DateTime> utcNow = null, TimeSpan? pruneInterval = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (concurrentLoads < 1) throw new ArgumentOutOfRangeException(nameof(concurrentLoads));
            if (!idleLifetime.HasValue && !maximumAge.HasValue) throw new ArgumentException("A lifetime is required.", nameof(idleLifetime));
            if (idleLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleLifetime));
            if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
            if (loadWait < TimeSpan.Zero || loadWait.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(loadWait));

            _capacity = capacity;
            _idleLifetime = idleLifetime;
            _maximumAge = maximumAge;
            _loads = new SemaphoreSlim(concurrentLoads, concurrentLoads);
            _loadWait = loadWait;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (pruneInterval.HasValue) _pruner = new Timer(_ => Prune(), null, pruneInterval.Value, pruneInterval.Value);
        }

        /// <summary>How many loaded results are held.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    PruneLocked();
                    return _entries.Values.Count(e => e.Completed);
                }
            }
        }

        /// <summary>Whether a loaded, unexpired result is held for the key - without counting as a use.</summary>
        public bool Contains(string key)
        {
            lock (_gate)
            {
                PruneLocked();
                return _entries.TryGetValue(key, out var entry) && entry.Completed;
            }
        }

        /// <summary>Forgets every expired result.</summary>
        public void Prune()
        {
            lock (_gate) PruneLocked();
        }

        /// <summary>The cached result, a load already under way, or a new load.</summary>
        /// <exception cref="ActivityAnalysisBusyException">The load could not start within the wait budget.</exception>
        public Task<T> GetAsync(string key, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A cache key is required.", nameof(key));
            if (load == null) throw new ArgumentNullException(nameof(load));
            cancellationToken.ThrowIfCancellationRequested();

            Entry entry;
            var start = false;
            lock (_gate)
            {
                PruneLocked();
                var now = _utcNow();
                if (_entries.TryGetValue(key, out entry))
                {
                    entry.LastUsedUtc = now;
                    if (entry.Completed) return Task.FromResult(entry.Value);
                }
                else
                {
                    entry = new Entry(key) { LastUsedUtc = now };
                    _entries.Add(key, entry);
                    start = true;
                }
            }

            // Off the request's thread and synchronisation context: the load is shared by everyone waiting on it.
            if (start) _ = Task.Run(() => LoadAsync(entry, load));
            return WaitAsync(entry.Completion.Task, cancellationToken);
        }

        private async Task LoadAsync(Entry entry, Func<CancellationToken, Task<T>> load)
        {
            T value = null;
            Exception failure = null;
            var acquired = false;
            try
            {
                acquired = await _loads.WaitAsync(_loadWait).ConfigureAwait(false);
                if (!acquired) throw new ActivityAnalysisBusyException();

                lock (_gate) MakeRoomLocked(entry);

                value = await load(CancellationToken.None).ConfigureAwait(false);
                if (value == null) throw new InvalidOperationException("The activity analysis loader returned nothing.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (acquired) _loads.Release();
            }

            lock (_gate)
            {
                if (failure == null)
                {
                    var now = _utcNow();
                    entry.Value = value;
                    entry.Completed = true;
                    entry.CompletedUtc = now;
                    entry.LastUsedUtc = now;
                    EvictLocked(entry, _capacity);
                    entry.Completion.TrySetResult(value);
                }
                else
                {
                    if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(entry.Key);
                    entry.Completion.TrySetException(failure);

                    // Observed here, so a load nobody is still waiting for never surfaces as an unobserved exception.
                    _ = entry.Completion.Task.Exception;
                }
            }
        }

        /// <summary>Before a load: evict the least recently used results until it will fit.</summary>
        private void MakeRoomLocked(Entry loading) => EvictLocked(loading, _capacity - 1);

        private void EvictLocked(Entry keep, int allowed)
        {
            while (true)
            {
                var completed = _entries.Values.Where(e => e.Completed && !ReferenceEquals(e, keep)).ToList();
                var held = completed.Count + (keep.Completed ? 1 : 0);
                if (held <= allowed || completed.Count == 0) return;

                var oldest = completed.OrderBy(e => e.LastUsedUtc).First();
                _entries.Remove(oldest.Key);
            }
        }

        private void PruneLocked()
        {
            var now = _utcNow();
            var expired = _entries.Values.Where(e => e.Completed && IsExpired(e, now)).Select(e => e.Key).ToList();
            foreach (var key in expired) _entries.Remove(key);
        }

        private bool IsExpired(Entry entry, DateTime now)
        {
            return (_idleLifetime.HasValue && now - entry.LastUsedUtc >= _idleLifetime.Value)
                || (_maximumAge.HasValue && now - entry.CompletedUtc >= _maximumAge.Value);
        }

        private static async Task<TResult> WaitAsync<TResult>(Task<TResult> shared, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return await shared.ConfigureAwait(false);

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false) != shared)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return await shared.ConfigureAwait(false);
            }
        }

        private sealed class Entry
        {
            internal Entry(string key)
            {
                Key = key;
            }

            internal string Key { get; }

            internal TaskCompletionSource<T> Completion { get; } =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal bool Completed { get; set; }

            internal T Value { get; set; }

            internal DateTime CompletedUtc { get; set; }

            internal DateTime LastUsedUtc { get; set; }
        }
    }

    /// <summary>The three caches behind the page, shared by every reader of one web process.</summary>
    public sealed class ActivityAnalysisCaches
    {
        /// <summary>At most two periods in memory: a 200,000-person read model is about 50 MB.</summary>
        public const int ReadModelCapacity = 2;

        /// <summary>Filtered weekly series are about 75 KB each.</summary>
        public const int WeeklyTotalsCapacity = 64;

        /// <summary>
        /// Both kinds of result are dropped after a quarter of an hour unused. A read model is also keyed by the weeks the
        /// table holds (<see cref="ActivityAnalysisSchema.DataVersion"/>): a run of the runbooks adds a week and deletes
        /// the weeks past their retention, so a period read before it is read again once the schema is next checked,
        /// rather than kept for as long as somebody is using it.
        /// </summary>
        public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);

        public static readonly TimeSpan SchemaLifetime = TimeSpan.FromMinutes(5);

        /// <summary>How long a request waits for a load slot before it is told the page is busy.</summary>
        public static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(90);

        /// <summary>The web process's caches.</summary>
        public static readonly ActivityAnalysisCaches Shared = new ActivityAnalysisCaches(pruneInterval: TimeSpan.FromMinutes(1));

        public ActivityAnalysisCaches(Func<DateTime> utcNow = null, TimeSpan? loadWait = null, TimeSpan? pruneInterval = null)
        {
            var wait = loadWait ?? LoadWait;
            Schemas = new ActivityAnalysisLoadCache<ActivityAnalysisSchema>(
                4, null, SchemaLifetime, 4, wait, utcNow, pruneInterval);

            // One period loads at a time: each is a scan of the weekly table, and several administrators choosing
            // different periods at once must not multiply that.
            ReadModels = new ActivityAnalysisLoadCache<ActivityAnalysisReadModel>(
                ReadModelCapacity, IdleLifetime, null, 1, wait, utcNow, pruneInterval);
            WeeklyTotals = new ActivityAnalysisLoadCache<ActivityAnalysisWeeklyTotals>(
                WeeklyTotalsCapacity, IdleLifetime, null, 2, wait, utcNow, pruneInterval);
        }

        public ActivityAnalysisLoadCache<ActivityAnalysisSchema> Schemas { get; }

        public ActivityAnalysisLoadCache<ActivityAnalysisReadModel> ReadModels { get; }

        public ActivityAnalysisLoadCache<ActivityAnalysisWeeklyTotals> WeeklyTotals { get; }
    }

    /// <summary>
    /// The page's figures for one database: the availability, a period's read model, and the report and people built
    /// from it for one reader.
    /// </summary>
    public sealed class ActivityAnalysisService
    {
        private readonly IActivityAnalysisSource _source;
        private readonly string _scope;
        private readonly ActivityAnalysisCaches _caches;

        /// <param name="scope">Identifies the database in the shared caches, so two databases never share figures.</param>
        public ActivityAnalysisService(IActivityAnalysisSource source, string scope, ActivityAnalysisCaches caches)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrEmpty(scope)) throw new ArgumentException("A cache scope is required.", nameof(scope));
            _scope = scope;
            _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        }

        public Task<ActivityAnalysisSchema> GetSchemaAsync(CancellationToken cancellationToken) =>
            _caches.Schemas.GetAsync(_scope, token => _source.ReadSchemaAsync(token), cancellationToken);

        public Task<ActivityAnalysisReadModel> GetReadModelAsync(ActivityAnalysisPeriod period, CancellationToken cancellationToken)
        {
            if (period == null) throw new ArgumentNullException(nameof(period));
            return GetCurrentReadModelAsync(period, cancellationToken);
        }

        private async Task<ActivityAnalysisReadModel> GetCurrentReadModelAsync(ActivityAnalysisPeriod period, CancellationToken cancellationToken)
        {
            var schema = await GetSchemaAsync(cancellationToken).ConfigureAwait(false);
            return await _caches.ReadModels.GetAsync(
                _scope + "\n" + period.Key + "\n" + schema.DataVersion,
                token => _source.LoadReadModelAsync(period, token),
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The weekly series of exactly the matching people, read once per read model and set of people - so changing
        /// the selected metrics never reads again. <c>null</c> when the evaluation needs none.
        /// </summary>
        public Task<ActivityAnalysisWeeklyTotals> GetWeeklyTotalsAsync(ActivityAnalysisEvaluation evaluation, CancellationToken cancellationToken)
        {
            if (evaluation == null) throw new ArgumentNullException(nameof(evaluation));
            if (!evaluation.NeedsWeeklyTotals) return Task.FromResult<ActivityAnalysisWeeklyTotals>(null);

            var ids = evaluation.MatchingUserIds();
            var key = _scope + "\n" + evaluation.Model.Id + "\n" + ids.Length + ":" + Hash(ids);
            return _caches.WeeklyTotals.GetAsync(
                key, token => LoadWeeklyTotalsAsync(evaluation, ids, token), cancellationToken);
        }

        /// <summary>
        /// Reads the smaller side. When most of the model's people match - a licence most staff hold, "at least one
        /// Teams call" - reading them would send nearly every id and aggregate nearly every row, so it reads everybody
        /// else and takes their figures from the population's, which the model already holds. That subtraction is only
        /// sound against the table the model was read from: when the runbooks have changed the weeks since, the
        /// matching people are read after all.
        /// </summary>
        private async Task<ActivityAnalysisWeeklyTotals> LoadWeeklyTotalsAsync(
            ActivityAnalysisEvaluation evaluation, int[] matching, CancellationToken cancellationToken)
        {
            var model = evaluation.Model;
            if (!model.PopulationWeeksExact || matching.Length <= model.PeopleCount / 2)
            {
                return await _source.LoadWeeklyTotalsAsync(model, matching, cancellationToken).ConfigureAwait(false);
            }

            var others = await _source.LoadWeeklyTotalsAsync(model, evaluation.UnmatchedUserIds(), cancellationToken)
                .ConfigureAwait(false);
            if (others.DataVersion == null || others.DataVersion != model.DataVersion)
            {
                return await _source.LoadWeeklyTotalsAsync(model, matching, cancellationToken).ConfigureAwait(false);
            }

            return ActivityAnalysisWeeklyTotals.Except(model.PopulationWeeks, others);
        }

        /// <summary>The report for a resolved query and one reader.</summary>
        public async Task<ActivityAnalysisReport> GetReportAsync(
            ActivityAnalysisQuery query, ActivityAnalysisAudience audience, UserFilterEcho userFilter, CancellationToken cancellationToken)
        {
            var evaluation = await EvaluateAsync(query, audience, cancellationToken).ConfigureAwait(false);
            var weeks = await GetWeeklyTotalsAsync(evaluation, cancellationToken).ConfigureAwait(false);
            return evaluation.BuildReport(weeks, userFilter);
        }

        /// <summary>The matching people, for a reader holding See PII. The caller checks the permission.</summary>
        public async Task<ActivityAnalysisPeople> GetPeopleAsync(
            ActivityAnalysisQuery query, ActivityAnalysisAudience audience, string department, bool noDepartment,
            ActivityAnalysisMetric sort, int top, CancellationToken cancellationToken)
        {
            var evaluation = await EvaluateAsync(query, audience, cancellationToken).ConfigureAwait(false);
            return evaluation.BuildPeople(department, noDepartment, sort, top);
        }

        public async Task<ActivityAnalysisEvaluation> EvaluateAsync(
            ActivityAnalysisQuery query, ActivityAnalysisAudience audience, CancellationToken cancellationToken)
        {
            if (query?.Period == null) throw new ArgumentException("The query must be resolved first.", nameof(query));
            var model = await GetReadModelAsync(query.Period, cancellationToken).ConfigureAwait(false);
            return model.Evaluate(query, audience, cancellationToken);
        }

        /// <summary>The same set of people always hashes the same, so readers the filters treat alike share a series.</summary>
        internal static string Hash(int[] sortedIds)
        {
            var bytes = new byte[sortedIds.Length * sizeof(int)];
            Buffer.BlockCopy(sortedIds, 0, bytes, 0, bytes.Length);
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }
    }
}
