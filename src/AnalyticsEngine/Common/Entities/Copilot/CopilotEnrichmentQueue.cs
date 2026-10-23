using Azure;
using Azure.Data.Tables;
using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.Copilot
{
    /// <summary>
    /// Durable at-least-once work in AnalyticsState. Each event has an idempotency/lease row and a due-time index
    /// row in the same partition. Enqueue, reschedule and removal are atomic Table transactions. No audit payload,
    /// user identifier, URL or resource name is copied here.
    /// </summary>
    public sealed class CopilotEnrichmentQueue
    {
        private readonly StateStore.LazyTableClient _table;
        private readonly string _partition;

        public CopilotEnrichmentQueue(AppConfig config, ILogger logger)
            : this(StateStore.GetTable(config, logger), StatePartitions.CopilotEnrichment) { }

        internal CopilotEnrichmentQueue(TableClient table, string partition)
            : this(new StateStore.LazyTableClient(ct => Task.FromResult(table)), partition) { }

        private CopilotEnrichmentQueue(StateStore.LazyTableClient table, string partition)
        {
            _table = table;
            _partition = partition;
        }

        public async Task EnqueueAsync(Guid eventId)
        {
            var now = DateTimeOffset.UtcNow;
            var anchor = new TableEntity(_partition, AnchorKey(eventId))
            {
                ["EventId"] = eventId,
                ["CreatedUtc"] = now,
                ["Attempts"] = 0,
                ["DueKey"] = DueKey(eventId, now)
            };
            await ExecuteAsync(async table =>
            {
                if ((await table.GetEntityIfExistsAsync<TableEntity>(_partition, anchor.RowKey)).HasValue)
                    return;
                try
                {
                    await table.SubmitTransactionAsync(new[]
                    {
                        new TableTransactionAction(TableTransactionActionType.Add, anchor),
                        new TableTransactionAction(TableTransactionActionType.Add, DueEntity(eventId, (string)anchor["DueKey"]))
                    });
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    // Only a durable competing enqueue is success; a conflicting index without its anchor is not.
                    if (!(await table.GetEntityIfExistsAsync<TableEntity>(_partition, anchor.RowKey)).HasValue)
                        throw;
                }
            });
        }

        public async Task<IReadOnlyList<CopilotEnrichmentWorkItem>> ReadDueAsync(DateTimeOffset now, int take)
        {
            if (take < 1 || take > 100) throw new ArgumentOutOfRangeException(nameof(take));
            var rows = new List<CopilotEnrichmentWorkItem>();
            await ExecuteAsync(async table =>
            {
                var upper = "due-" + now.UtcDateTime.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-~";
                var filter = TableClient.CreateQueryFilter($"PartitionKey eq {_partition} and RowKey ge {"due-"} and RowKey le {upper}");
                var pages = table.QueryAsync<TableEntity>(filter, take, new[] { "PartitionKey", "RowKey", "EventId" }).AsPages();
                var enumerator = pages.GetAsyncEnumerator();
                try
                {
                    while (await enumerator.MoveNextAsync())
                    {
                        foreach (var row in enumerator.Current.Values)
                        {
                            rows.Add(new CopilotEnrichmentWorkItem(row.GetGuid("EventId").Value, row.RowKey));
                            if (rows.Count == take) return;
                        }
                    }
                }
                finally { await enumerator.DisposeAsync(); }
            });
            return rows;
        }

        public async Task<CopilotEnrichmentClaim> TryClaimAsync(CopilotEnrichmentWorkItem item, DateTimeOffset now)
        {
            CopilotEnrichmentClaim claim = null;
            await ExecuteAsync(async table =>
            {
                var response = await table.GetEntityIfExistsAsync<TableEntity>(_partition, AnchorKey(item.EventId));
                if (!response.HasValue) return; // A concurrent worker completed it after the due query.
                var anchor = response.Value;
                if (anchor.GetString("DueKey") != item.DueKey || anchor.GetDateTimeOffset("LeaseUntilUtc") > now)
                    return;
                anchor["LeaseUntilUtc"] = now.AddMinutes(10);
                try
                {
                    var updated = await table.UpdateEntityAsync(anchor, anchor.ETag, TableUpdateMode.Replace);
                    if (!updated.Headers.TryGetValue("ETag", out var etag))
                        throw new InvalidOperationException("The enrichment lease update returned no ETag; work remains queued until its lease expires.");
                    anchor.ETag = new ETag(etag);
                    claim = new CopilotEnrichmentClaim(item, anchor);
                }
                catch (RequestFailedException ex) when (ex.Status == 404 || ex.Status == 412) { }
            });
            return claim;
        }

        public Task CompleteAsync(CopilotEnrichmentClaim claim) => ExecuteAsync(async table =>
        {
            await table.SubmitTransactionAsync(new[]
            {
                new TableTransactionAction(TableTransactionActionType.Delete, claim.Anchor, claim.Anchor.ETag),
                new TableTransactionAction(TableTransactionActionType.Delete, DueEntity(claim.EventId, claim.Anchor.GetString("DueKey")), ETag.All)
            });
        });

        public Task DeferAsync(CopilotEnrichmentClaim claim, DateTimeOffset now) => ExecuteAsync(async table =>
        {
            var anchor = claim.Anchor;
            var oldDue = anchor.GetString("DueKey");
            var attempts = Math.Min(30, (anchor.GetInt32("Attempts") ?? 0) + 1);
            var delayMinutes = Math.Min(360, Math.Pow(2, Math.Min(attempts, 9)));
            var newDue = DueKey(claim.EventId, now.AddMinutes(delayMinutes));
            anchor["Attempts"] = attempts;
            anchor["DueKey"] = newDue;
            anchor.Remove("LeaseUntilUtc");
            await table.SubmitTransactionAsync(new[]
            {
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, anchor, anchor.ETag),
                new TableTransactionAction(TableTransactionActionType.Delete, DueEntity(claim.EventId, oldDue), ETag.All),
                new TableTransactionAction(TableTransactionActionType.Add, DueEntity(claim.EventId, newDue))
            });
        });

        private async Task ExecuteAsync(Func<TableClient, Task> action)
        {
            var client = await _table.GetAsync(CancellationToken.None);
            try { await action(client); }
            catch (RequestFailedException ex) when (StorageTableClientFactory.IsAccessDenied(ex))
            {
                _table.Invalidate(client);
                throw;
            }
        }

        private static string AnchorKey(Guid id) => "event-" + id.ToString("N");
        private static string DueKey(Guid id, DateTimeOffset due) =>
            "due-" + due.UtcDateTime.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + id.ToString("N");
        private TableEntity DueEntity(Guid id, string key) => new TableEntity(_partition, key) { ["EventId"] = id };
    }

    public sealed class CopilotEnrichmentWorkItem
    {
        internal CopilotEnrichmentWorkItem(Guid eventId, string dueKey) { EventId = eventId; DueKey = dueKey; }
        public Guid EventId { get; }
        internal string DueKey { get; }
    }

    public sealed class CopilotEnrichmentClaim
    {
        internal CopilotEnrichmentClaim(CopilotEnrichmentWorkItem item, TableEntity anchor)
        { EventId = item.EventId; Anchor = anchor; }
        public Guid EventId { get; }
        public int Attempts => Anchor.GetInt32("Attempts") ?? 0;
        internal TableEntity Anchor { get; }
    }
}
