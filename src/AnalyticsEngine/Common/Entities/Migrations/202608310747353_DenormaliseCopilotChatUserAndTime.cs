namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds denormalised <c>user_id</c> and <c>time_stamp</c> columns to <c>dbo.copilot_chats</c>, backfills
    /// them from <c>dbo.audit_events</c>, and indexes them - so no Copilot report has to join
    /// <c>audit_events</c> ever again.
    ///
    /// <para>
    /// <b>Why.</b> A Copilot interaction has no date of its own: the timestamp and the user live on
    /// <c>dbo.audit_events</c>. Every Copilot query therefore ran
    /// <c>copilot_chats INNER JOIN audit_events</c>, and no index could make that read date-selective,
    /// because an index key must be a column of the table being indexed and the date is on the OTHER table.
    /// See issue #360, and the option comparison below for what was measured instead of assumed.
    /// </para>
    ///
    /// <para>
    /// <b>An earlier explanation of this migration was WRONG and is withdrawn.</b> It claimed the optimiser
    /// seeked <c>IX_user_id</c> once per licensed user and dragged in every audit event those users ever
    /// generated. A read-only diagnostic against a real customer tenant denied it: <c>IX_user_id</c> showed
    /// 3 seeks against 230 on <c>IX_audit_events_time_stamp</c>, Copilot is 55% of <c>audit_events</c> (not a
    /// small minority), and licensed users' audit activity is ~87% Copilot - so there is little non-Copilot
    /// data to drag in. The structural argument above is the one that survived contact with real data; do not
    /// reinstate the plan-shape story.
    /// </para>
    ///
    /// <para>
    /// <b>Measured before / after.</b> The numbers below come from a bench built to match a REAL customer
    /// tenant's measured shape - 10.85M <c>audit_events</c> at ~1.7 KB/row (a 19.9 GB clustered index,
    /// because <c>event_data</c> holds the raw JSON), 6.0M <c>copilot_chats</c>, Copilot = 55% of
    /// <c>audit_events</c>, 4.4 years of retention with 85% of Copilot activity inside the last year, and
    /// licensed users whose audit activity is ~87% Copilot. Query text extracted from the compiled
    /// assembly, medians of 3 warm runs, cold run discarded, <c>DBCC FREEPROCCACHE</c> before each run,
    /// <c>OPTION (RECOMPILE)</c>. <c>LicensedUsers</c>, 28-day window:
    /// </para>
    /// <code>
    ///   option                                          hand-maintained dup?   median    write cost
    ///   ----------------------------------------------  --------------------  --------  ---------------------
    ///   baseline: join to audit_events                   no                    13.0 s    -
    ///   + covering index on copilot_chats(event_id)      no                    10.4 s    negligible
    ///   + indexed view over the join                     no (engine-maintained) 6.7 s    +95% on audit_events
    ///   THIS: denormalised columns                       yes                    5.6 s    copilot_chats only
    /// </code>
    ///
    /// <para>
    /// <b>Why the duplication is structural, not lazy tuning.</b> An index key must be a column of the table
    /// being indexed. The date lives on <c>audit_events</c>, so NO index on <c>copilot_chats</c> can ever be
    /// date-ordered unless the date is on <c>copilot_chats</c>. That is why the covering index on
    /// <c>copilot_chats(event_id)</c> barely moves the needle (10.4 s) while this does (5.6 s): only two
    /// things give date-ordered access to Copilot interactions - put the date on the row, or materialise the
    /// join. Do not "normalise this away" without re-reading that sentence.
    /// </para>
    ///
    /// <para>
    /// <b>Why not an indexed view</b> (which would be engine-maintained and so could not drift): it reads
    /// almost as well (6.7 s) but it must be maintained SYNCHRONOUSLY on every insert into EITHER table.
    /// Measured: inserting 50,000 audit events went from 113.5 s to 221.2 s, a <b>95% penalty on every audit
    /// event</b> - including the ~45% that are not Copilot at all - paid on every import cycle for ever, to
    /// save about a second on a report opened occasionally. The denormalised columns add nothing to
    /// <c>audit_events</c>: they are written by the <c>copilot_chats</c> insert that was happening anyway.
    /// The decision rests on WHERE the write cost lands, not on the read margin.
    /// </para>
    ///
    /// <para>
    /// <b>Honest limits of this evidence.</b> The bench reproduces the customer's data SHAPE but not their
    /// environment: production is Azure SQL Database (tier-capped IOPS/CPU) with the importer running
    /// concurrently, and their <c>copilot_chats</c> is ~7x wider than the synthetic one. The observed
    /// production p50 for this step is ~90 s, whereas the same shape runs the un-migrated query in ~13 s on
    /// the bench hardware - so the absolute production improvement is an EXTRAPOLATION from the 2.3x ratio,
    /// not a measurement. The ratio is the defensible claim; "90 s becomes 39 s" is not.
    /// </para>
    ///
    /// <para>
    /// <b>Index shape.</b> <c>(time_stamp, user_id) INCLUDE (app_host, agent_id)</c>, keyed on
    /// <c>time_stamp</c> FIRST - measured, not assumed. <c>LicensedUsers</c> reads the full 365-day history
    /// so it has no date selectivity to exploit either way, but every OTHER Copilot query is window-scoped
    /// and there a leading <c>time_stamp</c> wins: on the customer-shaped bench the <c>UsageByApp</c>
    /// aggregate over 28 days measured <b>0.23 s</b> with <c>(time_stamp, user_id)</c> against <b>0.36 s</b>
    /// with <c>(user_id, time_stamp)</c>. The clustering key <c>event_id</c> rides along as the row locator,
    /// so queries that join the Copilot detail tables still get it without a lookup.
    /// </para>
    ///
    /// <para>
    /// <b>Upgrade time - measured.</b> On the 12,000,000-row synthetic bench: the column add was
    /// metadata-only (0 ms), the backfill took <b>106.7 s</b> and the (offline) index build <b>68.9 s</b>,
    /// for a total of <b>176 s</b>, producing a 640 MB index. Scale roughly linearly with the number of
    /// Copilot interactions: about 15 s per million rows backfilled plus 6 s per million indexed. This is
    /// proportional to <c>copilot_chats</c>, NOT to the size of <c>audit_events</c>.
    /// </para>
    ///
    /// <para>
    /// <b>A compact separate fact table would be faster still</b> (10,804 ms / 13,560 ms - roughly 2x better
    /// than this, and much flatter as the window widens) because it is physically ordered by
    /// <c>(time_stamp, user_id)</c> rather than being a secondary index over a GUID-clustered table. It was
    /// measured and deliberately NOT taken: a second copy of the data can drift from the first, and this
    /// design cannot, because the values are written on the same row by the same statement. Revisit only if
    /// these numbers prove insufficient at production scale.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> Idempotent and guarded - the column add, the backfill and the index build are each
    /// checked independently, so a partial apply converges on re-run. Every step runs with
    /// <c>suppressTransaction: true</c> so it commits independently and a multi-hour backfill cannot be rolled
    /// back wholesale. <c>Configuration.CommandTimeout = 0</c> already removes the per-command timeout.
    /// Progress is emitted with <c>RAISERROR ... WITH NOWAIT</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The backfill is batched by the CLUSTERED KEY, not by <c>WHERE time_stamp IS NULL</c>.</b> That is not
    /// a style preference. The obvious <c>UPDATE TOP (n) ... WHERE time_stamp IS NULL</c> loop was measured at
    /// 792,908 ms and over 157,000,000 logical reads for 12M rows, because every batch rescans the table from
    /// the beginning looking for rows that are still NULL - O(N^2) in the number of batches. Walking the
    /// clustered key with a watermark makes each batch a bounded range seek and the whole backfill O(N).
    /// </para>
    ///
    /// <para>
    /// <b>Upgrade time.</b> The column add is metadata-only (measured at 0 ms - <c>NULL</c>able columns with no
    /// default are not a table rewrite). The cost is the backfill and the index build, both proportional to
    /// the number of Copilot interactions, NOT to the size of <c>audit_events</c>. Run it in a maintenance
    /// window with the importer stopped where <c>ONLINE</c> index builds are unavailable (see
    /// <c>SERVERPROPERTY('EngineEdition')</c> gating below).
    /// </para>
    /// </summary>
    public partial class DenormaliseCopilotChatUserAndTime : DbMigration
    {
        /// <summary>
        /// The whole migration as one guarded, idempotent, resumable script. Kept as a <c>public const</c> so
        /// the shipped <c>202608310747353_DenormaliseCopilotChatUserAndTime.manual.sql</c> can embed it
        /// verbatim for DBAs who upgrade the database by hand rather than running the installer.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DenormaliseCopilotChatUserAndTime';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);
DECLARE @rows bigint;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Decide once whether ONLINE index builds are attemptable: Enterprise (3), Azure SQL DB (5), MI (8).
-- Express / Standard / LocalDB do NOT support them. The attempt is still wrapped in TRY/CATCH via
-- sp_executesql, because the 'online index operations' error aborts the batch and is only catchable
-- when the statement runs through sp_executesql.
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10))
         + N'; ONLINE index build ' + CASE WHEN @canOnline = 1 THEN N'will be attempted.' ELSE N'not supported; will build offline.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-------------------------------------------------------------------------------------------------
