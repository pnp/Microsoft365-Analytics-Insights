using Microsoft.Extensions.Logging;
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
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, int> _remaining = new ConcurrentDictionary<string, int>();
        private long _markedDone;

        public BlobCommitTracker(IProcessedBlobStore store, ILogger logger)
        {
            _store = store;
            _logger = logger;
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
            await _store.MarkProcessedAsync(blobIds);
            Interlocked.Add(ref _markedDone, blobIds.Count);
        }
    }
}
