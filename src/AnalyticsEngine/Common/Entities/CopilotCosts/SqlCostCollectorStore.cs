using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotCosts
{
    public sealed class SqlCostCollectorStore : ICostPublicationFence
    {
        private const string LockSql = @"DECLARE @r int;
EXEC @r=sys.sp_getapplock @Resource=N'CopilotCostPublication',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=10000;
IF @r<0 THROW 51001,'Preview publication is busy.',1;";
        private readonly Func<SqlConnection> _connection;
        public SqlCostCollectorStore(Func<SqlConnection> connection) { _connection = connection ?? throw new ArgumentNullException(nameof(connection)); }

        private async Task<T> UseAsync<T>(Func<SqlConnection, Task<T>> action, CancellationToken ct)
        {
            try
            {
                using (var db = _connection())
                {
                    await db.OpenAsync(ct);
                    return await action(db);
                }
            }
            catch (SqlException ex) when (ex.Number == 51001) { throw new CostConnectionException("busy"); }
            catch (SqlException ex) when (ex.Number == 51002) { throw new CostConnectionException("disconnected"); }
            catch (SqlException) { throw new CostConnectionException("databaseUnavailable"); }
            catch (InvalidOperationException) { throw new CostConnectionException("databaseUnavailable"); }
            catch (ArgumentException) { throw new CostConnectionException("databaseUnavailable"); }
            catch (OperationCanceledException) { throw new CostConnectionException("databaseUnavailable"); }
            catch (Azure.Identity.AuthenticationFailedException) { throw new CostConnectionException("databaseUnavailable"); }
        }

        private static SqlCommand Command(SqlConnection db, string sql, SqlTransaction tx = null, params object[] args)
        {
            var cmd = new SqlCommand(sql, db, tx) { CommandTimeout = 20 };
            for (var i = 0; i < args.Length; i += 2)
            {
                var value = args[i + 1];
                var parameter = cmd.Parameters.AddWithValue((string)args[i], value ?? DBNull.Value);
                if (value is string) { parameter.SqlDbType = SqlDbType.NVarChar; parameter.Size = -1; }
                if ((string)args[i] == "@generation") { parameter.SqlDbType = SqlDbType.VarChar; parameter.Size = 32; }
                if ((string)args[i] == "@outcome") { parameter.SqlDbType = SqlDbType.VarChar; parameter.Size = 40; }
                if (value is byte[]) { parameter.SqlDbType = SqlDbType.VarBinary; parameter.Size = 32; }
            }
            return cmd;
        }

        private static async Task ExecuteAsync(SqlConnection db, string sql, SqlTransaction tx, CancellationToken ct, params object[] args)
        {
            using (var cmd = Command(db, sql, tx, args)) await cmd.ExecuteNonQueryAsync(ct);
        }

        public Task ExecuteExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, LockSql, null, ct);
                try { await action(ct); return true; }
                finally
                {
                    // Session locks survive connection pooling unless explicitly released.
                    using (var cmd = Command(db, "EXEC sys.sp_releaseapplock @Resource=N'CopilotCostPublication',@LockOwner='Session';"))
                        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }, ct);

        public Task InvalidateAndExecuteExclusiveAsync(Func<CancellationToken, Task<string>> readGeneration,
            Func<CancellationToken, Task> action, CancellationToken ct) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, LockSql, null, ct);
                try
                {
                    var generation = await readGeneration(ct);
                    // Commit the SQL block BEFORE touching Table. If Table fails, even an intact
                    // old cache cannot resume publication; only explicit reconnect's NEW generation
                    // can reopen it. This is intentionally not rolled back with the remote action.
                    await ExecuteAsync(db, @"
UPDATE dbo.copilot_cost_control SET generation=@generation,blocked=1 WHERE singleton=1;
IF @@ROWCOUNT=0 INSERT dbo.copilot_cost_control(singleton,generation,blocked) VALUES(1,@generation,1);",
                        null, ct, "@generation", generation);
                    await action(ct);
                    return true;
                }
                finally
                {
                    using (var cmd = Command(db, "EXEC sys.sp_releaseapplock @Resource=N'CopilotCostPublication',@LockOwner='Session';"))
                        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }, ct);

        public Task<CostCollectorRun> AcquireRunAsync(string generation, DateTimeOffset now, CancellationToken ct,
            Func<CancellationToken, Task> assertTableOwner = null) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, LockSql, null, ct);
                try
                {
                    if (assertTableOwner != null) await assertTableOwner(ct);
                    using (var tx = db.BeginTransaction(IsolationLevel.Serializable))
                    {
                        // One small transaction chooses the next run; no Graph I/O holds SQL locks.
                        var owner = Guid.NewGuid();
                        using (var cmd = Command(db, @"
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_control WITH(UPDLOCK,HOLDLOCK) WHERE singleton=1)
 INSERT dbo.copilot_cost_control(singleton,generation,blocked) VALUES(1,@generation,0);
ELSE
 UPDATE dbo.copilot_cost_control SET generation=@generation,blocked=0 WHERE singleton=1 AND generation<>@generation;
IF EXISTS(SELECT 1 FROM dbo.copilot_cost_control WHERE singleton=1 AND blocked=1)
 THROW 51002,'Preview connection is disconnected.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_runs WITH(UPDLOCK,HOLDLOCK)
 WHERE generation=@generation AND inventory_date=@day)
 INSERT dbo.copilot_cost_runs(run_id,generation,inventory_date,next_link,last_served)
 VALUES(NEWID(),@generation,@day,N'https://graph.microsoft.com/v1.0/users?$select=id&$top=999',@now);
DECLARE @id uniqueidentifier;
SELECT TOP(1) @id=r.run_id FROM dbo.copilot_cost_runs r WITH(UPDLOCK,HOLDLOCK)
 WHERE r.generation=@generation AND (r.owned_until IS NULL OR r.owned_until<=@now)
 AND (r.not_before IS NULL OR r.not_before<=@now)
 AND (r.inventory_complete=0 OR r.context_phase<2 OR EXISTS(
 SELECT 1 FROM dbo.copilot_cost_work w WHERE w.run_id=r.run_id AND w.completed=0 AND w.next_attempt<=@now))
 ORDER BY r.last_served,r.inventory_date;
UPDATE dbo.copilot_cost_runs SET owner=@owner,fence=fence+1,owned_until=DATEADD(second,90,@now),
 last_served=@now,last_attempt=@now WHERE run_id=@id;
SELECT run_id,generation,fence,inventory_complete,next_link,inventory_pages
 FROM dbo.copilot_cost_runs WHERE run_id=@id;", tx,
                            "@generation", generation, "@day", now.UtcDateTime.Date, "@now", now, "@owner", owner))
                        using (var reader = await cmd.ExecuteReaderAsync(ct))
                        {
                            CostCollectorRun run = null;
                            if (await reader.ReadAsync(ct))
                                run = new CostCollectorRun
                                {
                                    Id = reader.GetGuid(0), Generation = reader.GetString(1), Owner = owner,
                                    Fence = reader.GetInt64(2), InventoryComplete = reader.GetBoolean(3),
                                    NextLink = reader.IsDBNull(4) ? null : reader.GetString(4), Pages = reader.GetInt32(5)
                                };
                            reader.Close();
                            tx.Commit();
                            return run;
                        }
                    }
                }
                finally
                {
                    using (var cmd = Command(db, "EXEC sys.sp_releaseapplock @Resource=N'CopilotCostPublication',@LockOwner='Session';"))
                        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }, ct);

        private static object[] RunArgs(CostCollectorRun run) => new object[]
            { "@run", run.Id, "@owner", run.Owner, "@fence", run.Fence, "@generation", run.Generation };

        private const string GuardSql = @"
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_control WITH(UPDLOCK,HOLDLOCK)
 WHERE singleton=1 AND generation=@generation AND blocked=0)
 THROW 51002,'Preview connection is disconnected.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_runs WITH(UPDLOCK,HOLDLOCK)
 WHERE run_id=@run AND generation=@generation AND owner=@owner AND fence=@fence AND owned_until>SYSDATETIMEOFFSET())
 THROW 51001,'Preview owner expired.',1;";

        public Task SaveInventoryPageAsync(CostCollectorRun run, IReadOnlyCollection<Guid> ids, string next, CancellationToken ct) =>
            UseAsync(async db =>
            {
                using (var tx = db.BeginTransaction())
                {
                    await ExecuteAsync(db, GuardSql, tx, ct, RunArgs(run));
                    if (run.NextLink == null || run.Pages >= 2000 || next?.Length > 4000)
                        throw new CostConnectionException("invalidPage");
                    await ExecuteAsync(db, @"INSERT dbo.copilot_cost_pages(run_id,url_hash,kind,url,completed)
VALUES(@run,HASHBYTES('SHA2_256',@url),'directory',@url,1);", tx, ct, "@run", run.Id, "@url", run.NextLink);
                    if (next != null)
                    {
                        using (var cmd = Command(db, @"SELECT COUNT(*) FROM dbo.copilot_cost_pages
WHERE run_id=@run AND url_hash=HASHBYTES('SHA2_256',@url);", tx, "@run", run.Id, "@url", next))
                            if ((int)await cmd.ExecuteScalarAsync(ct) != 0) throw new CostConnectionException("invalidPage");
                    }
                    await ExecuteAsync(db, @"
INSERT dbo.copilot_cost_work(run_id,object_id,next_attempt)
 SELECT @run,CONVERT(uniqueidentifier,j.value),SYSDATETIMEOFFSET() FROM OPENJSON(@ids) j
 WHERE NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_work w WHERE w.run_id=@run AND w.object_id=CONVERT(uniqueidentifier,j.value));
UPDATE dbo.copilot_cost_runs SET next_link=@next,inventory_complete=CASE WHEN @next IS NULL THEN 1 ELSE 0 END,
 inventory_pages=inventory_pages+1,outcome='inventory' WHERE run_id=@run;", tx, ct,
                        "@run", run.Id, "@ids", JsonConvert.SerializeObject(ids), "@next", next);
                    await ExecuteAsync(db, LinkSql, tx, ct, "@run", run.Id, "@ids", JsonConvert.SerializeObject(ids));
                    tx.Commit();
                    return true;
                }
            }, ct);

        public Task<IReadOnlyList<CostCollectorWork>> ReadWorkAsync(CostCollectorRun run, DateTimeOffset now, CancellationToken ct) =>
            UseAsync<IReadOnlyList<CostCollectorWork>>(async db =>
            {
                var rows = new List<CostCollectorWork>();
                using (var cmd = Command(db, @"
SELECT TOP(100) object_id,attempts FROM dbo.copilot_cost_work
 WHERE run_id=@run AND completed=0 AND next_attempt<=@now
 ORDER BY next_attempt,attempts,object_id;", null, "@run", run.Id, "@now", now))
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) rows.Add(new CostCollectorWork { UserId = reader.GetGuid(0), Attempts = reader.GetInt32(1) });
                return rows;
            }, ct);

        // Bulk nullable linking: one scan per inventory page, never a lookup per user. The source column
        // is existing nvarchar(max), so no performance-motivated index/column migration is smuggled in.
        public Task LinkInventoryPageAsync(CostCollectorRun run, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, LinkSql,
                    null, ct, "@run", run.Id, "@ids", JsonConvert.SerializeObject(ids));
                return true;
            }, ct);

        public const string LinkSql = @"
DECLARE @links TABLE(object_id uniqueidentifier PRIMARY KEY,id int NOT NULL);
INSERT @links(object_id,id)
 SELECT TRY_CONVERT(uniqueidentifier,u.azure_ad_id) AS object_id,MIN(u.id) AS id
 FROM dbo.users u JOIN OPENJSON(@ids) j ON u.azure_ad_id=CONVERT(nvarchar(36),j.value)
 GROUP BY TRY_CONVERT(uniqueidentifier,u.azure_ad_id);
UPDATE w SET user_id=l.id FROM dbo.copilot_cost_work w JOIN @links l ON l.object_id=w.object_id WHERE w.run_id=@run;
;WITH pending AS(
 SELECT TOP(1000) o.observation_date,o.object_id,o.user_id,l.id
 FROM @links l JOIN dbo.copilot_cost_observations o ON o.object_id=l.object_id
 WHERE o.user_id IS NULL ORDER BY o.object_id,o.observation_date)
UPDATE pending SET user_id=id;";

        public Task PublishAsync(CostCollectorRun run, CostUserObservation observation,
            Func<CancellationToken, Task> assertTableOwner, CancellationToken ct) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, LockSql, null, ct);
                try
                {
                    using (var tx = db.BeginTransaction(IsolationLevel.Serializable))
                    {
                        await ExecuteAsync(db, GuardSql, tx, ct, RunArgs(run));
                        // Disconnect takes this exact SQL lock BEFORE mutating Table. A remote check
                        // alone would have a TOCTOU gap; the shared lock closes that gap.
                        await assertTableOwner(ct);
                        using (var cmd = Command(db, "SELECT COUNT(*) FROM dbo.copilot_cost_history WHERE publication_id=@publication;",
                            tx, "@publication", observation.PublicationId))
                        {
                            if ((int)await cmd.ExecuteScalarAsync(ct) != 0) { tx.Commit(); return true; }
                        }
                        var date = observation.ObservedAt.UtcDateTime.Date;
                        var user = Guid.Parse(observation.Id);
                        var quality = observation.UnevaluatedServices.Count == 0 ? 2 : 1;
                        await ExecuteAsync(db, @"
INSERT dbo.copilot_cost_history(run_id,publication_id,observation_date,object_id,observed_at,quality,bundle)
 VALUES(@run,@publication,@day,@user,@at,@quality,@bundle);
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_observations WHERE observation_date=@day AND object_id=@user)
 INSERT dbo.copilot_cost_observations(observation_date,object_id,user_id,observed_at,quality,selection_status)
 SELECT @day,@user,user_id,@at,0,@selection FROM dbo.copilot_cost_work WHERE run_id=@run AND object_id=@user;
SELECT quality FROM dbo.copilot_cost_observations WHERE observation_date=@day AND object_id=@user;", tx, ct,
                            "@run", run.Id, "@publication", observation.PublicationId, "@day", date, "@user", user, "@at", observation.ObservedAt,
                            "@quality", quality, "@bundle", JsonConvert.SerializeObject(observation),
                            "@selection", observation.SelectionDataStatus.ToString(Formatting.None));
                        int existing;
                        DateTimeOffset existingAt;
                        using (var cmd = Command(db, "SELECT quality,observed_at FROM dbo.copilot_cost_observations WHERE observation_date=@day AND object_id=@user;",
                            tx, "@day", date, "@user", user))
                        using (var reader = await cmd.ExecuteReaderAsync(ct))
                        {
                            if (!await reader.ReadAsync(ct)) throw new CostConnectionException("databaseUnavailable");
                            existing = reader.GetByte(0);
                            existingAt = reader.GetDateTimeOffset(1);
                        }
                        var promote = quality >= existing && observation.ObservedAt >= existingAt;
                        var retainedQuality = promote ? quality : existing;
                        if (promote)
                        {
                            if (quality == 2)
                                await ExecuteAsync(db, "DELETE dbo.copilot_cost_services WHERE observation_date=@day AND object_id=@user;",
                                    tx, ct, "@day", date, "@user", user);
                            foreach (var service in observation.Services)
                            {
                                byte[] hash;
                                using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(service.ServiceId));
                                // A partial observation updates only evaluated categories; absent categories
                                // retain previous same-day facts. History always retains the complete attempt.
                                await ExecuteAsync(db, @"
DELETE dbo.copilot_cost_services WHERE observation_date=@day AND object_id=@user AND category_hash=@hash;
INSERT dbo.copilot_cost_services(observation_date,object_id,category_hash,category,policy_id,consumed_quantity,unit,
 allowed,decision_reason,source_modified,period_limit,per_user_limit,capacity,remaining,data_status)
VALUES(@day,@user,@hash,@category,@policy,@consumed,@unit,@allowed,@reason,@modified,@period,@peruser,@capacity,@remaining,@status);",
                                    tx, ct, "@day", date, "@user", user, "@hash", hash, "@category", service.ServiceId,
                                    "@policy", service.SpendingPolicyId, "@consumed", service.ConsumedQuantity,
                                    "@unit", service.Unit, "@allowed", service.IsConsumptionAllowed, "@reason", service.AccessDecisionReason,
                                    "@modified", service.Freshness?.LastModifiedDateTime,
                                    "@period", Serialize(service.ConfiguredPeriodLimit), "@peruser", Serialize(service.ConfiguredPerUserLimit),
                                    "@capacity", Serialize(service.EffectiveCapacityQuantity), "@remaining", Serialize(service.RemainingQuantity),
                                    "@status", Serialize(service.DataStatus));
                            }
                            await ExecuteAsync(db, @"
DELETE dbo.copilot_cost_unevaluated WHERE observation_date=@day AND object_id=@user;
UPDATE dbo.copilot_cost_observations SET observed_at=@at,quality=@quality,selection_status=@selection
 WHERE observation_date=@day AND object_id=@user;", tx, ct, "@day", date, "@user", user,
                                "@at", observation.ObservedAt, "@quality", quality,
                                "@selection", observation.SelectionDataStatus.ToString(Formatting.None));
                            for (var i = 0; i < observation.UnevaluatedServices.Count; i++)
                            {
                                var row = observation.UnevaluatedServices[i];
                                await ExecuteAsync(db, @"INSERT dbo.copilot_cost_unevaluated(observation_date,object_id,ordinal,category,reason)
VALUES(@day,@user,@i,@category,@reason);", tx, ct, "@day", date, "@user", user, "@i", i,
                                    "@category", CostGraphSource.Text(row["serviceId"]), "@reason", CostGraphSource.Text(row["reason"]));
                            }
                        }
                        await ExecuteAsync(db, @"
UPDATE dbo.copilot_cost_work SET completed=@complete,attempts=attempts+1,observed=1,
 measured=CASE WHEN @promote=1 THEN @measured ELSE measured END,
 empty=CASE WHEN @promote=1 THEN @empty ELSE empty END,
 partial=CASE WHEN @promote=1 THEN @partial ELSE partial END,
 unevaluated=CASE WHEN @promote=1 THEN @unevaluated ELSE unevaluated END,
 next_attempt=DATEADD(hour,1,SYSDATETIMEOFFSET()),outcome=@outcome WHERE run_id=@run AND object_id=@user;
UPDATE dbo.copilot_cost_runs SET outcome=@outcome WHERE run_id=@run;
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_work WHERE run_id=@run AND completed=0)
 UPDATE dbo.copilot_cost_runs SET last_completed=SYSDATETIMEOFFSET(),outcome='complete' WHERE run_id=@run;", tx, ct,
                            "@run", run.Id, "@user", user, "@complete", retainedQuality == 2,
                            "@promote", promote, "@measured", observation.Services.Any(s => s.ConsumedQuantity.HasValue),
                            "@empty", observation.Services.Count == 0 && observation.UnevaluatedServices.Count == 0,
                            "@partial", quality == 1, "@unevaluated", observation.UnevaluatedServices.Count,
                            "@outcome", retainedQuality == 2 ? "observed" : "partial");
                        await assertTableOwner(ct);
                        tx.Commit();
                        return true;
                    }
                }
                finally
                {
                    using (var cmd = Command(db, "EXEC sys.sp_releaseapplock @Resource=N'CopilotCostPublication',@LockOwner='Session';"))
                        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }, ct);

        private static string Serialize(object value) => value == null ? null : JsonConvert.SerializeObject(value);

        public Task RecordFailureAsync(CostCollectorRun run, CostCollectorWork work, string outcome, DateTimeOffset now, CancellationToken ct, TimeSpan? retryAfter = null) =>
            UseAsync(async db =>
            {
                // Only caller-controlled stable classifications reach SQL, never exception messages.
                if (!CostCollector.Outcomes.Contains(outcome)) outcome = "sourceFailure";
                using (var tx = db.BeginTransaction())
                {
                    await ExecuteAsync(db, GuardSql, tx, ct, RunArgs(run));
                    await ExecuteAsync(db, @"
UPDATE dbo.copilot_cost_work SET attempts=attempts+1,next_attempt=@next,outcome=@outcome,
 completed=CASE WHEN @outcome='notFound' THEN 1 ELSE 0 END
 WHERE run_id=@run AND object_id=@user AND completed=0;
UPDATE dbo.copilot_cost_runs SET outcome=@outcome WHERE run_id=@run;
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_work WHERE run_id=@run AND completed=0)
 UPDATE dbo.copilot_cost_runs SET last_completed=SYSDATETIMEOFFSET(),outcome='complete'
 WHERE run_id=@run AND inventory_complete=1 AND context_phase=2;", tx, ct,
                        "@run", run.Id, "@user", work.UserId, "@outcome", outcome,
                        "@next", now.Add(Max(TimeSpan.FromMinutes(Math.Min(1440, 15 * Math.Pow(2, Math.Min(work.Attempts, 7)))), retryAfter)));
                    tx.Commit();
                    return true;
                }
            }, ct);

        private static TimeSpan Max(TimeSpan baseline, TimeSpan? retry) =>
            retry.HasValue && retry.Value > baseline ? retry.Value : baseline;

        public Task<CostContextTask> ReadContextTaskAsync(CostCollectorRun run, CancellationToken ct) =>
            UseAsync(async db =>
            {
                using (var tx = db.BeginTransaction())
                {
                    await ExecuteAsync(db, GuardSql, tx, ct, RunArgs(run));
                    await ExecuteAsync(db, @"
IF EXISTS(SELECT 1 FROM dbo.copilot_cost_runs WHERE run_id=@run AND context_phase=0)
BEGIN
 INSERT dbo.copilot_cost_pages(run_id,url_hash,kind,url)
 VALUES(@run,HASHBYTES('SHA2_256',@policies),'policies',@policies),(@run,HASHBYTES('SHA2_256',@tenant),'tenant',@tenant);
 UPDATE dbo.copilot_cost_runs SET context_phase=1 WHERE run_id=@run;
END;", tx, ct, "@run", run.Id,
                        "@policies", "https://graph.microsoft.com/beta/copilot/costManagement/spendingPolicies",
                        "@tenant", "https://graph.microsoft.com/beta/copilot/costManagement/getTenantCreditBalance");
                    using (var cmd = Command(db, @"SELECT TOP(1) url,kind FROM dbo.copilot_cost_pages
WHERE run_id=@run AND completed=0 ORDER BY kind,url_hash;", tx, "@run", run.Id))
                    using (var r = await cmd.ExecuteReaderAsync(ct))
                    {
                        CostContextTask result = null;
                        if (await r.ReadAsync(ct)) result = new CostContextTask { Url = r.GetString(0), Kind = r.GetString(1) };
                        r.Close();
                        tx.Commit();
                        return result;
                    }
                }
            }, ct);

        public Task SaveContextAsync(CostCollectorRun run, CostContextTask task, CostContextPage page,
            Func<CancellationToken, Task> assertTableOwner, CancellationToken ct) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, LockSql, null, ct);
                try
                {
                    using (var tx = db.BeginTransaction())
                    {
                        await ExecuteAsync(db, GuardSql, tx, ct, RunArgs(run));
                        await assertTableOwner(ct);
                        await ExecuteAsync(db, @"
INSERT dbo.copilot_cost_context(run_id,observed_at,kind,source_url,document) VALUES(@run,SYSDATETIMEOFFSET(),@kind,@url,@json);
UPDATE dbo.copilot_cost_pages SET completed=1 WHERE run_id=@run AND url_hash=HASHBYTES('SHA2_256',@url);",
                            tx, ct, "@run", run.Id, "@kind", task.Kind, "@url", task.Url, "@json", page.Document);
                        foreach (var following in page.Following)
                        {
                            if (following.Url.Length > 4000) throw new CostConnectionException("invalidPage");
                            using (var cmd = Command(db, @"SELECT COUNT(*) FROM dbo.copilot_cost_pages
WHERE run_id=@run AND (url_hash=HASHBYTES('SHA2_256',@url) OR
 (SELECT COUNT(*) FROM dbo.copilot_cost_pages WHERE run_id=@run)>5000);",
                                tx, "@run", run.Id, "@url", following.Url))
                                if ((int)await cmd.ExecuteScalarAsync(ct) != 0) throw new CostConnectionException("invalidPage");
                            // A repeated continuation is a loop, not a complete context snapshot.
                            await ExecuteAsync(db, @"INSERT dbo.copilot_cost_pages(run_id,url_hash,kind,url)
VALUES(@run,HASHBYTES('SHA2_256',@url),@kind,@url);", tx, ct,
                                "@run", run.Id, "@url", following.Url, "@kind", following.Kind);
                        }
                        await ExecuteAsync(db, @"
IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_pages WHERE run_id=@run AND completed=0)
BEGIN
 UPDATE dbo.copilot_cost_runs SET context_phase=2,outcome='context' WHERE run_id=@run;
 IF NOT EXISTS(SELECT 1 FROM dbo.copilot_cost_work WHERE run_id=@run AND completed=0)
  UPDATE dbo.copilot_cost_runs SET last_completed=SYSDATETIMEOFFSET(),outcome='complete' WHERE run_id=@run;
END;", tx, ct, "@run", run.Id);
                        await assertTableOwner(ct);
                        tx.Commit();
                        return true;
                    }
                }
                finally
                {
                    using (var cmd = Command(db, "EXEC sys.sp_releaseapplock @Resource=N'CopilotCostPublication',@LockOwner='Session';"))
                        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }, ct);

        public Task<Tuple<int, string>> ReadContextCursorAsync(CostCollectorRun run, CancellationToken ct) =>
            UseAsync(async db =>
            {
                using (var cmd = Command(db, "SELECT context_phase,context_next FROM dbo.copilot_cost_runs WHERE run_id=@run;", null, "@run", run.Id))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await r.ReadAsync(ct)) throw new CostConnectionException("databaseUnavailable");
                    return Tuple.Create(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1));
                }
            }, ct);

        public Task ReleaseAsync(CostCollectorRun run, CancellationToken ct) =>
            UseAsync(async db =>
            {
                await ExecuteAsync(db, @"UPDATE dbo.copilot_cost_runs SET owner=NULL,owned_until=NULL
WHERE run_id=@run AND owner=@owner AND fence=@fence;", null, ct, "@run", run.Id, "@owner", run.Owner, "@fence", run.Fence);
                return true;
            }, ct);

        public Task RecordRunOutcomeAsync(CostCollectorRun run, string outcome, CancellationToken ct, TimeSpan? retryAfter = null) =>
            UseAsync(async db =>
            {
                if (!CostCollector.Outcomes.Contains(outcome)) outcome = "sourceFailure";
                await ExecuteAsync(db, @"UPDATE dbo.copilot_cost_runs SET outcome=@outcome,not_before=@next
WHERE run_id=@run AND owner=@owner AND fence=@fence;", null, ct,
                    "@outcome", outcome, "@run", run.Id, "@owner", run.Owner, "@fence", run.Fence,
                    "@next", DateTimeOffset.UtcNow.Add(Max(TimeSpan.FromMinutes(5), retryAfter)));
                return true;
            }, ct);

        public Task<CostCollectorStatus> ReadStatusAsync(CancellationToken ct) =>
            UseAsync(async db =>
            {
                using (var cmd = Command(db, StatusSql))
                using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await r.ReadAsync(ct)) return new CostCollectorStatus();
                    var result = new CostCollectorStatus
                    {
                        InventoryComplete = r.GetBoolean(0), LastAttempt = r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetDateTimeOffset(2),
                        LastCompleted = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetDateTimeOffset(3), LastOutcome = r.GetString(4),
                        DirectoryUsers = r.GetInt64(5), Pending = r.GetInt64(6), Retrying = r.GetInt64(7),
                        Observed = r.GetInt64(8), Failed = r.GetInt64(9), Denied = r.GetInt64(10),
                        Measured = r.GetInt64(11), Empty = r.GetInt64(12), Partial = r.GetInt64(13), Unevaluated = r.GetInt64(14)
                    };
                    result.Phase = !result.InventoryComplete ? "inventory" : r.GetInt32(1) < 2 ? "context"
                        : result.Pending + result.Retrying > 0 ? "users" : "complete";
                    return result;
                }
            }, ct);

        public const string StatusSql = @"
