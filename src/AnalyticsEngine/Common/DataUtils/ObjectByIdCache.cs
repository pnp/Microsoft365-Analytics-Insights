using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace DataUtils
{
    /// <summary>
    /// An in-memory cache of objects by id, filled on a miss from <see cref="Load"/> and, failing that, an optional
    /// "create" callback.
    /// </summary>
    /// <remarks>
    /// Lookups are per key. A hit is answered without taking any lock; callers asking for the same id share one load (and
    /// one create); different ids load at the same time. The cache used to hold a single lock across every load, so a
    /// hit queued behind somebody else's slow - e.g. throttled - Graph call and parallel workers took turns. A load that
    /// fails, or finds nothing, is not remembered: the next caller tries again.
    ///
    /// Concurrency is only ever as wide as the callers make it. The database-backed caches share one EF context, which is
    /// not thread-safe, and each is used from a single sequential flow, exactly as before.
    /// </remarks>
    public abstract class ObjectByIdCache<T> where T : class
    {
        private static readonly StringComparer _sqlStringComparer = CultureInfo.GetCultureInfo(1033).CompareInfo
                            .GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth);
        private readonly ConcurrentDictionary<string, T> _objectCache = new ConcurrentDictionary<string, T>(_sqlStringComparer);

        // Lazy, so that of the factories GetOrAdd may run in a race, only the one stored is ever started.
        private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _loadsInFlight = new ConcurrentDictionary<string, Lazy<Task<T>>>(_sqlStringComparer);
        private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _createsInFlight = new ConcurrentDictionary<string, Lazy<Task<T>>>(_sqlStringComparer);

        /// <summary>
        /// Load resource either from cache or external load.
        /// </summary>
        /// <param name="loadMethodFoundNoResultCallback">If nothing in cache found or externally, callback with result</param>
        /// <exception cref="ArgumentException">Nothing found by <paramref name="id"/>, and no callback produced one.</exception>
        public async Task<T> GetResource(string id, Func<Task<T>> loadMethodFoundNoResultCallback)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"'{nameof(id)}' cannot be null or empty.", nameof(id));
            }

            if (_objectCache.TryGetValue(id, out var cached))
            {
                return cached;
            }

            T obj = await SingleFlight(_loadsInFlight, id, () => Load(id));

            // No object from load? Invoke callback
            if (obj == null && loadMethodFoundNoResultCallback != null)
            {
                obj = await SingleFlight(_createsInFlight, id, loadMethodFoundNoResultCallback);
            }

            if (obj == null)
            {
                throw new ArgumentException($"No resources of type {typeof(T).Name} in cache or external load found by ID {id}");
            }
            return obj;
        }

        /// <summary>
        /// Runs <paramref name="produce"/> once for everyone asking for <paramref name="id"/> at the same time, and caches a
        /// non-null result. The in-flight entry is dropped as soon as it finishes, so a failure is retried by the next caller.
        /// </summary>
        private async Task<T> SingleFlight(ConcurrentDictionary<string, Lazy<Task<T>>> inFlight, string id, Func<Task<T>> produce)
        {
            var flight = inFlight.GetOrAdd(id, _ => new Lazy<Task<T>>(() => ProduceAndCache(id, produce)));
            try
            {
                return await flight.Value;
            }
            finally
            {
                // Removes this flight only - never a newer one for the same id.
                ((ICollection<KeyValuePair<string, Lazy<Task<T>>>>)inFlight).Remove(new KeyValuePair<string, Lazy<Task<T>>>(id, flight));
            }
        }

        private async Task<T> ProduceAndCache(string id, Func<Task<T>> produce)
        {
            // An earlier flight for this id may have finished between the caller's cache check and this one starting.
            if (_objectCache.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var obj = await produce();
            return obj == null ? null : _objectCache.GetOrAdd(id, obj);
        }

        public async Task<T> GetResource(string id)
        {
            return await GetResource(id, null);
        }

        /// <summary>A cache hit only: never loads.</summary>
        public bool TryGetCached(string id, out T value)
        {
            value = null;
            return !string.IsNullOrEmpty(id) && _objectCache.TryGetValue(id, out value);
        }

        /// <summary>Caches an object the caller loaded itself, keeping one already cached under the same id.</summary>
        public T AddOrGetCached(string id, T value)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException($"'{nameof(id)}' cannot be null or empty.", nameof(id));
            if (value == null) throw new ArgumentNullException(nameof(value));
            return _objectCache.GetOrAdd(id, value);
        }

        public async Task<T> GetResourceOrNullIfNotExists(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException($"'{nameof(id)}' cannot be null or empty.", nameof(id));

            T r = null;
            try
            {
                r = await GetResource(id);
            }
            catch (ArgumentException)
            {
                // This is ok - we couldn't find an object in cache or externally by this ID (hence this exception), so we return null
            }
            return r;
        }

        public abstract Task<T> Load(string id);
    }
}
