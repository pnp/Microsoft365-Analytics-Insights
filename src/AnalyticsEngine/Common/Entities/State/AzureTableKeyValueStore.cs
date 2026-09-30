using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// <see cref="IKeyValueStore"/> over one partition of an Azure Table. Each key is one entity (row), so reads and
    /// writes are single point operations - one round trip each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entity layout, chosen so an operator can read and clear state in Azure Storage Explorer:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>RowKey</c> - the key itself when it is a legal row key, otherwise an escaped form (see
    /// <see cref="ToRowKey"/>). <c>Key</c> always holds the key exactly as the code uses it.</description></item>
    /// <item><description><c>Value</c> - the value as plain text when it fits one string property (the common case:
    /// timestamps and delta tokens).</description></item>
    /// <item><description><c>ValueGzipChunks</c> + <c>ValueGzip0..n</c> - larger values (e.g. the sent-email no-mailbox
    /// list on a big tenant), gzip-compressed and split across binary properties, because a string property is capped at
    /// 64 KiB. Still one entity, so a write is atomic and a reader never sees half of one.</description></item>
    /// <item><description><c>ExpiresUtc</c> - only for values stored with a time-to-live. Azure Tables has no native
    /// expiry, so an expired value reads as missing and is deleted when met, and <see cref="PurgeExpiredAsync"/> removes
    /// the ones nobody reads again.</description></item>
    /// </list>
    /// </remarks>
    public sealed class AzureTableKeyValueStore : IKeyValueStore
    {
        internal const string KeyProperty = "Key";
        internal const string ValueProperty = "Value";
        internal const string CompressedChunkCountProperty = "ValueGzipChunks";
        internal const string CompressedChunkPropertyPrefix = "ValueGzip";
        internal const string ExpiresProperty = "ExpiresUtc";

        /// <summary>A string property holds at most 64 KiB of UTF-16, i.e. 32,768 characters. Kept below that on purpose.</summary>
        internal const int MaxPlainValueChars = 32000;

        /// <summary>A binary property holds at most 64 KiB.</summary>
        internal const int CompressedChunkBytes = 64000;

        /// <summary>An entity holds at most 1 MiB in total, so the compressed value is capped comfortably below that.</summary>
        internal const int MaxCompressedChunks = 14;

        /// <summary>Longer (escaped) keys are stored under a hash; a row key may be at most 1 KiB.</summary>
        internal const int MaxReadableRowKeyLength = 255;

        /// <summary>
        /// Prefix of a hashed row key. <see cref="ToRowKey"/> always escapes '%' as "%25", so "%%" can never begin an
        /// escaped key and the two forms cannot collide.
        /// </summary>
        internal const string HashedRowKeyPrefix = "%%";

        /// <summary>Azure Tables accepts at most 100 operations in one entity-group transaction.</summary>
        private const int MaxTransactionSize = 100;

        private readonly Func<CancellationToken, Task<TableClient>> _getTable;
        private readonly string _tableName;
        private readonly string _partitionKey;
        private readonly Func<DateTimeOffset> _utcNow;

        /// <summary>A store over an already-opened table. Tests use this with an in-memory <see cref="TableClient"/>.</summary>
        public AzureTableKeyValueStore(TableClient table, string partitionKey, Func<DateTimeOffset> utcNow = null)
            : this(AlreadyOpened(table), table?.Name, partitionKey, utcNow)
        {
        }

        /// <param name="getTable">Opens (and creates, first time) the table. Called per operation; expected to cache.</param>
        internal AzureTableKeyValueStore(Func<CancellationToken, Task<TableClient>> getTable, string tableName, string partitionKey,
            Func<DateTimeOffset> utcNow = null)
        {
            if (string.IsNullOrEmpty(partitionKey)) throw new ArgumentException("A partition key is required.", nameof(partitionKey));

            _getTable = getTable ?? throw new ArgumentNullException(nameof(getTable));
            _tableName = tableName;
            _partitionKey = partitionKey;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        private static Func<CancellationToken, Task<TableClient>> AlreadyOpened(TableClient table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            var opened = Task.FromResult(table);
            return _ => opened;
        }

        public string PartitionKey => _partitionKey;

        public string Description => $"Azure Table '{_tableName}', partition '{_partitionKey}'";

        public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
        {
            var rowKey = ToRowKey(key);
            var table = await _getTable(cancellationToken).ConfigureAwait(false);

            var response = await table.GetEntityIfExistsAsync<TableEntity>(_partitionKey, rowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!response.HasValue)
            {
                return null;
            }

            var entity = response.Value;
            if (IsExpired(entity))
            {
                await TryDeleteExpiredAsync(table, entity, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return DecodeValue(entity, key);
        }

        public async Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            if (value == null)
            {
                await DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                return;
            }

            var entity = new TableEntity(_partitionKey, ToRowKey(key))
            {
                { KeyProperty, key },
            };
            EncodeValue(entity, key, value);
            if (timeToLive.HasValue)
            {
                entity[ExpiresProperty] = _utcNow() + timeToLive.Value;
            }

            var table = await _getTable(cancellationToken).ConfigureAwait(false);

            // Replace, not merge: a value that shrinks from compressed chunks back to plain text must not leave the old
            // chunks behind, and a value written without a TTL must lose any previous expiry.
            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            var rowKey = ToRowKey(key);
            var table = await _getTable(cancellationToken).ConfigureAwait(false);

            try
            {
                var response = await table.DeleteEntityAsync(_partitionKey, rowKey, ETag.All, cancellationToken).ConfigureAwait(false);

                // The SDK answers a delete of a missing entity with the service's 404 rather than an exception.
                return response == null || response.Status != 404;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            var rowKey = ToRowKey(key);
            var table = await _getTable(cancellationToken).ConfigureAwait(false);

            // Select only the expiry: the value itself (a delta token, a refresh token) is never read to answer this.
            var response = await table.GetEntityIfExistsAsync<TableEntity>(_partitionKey, rowKey, new[] { ExpiresProperty }, cancellationToken)
                .ConfigureAwait(false);

            return response.HasValue && !IsExpired(response.Value);
        }

        /// <summary>
        /// Deletes up to <paramref name="maxToDelete"/> values in this partition whose time-to-live has passed. Values
        /// written without a TTL are never touched, and neither is a value rewritten after this purge found it expired.
        /// Returns how many were deleted.
        /// </summary>
        public async Task<int> PurgeExpiredAsync(int maxToDelete = 10000, CancellationToken cancellationToken = default)
        {
            if (maxToDelete <= 0) return 0;

            var expired = await FindExpiredAsync(maxToDelete, cancellationToken).ConfigureAwait(false);
            return await DeleteUnlessChangedAsync(expired, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Up to <paramref name="maxToFind"/> rows in this partition whose time-to-live has passed, with their ETags.</summary>
        internal async Task<List<TableEntity>> FindExpiredAsync(int maxToFind, CancellationToken cancellationToken)
        {
            var table = await _getTable(cancellationToken).ConfigureAwait(false);

            // The property name is literal text: CreateQueryFilter quotes every interpolated hole as an OData VALUE.
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {_partitionKey} and ExpiresUtc le {_utcNow()}");

            var expired = new List<TableEntity>();
            var pages = table.QueryAsync<TableEntity>(filter, maxPerPage: 1000, select: new[] { "PartitionKey", "RowKey" },
                cancellationToken: cancellationToken).AsPages().GetAsyncEnumerator(cancellationToken);
            try
            {
                while (expired.Count < maxToFind && await pages.MoveNextAsync().ConfigureAwait(false))
                {
                    expired.AddRange(pages.Current.Values.Take(maxToFind - expired.Count));
                }
            }
            finally
            {
                await pages.DisposeAsync().ConfigureAwait(false);
            }

            return expired;
        }

        /// <summary>
        /// Deletes each of <paramref name="expired"/> only if it is unchanged since it was found - its ETag still matches -
        /// and returns how many were deleted. A value rewritten meanwhile (a fresh result another worker has just cached)
        /// has a new ETag and is kept, as is a row something else already deleted; the rest of its batch is still deleted.
        /// </summary>
        internal async Task<int> DeleteUnlessChangedAsync(IReadOnlyList<TableEntity> expired, CancellationToken cancellationToken)
        {
            var table = await _getTable(cancellationToken).ConfigureAwait(false);

            var deleted = 0;
            for (var i = 0; i < expired.Count; i += MaxTransactionSize)
            {
                var batch = new List<TableTransactionAction>();
                for (var j = i; j < Math.Min(i + MaxTransactionSize, expired.Count); j++)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, expired[j], expired[j].ETag));
                }

                while (batch.Count > 0)
                {
                    try
                    {
                        await table.SubmitTransactionAsync(batch, cancellationToken).ConfigureAwait(false);
                        deleted += batch.Count;
                        break;
                    }
                    catch (TableTransactionFailedException ex) when (ex.FailedTransactionActionIndex.HasValue
                        && ex.FailedTransactionActionIndex.Value >= 0 && ex.FailedTransactionActionIndex.Value < batch.Count)
                    {
                        // A transaction is all-or-nothing: leave out the row that changed or went away, and delete the rest.
                        batch.RemoveAt(ex.FailedTransactionActionIndex.Value);
                    }
                    catch (TableTransactionFailedException)
                    {
                        // No failing row named: skip this batch. Those rows read as missing anyway, and the next purge retries.
                        break;
                    }
                }
            }

            return deleted;
        }

        /// <summary>
        /// The row key for <paramref name="key"/>: the key itself when it is legal, with the characters Azure Tables
        /// forbids in keys ('/', '\', '#', '?' and control characters) - and '%', the escape character - written as
        /// <c>%XX</c>. A result longer than <see cref="MaxReadableRowKeyLength"/> is replaced by
        /// <see cref="HashedRowKeyPrefix"/> + the key's SHA-256. Deterministic, so the same key always finds its row.
        /// </summary>
        /// <remarks>
        /// Guest user principal names contain '#' (<c>alex_contoso.com#EXT#@fabrikam.onmicrosoft.com</c>), which is why
        /// escaping is needed at all for keys such as the sent-email delta tokens.
        /// </remarks>
        public static string ToRowKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A key is required.", nameof(key));

            StringBuilder escaped = null;
            for (var i = 0; i < key.Length; i++)
            {
                var c = key[i];
                if (MustEscape(c))
                {
                    if (escaped == null)
                    {
                        escaped = new StringBuilder(key.Length + 16).Append(key, 0, i);
                    }
                    escaped.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                }
                else if (escaped != null)
                {
                    escaped.Append(c);
                }
            }

            var rowKey = escaped?.ToString() ?? key;
            return rowKey.Length <= MaxReadableRowKeyLength ? rowKey : HashedRowKeyPrefix + Sha256Hex(key);
        }

        /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="value"/>.</summary>
        public static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // Every character here is below U+00A0, so two hex digits always suffice.
        private static bool MustEscape(char c) => c == '%' || c == '/' || c == '\\' || c == '#' || c == '?' || char.IsControl(c);

        private bool IsExpired(TableEntity entity)
        {
            var expires = entity.GetDateTimeOffset(ExpiresProperty);
            return expires.HasValue && expires.Value <= _utcNow();
        }

        private static async Task TryDeleteExpiredAsync(TableClient table, TableEntity entity, CancellationToken cancellationToken)
        {
            try
            {
                // Conditional on the ETag just read, so a value re-written meanwhile is not deleted with it.
                await table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException)
            {
                // Best effort: it reads as missing either way, and PurgeExpiredAsync catches what this misses.
            }
        }

        internal static void EncodeValue(TableEntity entity, string key, string value)
        {
            if (value.Length <= MaxPlainValueChars)
            {
                entity[ValueProperty] = value;
                return;
            }

            var compressed = Compress(value);
            var chunkCount = (compressed.Length + CompressedChunkBytes - 1) / CompressedChunkBytes;
            if (chunkCount > MaxCompressedChunks)
            {
                throw new InvalidOperationException(
                    $"The value for '{key}' is too large for Azure Table storage: {value.Length:N0} characters compress to " +
                    $"{compressed.Length:N0} bytes, over the {MaxCompressedChunks * CompressedChunkBytes:N0}-byte limit of one table entity.");
            }

            entity[CompressedChunkCountProperty] = chunkCount;
            for (var i = 0; i < chunkCount; i++)
            {
                var offset = i * CompressedChunkBytes;
                var chunk = new byte[Math.Min(CompressedChunkBytes, compressed.Length - offset)];
                Buffer.BlockCopy(compressed, offset, chunk, 0, chunk.Length);
                entity[CompressedChunkPropertyPrefix + i.ToString(CultureInfo.InvariantCulture)] = chunk;
            }
        }

        internal static string DecodeValue(TableEntity entity, string key)
        {
            var plain = entity.GetString(ValueProperty);
            if (plain != null)
            {
                return plain;
            }

            var chunkCount = entity.GetInt32(CompressedChunkCountProperty);
            if (!chunkCount.HasValue)
            {
                // A row with no value at all, e.g. one edited by hand. Nothing usable is stored.
                return null;
            }

            using (var compressed = new MemoryStream())
            {
                for (var i = 0; i < chunkCount.Value; i++)
                {
                    var chunk = entity.GetBinary(CompressedChunkPropertyPrefix + i.ToString(CultureInfo.InvariantCulture));
                    if (chunk == null)
                    {
                        throw new InvalidDataException($"The stored value for '{key}' is missing compressed chunk {i} of {chunkCount.Value}.");
                    }
                    compressed.Write(chunk, 0, chunk.Length);
                }

                return Decompress(compressed.ToArray());
            }
        }

        private static byte[] Compress(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                return output.ToArray();
            }
        }

        private static string Decompress(byte[] compressed)
        {
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
