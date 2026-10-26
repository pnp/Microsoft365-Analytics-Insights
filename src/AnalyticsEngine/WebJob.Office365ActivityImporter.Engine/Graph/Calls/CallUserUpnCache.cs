using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Entra object id to user principal name for the people on Teams calls, shared by every queue worker for the life of
    /// the process.
    /// </summary>
    /// <remarks>
    /// A call names each person by id only, and the same people are on call after call - above all the organisers - so
    /// looking each one up for every call put a Graph request per person per call on the import path. Here a person is
    /// looked up once (with <c>$select=userPrincipalName</c>, as nothing else is used) and remembered:
    /// <list type="bullet">
    /// <item>Entries expire (<see cref="PositiveTimeToLive"/>, 24 hours by default), so a renamed UPN is picked up.
    /// An id Entra ID says doesn't exist is remembered for the shorter <see cref="NegativeTimeToLive"/>.</item>
    /// <item>Concurrent workers asking for the same id share one request, and at most <see cref="MaxConcurrentLookups"/>
    /// requests are in flight across the process.</item>
    /// <item>It is bounded: past <see cref="MaxEntries"/> the expired entries go, then the oldest, so a 200,000-user tenant
    /// cannot grow it without limit.</item>
    /// <item>A failed lookup (throttling, an outage, permissions) is never remembered, and throws to the caller.</item>
    /// </list>
    /// </remarks>
    public sealed class CallUserUpnCache
    {
        public static readonly TimeSpan DefaultPositiveTimeToLive = TimeSpan.FromHours(24);
        public static readonly TimeSpan DefaultNegativeTimeToLive = TimeSpan.FromHours(1);
        public const int DefaultMaxEntries = 100000;
        public const int DefaultMaxConcurrentLookups = 16;

        private readonly Func<string, Task<string>> _fetchUpn;
        private readonly IClock _clock;
        private readonly SemaphoreSlim _lookupSlots;
        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private int _trimming;
        private long _hits;
        private long _lookups;

        /// <param name="fetchUpn">Looks one id up in Entra ID: its UPN, or null when there is no such user. Throws on any other failure.</param>
        public CallUserUpnCache(Func<string, Task<string>> fetchUpn, IClock clock = null, TimeSpan? positiveTimeToLive = null, TimeSpan? negativeTimeToLive = null,
            int maxEntries = DefaultMaxEntries, int maxConcurrentLookups = DefaultMaxConcurrentLookups)
        {
            if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            if (maxConcurrentLookups < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentLookups));

            _fetchUpn = fetchUpn ?? throw new ArgumentNullException(nameof(fetchUpn));
            _clock = clock ?? SystemClock.Instance;
            PositiveTimeToLive = positiveTimeToLive ?? DefaultPositiveTimeToLive;
            NegativeTimeToLive = negativeTimeToLive ?? DefaultNegativeTimeToLive;
            MaxEntries = maxEntries;
            MaxConcurrentLookups = maxConcurrentLookups;
            _lookupSlots = new SemaphoreSlim(maxConcurrentLookups, maxConcurrentLookups);
        }

        /// <summary>The cache for one Graph client: <c>GET /users/{id}?$select=userPrincipalName</c>.</summary>
        public static CallUserUpnCache ForGraph(GraphServiceClient graphClient)
        {
            if (graphClient == null) throw new ArgumentNullException(nameof(graphClient));
            return new CallUserUpnCache(async id =>
            {
                try
                {
                    var user = await graphClient.Users[id].GetAsync(request => request.QueryParameters.Select = new[] { "userPrincipalName" });
                    return user?.UserPrincipalName;
                }
                catch (ODataError ex) when (UserLookupCache.IsUserNotFound(ex))
                {
                    return null;
                }
            });
        }

        public TimeSpan PositiveTimeToLive { get; }
        public TimeSpan NegativeTimeToLive { get; }
        public int MaxEntries { get; }
        public int MaxConcurrentLookups { get; }

        /// <summary>Entries held now, expired ones included until they are next asked for or trimmed.</summary>
        public int Count => _entries.Count;

        /// <summary>Requests answered from the cache, or by sharing another worker's in-flight request.</summary>
        public long Hits => Interlocked.Read(ref _hits);

        /// <summary>Requests sent to Entra ID.</summary>
        public long Lookups => Interlocked.Read(ref _lookups);

        /// <summary>The user's UPN, or null when Entra ID has no such user. Throws when the look-up itself fails.</summary>
        public async Task<string> GetUpnAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return null;
            }

            while (true)
            {
                var added = false;
                if (_entries.TryGetValue(userId, out var entry))
                {
                    if (entry.IsExpired(_clock.UtcNow))
                    {
                        ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(userId, entry));
                        continue;
                    }
                    Interlocked.Increment(ref _hits);
                }
                else
                {
                    entry = new Entry();
                    if (_entries.TryAdd(userId, entry))
                    {
                        added = true;
                    }
                    else
                    {
                        continue;   // Another worker added it first: share theirs.
                    }
                }

                try
                {
                    var upn = await entry.Result.GetOrStart(() => LookUpAsync(userId, entry)).ConfigureAwait(false);
                    if (added)
                    {
                        TrimIfOverCapacity();
                    }
                    return upn;
                }
                catch
                {
                    // Not remembered: the next caller looks again.
                    ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(userId, entry));
                    throw;
                }
            }
        }

        private async Task<string> LookUpAsync(string userId, Entry entry)
        {
            await _lookupSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _lookups);
                var upn = await _fetchUpn(userId).ConfigureAwait(false);
                entry.SetExpiry(_clock.UtcNow + (string.IsNullOrEmpty(upn) ? NegativeTimeToLive : PositiveTimeToLive));
                return string.IsNullOrEmpty(upn) ? null : upn;
            }
            finally
            {
                _lookupSlots.Release();
            }
        }

        private void TrimIfOverCapacity()
        {
            if (_entries.Count <= MaxEntries || Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var now = _clock.UtcNow;
                var collection = (ICollection<KeyValuePair<string, Entry>>)_entries;
                foreach (var expired in _entries.Where(e => e.Value.IsExpired(now)).ToList())
                {
                    collection.Remove(expired);
                }

                // Still over: drop the entries that expire soonest, down to 90% so the next insert doesn't trim again.
                var target = MaxEntries - MaxEntries / 10;
                if (_entries.Count > target)
                {
                    foreach (var oldest in _entries.Where(e => e.Value.IsCompleted).OrderBy(e => e.Value.ExpiresUtc).Take(_entries.Count - target).ToList())
                    {
                        collection.Remove(oldest);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _trimming, 0);
            }
        }

        private sealed class Entry
        {
            // Until the look-up finishes the entry never expires, so every concurrent caller shares its one request.
            private long _expiresTicks = DateTime.MaxValue.Ticks;
            public readonly SharedTask Result = new SharedTask();

            public DateTime ExpiresUtc => new DateTime(Interlocked.Read(ref _expiresTicks), DateTimeKind.Utc);
            public void SetExpiry(DateTime expiresUtc) => Interlocked.Exchange(ref _expiresTicks, expiresUtc.Ticks);
            public bool IsCompleted => Result.IsCompleted;
            public bool IsExpired(DateTime nowUtc) => IsCompleted && ExpiresUtc <= nowUtc;
        }

        /// <summary>Starts a task once, however many callers ask, and hands every one of them the same task.</summary>
        private sealed class SharedTask
        {
            private readonly object _lock = new object();
            private Task<string> _task;

            public bool IsCompleted
            {
                get { lock (_lock) { return _task != null && _task.IsCompleted; } }
            }

            public Task<string> GetOrStart(Func<Task<string>> start)
            {
                lock (_lock)
                {
                    return _task ?? (_task = start());
                }
            }
        }
    }
}
