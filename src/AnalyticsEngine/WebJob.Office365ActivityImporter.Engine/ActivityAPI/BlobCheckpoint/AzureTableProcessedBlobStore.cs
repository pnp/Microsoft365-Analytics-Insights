using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// Durable <see cref="IProcessedBlobStore"/> backed by Azure Table storage, so the processed-blob
    /// checkpoint survives process restarts. Deliberately NOT the analytics SQL database (which is reserved
    /// for usage-relevant data). One partition holds all entries; the blob content id is hashed to a valid
    /// RowKey and the original id kept in a property. Entries older than the retention window are purged on
    /// read to keep the table bounded (a blob that old can never be re-listed by the API).
    /// </summary>
    public class AzureTableProcessedBlobStore : IProcessedBlobStore
    {
        private const string PartitionKeyValue = "auditblob";
        private const string BlobIdProperty = "BlobId";
        private const string DefaultTableName = "AuditImporterProcessedBlobs";
        private const int MaxTransaction = 100; // Azure Table batch limit (single partition).

        private readonly TableClient _table;
        private readonly TimeSpan _retention;
        private readonly ILogger _logger;

        public AzureTableProcessedBlobStore(string storageConnectionString, TimeSpan retention, ILogger logger, string tableName = DefaultTableName)
        {
            _retention = retention > TimeSpan.Zero ? retention : TimeSpan.FromDays(8);
            _logger = logger;
            _table = new TableClient(storageConnectionString, tableName);
            _table.CreateIfNotExists(); // throws if the connection string is unusable -> factory falls back.
        }

        public async Task<ISet<string>> GetProcessedBlobIdsAsync()
        {
            var cutoff = DateTimeOffset.UtcNow - _retention;
            var result = new HashSet<string>();
            var expired = new List<TableEntity>();

            // Single-partition scan; Timestamp is a system property maintained by the service.
            foreach (var e in _table.Query<TableEntity>(x => x.PartitionKey == PartitionKeyValue))
            {
                var blobId = e.GetString(BlobIdProperty);
                if (string.IsNullOrEmpty(blobId)) continue;
                if (e.Timestamp.HasValue && e.Timestamp.Value < cutoff) expired.Add(e);
                else result.Add(blobId);
            }

            if (expired.Count > 0) await DeleteBatchAsync(expired);
            return result;
        }

        public async Task MarkProcessedAsync(IReadOnlyCollection<string> blobIds)
        {
            if (blobIds == null || blobIds.Count == 0) return;

            var entities = blobIds
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .Select(id => new TableEntity(PartitionKeyValue, RowKeyFor(id)) { { BlobIdProperty, id } })
                .ToList();

            foreach (var chunk in Chunk(entities, MaxTransaction))
            {
                var actions = chunk.Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e));
                await _table.SubmitTransactionAsync(actions);
            }
        }

        private async Task DeleteBatchAsync(List<TableEntity> entities)
        {
            try
            {
                foreach (var chunk in Chunk(entities, MaxTransaction))
                {
                    var actions = chunk.Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e));
                    await _table.SubmitTransactionAsync(actions);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Blob checkpoint: failed to purge expired Azure Table entries (non-fatal).");
            }
        }

        // Table RowKey can't contain / \ # ? or control chars and is length-limited; hash the (possibly
        // URL-like) blob id to a fixed, always-valid key. The original id is stored in the BlobId property.
        private static string RowKeyFor(string blobId)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(blobId));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }
}
