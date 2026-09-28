using Azure;
using Azure.Data.Tables;
using Common.Entities.UserOrgs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// The user organisation change log in Azure Table Storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One partition per import (the partition key is <see cref="UserOrgChangeLogKeys.LogId"/>). Its
    /// summary is the row <see cref="UserOrgChangeLogKeys.SummaryRowKey"/>; each change's row key is its
    /// lower-case UPN (<see cref="UserOrgChangeLogKeys.ChangeRowKey"/>), so a page reads alphabetically
    /// and a UPN search is a key-range scan of one partition, never a table scan.
    /// </para>
    /// <para>
    /// Writes are upserts in 100-entity transactions - the service's limit for one partition - a few at
    /// a time. A partition takes about 2,000 entities a second, so a 200,000-person first import takes
    /// under two minutes to log, in the background, after the import itself has finished. Nothing here
    /// expires: an audit trail that deletes itself is not one. Delete old partitions from the storage
    /// account if they are no longer wanted.
    /// </para>
    /// </remarks>
    internal sealed class TableStorageUserOrgChangeLog : IUserOrgChangeLog
    {
        public const string TableName = "UserOrgImportChanges";

        private const int MaxTransaction = 100;
        private const int Parallelism = 4;

        private static readonly string[] ChangeColumns = { "Upn", "Before", "After", "Kind" };

        private readonly TableClient _table;

        public TableStorageUserOrgChangeLog(TableClient table)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
        }

        public UserOrgChangeLogStatus Destination => UserOrgChangeLogStatus.TableStorage;

        public async Task AppendAsync(UserOrgChangeLogImport import, IReadOnlyList<UserOrgChangeRecord> changes, CancellationToken cancellationToken)
        {
            if (import == null)
            {
                throw new ArgumentNullException(nameof(import));
            }

            // A transaction may not touch the same entity twice, so a repeated key keeps its last change.
            var entities = (changes ?? new UserOrgChangeRecord[0])
                .Select(c => ToEntity(import.LogId, c))
                .GroupBy(e => e.RowKey, StringComparer.Ordinal)
                .Select(g => g.Last())
                .ToList();

            using (var gate = new SemaphoreSlim(Parallelism))
            {
                var batches = new List<Task>();
                for (var i = 0; i < entities.Count; i += MaxTransaction)
                {
                    var batch = entities.GetRange(i, Math.Min(MaxTransaction, entities.Count - i));
                    batches.Add(SubmitAsync(batch, gate, cancellationToken));
                }

                await Task.WhenAll(batches).ConfigureAwait(false);
            }
        }

        public Task CompleteAsync(UserOrgChangeLogImport import, CancellationToken cancellationToken)
        {
            if (import == null)
            {
                throw new ArgumentNullException(nameof(import));
            }

            return _table.UpsertEntityAsync(ToSummaryEntity(import), TableUpdateMode.Replace, cancellationToken);
        }

        public async Task<UserOrgChangeLogImport> GetImportAsync(string logId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(logId))
            {
                return null;
            }

            var response = await _table
                .GetEntityIfExistsAsync<TableEntity>(logId, UserOrgChangeLogKeys.SummaryRowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.HasValue ? FromSummaryEntity(response.Value) : null;
        }

        public async Task<UserOrgChangeLogPage> GetChangesAsync(
            string logId,
            string search,
            string continuation,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var size = Math.Max(1, Math.Min(pageSize, UserOrgChangeLogKeys.MaxPageSize));
            var prefix = UserOrgChangeLogKeys.SearchPrefix(search);
            var filter = TableClient.CreateQueryFilter(
                $"PartitionKey eq {logId} and RowKey ge {prefix} and RowKey lt {UserOrgChangeLogKeys.UpperBound(prefix)}");

            var pages = _table
                .QueryAsync<TableEntity>(filter, size, ChangeColumns, cancellationToken)
                .AsPages(continuation, size);

            var enumerator = pages.GetAsyncEnumerator(cancellationToken);
            try
            {
                bool any;
                try
                {
                    any = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (continuation != null && (ex is RequestFailedException || ex is ArgumentException || ex is FormatException))
                {
                    // A continuation the service does not recognise - edited, or from an older page. Asked
                    // again rather than answered with a fault.
                    throw new UserOrgValidationException(
                        "That page of changes is no longer available. Open the change list again.",
                        UserOrgImportRefusalCodes.ChangePageExpired,
                        null,
                        ex);
                }

                if (!any)
                {
                    return new UserOrgChangeLogPage();
                }

                var page = enumerator.Current;
                return new UserOrgChangeLogPage
                {
                    Items = page.Values.Select(FromEntity).ToList(),
                    Continuation = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken,
                };
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        internal static TableEntity ToEntity(string logId, UserOrgChangeRecord change)
        {
            var entity = new TableEntity(logId, UserOrgChangeLogKeys.ChangeRowKey(change.Upn, change.UserId))
            {
                { "Upn", change.Upn },
                { "Kind", KindKey(change.Kind) },
            };

            // Omitted rather than stored as null: a missing property IS the absence of a value.
            if (change.OldValue != null)
            {
                entity.Add("Before", change.OldValue);
            }

            if (change.NewValue != null)
            {
                entity.Add("After", change.NewValue);
            }

            return entity;
        }

        internal static UserOrgChangeLogEntry FromEntity(TableEntity entity)
        {
            var before = entity.GetString("Before");
            var after = entity.GetString("After");
            return new UserOrgChangeLogEntry
            {
                Upn = entity.GetString("Upn"),
                Before = before,
                After = after,
                Kind = UserOrgChangeLogKeys.KindOf(before, after),
            };
        }

        internal static TableEntity ToSummaryEntity(UserOrgChangeLogImport import)
        {
            var entity = new TableEntity(import.LogId, UserOrgChangeLogKeys.SummaryRowKey)
            {
                { "JobId", import.JobId },
                { "OrgTypeId", import.OrgTypeId },
                { "Mode", import.Mode == UserOrgImportMode.Replace ? "replace" : "merge" },
                { "QueuedUtc", Utc(import.QueuedUtc) },
                { "RowsTotal", import.RowsTotal },
                { "RowsUnknownUpn", import.RowsUnknownUpn },
                { "RowsInvalid", import.RowsInvalid },
                { "Added", import.Added },
                { "Changed", import.Changed },
                { "Cleared", import.Cleared },
                { "StoredChanges", import.StoredChanges },
                { "WrittenUtc", Utc(import.WrittenUtc) },
                { "SchemaVersion", 1 },
            };

            AddIfSet(entity, "OrgTypeName", import.OrgTypeName);
            AddIfSet(entity, "StartedBy", import.StartedBy);
            AddIfSet(entity, "FileName", import.FileName);
            if (import.FinishedUtc.HasValue)
            {
                entity.Add("FinishedUtc", Utc(import.FinishedUtc.Value));
            }

            return entity;
        }

        internal static UserOrgChangeLogImport FromSummaryEntity(TableEntity entity)
        {
            return new UserOrgChangeLogImport
            {
                LogId = entity.PartitionKey,
                JobId = entity.GetInt32("JobId") ?? 0,
                OrgTypeId = entity.GetInt32("OrgTypeId") ?? 0,
                OrgTypeName = entity.GetString("OrgTypeName"),
                Mode = string.Equals(entity.GetString("Mode"), "replace", StringComparison.Ordinal) ? UserOrgImportMode.Replace : UserOrgImportMode.Merge,
                StartedBy = entity.GetString("StartedBy"),
                FileName = entity.GetString("FileName"),
                QueuedUtc = entity.GetDateTimeOffset("QueuedUtc")?.UtcDateTime ?? DateTime.MinValue,
                FinishedUtc = entity.GetDateTimeOffset("FinishedUtc")?.UtcDateTime,
                RowsTotal = entity.GetInt32("RowsTotal") ?? 0,
                RowsUnknownUpn = entity.GetInt32("RowsUnknownUpn") ?? 0,
                RowsInvalid = entity.GetInt32("RowsInvalid") ?? 0,
                Added = entity.GetInt32("Added") ?? 0,
                Changed = entity.GetInt32("Changed") ?? 0,
                Cleared = entity.GetInt32("Cleared") ?? 0,
                StoredChanges = entity.GetInt32("StoredChanges") ?? 0,
                WrittenUtc = entity.GetDateTimeOffset("WrittenUtc")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        private async Task SubmitAsync(List<TableEntity> batch, SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _table
                    .SubmitTransactionAsync(
                        batch.Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private static string KindKey(UserOrgChangeKind kind)
        {
            switch (kind)
            {
                case UserOrgChangeKind.Added: return "added";
                case UserOrgChangeKind.Changed: return "changed";
                default: return "cleared";
            }
        }

        private static DateTimeOffset Utc(DateTime value)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        private static void AddIfSet(TableEntity entity, string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                entity.Add(name, value);
            }
        }
    }
}
