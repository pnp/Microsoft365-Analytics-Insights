using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// Records which Office 365 Management Activity API content blobs (by their content id) have been fully
    /// downloaded AND committed, so the importer can skip re-downloading them on subsequent cycles.
    /// Consecutive import cycles overlap almost entirely (the download window shifts only slightly each
    /// cycle), so without this the ~99% of blobs already processed are re-downloaded and re-parsed every
    /// cycle. It also gives restart-resilience: a blob is only recorded after its data is committed.
    ///
    /// NOTE: this is operational bookkeeping, deliberately NOT stored in the analytics SQL database (which is
    /// reserved for usage-relevant data). Implementations persist to Azure Table storage, or fall back to an
    /// in-memory store that lives for the duration of the (long-running) importer process.
    /// </summary>
    public interface IProcessedBlobStore
    {
        /// <summary>
        /// The set of blob content ids known to have been fully committed (within the retention window).
        /// Used to filter the current cycle's summaries before downloading.
        /// </summary>
        Task<ISet<string>> GetProcessedBlobIdsAsync();

        /// <summary>
        /// Records the given blob content ids as fully committed (stamped with the current time).
        /// Called only after a blob's events have all been persisted.
        /// </summary>
        Task MarkProcessedAsync(IReadOnlyCollection<string> blobIds);
    }
}