-- 1. Columns. Adding a NULLable column with no default is a metadata-only change (measured 0 ms),
--    so this is safe even on a very large table.
-------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.copilot_chats', 'user_id') IS NULL
BEGIN
    RAISERROR('DenormaliseCopilotChatUserAndTime: adding dbo.copilot_chats.user_id...', 0, 1) WITH NOWAIT;
    ALTER TABLE dbo.copilot_chats ADD user_id int NULL;
END
ELSE
    RAISERROR('DenormaliseCopilotChatUserAndTime: dbo.copilot_chats.user_id already exists; skipping.', 0, 1) WITH NOWAIT;

IF COL_LENGTH('dbo.copilot_chats', 'time_stamp') IS NULL
BEGIN
    RAISERROR('DenormaliseCopilotChatUserAndTime: adding dbo.copilot_chats.time_stamp...', 0, 1) WITH NOWAIT;
    ALTER TABLE dbo.copilot_chats ADD time_stamp datetime NULL;
END
ELSE
    RAISERROR('DenormaliseCopilotChatUserAndTime: dbo.copilot_chats.time_stamp already exists; skipping.', 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// The backfill and the index build. Separate batch from <see cref="Up_Sql"/> because the columns are
        /// created there and T-SQL resolves column names for the whole batch up front: referencing
        /// <c>time_stamp</c> in the same batch that adds it fails with
        /// <c>Invalid column name 'time_stamp'</c> even though the ALTER precedes it.
        /// </summary>
        public const string Up_Backfill_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DenormaliseCopilotChatUserAndTime';
DECLARE @stepStart datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

-------------------------------------------------------------------------------------------------
-- 2. Backfill, batched along the CLUSTERED KEY (event_id).
--
--    NOT 'UPDATE TOP (n) ... WHERE time_stamp IS NULL': that was measured at 792,908 ms and
--    157M+ logical reads for 12M rows, because each batch rescans from the start of the table to
--    find rows that are still NULL. Walking the clustered key with a watermark makes every batch a
--    bounded range seek, so the backfill is O(N) and is resumable - a re-run re-walks the key order
--    but the NULL check inside each already-bounded range makes completed batches almost free.
--
--    LEFT-less INNER JOIN is correct here: a copilot_chats row without its audit event cannot exist
--    (foreign key copilot_chats.event_id -> audit_events.id), and if one somehow did, leaving it NULL
--    matches the old INNER JOIN semantics exactly - it was invisible to every report before too.
--
--    time_stamp IS NULL is the canonical 'not yet backfilled' test: audit_events.time_stamp is NOT NULL,
--    so a backfilled row always has one. It is NOT written as OR user_id IS NULL because
--    audit_events.user_id IS nullable, so a correctly-backfilled row may legitimately keep a NULL user_id -
--    testing for that would make this loop re-write the same rows on every run and never converge. It is
--    also the SARGable form once IX_copilot_chats_time_stamp_user_id exists (NULLs sort first).
--
--    NOTE: this migration is stamped once and never runs again, but the columns stay NULLable, so a chat
--    inserted by an OLD importer during the upgrade window would keep NULL for ever and be invisible to
--    every report. The importer therefore runs a bounded self-healing repair
--    (repair_denormalised_copilot_columns.sql, called once per cycle from the web-job top level in
--    Program.cs, OUTSIDE the DownloadActivityData try/catch) for exactly that case. Do not remove one
--    without the other, and do not move that call inside the import - see the header of that script for
--    the three placements that were tried and each left a hole.
-------------------------------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM dbo.copilot_chats WHERE time_stamp IS NULL)
BEGIN
    DECLARE @batch int = 50000;
    DECLARE @lo uniqueidentifier = '00000000-0000-0000-0000-000000000000';
    DECLARE @hi uniqueidentifier;
    DECLARE @updated bigint = 0;
    DECLARE @batchNo int = 0;

    RAISERROR('DenormaliseCopilotChatUserAndTime: backfilling user_id / time_stamp from dbo.audit_events...', 0, 1) WITH NOWAIT;

    WHILE 1 = 1
    BEGIN
        SET @hi = NULL;

        SELECT TOP (1) @hi = b.event_id
        FROM (
            SELECT TOP (@batch) c.event_id
            FROM dbo.copilot_chats AS c
            WHERE c.event_id > @lo
            ORDER BY c.event_id
        ) AS b
        ORDER BY b.event_id DESC;

        IF @hi IS NULL BREAK;

        UPDATE c
        SET c.user_id    = ae.user_id,
            c.time_stamp = ae.time_stamp
        FROM dbo.copilot_chats AS c
        INNER JOIN dbo.audit_events AS ae ON ae.id = c.event_id
        WHERE c.event_id > @lo
          AND c.event_id <= @hi
          AND c.time_stamp IS NULL;

        SET @updated += @@ROWCOUNT;
        SET @batchNo += 1;
        SET @lo = @hi;

        IF @batchNo % 20 = 0
        BEGIN
            SET @msg = @migration + N': backfill in progress - ' + CAST(@updated AS nvarchar(20))
                     + N' row(s) written after ' + CAST(@batchNo AS nvarchar(10)) + N' batch(es), '
                     + CAST(DATEDIFF(SECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N's elapsed.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END

    SET @msg = @migration + N': backfill complete - ' + CAST(@updated AS nvarchar(20)) + N' row(s) in '
             + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('DenormaliseCopilotChatUserAndTime: backfill already complete; skipping.', 0, 1) WITH NOWAIT;

-------------------------------------------------------------------------------------------------
-- 3. The covering index. Keyed (time_stamp, user_id) - see the migration doc comment for why
--    time_stamp leads. event_id rides along as the clustering key / row locator.
-------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('dbo.copilot_chats')
                 AND name = 'IX_copilot_chats_time_stamp_user_id')
BEGIN
    SET @stepStart = SYSUTCDATETIME();

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            RAISERROR('DenormaliseCopilotChatUserAndTime: creating IX_copilot_chats_time_stamp_user_id (ONLINE)...', 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [IX_copilot_chats_time_stamp_user_id]
                         ON [dbo].[copilot_chats] ([time_stamp], [user_id])
                         INCLUDE ([app_host], [agent_id]) WITH (ONLINE = ON);';
            EXEC sp_executesql @sql;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE index build unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID('dbo.copilot_chats')
                     AND name = 'IX_copilot_chats_time_stamp_user_id')
    BEGIN
        RAISERROR('DenormaliseCopilotChatUserAndTime: creating IX_copilot_chats_time_stamp_user_id (offline)...', 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [IX_copilot_chats_time_stamp_user_id]
                     ON [dbo].[copilot_chats] ([time_stamp], [user_id])
                     INCLUDE ([app_host], [agent_id]);';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': index created in '
             + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('DenormaliseCopilotChatUserAndTime: IX_copilot_chats_time_stamp_user_id already exists; skipping.', 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// The reverse: drop the index and both columns. Kept as a <c>public const</c> so tests can replay
        /// it directly, matching <c>AddAuditEventsOperationIndex.Down_Sql</c>.
        /// </summary>
        public const string Down_Sql = @"
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID('dbo.copilot_chats')
             AND name = 'IX_copilot_chats_time_stamp_user_id')
    DROP INDEX [IX_copilot_chats_time_stamp_user_id] ON [dbo].[copilot_chats];

IF COL_LENGTH('dbo.copilot_chats', 'time_stamp') IS NOT NULL
    ALTER TABLE dbo.copilot_chats DROP COLUMN time_stamp;

IF COL_LENGTH('dbo.copilot_chats', 'user_id') IS NOT NULL
    ALTER TABLE dbo.copilot_chats DROP COLUMN user_id;
";

        public override void Up()
        {
            // suppressTransaction on every step: each commits independently so a long backfill cannot be
            // rolled back wholesale, schema locks release promptly, and a partial apply converges on re-run.
            Sql(Up_Sql, suppressTransaction: true);
            Sql(Up_Backfill_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
