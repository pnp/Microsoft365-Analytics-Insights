extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using System;
using System.Collections.Generic;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-process <see cref="IHealthCache"/> for tests: no <c>MemoryCache.Default</c>, so no state leaks
    /// between test cases, and expiry is driven by an explicit clock instead of wall time (issue #379).
    /// </summary>
    public class InMemoryHealthCache : IHealthCache
    {
        private class Entry
        {
            public object Value;
            public DateTime ExpiresUtc;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>"Now" for expiry purposes. Advance it to make cached entries expire.</summary>
        public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public int HitCount { get; private set; }
        public int MissCount { get; private set; }

        /// <summary>Every key that has been written, in order (a key written twice appears twice).</summary>
        public List<string> Writes { get; } = new List<string>();

        /// <summary>Moves the clock forward, expiring anything whose TTL has now elapsed.</summary>
        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

        public bool TryGet<T>(string key, out T value) where T : class
        {
            value = null;
            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > UtcNow)
            {
                value = entry.Value as T;
            }

            if (value != null) HitCount++; else MissCount++;
            return value != null;
        }

        public void Set<T>(string key, T value, TimeSpan ttl) where T : class
        {
            Writes.Add(key);
            _entries[key] = new Entry { Value = value, ExpiresUtc = UtcNow.Add(ttl) };
        }
    }
}
