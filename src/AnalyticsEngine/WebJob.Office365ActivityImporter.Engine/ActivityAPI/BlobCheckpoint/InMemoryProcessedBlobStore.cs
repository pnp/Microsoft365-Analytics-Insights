using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// In-memory <see cref="IProcessedBlobStore"/> used when no Azure Storage connection string is
    /// configured. Backed by a <b>static</b> map so it persists across import cycles within the same
    /// long-running importer process (each cycle builds a new store instance but they share the map).
    /// It does NOT survive a process restart - the Azure Table implementation is the durable option.
    ///
    /// Entries older than the retention window are purged so the map stays bounded (a blob processed longer
    /// ago than the API's own lookback window can never be re-listed, so it is safe to forget).
    /// </summary>
    public class InMemoryProcessedBlobStore : IProcessedBlobStore
    {
        // Shared across all instances (and therefore all cycles) in this process.
        private static readonly ConcurrentDictionary<string, DateTime> _processed = new ConcurrentDictionary<string, DateTime>();

        private readonly TimeSpan _retention;

        public InMemoryProcessedBlobStore(TimeSpan retention)
        {
            _retention = retention > TimeSpan.Zero ? retention : TimeSpan.FromDays(8);
        }

        public Task<ISet<string>> GetProcessedBlobIdsAsync()
        {
            Purge();
            return Task.FromResult<ISet<string>>(new HashSet<string>(_processed.Keys));
        }

        public Task MarkProcessedAsync(IReadOnlyCollection<string> blobIds)
        {
            if (blobIds != null)
            {
                var now = DateTime.UtcNow;
                foreach (var id in blobIds)
                {
                    if (!string.IsNullOrEmpty(id)) _processed[id] = now;
                }
            }
            return Task.CompletedTask;
        }

        private void Purge()
        {
            var cutoff = DateTime.UtcNow - _retention;
            foreach (var kvp in _processed)
            {
                if (kvp.Value < cutoff) _processed.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>Clears the shared state. For tests / load-test scenarios that need a fresh COLD start.</summary>
        public static void ResetSharedState() => _processed.Clear();
    }
}
