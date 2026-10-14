using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// Process-local <see cref="IKeyValueStore"/>. Used where no Storage connection string is configured, and by tests.
    /// </summary>
    /// <remarks>
    /// Values live only as long as this instance, so a caller that needs them to survive across import cycles must
    /// hold ONE instance for the life of the process. Thread-safe: the importers read and write concurrently.
    /// </remarks>
    public sealed class InMemoryKeyValueStore : IConditionalKeyValueStore
    {
        /// <summary>How many writes happen between sweeps of expired entries, so a TTL'd cache cannot grow without bound.</summary>
        private const int SweepEveryWrites = 1000;

        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Func<DateTime> _utcNow;
        private int _writesSinceSweep;
        private long _lastVersion;

        public InMemoryKeyValueStore() : this(() => DateTime.UtcNow)
        {
        }

        /// <param name="utcNow">The clock that decides expiry. Tests pass their own.</param>
        public InMemoryKeyValueStore(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public string Description => "in memory (this process only)";

        /// <summary>How many live (unexpired) values are held.</summary>
        public int Count
        {
            get
            {
                var now = _utcNow();
                var count = 0;
                foreach (var entry in _entries.Values)
                {
                    if (!entry.IsExpired(now)) count++;
                }
                return count;
            }
        }

        public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            cancellationToken.ThrowIfCancellationRequested();

            if (_entries.TryGetValue(key, out var entry))
            {
                if (!entry.IsExpired(_utcNow()))
                {
                    return Task.FromResult(entry.Value);
                }

                _entries.TryRemove(key, out _);
            }

            return Task.FromResult<string>(null);
        }

        public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            cancellationToken.ThrowIfCancellationRequested();

            if (value == null)
            {
                _entries.TryRemove(key, out _);
                return Task.CompletedTask;
            }

            DateTime? expiresUtc = null;
            if (timeToLive.HasValue)
            {
                expiresUtc = _utcNow() + timeToLive.Value;
            }

            _entries[key] = new Entry(value, expiresUtc, NextVersion());

            if (Interlocked.Increment(ref _writesSinceSweep) >= SweepEveryWrites)
            {
                Interlocked.Exchange(ref _writesSinceSweep, 0);
                SweepExpired();
            }

            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            cancellationToken.ThrowIfCancellationRequested();

            var existed = _entries.TryRemove(key, out var entry) && !entry.IsExpired(_utcNow());
            return Task.FromResult(existed);
        }

        public Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_entries.TryGetValue(key, out var entry)) return Task.FromResult(new VersionedValue(null, null));

            // As in Azure Tables, an expired entry reads as missing but keeps its version, so a conditional write can replace it.
            return Task.FromResult(new VersionedValue(entry.IsExpired(_utcNow()) ? null : entry.Value, entry.Version));
        }

        public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            if (value == null) throw new ArgumentNullException(nameof(value));
            cancellationToken.ThrowIfCancellationRequested();

            var replacement = new Entry(value, null, NextVersion());
            if (expectedVersionToken == null)
            {
                return Task.FromResult(_entries.TryAdd(key, replacement));
            }

            // TryUpdate compares the entry by reference, so it succeeds only if nothing replaced it since it was read.
            return Task.FromResult(
                _entries.TryGetValue(key, out var current)
                && string.Equals(current.Version, expectedVersionToken, StringComparison.Ordinal)
                && _entries.TryUpdate(key, replacement, current));
        }

        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            return await GetStringAsync(key, cancellationToken).ConfigureAwait(false) != null;
        }

        private void SweepExpired()
        {
            var now = _utcNow();
            foreach (var pair in _entries)
            {
                if (pair.Value.IsExpired(now))
                {
                    _entries.TryRemove(pair.Key, out _);
                }
            }
        }

        private string NextVersion() => Interlocked.Increment(ref _lastVersion).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A key is required.", nameof(key));
        }

        private sealed class Entry
        {
            public Entry(string value, DateTime? expiresUtc, string version)
            {
                Value = value;
                ExpiresUtc = expiresUtc;
                Version = version;
            }

            public string Version { get; }

            public string Value { get; }

            public DateTime? ExpiresUtc { get; }

            public bool IsExpired(DateTime nowUtc) => ExpiresUtc.HasValue && ExpiresUtc.Value <= nowUtc;
        }
    }
}
