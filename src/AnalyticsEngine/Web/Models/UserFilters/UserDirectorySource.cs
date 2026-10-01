using Common.Entities.Config;
using Common.Entities.UserFilters;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserFilters
{
    /// <summary>Supplies the directory snapshot user filters are evaluated against.</summary>
    internal interface IUserDirectorySource
    {
        /// <summary>The current snapshot, loading it first if there is none yet.</summary>
        Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken);

        /// <summary>Starts a load if none is cached or running, without waiting for it.</summary>
        void Prefetch();

        /// <summary>Forgets the cached snapshot, so the next request reads the directory again.</summary>
        void Invalidate();
    }

    /// <summary>
    /// Keeps one <see cref="UserDirectorySnapshot"/> per web process and refreshes it in the background.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a snapshot at all.</b> A filter is evaluated against every person in the directory,
    /// and a reader changing a filter expects the page to follow within a second or two. Reading the
    /// directory costs one scan of <c>dbo.users</c> - a few seconds on a 200,000-user tenant - so it
    /// is read once and shared by every filter, every report and every user of this process.</para>
    /// <para><b>Stale while refreshing.</b> Past <see cref="_freshFor"/> the next request still gets
    /// the snapshot it has, and a refresh starts behind it: the directory changes when the importer
    /// runs, typically a few times a day, so a snapshot minutes old is as good as a new one and not
    /// worth making a reader wait for. Only past <see cref="_usableFor"/> - long enough that an admin's
    /// org type changes should certainly show - does a request wait for the new one.</para>
    /// <para><b>Shared, un-cancellable load.</b> Concurrent first requests share one load, and the load
    /// is never tied to a request's cancellation token: a reader who navigates away must not cancel a
    /// load that other readers are waiting on (the same trap as issue #441).</para>
    /// </remarks>
    internal sealed class CachedUserDirectorySource : IUserDirectorySource
    {
        public static readonly CachedUserDirectorySource Default = new CachedUserDirectorySource(
            () => UserFilterStores.CreateDirectoryLoader(new AppConfig().ConnectionStrings.SQL),
            freshFor: TimeSpan.FromMinutes(5),
            usableFor: TimeSpan.FromMinutes(30));

        private readonly Func<IUserDirectoryLoader> _loaderFactory;
        private readonly TimeSpan _freshFor;
        private readonly TimeSpan _usableFor;
        private readonly Func<DateTime> _utcNow;
        private readonly object _sync = new object();

        private UserDirectorySnapshot _current;
        private DateTime _currentLoadedUtc;
        private Task<UserDirectorySnapshot> _loading;
        private int _loadingId;
        private int _nextLoadId;
        private int _generation;

        public CachedUserDirectorySource(
            Func<IUserDirectoryLoader> loaderFactory,
            TimeSpan freshFor,
            TimeSpan usableFor,
            Func<DateTime> utcNow = null)
        {
            _loaderFactory = loaderFactory ?? throw new ArgumentNullException(nameof(loaderFactory));
            _freshFor = freshFor;
            _usableFor = usableFor < freshFor ? freshFor : usableFor;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken)
        {
            // Bounded: only an admin invalidating again and again during one read can get round this
            // more than once, and past the budget the request fails rather than answering from a read
            // that predates the latest change.
            for (var attempt = 0; ; attempt++)
            {
                Task<UserDirectorySnapshot> loading;
                int generation;

                lock (_sync)
                {
                    var age = _current == null ? TimeSpan.MaxValue : _utcNow() - _currentLoadedUtc;

                    if (_current != null && age < _freshFor) return _current;

                    loading = StartLoadLocked();
                    generation = _generation;

                    // Stale but usable: answer now, let the refresh land for the next request.
                    if (_current != null && age < _usableFor)
                    {
                        Observe(loading);
                        return _current;
                    }
                }

                // Only the wait is cancellable - the load carries on for everyone else.
                var completed = await Task.WhenAny(loading, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                if (completed != loading)
                {
                    Observe(loading);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var snapshot = await loading.ConfigureAwait(false);

                // Invalidated while this request waited: the load it joined read the directory before the
                // change that prompted the invalidation - an org type just disabled, a CSV just imported -
                // so answering with it would contradict what the admin has just been told. Wait for a
                // load that started after the change instead.
                lock (_sync)
                {
                    if (generation == _generation) return snapshot;
                }

                if (attempt >= MaxInvalidationRetries)
                {
                    throw new InvalidOperationException(
                        "The directory changed repeatedly while it was being read; the request was not answered from a stale read.");
                }
            }
        }

        /// <summary>How many times one request will wait out an invalidation before giving up.</summary>
        private const int MaxInvalidationRetries = 4;

        public void Prefetch()
        {
            lock (_sync)
            {
                if (_current != null && _utcNow() - _currentLoadedUtc < _freshFor) return;
                Observe(StartLoadLocked());
            }
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                _current = null;
                // A load already running read the directory before whatever change prompted this; it
                // must not be allowed to install its result as current.
                _generation++;
                _loading = null;
                _loadingId = 0;
            }
        }

        private Task<UserDirectorySnapshot> StartLoadLocked()
        {
            if (_loading != null) return _loading;

            var id = ++_nextLoadId;
            var generation = _generation;

            // Continuations run asynchronously so that nobody awaiting the load ever resumes inline on
            // the thread completing it - which could be holding this source's lock.
            var completion = new TaskCompletionSource<UserDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loadingId = id;
            _loading = completion.Task;

            // Off the request's thread and its synchronisation context: the load is shared, and must
            // neither capture ASP.NET's context nor be cancelled with the request that began it.
            Task.Run(() => LoadAndInstallAsync(id, generation, completion));

            return completion.Task;
        }

        /// <summary>
        /// Reads the directory and installs the result before completing the shared task, so a request
        /// that resumes on its completion - or arrives a moment after it - always finds the new snapshot
        /// current, rather than racing a continuation to it. Never throws: a failure is handed to
        /// whoever is waiting, and forgotten so the next request tries again.
        /// </summary>
        private async Task LoadAndInstallAsync(int id, int generation, TaskCompletionSource<UserDirectorySnapshot> completion)
        {
            UserDirectorySnapshot snapshot;
            try
            {
                snapshot = await _loaderFactory().LoadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Forget(id);
                completion.TrySetException(ex);
                return;
            }

            lock (_sync)
            {
                if (generation == _generation)
                {
                    _current = snapshot;
                    _currentLoadedUtc = _utcNow();
                }
            }

            Forget(id);
            completion.TrySetResult(snapshot);
        }

        private void Forget(int id)
        {
            lock (_sync)
            {
                if (_loadingId != id) return;
                _loading = null;
                _loadingId = 0;
            }
        }

        /// <summary>
        /// Marks a load's failure as seen when nobody is going to await it - a background refresh
        /// behind a stale answer, or a prefetch - so it never surfaces as an unobserved task exception.
        /// </summary>
        private static void Observe(Task task)
        {
            task.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
