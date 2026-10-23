using DataUtils.Sql;
using Common.Entities.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    /// <summary>
    /// Operator-only bounded repair of DLP records previously classified as file/mail activity. Positive evidence is
    /// a retained DLP rule-match row or an exact documented DLP operation, never a workload or a "Dlp%" prefix.
    /// Audit events, raw JSON, policy matches and shared dimensions are retained. The removed metadata and resolved
    /// lookup values are durably archived first, then byte-checked again under SQL locks. No schema changes.
    /// </summary>
    public static class DlpHistoricalRepair
    {
        public static async Task<DlpHistoricalRepairBatch> RunBatchAsync(string connectionString, DateTime fromUtc,
            DateTime toUtc, DateTime? afterTime, int? afterUserId, Guid? afterEventId, int take, ILogger logger, IKeyValueStore archive)
        {
            if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc >= toUtc)
                throw new ArgumentException("A nonempty half-open UTC window is required.");
            if (take < 1 || take > 500) throw new ArgumentOutOfRangeException(nameof(take));
            if (afterTime.HasValue != afterEventId.HasValue) throw new ArgumentException("Both checkpoint fields are required.");
            if (afterTime.HasValue && (afterTime < fromUtc || afterTime >= toUtc)) throw new ArgumentOutOfRangeException(nameof(afterTime));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (archive == null) throw new ArgumentNullException(nameof(archive), "A durable Azure Table archive is required before correcting historical metadata.");
            var result = new DlpHistoricalRepairBatch();
            var ids = new List<Guid>();
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(@"
SELECT TOP (@take) e.id, e.time_stamp, e.user_id
FROM dbo.audit_events e
LEFT JOIN dbo.event_operations o ON o.id = e.operation_id
WHERE e.time_stamp >= @from AND e.time_stamp < @to
  AND (o.operation_name IN ('DlpRuleMatch', 'DlpRuleUndo', 'DlpInfo')
       OR EXISTS (SELECT 1 FROM dbo.dlp_rule_matches m WHERE m.event_id = e.id))
  AND (@after IS NULL OR e.time_stamp > @after OR (e.time_stamp = @after AND (
       e.user_id > @user OR (@user IS NULL AND e.user_id IS NOT NULL)
       OR ((e.user_id = @user OR (e.user_id IS NULL AND @user IS NULL)) AND e.id > @event))))
ORDER BY e.time_stamp, e.user_id, e.id OPTION (RECOMPILE);", connection) { CommandTimeout = 300 })
                {
                    command.Parameters.Add("@take", SqlDbType.Int).Value = take;
                    command.Parameters.Add("@from", SqlDbType.DateTime).Value = fromUtc;
                    command.Parameters.Add("@to", SqlDbType.DateTime).Value = toUtc;
                    command.Parameters.Add("@after", SqlDbType.DateTime).Value = (object)afterTime ?? DBNull.Value;
                    command.Parameters.Add("@user", SqlDbType.Int).Value = (object)afterUserId ?? DBNull.Value;
                    command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = (object)afterEventId ?? DBNull.Value;
                    using (var reader = await command.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            ids.Add(reader.GetGuid(0));
                            result.AfterEventId = reader.GetGuid(0);
                            result.AfterTime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                            result.AfterUserId = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                        }
                }
                result.Scanned = ids.Count;
                if (ids.Count == 0) return result;
                var snapshots = await ReadSnapshotsAsync(connection, ids);
                if (snapshots.Count == 0) return result;
                // Bounded network concurrency; no table-wide SQL locks are held while Table storage is accessed.
                for (var start = 0; start < snapshots.Count; start += 8)
                {
                    var batch = snapshots.GetRange(start, Math.Min(8, snapshots.Count - start));
                    await Task.WhenAll(batch.Select(async snapshot =>
                    {
                        var key = snapshot.EventId.ToString("N") + "-" + BitConverter.ToString(snapshot.Hash).Replace("-", "");
                        await archive.SetStringAsync(key, snapshot.Json);
                        if (!string.Equals(await archive.GetStringAsync(key), snapshot.Json, StringComparison.Ordinal))
                            throw new InvalidOperationException("DLP historical archive verification failed. SQL metadata was not removed.");
                    }));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 90;
                    for (var i = 0; i < snapshots.Count; i++)
                    {
                        command.Parameters.Add("@id" + i, SqlDbType.UniqueIdentifier).Value = snapshots[i].EventId;
                        command.Parameters.Add("@hash" + i, SqlDbType.VarBinary, 32).Value = snapshots[i].Hash;
                    }
                    command.CommandText = @"
SET XACT_ABORT ON;
SET NOCOUNT ON;
DECLARE @events TABLE (id uniqueidentifier PRIMARY KEY, snapshot_hash varbinary(32));
INSERT @events VALUES " + string.Join(",", Enumerable.Range(0, snapshots.Count).Select(i => "(@id" + i + ", @hash" + i + ")")) + @";
BEGIN TRY
    BEGIN TRANSACTION;
    -- Pin the tables while checking for custom schema/cascades/triggers, so DDL cannot race the safety guard.
    DECLARE @lock uniqueidentifier;
    SELECT TOP (1) @lock = event_id FROM dbo.event_meta_sharepoint WITH (TABLOCKX, HOLDLOCK);
    SELECT TOP (1) @lock = event_id FROM dbo.event_meta_exchange WITH (TABLOCKX, HOLDLOCK);
    SELECT TOP (1) @lock = event_id FROM dbo.event_meta_general WITH (TABLOCKX, HOLDLOCK);
    IF EXISTS (SELECT 1 FROM @events e WHERE HASHBYTES('SHA2_256', CONVERT(varbinary(max), " + MetadataJsonSql + @")) <> e.snapshot_hash)
        THROW 50004, 'DLP historical metadata changed after archival. Nothing was removed; re-run to archive the changed state before retrying.', 1;
    DECLARE @targets TABLE (object_id int);
    IF EXISTS (SELECT 1 FROM dbo.event_meta_sharepoint s JOIN @events e ON e.id = s.event_id)
        INSERT @targets VALUES (OBJECT_ID('dbo.event_meta_sharepoint'));
    IF EXISTS (SELECT 1 FROM dbo.event_meta_exchange x JOIN @events e ON e.id = x.event_id)
        INSERT @targets VALUES (OBJECT_ID('dbo.event_meta_exchange'));
    IF EXISTS (SELECT 1 FROM sys.foreign_keys f JOIN @targets t ON t.object_id = f.referenced_object_id)
       OR EXISTS (SELECT 1 FROM sys.triggers tr JOIN @targets t ON t.object_id = tr.parent_id WHERE tr.is_disabled = 0)
       OR EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.parent_id = OBJECT_ID('dbo.event_meta_general') AND tr.is_disabled = 0
                  AND EXISTS (SELECT 1 FROM dbo.event_meta_general g JOIN @events e ON e.id = g.event_id WHERE g.workload <> 'DLP' OR g.workload IS NULL))
       OR EXISTS (SELECT 1 FROM sys.columns c JOIN @targets t ON t.object_id = c.object_id
                  WHERE (t.object_id = OBJECT_ID('dbo.event_meta_exchange') AND c.name NOT IN ('event_id', 'object_id'))
                     OR (t.object_id = OBJECT_ID('dbo.event_meta_sharepoint')
                         AND c.name NOT IN ('event_id', 'file_extension_id', 'file_name_id', 'url_id', 'related_web_id', 'item_type_id')))
        THROW 50003, 'DLP historical repair refused: workload metadata has custom columns, incoming foreign keys or enabled triggers. No rows were changed; review custom payload before repairing.', 1;

    DELETE s FROM dbo.event_meta_sharepoint s JOIN @events e ON e.id = s.event_id;
    DECLARE @sp int = @@ROWCOUNT;
    DELETE x FROM dbo.event_meta_exchange x JOIN @events e ON e.id = x.event_id;
    DECLARE @ex int = @@ROWCOUNT;
    UPDATE g SET workload = 'DLP' FROM dbo.event_meta_general g JOIN @events e ON e.id = g.event_id
    WHERE g.workload <> 'DLP' OR g.workload IS NULL;
    DECLARE @general int = @@ROWCOUNT;
    COMMIT;
    SELECT @sp, @ex, @general;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
END CATCH;";
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync()) throw new InvalidOperationException("DLP historical repair returned no commit result.");
                        result.SharePointRowsRemoved = reader.GetInt32(0);
                        result.ExchangeRowsRemoved = reader.GetInt32(1);
                        result.GeneralRowsReclassified = reader.GetInt32(2);
                    }
                }
            }
            logger.LogInformation("DLP historical repair: scanned {Scanned}; removed derived SharePoint rows {SharePoint}, Exchange rows {Exchange}; " +
                "reclassified general rows {General}. Audit and policy evidence was retained.",
                result.Scanned, result.SharePointRowsRemoved, result.ExchangeRowsRemoved, result.GeneralRowsReclassified);
            return result;
        }

        // Include native row fields AND their resolved values: deleting a derived row can otherwise remove the only
        // retained document/message context, and orphan-dimension cleanup can later remove the lookup rows.
        private const string MetadataJsonSql = @"(SELECT 1 AS [schemaVersion], e.id AS [auditEventId],
