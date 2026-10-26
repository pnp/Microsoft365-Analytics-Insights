using System;
using System.Collections.Concurrent;
using System.Runtime.Caching;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// One computation per key, shared by every request that wants it while it runs, its result cached for a while
    /// and its failure for less.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cache in front of a heavy query only helps once something is in it. Every request that arrives while the
    /// cache is cold - the burst after an App Service recycle, a page opened by several admins at once - used to run
    /// the whole query itself; these join the one already running instead.
    /// </para>
    /// <para>
    /// The computation has a deadline, enforced twice: its token is cancelled, so SQL that honours cancellation
    /// stops, and nobody waits beyond it, so work that ignores the token cannot hold a request up. A computation that
    /// runs out of time fails with <see cref="TimeoutException"/>. A failure is cached briefly, so a struggling
    /// database is not handed the same query again by every page load. A request that gives up stops waiting; the
    /// computation carries on for the others, so it never runs on, or is cancelled by, any one request.
    /// </para>
    /// </remarks>
    internal sealed class SharedComputation<T> where T : class
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _inFlight =
            new ConcurrentDictionary<string, Lazy<Task<T>>>(StringComparer.Ordinal);
        private readonly ObjectCache _cache;
        private readonly string _cacheKeyPrefix;
        private readonly TimeSpan _successTtl;
        private readonly TimeSpan _failureTtl;
        private readonly TimeSpan _deadline;
        private readonly Func<T, bool> _isDegraded;

        /// <param name="isDegraded">
        /// Whether a result is only partly complete - some of it could not be worked out. Such a result is served, but
        /// reused for <paramref name="failureTtl"/> rather than <paramref name="successTtl"/>, so the gap is filled
        /// soon after whatever caused it passes. Null treats every result as complete.
        /// </param>
        internal SharedComputation(ObjectCache cache, string cacheKeyPrefix, TimeSpan successTtl, TimeSpan failureTtl, TimeSpan deadline,
            Func<T, bool> isDegraded = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _cacheKeyPrefix = cacheKeyPrefix ?? string.Empty;
            _successTtl = successTtl;
            _failureTtl = failureTtl;
            _deadline = deadline;
            _isDegraded = isDegraded;
        }

        /// <param name="key">What the result depends on; computations for different keys run independently.</param>
        /// <param name="compute">Runs on the thread pool, with a token cancelled at the deadline.</param>
        /// <param name="cancellationToken">This caller's request; cancelling it only stops this caller waiting.</param>
        internal async Task<T> GetAsync(string key, Func<CancellationToken, Task<T>> compute, CancellationToken cancellationToken)
        {
            var cacheKey = _cacheKeyPrefix + key;
            var cached = _cache.Get(cacheKey);
            if (cached is T hit) return hit;
            if (cached is CachedFailure failure) failure.Rethrow();

            var shared = _inFlight.GetOrAdd(key, k => new Lazy<Task<T>>(() => Start(k, cacheKey, compute))).Value;
            return await WaitAsync(shared, cancellationToken).ConfigureAwait(false);
        }

        private Task<T> Start(string key, string cacheKey, Func<CancellationToken, Task<T>> compute)
        {
            var running = RunAsync(key, cacheKey, compute);

            // Observed here, so a failure nobody is still waiting for is never an unobserved task exception.
            running.ContinueWith(t => { var ignored = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return running;
        }

        private async Task<T> RunAsync(string key, string cacheKey, Func<CancellationToken, Task<T>> compute)
        {
            var deadline = new CancellationTokenSource();
            var abandoned = false;
            try
            {
                deadline.CancelAfter(_deadline);
                var computing = Task.Run(() => compute(deadline.Token));
                await Task.WhenAny(computing, Task.Delay(Timeout.Infinite, deadline.Token)).ConfigureAwait(false);

                if (!computing.IsCompleted)
                {
                    abandoned = true;
                    _ = computing.ContinueWith(t =>
                    {
                        var ignored = t.Exception;
                        deadline.Dispose();
                    }, TaskContinuationOptions.ExecuteSynchronously);
                    throw TimedOut();
                }

                T result;
                try
                {
                    result = await computing.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    throw TimedOut();
                }

                if (result != null)
                {
                    var ttl = _isDegraded != null && _isDegraded(result) ? _failureTtl : _successTtl;
                    _cache.Set(cacheKey, result, DateTimeOffset.UtcNow.Add(ttl));
                }

                return result;
            }
            catch (Exception ex)
            {
                _cache.Set(cacheKey, new CachedFailure(ex), DateTimeOffset.UtcNow.Add(_failureTtl));
                throw;
            }
            finally
            {
                if (!abandoned) deadline.Dispose();

                // After the cache is written, so a caller arriving now finds the result rather than starting again.
                _inFlight.TryRemove(key, out _);
            }
        }

        private TimeoutException TimedOut() =>
            new TimeoutException($"Not finished within {_deadline.TotalSeconds:0} seconds; the work was abandoned.");

        private static async Task<T> WaitAsync(Task<T> shared, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled && !shared.IsCompleted)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false) != shared)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            return await shared.ConfigureAwait(false);
        }

        private sealed class CachedFailure
        {
            private readonly ExceptionDispatchInfo _failure;

            internal CachedFailure(Exception failure)
            {
                _failure = ExceptionDispatchInfo.Capture(failure);
            }

            internal void Rethrow() => _failure.Throw();
        }
    }
}
