/* =====================================================================================================
   MANUAL DATABASE UPGRADE SCRIPT
   Migration: 202609101200001_RetireImportDbHacks

   For DBAs who upgrade the database by hand instead of running AnalyticsInstaller.exe --initdb.
   Applies the migration's Up SQL verbatim (the same Up_Sql constant the installer uses), then stamps
   dbo.__MigrationHistory so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app
   Health page treat it as applied.

   PREREQUISITE
     202609101100001_UniqueUrlsFullUrlIndex must already be stamped - the script refuses to run
     otherwise. Run the release's manual scripts in migration-id order.

   WHAT IT DOES
     Brings two objects under the migration chain that used to be created at runtime by the App Insights
     web-job (the now-deleted ImportDbHacks class):
       * dbo.hits.IX_PageRequestID          - UNIQUE index on page_request_id (issue #165)
       * dbo.sessions.IX_ai_session_id      - NON-unique index, plus the case-sensitive collation on
                                              sessions.ai_session_id

   EXPECTED RUNTIME
     On any database that has run the App Insights importer in the last several versions both objects
     ALREADY EXIST, so this is a metadata-only no-op that completes in milliseconds and never touches
     dbo.hits or dbo.sessions. It only does real work on a fresh install, or on a deployment that
     somehow never ran that importer - in which case it de-duplicates dbo.hits in batches and builds the
     indexes (ONLINE where the edition supports it), and should be run in a maintenance window.

   SAFE TO RE-RUN: every step is guarded and idempotent.
   ===================================================================================================== */

SET NOCOUNT ON;

/* -----------------------------------------------------------------------------------------------------
   Pre-flight. Anything wrong here aborts with SET NOEXEC ON having changed nothing.
   ----------------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('RetireImportDbHacks: dbo.__MigrationHistory does not exist - this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101100001_UniqueUrlsFullUrlIndex')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101200001_RetireImportDbHacks')
BEGIN
    RAISERROR('RetireImportDbHacks: prerequisite migration 202609101100001_UniqueUrlsFullUrlIndex is not stamped in __MigrationHistory. Run the manual scripts in migration-id order. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

RAISERROR('RetireImportDbHacks: pre-flight checks passed.', 0, 1) WITH NOWAIT;
GO

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
      text, so the project's "customer text must be nvarchar" rule does not apply, and widening it would
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
                -- Through sp_executesql on purpose: the "Online index operations can only be performed in
                -- Enterprise edition" error aborts the batch and is NOT catchable for a plain statement,
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

/* =====================================================================================================
   POST-FLIGHT: prove the schema work actually landed BEFORE stamping __MigrationHistory.

   sqlcmd and SSMS do NOT stop at a severity-16 error - they abandon the failing batch and carry on with
   the next one. Without this check, a CREATE INDEX that failed would still fall through to the stamp
   below and the database would be recorded as migrated while the objects were missing.

   This checks SCHEMA ONLY. It must never be widened into a check on DATA STATE (e.g. "are there zero
   duplicate hits left"): a data-state guard cannot be satisfied reliably on a live database and has
   already broken a customer upgrade once (see DenormaliseCopilotChatUserAndTime).
   ===================================================================================================== */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.hits') AND name = N'IX_PageRequestID' AND is_unique = 1)
   OR NOT EXISTS (SELECT 1 FROM sys.indexes
                  WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'IX_ai_session_id' AND is_unique = 0)
BEGIN
    RAISERROR('RetireImportDbHacks: NOT stamped - the schema work did not complete. Expected a UNIQUE dbo.hits.IX_PageRequestID and a NON-UNIQUE dbo.sessions.IX_ai_session_id. Review the errors above (sqlcmd/SSMS continue past failed batches, so the real error may be further up), then re-run this script - it is idempotent and will resume.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

/* =====================================================================================================
   Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
   page treat it as applied.

   This migration does NOT change the EF entity model - collation and index shape are physical only - so
   its snapshot is byte-identical to its predecessor's and the stamp simply copies that row rather than
   embedding the model blob again.

   Guarded so a re-run is a no-op, and conditional on the predecessor being present so the scripts cannot
   be applied out of order.
   ===================================================================================================== */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101200001_RetireImportDbHacks')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101100001_UniqueUrlsFullUrlIndex')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609101200001_RetireImportDbHacks', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609101100001_UniqueUrlsFullUrlIndex';
        RAISERROR('RetireImportDbHacks: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('RetireImportDbHacks: the schema change was applied, but prerequisite migration 202609101100001_UniqueUrlsFullUrlIndex is missing from __MigrationHistory, so it was NOT stamped. Run the manual scripts in migration-id order.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('RetireImportDbHacks: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
SET NOEXEC OFF;
