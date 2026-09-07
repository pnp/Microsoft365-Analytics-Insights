using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LicenceActivity
{
    public sealed class LicenceActivityReadModelExpiredException : Exception
    {
        public LicenceActivityReadModelExpiredException()
            : base("The source snapshot expired or was evicted. Refresh the licence report.") { }
    }

    public sealed class LicenceActivityReadModelBusyException : Exception
    {
        public LicenceActivityReadModelBusyException()
            : base("Another licence report snapshot is loading. Retry in a few seconds.") { }
    }

    public sealed class LicenceActivityReadModelLease
    {
        internal LicenceActivityReadModelLease(LicenceActivityReadModel model, DateTime expiresUtc)
        {
            Model = model;
            ExpiresUtc = expiresUtc;
        }

        public LicenceActivityReadModel Model { get; }
        public DateTime ExpiresUtc { get; }
    }

    /// <summary>
    /// Holds unique-user facts, compact memberships and ranking IDs, not a user-by-SKU object graph.
    /// Completed HTTP snapshots retain only the model ID, so eviction also releases the large model.
    /// </summary>
    public sealed class LicenceActivityReadModelCache
    {
        public const int DefaultCapacity = 2;
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _capacity;
        private readonly TimeSpan _lifetime;
        private readonly TimeSpan _loadTimeout;
        private readonly Func<DateTime> _utcNow;
        private int _activeLoads;

        public LicenceActivityReadModelCache(
            int capacity = DefaultCapacity, TimeSpan? lifetime = null,
            Func<DateTime> utcNow = null, TimeSpan? loadTimeout = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _lifetime = lifetime ?? DefaultLifetime;
            _loadTimeout = loadTimeout ?? TimeSpan.FromSeconds(30);
            if (_lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (_loadTimeout <= TimeSpan.Zero || _loadTimeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(loadTimeout));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public Task<LicenceActivityReadModelLease> GetAsync(
            string scope, string key, Func<CancellationToken, Task<LicenceActivityReadModel>> load,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(scope)) throw new ArgumentException("A cache scope is required.", nameof(scope));
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A cache key is required.", nameof(key));
            if (load == null) throw new ArgumentNullException(nameof(load));
            cancellationToken.ThrowIfCancellationRequested();
            var compoundKey = scope + "\n" + key;
            Entry entry;
            var start = false;
            lock (_gate)
            {
                Prune();
                if (!_entries.TryGetValue(compoundKey, out entry))
                {
                    // One cold model fans out to five bounded workload queries. Do not multiply
                    // that SQL pressure when several administrators select different ranges.
                    if (_activeLoads != 0) throw new LicenceActivityReadModelBusyException();
                    if (_entries.Count >= _capacity)
                    {
                        var oldest = _entries.Values.OrderBy(e => e.Value.ExpiresUtc).First();
                        _entries.Remove(oldest.Key);
                    }
                    entry = new Entry(compoundKey, scope, _loadTimeout);
                    _entries.Add(compoundKey, entry);
                    _activeLoads++;
                    start = true;
                }
            }
            if (start)
                _ = Task.Run(() => LoadAsync(entry, load));
            return WaitForCallerAsync(entry.Completion.Task, cancellationToken);
        }

        public LicenceActivityReadModelLease Find(string scope, string modelId)
        {
            lock (_gate)
            {
                Prune();
                var entry = _entries.Values.FirstOrDefault(e =>
                    e.Scope == scope && e.Value != null && e.Value.Model.Id == modelId);
                return entry?.Value ?? throw new LicenceActivityReadModelExpiredException();
            }
        }

        public bool Contains(string scope, string modelId)
        {
            lock (_gate)
            {
                Prune();
                return _entries.Values.Any(e =>
                    e.Scope == scope && e.Value != null && e.Value.Model.Id == modelId);
            }
        }

        private async Task LoadAsync(Entry entry, Func<CancellationToken, Task<LicenceActivityReadModel>> load)
        {
            LicenceActivityReadModelLease value = null;
            Exception failure = null;
            try
            {
                entry.Lifetime.Token.ThrowIfCancellationRequested();
                var model = await load(entry.Lifetime.Token).ConfigureAwait(false);
                entry.Lifetime.Token.ThrowIfCancellationRequested();
                if (model == null) throw new InvalidOperationException("The licence read-model loader returned no data.");
                value = new LicenceActivityReadModelLease(model, _utcNow().Add(_lifetime));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                entry.Lifetime.Dispose();
            }
            lock (_gate)
            {
                // Release before publication: a resumed caller can immediately admit another range.
                // The loader has already cancelled/disposed all SQL work before reaching this point.
                _activeLoads--;
                if (failure == null)
                {
                    entry.Value = value;
                    entry.Completion.TrySetResult(value);
                }
                else
                {
                    _entries.Remove(entry.Key);
                    entry.Completion.TrySetException(failure);
                    _ = entry.Completion.Task.Exception;
                }
            }
        }

        private static async Task<T> WaitForCallerAsync<T>(Task<T> shared, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cancellationToken.CanBeCanceled) return await shared.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false) != shared)
                    throw new OperationCanceledException(cancellationToken);
                return await shared.ConfigureAwait(false);
            }
        }

        private void Prune()
        {
            var now = _utcNow();
            foreach (var key in _entries.Where(e => e.Value.Value != null && e.Value.Value.ExpiresUtc <= now)
                .Select(e => e.Key).ToArray())
                _entries.Remove(key);
        }

        private sealed class Entry
        {
            internal Entry(string key, string scope, TimeSpan timeout)
            {
                Key = key;
                Scope = scope;
                Lifetime = new CancellationTokenSource(timeout);
            }
            internal string Key { get; }
            internal string Scope { get; }
            internal CancellationTokenSource Lifetime { get; }
            internal LicenceActivityReadModelLease Value { get; set; }
            internal TaskCompletionSource<LicenceActivityReadModelLease> Completion { get; } =
                new TaskCompletionSource<LicenceActivityReadModelLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
