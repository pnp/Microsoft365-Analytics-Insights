namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Moves the last of the runtime schema hacks into a migration, so the database schema is fully
    /// described by <c>Create DB.sql</c> plus the migration chain rather than partly by whatever the
    /// App Insights web-job happened to do at startup.
    ///
    /// WHAT WAS WRONG
    ///   <c>WebJob.AppInsightsImporter.Engine.Sql.ImportDbHacks</c> - "the class of shame" - created two
    ///   objects at runtime that exist in no migration and in no <c>Create DB.sql</c>:
    ///     * <c>dbo.hits.IX_PageRequestID</c>, the UNIQUE index on <c>page_request_id</c>. This is the
    ///       constraint the whole of #165 is about, and it existed only because the importer created it
    ///       on startup. A deployment that had never run the App Insights web-job had no such constraint,
    ///       while <c>__MigrationHistory</c> and the Health page both reported the schema up to date.
    ///     * <c>dbo.sessions.IX_ai_session_id</c> plus the case-sensitive collation on
    ///       <c>sessions.ai_session_id</c>, re-checked on EVERY import batch.
    ///
    ///   The duplicate-hit cleanup was also expensive in a way that scaled with the largest table in the
    ///   database: on every importer start it aggregated the whole of <c>dbo.hits</c> to find duplicate
    ///   <c>page_request_id</c>s, deleted them one row at a time through a CURSOR with a PRINT per row,
    ///   and then aggregated <c>dbo.hits</c> a second time to return a result set nobody read. On a
    ///   ~200,000-user tenant that is two full passes of the biggest table before the import can begin.
    ///
    /// WHAT THIS DOES
    ///   Brings both objects under the migration chain, idempotently. On any database that has run the
    ///   App Insights importer in the last several versions they already exist, so this is a
    ///   METADATA-ONLY no-op: the guards below check <c>sys.indexes</c> and <c>sys.columns</c> and skip
    ///   without ever touching <c>dbo.hits</c> or <c>dbo.sessions</c>. It only does real work on a fresh
    ///   install, or on a deployment that somehow never ran the importer.
    ///
    ///   Where it does have to de-duplicate, it does so SET-BASED and in batches instead of through a
    ///   cursor.
    ///
    /// NOT PERFORMANCE-MOTIVATED as a schema change - the indexes it creates already exist in the field,
    /// so there is no before/after query to benchmark. The performance win is the removal of the
    /// per-startup and per-batch work from the importer, which is a code change, not a schema one.
    ///
    /// UPGRADE COST
    ///   * Normal case - the App Insights importer has run at some point, so both indexes and the
    ///     collation are already correct: METADATA ONLY. Every guard is a sys.indexes / sys.columns
    ///     lookup and neither dbo.hits nor dbo.sessions is touched. Measured at milliseconds, and
    ///     independent of table size. This is what essentially every upgrading customer gets.
    ///   * Fresh install, or a deployment that never ran that importer: dbo.hits and dbo.sessions are
    ///     empty or tiny, so the index builds are instant.
    ///   * The only slow case is a database that HAS a populated dbo.hits but has lost
    ///     IX_PageRequestID (a DBA dropped it, or a restore lost it). Then the de-duplication runs, and
    ///     it re-evaluates ROW_NUMBER() over the whole of dbo.hits once per 20,000-row batch - so its
    ///     cost is one full pass of dbo.hits per batch of duplicates removed. With no duplicates it is
    ///     a single pass and nothing is deleted. If you are in that state with a large dbo.hits AND
    ///     many duplicates, expect this to be the long pole of the upgrade and run it in a maintenance
    ///     window; the subsequent UNIQUE index build on dbo.hits is ONLINE only on Enterprise / Azure
    ///     SQL DB / Azure SQL MI and offline (table-locking) everywhere else.
    ///
    /// IX_ai_session_id IS DELIBERATELY NOT UNIQUE. <c>sessions.ai_session_id</c> can legitimately
    /// duplicate, and the hits merge depends on tolerating that - see the ROW_NUMBER() fan-out defence in
    /// "Migrate Hits Import into Hits.sql" and issue #165. The hack actively rebuilt this index as
    /// non-unique if it found it unique, and that behaviour is preserved here rather than quietly dropped.
    ///
    /// The EF entity model is unchanged - collation and index shape are physical only - so the .resx
    /// snapshot is a byte-identical copy of its predecessor's
    /// (202609101100001_UniqueUrlsFullUrlIndex), per the repo's migration rules. The manual upgrade
    /// script therefore stamps __MigrationHistory by copying the predecessor's row.
    /// </summary>
    public partial class RetireImportDbHacks : DbMigration
    {
        /// <summary>Width of the de-duplication batches. Bounded so the log and lock footprint stay small.</summary>
        public const int BatchSize = 20000;

        /// <summary>
        /// Creates <c>IX_PageRequestID</c> and <c>IX_ai_session_id</c> and fixes the
        /// <c>ai_session_id</c> collation, if they are not already correct. Exposed as a constant so the
        /// manual upgrade script and the unit tests use the exact same SQL. Idempotent, guarded,
        /// resumable and edition-aware.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'RetireImportDbHacks';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);
DECLARE @rows bigint;
DECLARE @total bigint;
DECLARE @batch int = 20000;
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;

SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).'
           ELSE N'is not supported on this edition.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

