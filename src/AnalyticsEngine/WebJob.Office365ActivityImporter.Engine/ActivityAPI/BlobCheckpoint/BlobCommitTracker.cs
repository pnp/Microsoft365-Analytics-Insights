using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// Tracks, per content blob, how many of its events are still uncommitted, and records a blob in the
    /// <see cref="IProcessedBlobStore"/> only once ALL its events have been committed. A blob's events can
    /// span several commit batches (batches mix events from many blobs), so we register the blob's event
    /// count when it is loaded and decrement as batches commit; when the count reaches zero the blob is
    /// marked processed. This is what makes the checkpoint safe: a blob is never recorded (and therefore
    /// never skipped next cycle) until its data is durably saved.
    /// </summary>
    public class BlobCommitTracker
    {
        private readonly IProcessedBlobStore _store;
        private readonly IActivityMetadataRecoveryStore _metadataRecoveryStore;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, int> _remaining = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, byte> _metadataRecoveryPending;
        private readonly bool _markProcessed;
        private long _markedDone;

        public BlobCommitTracker(IProcessedBlobStore store, ILogger logger)
            : this(store, logger, markProcessed: true)
        {
        }

        internal BlobCommitTracker(IProcessedBlobStore store, ILogger logger, bool markProcessed)
            : this(store, logger, store as IActivityMetadataRecoveryStore, null, markProcessed)
        {
        }

        internal BlobCommitTracker(IProcessedBlobStore store, ILogger logger,
            IActivityMetadataRecoveryStore metadataRecoveryStore,
            IEnumerable<string> metadataRecoveryPending,
            bool markProcessed)
        {
            _store = store;
            _metadataRecoveryStore = metadataRecoveryStore;
            _logger = logger;
            _metadataRecoveryPending = new ConcurrentDictionary<string, byte>(
                (metadataRecoveryPending ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
                    .Select(id => new KeyValuePair<string, byte>(id, 0)));
            _markProcessed = markProcessed;
        }

        public long MarkedDone => Interlocked.Read(ref _markedDone);

        /// <summary>
        /// Register a freshly-loaded blob's event count. Called before the blob's events are queued for
        /// saving, so a commit can never decrement a blob that wasn't registered first. A zero-event blob is
        /// marked processed immediately.
        /// </summary>
        public async Task RegisterAsync(string blobId, int eventCount)
        {
            if (string.IsNullOrEmpty(blobId)) return;

            int value = _remaining.AddOrUpdate(blobId, eventCount, (k, v) => v + eventCount);
            if (value <= 0 && _remaining.TryRemove(blobId, out _))
            {
                await MarkAsync(new[] { blobId });
            }
        }

        /// <summary>
        /// Account for the events in a just-committed batch, and record any blob whose events are now all
        /// committed.
        /// </summary>
        public async Task OnBatchCommittedAsync(IEnumerable<AbstractAuditLogContent> committedEvents)
        {
            List<string> done = null;
            foreach (var grp in committedEvents
                         .Where(e => !string.IsNullOrEmpty(e.SourceContentId))
                         .GroupBy(e => e.SourceContentId))
            {
                int dec = grp.Count();
                int value = _remaining.AddOrUpdate(grp.Key, -dec, (k, v) => v - dec);
                if (value <= 0 && _remaining.TryRemove(grp.Key, out _))
                {
                    (done ?? (done = new List<string>())).Add(grp.Key);
                }
            }

            if (done != null) await MarkAsync(done);
        }

        private async Task MarkAsync(IReadOnlyCollection<string> blobIds)
        {
            if (!_markProcessed) return;

            // Best-effort: a checkpoint write failure must not fail the import. If we fail to record a blob,
            // it is simply re-processed next cycle (its events dedup against audit_events), which is safe.
            try
            {
                await _store.MarkProcessedAsync(blobIds);
                Interlocked.Add(ref _markedDone, blobIds.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, $"Blob checkpoint: failed to record {blobIds.Count} processed blob(s); they will be re-processed next cycle.");
                return;
            }

            var recovered = blobIds.Where(id => _metadataRecoveryPending.TryRemove(id, out _)).ToList();
            if (recovered.Count == 0 || _metadataRecoveryStore == null) return;

            try
            {
                await _metadataRecoveryStore.ClearMetadataRecoveryPendingAsync(recovered);
            }
            catch (Exception ex)
            {
                // The processed marker is already durable, so a stale pending marker is harmless: the
                // processed id filters the blob before the recovery marker is consulted next cycle.
                _logger?.LogWarning(ex,
                    $"Blob checkpoint: failed to clear {recovered.Count} metadata-recovery marker(s) after recording the blobs as processed; stale markers are harmless.");
            }
        }
    }
}