SELECT TOP(1) r.inventory_complete,r.context_phase,r.last_attempt,
 (SELECT MAX(last_completed) FROM dbo.copilot_cost_runs),r.outcome,
 COUNT_BIG(w.object_id),
 COALESCE(SUM(CONVERT(bigint,CASE WHEN w.completed=0 AND w.attempts=0 THEN 1 ELSE 0 END)),0),
 COALESCE(SUM(CONVERT(bigint,CASE WHEN w.completed=0 AND w.attempts>0 THEN 1 ELSE 0 END)),0),
 COALESCE(SUM(CONVERT(bigint,w.observed)),0),
 COALESCE(SUM(CONVERT(bigint,CASE WHEN w.completed=0 AND w.outcome NOT IN('pending','partial','notFound','permissionDenied') THEN 1 ELSE 0 END)),0),
 COALESCE(SUM(CONVERT(bigint,CASE WHEN w.outcome IN('notFound','permissionDenied') THEN 1 ELSE 0 END)),0),
 COALESCE(SUM(CONVERT(bigint,w.measured)),0),COALESCE(SUM(CONVERT(bigint,w.empty)),0),
 COALESCE(SUM(CONVERT(bigint,w.partial)),0),COALESCE(SUM(CONVERT(bigint,w.unevaluated)),0)
FROM (SELECT TOP(1) * FROM dbo.copilot_cost_runs ORDER BY last_attempt DESC) r
 LEFT JOIN dbo.copilot_cost_work w ON w.run_id=r.run_id
GROUP BY r.inventory_complete,r.context_phase,r.last_attempt,r.outcome;";
    }
}