RAISERROR('RetireImportDbHacks: on a database that has run the App Insights importer these objects already exist, in which case this migration is a metadata-only no-op and completes in milliseconds.', 0, 1) WITH NOWAIT;

/* =====================================================================================================
   1. dbo.sessions.ai_session_id - case-sensitive collation + a NON-UNIQUE IX_ai_session_id.

      Application Insights session ids differ only by case in some payloads, which is why the column is
      case-sensitive. The column stays varchar(50): these ids are machine-generated ASCII, not customer
      text, so the project's ""customer text must be nvarchar"" rule does not apply, and widening it would
      rewrite a very large table for no benefit.
   ===================================================================================================== */
IF OBJECT_ID(N'dbo.sessions', N'U') IS NULL
BEGIN
    RAISERROR('RetireImportDbHacks: dbo.sessions does not exist; skipping the session steps.', 0, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'ai_session_id')
BEGIN
    RAISERROR('RetireImportDbHacks: dbo.sessions.ai_session_id does not exist; skipping the session steps.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'ai_session_id'
                 AND collation_name <> N'SQL_Latin1_General_CP1_CS_AS')
    BEGIN
        SET @stepStart = SYSUTCDATETIME();

        -- ALTER COLUMN fails with error 5074 while an index depends on the column, so any existing
        -- index has to come off first and be rebuilt afterwards. The original hack altered the column
        -- before creating the index and so could not repair a database that already had both.
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'IX_ai_session_id')
        BEGIN
            RAISERROR('RetireImportDbHacks: dropping IX_ai_session_id so the collation can be changed...', 0, 1) WITH NOWAIT;
            DROP INDEX [IX_ai_session_id] ON [dbo].[sessions];
        END

        -- NULL is stated explicitly: ALTER COLUMN that omits it makes the column nullable regardless of
        -- what it was, so relying on the default would be a silent schema change on some databases.
        RAISERROR('RetireImportDbHacks: changing dbo.sessions.ai_session_id to a case-sensitive collation...', 0, 1) WITH NOWAIT;
        ALTER TABLE [dbo].[sessions] ALTER COLUMN [ai_session_id] varchar(50) COLLATE SQL_Latin1_General_CP1_CS_AS NULL;

        SET @msg = @migration + N': collation changed in '
            + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('RetireImportDbHacks: dbo.sessions.ai_session_id already has the case-sensitive collation.', 0, 1) WITH NOWAIT;

    -- The index must exist and must NOT be unique. A unique one is rebuilt rather than left in place:
    -- duplicate ai_session_id values are legitimate and the hits merge relies on being able to hold them.
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'IX_ai_session_id' AND is_unique = 1)
    BEGIN
        RAISERROR('RetireImportDbHacks: IX_ai_session_id is UNIQUE, which it must not be; rebuilding it as non-unique...', 0, 1) WITH NOWAIT;
        DROP INDEX [IX_ai_session_id] ON [dbo].[sessions];
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'IX_ai_session_id')
    BEGIN
        SET @stepStart = SYSUTCDATETIME();
        SET @onlineDone = 0;

        IF @canOnline = 1
        BEGIN
            BEGIN TRY
                RAISERROR('RetireImportDbHacks: creating IX_ai_session_id WITH (ONLINE = ON)...', 0, 1) WITH NOWAIT;
                -- Through sp_executesql on purpose: the ""Online index operations can only be performed in
                -- Enterprise edition"" error aborts the batch and is NOT catchable for a plain statement,
                -- but IS catchable when executed this way.
                SET @sql = N'CREATE NONCLUSTERED INDEX [IX_ai_session_id] ON [dbo].[sessions] ([ai_session_id] ASC) WITH (ONLINE = ON);';
                EXEC sp_executesql @sql;
                SET @onlineDone = 1;
            END TRY
            BEGIN CATCH
                SET @msg = @migration + N': ONLINE build unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END CATCH
        END

        IF @onlineDone = 0
        BEGIN
            RAISERROR('RetireImportDbHacks: creating IX_ai_session_id (offline)...', 0, 1) WITH NOWAIT;
            CREATE NONCLUSTERED INDEX [IX_ai_session_id] ON [dbo].[sessions] ([ai_session_id] ASC);
        END

        SET @msg = @migration + N': IX_ai_session_id created in '
            + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms ('
            + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('RetireImportDbHacks: IX_ai_session_id already exists and is non-unique; nothing to do.', 0, 1) WITH NOWAIT;
END

/* =====================================================================================================
   2. dbo.hits.page_request_id - de-duplicate, then a UNIQUE IX_PageRequestID.

      This is the constraint issue #165 is about. The check below is metadata-only, so on a database that
      already has the index - which is every deployment that has run the importer for several versions -
      dbo.hits is never touched.
   ===================================================================================================== */
IF OBJECT_ID(N'dbo.hits', N'U') IS NULL
BEGIN
    RAISERROR('RetireImportDbHacks: dbo.hits does not exist; skipping the hits steps.', 0, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.hits') AND name = N'page_request_id')
BEGIN
    RAISERROR('RetireImportDbHacks: dbo.hits.page_request_id does not exist; skipping the hits steps.', 0, 1) WITH NOWAIT;
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.hits') AND name = N'IX_PageRequestID' AND is_unique = 1)
BEGIN
    -- Already applied. Falls through rather than RETURNing so the manual script still reaches its
    -- __MigrationHistory stamp on a database that is already up to date.
    RAISERROR('RetireImportDbHacks: IX_PageRequestID already exists and is UNIQUE; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    -- A non-unique index of the same name would block the unique one being created.
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.hits') AND name = N'IX_PageRequestID')
    BEGIN
        RAISERROR('RetireImportDbHacks: dropping the existing non-unique IX_PageRequestID...', 0, 1) WITH NOWAIT;
        DROP INDEX [IX_PageRequestID] ON [dbo].[hits];
    END

    /* -------------------------------------------------------------------------------------------------
       De-duplicate, keeping the LOWEST id per page_request_id - the same row the old cursor kept, which
       skipped the first of each group and deleted the rest in id order.

       Set-based and batched rather than row-by-row. dbo.hits_clicked_elements cascades from dbo.hits, so
       the child rows of a deleted duplicate go with it, exactly as they did before.
       ------------------------------------------------------------------------------------------------- */
    SET @stepStart = SYSUTCDATETIME();
    SET @total = 0;
    SET @rows = 1;

    WHILE @rows > 0
    BEGIN
        ;WITH dupes AS (
            SELECT id, ROW_NUMBER() OVER (PARTITION BY page_request_id ORDER BY id) AS rn
            FROM dbo.hits
        )
        DELETE TOP (20000) FROM dupes WHERE rn > 1;

        SET @rows = @@ROWCOUNT;
        SET @total = @total + @rows;

        IF @rows > 0
        BEGIN
            SET @msg = @migration + N': deleted ' + CAST(@total AS nvarchar(20)) + N' duplicate hit(s) so far...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END

    SET @msg = @migration + N': removed ' + CAST(@total AS nvarchar(20)) + N' duplicate hit row(s) in '
        + CAST(DATEDIFF(SECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N's.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            RAISERROR('RetireImportDbHacks: creating UNIQUE IX_PageRequestID WITH (ONLINE = ON)...', 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE UNIQUE NONCLUSTERED INDEX [IX_PageRequestID] ON [dbo].[hits] ([page_request_id] ASC) WITH (ONLINE = ON);';
            EXEC sp_executesql @sql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE build unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END

    IF @onlineDone = 0
    BEGIN
        RAISERROR('RetireImportDbHacks: creating UNIQUE IX_PageRequestID (offline)...', 0, 1) WITH NOWAIT;
        CREATE UNIQUE NONCLUSTERED INDEX [IX_PageRequestID] ON [dbo].[hits] ([page_request_id] ASC);
    END

    SET @msg = @migration + N': UNIQUE IX_PageRequestID created in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms ('
        + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// Deliberately does nothing. See the remarks on <see cref="Down"/>.
        /// </summary>
        public const string Down_Sql = @"
RAISERROR('RetireImportDbHacks: Down is a no-op by design - see the migration source for why.', 0, 1) WITH NOWAIT;
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'RetireImportDbHacks'. Brings dbo.hits.IX_PageRequestID and dbo.sessions.IX_ai_session_id (plus the ai_session_id collation) under the migration chain, instead of leaving the App Insights web-job to create them at runtime. On any database that has run that importer these already exist, so this is a metadata-only no-op. Runs outside the migration transaction so it is resumable. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");

            Sql(Up_Sql, suppressTransaction: true);
        }

        /// <summary>
        /// A no-op, on purpose.
        /// </summary>
        /// <remarks>
        /// There is no honest inverse. Dropping <c>IX_PageRequestID</c> would remove the constraint that
        /// stops duplicate hits being created - the thing issue #165 exists to protect - and reverting the
        /// <c>ai_session_id</c> collation would rewrite a very large table to reintroduce a bug. Both
        /// objects also pre-date this migration on every existing deployment, so "reverting" would leave
        /// those databases in a state they were never in.
        ///
        /// Rolling back to before this migration therefore leaves the schema in place, which is the safe
        /// outcome: the objects are exactly what the code expects either way.
        /// </remarks>
        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'RetireImportDbHacks'. No-op: the indexes and collation are left in place deliberately - dropping them would reintroduce the duplicate-hit bug from issue #165.");

            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
