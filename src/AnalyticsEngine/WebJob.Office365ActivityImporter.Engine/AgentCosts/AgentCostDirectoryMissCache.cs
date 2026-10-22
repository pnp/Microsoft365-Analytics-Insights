using System;
using System.Collections.Concurrent;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Object ids the directory has said do not exist, remembered so they are not asked about - nor offered for
    /// re-attribution - on every pass.
    /// </summary>
    /// <remarks>
    /// <para>"Not in the directory" is settled for a billing row from a past period: almost always a deleted account.
    /// Recording it only as a count meant the next pass asked again, spending its directory budget on people who can
    /// never be found, while the capped re-attribution pass kept offering the same unresolvable ids ahead of ones that
    /// could be linked.</para>
    /// <para>Held in memory for the life of the process (one instance, created outside the import cycle), so a restart
    /// re-learns the misses at no more than one directory budget per pass. Entries expire after
    /// <see cref="DefaultRecheckAfter"/>, so an account restored from the recycle bin is eventually tried again.
    /// Bounded, so a tenant with a very long tail of deleted users cannot grow it without limit.</para>
    /// </remarks>
    public sealed class AgentCostDirectoryMissCache
    {
        /// <summary>How long a "not in the directory" answer is trusted before the id is tried again.</summary>
        public static readonly TimeSpan DefaultRecheckAfter = TimeSpan.FromDays(7);

        /// <summary>Most ids remembered. Far above a normal tenant's deleted-user tail; the oldest entries make way.</summary>
        public const int MaxEntries = 50000;

        private readonly ConcurrentDictionary<string, DateTime> _settledUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _recheckAfter;

        public AgentCostDirectoryMissCache() : this(() => DateTime.UtcNow, DefaultRecheckAfter)
        {
        }

        /// <param name="utcNow">The clock that decides expiry. Tests pass their own.</param>
        public AgentCostDirectoryMissCache(Func<DateTime> utcNow, TimeSpan recheckAfter)
        {
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _recheckAfter = recheckAfter;
        }

        /// <summary>True when the directory said this id does not exist, recently enough to trust the answer.</summary>
        public bool IsSettledMiss(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return false;

            var key = objectId.Trim();
            if (!_settledUtc.TryGetValue(key, out var settledUtc)) return false;
            if (_utcNow() - settledUtc < _recheckAfter) return true;

            _settledUtc.TryRemove(key, out _);
            return false;
        }

        /// <summary>Records that the directory has no such object.</summary>
        public void RecordMiss(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return;

            if (_settledUtc.Count >= MaxEntries) EvictOldest();
            _settledUtc[objectId.Trim()] = _utcNow();
        }

        /// <summary>How many misses are currently remembered (including any not yet found to have expired).</summary>
        public int Count => _settledUtc.Count;

        private void EvictOldest()
        {
            // Rare: only a tenant with tens of thousands of deleted billing users ever gets here. Dropping the older
            // half at once keeps it from happening on every write.
            var cutoff = _utcNow() - TimeSpan.FromTicks(_recheckAfter.Ticks / 2);
            foreach (var entry in _settledUtc)
            {
                if (entry.Value <= cutoff) _settledUtc.TryRemove(entry.Key, out _);
            }

            if (_settledUtc.Count >= MaxEntries) _settledUtc.Clear();
        }
    }
}
