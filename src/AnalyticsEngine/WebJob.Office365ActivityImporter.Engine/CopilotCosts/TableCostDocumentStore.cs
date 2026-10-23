using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.CopilotCosts
{
    public sealed class TableCostDocumentStore : ICostDocumentStore
    {
        public const int ChunkSize = 48 * 1024;
        public const int ChunkCount = 8;
        public const int MaximumBytes = ChunkSize * ChunkCount;
        private readonly TableClient _table;
        private readonly string _partition;

        public TableCostDocumentStore(TableClient table, string tenantId, string clientId)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _partition = "cost-v1-" + Guid.Parse(tenantId).ToString("N") + "-" + Guid.Parse(clientId).ToString("N");
        }

        public async Task<CostDocument> ReadAsync(string key, CancellationToken cancellationToken)
        {
            ValidateKey(key);
            try
            {
                var response = await _table.GetEntityIfExistsAsync<TableEntity>(_partition, key, cancellationToken: cancellationToken);
                return response.HasValue ? Decode(response.Value) : null;
            }
            catch (Azure.Identity.AuthenticationFailedException) { throw new CostConnectionException("storageUnavailable"); }
        }

        public static CostDocument Decode(TableEntity entity)
        {
            var size = entity.GetInt32("Length");
            if (!size.HasValue || size < 0 || size > MaximumBytes) throw new CostConnectionException("storageCorrupt");
            var chunks = new List<byte[]>();
            for (var i = 0; i < ChunkCount; i++)
            {
                var bytes = entity.GetBinary("Data" + i);
                if (bytes == null || bytes.Length > ChunkSize) throw new CostConnectionException("storageCorrupt");
                chunks.Add(bytes);
            }
            var content = chunks.SelectMany(c => c).ToArray();
            if (content.Length != size || CostConnectionEncoding.Hash(Convert.ToBase64String(content)) != entity.GetString("Hash"))
                throw new CostConnectionException("storageCorrupt");
            return new CostDocument { Version = entity.ETag.ToString(), Content = content };
        }

        public static TableEntity Encode(string partition, string key, byte[] content)
        {
            if (content == null || content.Length > MaximumBytes) throw new CostConnectionException("cacheTooLarge");
            var entity = new TableEntity(partition, key)
            {
                ["Length"] = content.Length,
                ["Hash"] = CostConnectionEncoding.Hash(Convert.ToBase64String(content))
            };
            // Binary properties stay below 64 KiB and the entire entity below 1 MiB. One conditional
            // entity replacement publishes all chunks atomically and erases old token bytes on disconnect.
            for (var i = 0; i < ChunkCount; i++)
            {
                var offset = i * ChunkSize;
                var data = new byte[Math.Max(0, Math.Min(ChunkSize, content.Length - offset))];
                if (data.Length > 0) Buffer.BlockCopy(content, offset, data, 0, data.Length);
                entity["Data" + i] = data;
            }
            return entity;
        }

        public async Task<string> WriteAsync(string key, byte[] content, string expectedVersion, CancellationToken cancellationToken)
        {
            ValidateKey(key);
            var entity = Encode(_partition, key, content);
            try
            {
                var response = expectedVersion == null
                    ? await _table.AddEntityAsync(entity, cancellationToken)
                    : await _table.UpdateEntityAsync(entity, new ETag(expectedVersion), TableUpdateMode.Replace, cancellationToken);
                return response.Headers.ETag.Value.ToString();
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
            {
                throw new CostConnectionException("busy");
            }
            catch (Azure.Identity.AuthenticationFailedException) { throw new CostConnectionException("storageUnavailable"); }
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 120 || key.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("Invalid cost document key.", nameof(key));
        }
    }
}
