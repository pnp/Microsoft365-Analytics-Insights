using System;
using System.Runtime.Caching;

namespace Web.AnalyticsWeb.Models.Health
{
    /// <summary>
    /// Cache port for the Health sections. Extracted so the cache-hit / cache-expired policy is
    /// assertable and tests don't leak state between cases through the process-wide
    /// <see cref="MemoryCache.Default"/>. See issues #379 / #381.
    /// </summary>
    public interface IHealthCache
    {
        /// <summary>
        /// Returns the cached value for <paramref name="key"/>. A value cached under a different type
        /// counts as a miss (matching the <c>is T</c> check this replaces).
        /// </summary>
        bool TryGet<T>(string key, out T value) where T : class;

        /// <summary>Caches <paramref name="value"/> for <paramref name="ttl"/> from now.</summary>
        void Set<T>(string key, T value, TimeSpan ttl) where T : class;
    }

    /// <summary>
    /// Production <see cref="IHealthCache"/>: the process-wide <see cref="MemoryCache.Default"/> with an
    /// absolute expiry, exactly as the Health sections have always cached.
    /// </summary>
    public sealed class MemoryCacheHealthCache : IHealthCache
    {
        public static readonly MemoryCacheHealthCache Instance = new MemoryCacheHealthCache();

        public bool TryGet<T>(string key, out T value) where T : class
        {
            value = MemoryCache.Default.Get(key) as T;
            return value != null;
        }

        public void Set<T>(string key, T value, TimeSpan ttl) where T : class
        {
            MemoryCache.Default.Set(key, value, new CacheItemPolicy
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.Add(ttl)
            });
        }
    }
}