JSON_QUERY((SELECT s.*,
    JSON_QUERY((SELECT n.* FROM dbo.event_file_names n WHERE n.id = s.file_name_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [fileName],
    JSON_QUERY((SELECT x.* FROM dbo.event_file_ext x WHERE x.id = s.file_extension_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [fileExtension],
    JSON_QUERY((SELECT u.* FROM dbo.urls u WHERE u.id = s.url_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [url],
    JSON_QUERY((SELECT w.* FROM dbo.webs w WHERE w.id = s.related_web_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [web],
    JSON_QUERY((SELECT t.* FROM dbo.event_types t WHERE t.id = s.item_type_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [itemType]
    FROM dbo.event_meta_sharepoint s WHERE s.event_id = e.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [sharePoint],
JSON_QUERY((SELECT x.* FROM dbo.event_meta_exchange x WHERE x.event_id = e.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) AS [exchange],
(SELECT g.workload FROM dbo.event_meta_general g WHERE g.event_id = e.id) AS [generalWorkload]
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)";

        private static async Task<List<Snapshot>> ReadSnapshotsAsync(SqlConnection connection, List<Guid> ids)
        {
            var snapshots = new List<Snapshot>();
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = 90;
                for (var i = 0; i < ids.Count; i++)
                    command.Parameters.Add("@id" + i, SqlDbType.UniqueIdentifier).Value = ids[i];
                command.CommandText = @"
DECLARE @events TABLE (id uniqueidentifier PRIMARY KEY);
INSERT @events VALUES " + string.Join(",", Enumerable.Range(0, ids.Count).Select(i => "(@id" + i + ")")) + @";
SELECT e.id, CASE WHEN EXISTS (SELECT 1 FROM dbo.event_meta_exchange x WHERE x.event_id = e.id AND DATALENGTH(x.object_id) > 65536)
                 THEN NULL ELSE " + MetadataJsonSql + @" END
FROM @events e
WHERE EXISTS (SELECT 1 FROM dbo.event_meta_sharepoint s WHERE s.event_id = e.id)
   OR EXISTS (SELECT 1 FROM dbo.event_meta_exchange x WHERE x.event_id = e.id)
   OR EXISTS (SELECT 1 FROM dbo.event_meta_general g WHERE g.event_id = e.id AND (g.workload <> 'DLP' OR g.workload IS NULL));";
                using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync())
                    {
                        if (reader.IsDBNull(1)) throw new InvalidOperationException("DLP historical metadata exceeds the archive bound. No metadata was removed.");
                        var json = reader.GetString(1);
                        if (json.Length * 2 > 131072) throw new InvalidOperationException("DLP historical metadata exceeds the 128 KiB per-event archive bound. No metadata was removed.");
                        using (var hash = SHA256.Create())
                            snapshots.Add(new Snapshot { EventId = reader.GetGuid(0), Json = json, Hash = hash.ComputeHash(Encoding.Unicode.GetBytes(json)) });
                    }
            }
            return snapshots;
        }

        private sealed class Snapshot
        {
            public Guid EventId;
            public string Json;
            public byte[] Hash;
        }
    }

    public sealed class DlpHistoricalRepairBatch
    {
        public int Scanned { get; set; }
        public int SharePointRowsRemoved { get; set; }
        public int ExchangeRowsRemoved { get; set; }
        public int GeneralRowsReclassified { get; set; }
        public DateTime? AfterTime { get; set; }
        public int? AfterUserId { get; set; }
        public Guid? AfterEventId { get; set; }
    }
}
