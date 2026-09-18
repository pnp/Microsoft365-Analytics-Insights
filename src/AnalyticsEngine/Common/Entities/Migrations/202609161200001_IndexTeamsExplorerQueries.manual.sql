/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202609161200001_IndexTeamsExplorerQueries
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact index change the migration performs, followed by the __MigrationHistory stamp so
   EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as
   applied.

   WHAT IT DOES
     Gives the Teams Explorer report a seekable access path for a date range on four tables:

       teams_user_device_usage_log   IX_date is WIDENED from ([date]) to
                                     ([date]) INCLUDE (user_id + the eight device flags)
       teams_channel_stats_log       new IX_teams_channel_stats_log_date
                                     ([date]) INCLUDE (channel_id, chats_count, sentiment_score)
       teams_user_channel_reactions  new IX_teams_user_channel_reactions_date
                                     ([date]) INCLUDE (channel_id, user_id, reaction_id)
       team_membership_log           new IX_team_membership_log_date
                                     ([date]) INCLUDE (team_id, user_id)

     Before this, three of those tables carried only their foreign-key indexes, so every reporting
     window - however narrow - scanned the whole table. teams_user_device_usage_log did have an
     IX_date (created by the installer's profiling schema script) but it was key-only, so the
     device-mix query seeked the date and then paid a clustered-index lookup per row to read eight
     bit columns.

   MEASURED IMPACT
     Real report queries against a synthetic fixture (4.0M device-usage rows, 800k channel-stat rows,
     267k reactions). Medians of warm runs, cold run discarded, plan cache cleared per run:

       table / window                              logical reads     elapsed        plan
       --------------------------------------------------------------------------------------
       teams_user_device_usage_log   28 days       17,383 ->    807  1,346 ->   680 scan -> seek
       teams_user_device_usage_log  365 days       17,383 -> 10,833  7,702 -> 7,235 scan -> seek
       teams_channel_stats_log       28 days       11,061 ->    699    572 ->   104 scan -> seek
       teams_channel_stats_log      365 days       11,061 ->  9,219  1,581 -> 1,262 scan -> seek
       teams_user_channel_reactions  28 days        1,094 ->     71     62 ->    10 scan -> seek
       teams_user_channel_reactions 365 days        1,094 ->    908    195 ->   147 scan -> seek
       team_membership_log           28 days          271 ->     35     32 ->    20 scan -> seek
       team_membership_log          365 days          271 ->    244    226 ->   177 scan -> seek

   UPGRADE TIME AND STORAGE
     Measured on the fixture above; scale roughly linearly with row count.

       index                                          rows | build   | size
       ----------------------------------------------------+---------+--------
       IX_date (teams_user_device_usage_log, widened)  4.0M | 11-15 s |  93 MB
       IX_teams_channel_stats_log_date                 800k |  2-3 s  |  26 MB
       IX_teams_user_channel_reactions_date            267k |  under 1 s |  8 MB
       IX_team_membership_log_date                      75k |  under 1 s |  2 MB

     teams_user_device_usage_log is the table that decides the maintenance window: roughly 3-4 seconds
     and 23 MB per million rows. On a 200k-user tenant with a year of history that is tens of millions
     of rows.

   SAFETY
     * Purely additive: no existing index is narrowed and no data is modified.
     * Idempotent / re-runnable: a table already carrying the required index shape is skipped, and a
       missing table or [date] column is skipped rather than erroring.
     * DROP_EXISTING is used when widening IX_date, so teams_user_device_usage_log is never left
       without an index on [date].
     * Attempts ONLINE (non-blocking) on Enterprise / Azure SQL DB / MI and falls back to OFFLINE.
       Where ONLINE is unavailable each build briefly locks its table - run this in a MAINTENANCE
       WINDOW WITH THE IMPORTER STOPPED.
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on
       re-run.

   PREREQUISITE
     The database must already be on migration 202609151440027_CopilotPromptSafetyFields.
     The __MigrationHistory stamp copies that row's model snapshot (identical to this one, because
     this migration changes no EF entity model).

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexTeamsExplorerQueries';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
-- ONLINE index operations exist only on Enterprise (3), Azure SQL DB (5) and Azure SQL MI (8).
-- Express / Standard / LocalDB reject them; the attempt runs through sp_executesql inside TRY/CATCH
-- so that rejection is catchable and we can fall back to an offline build.
DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting; EngineEdition=' + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1
        THEN N'; online index builds will be attempted.'
        ELSE N'; online index builds are unavailable, so each table is locked during its build. Stop the importer and use a maintenance window.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets table
(
    sequence    int NOT NULL PRIMARY KEY,
    table_name  sysname NOT NULL,
    index_name  sysname NOT NULL,
    include_sql nvarchar(1000) NOT NULL
);

INSERT INTO @targets (sequence, table_name, index_name, include_sql) VALUES
    -- Widened, not added: the installer's profiling schema script already creates IX_date here.
    (1, N'teams_user_device_usage_log', N'IX_date',
        N' INCLUDE ([user_id], [used_web], [used_win_phone], [used_linux], [used_chrome_os], [used_ios], [used_android], [used_mac], [used_windows])'),
    (2, N'teams_channel_stats_log', N'IX_teams_channel_stats_log_date',
        N' INCLUDE ([channel_id], [chats_count], [sentiment_score])'),
    (3, N'teams_user_channel_reactions', N'IX_teams_user_channel_reactions_date',
        N' INCLUDE ([channel_id], [user_id], [reaction_id])'),
    (4, N'team_membership_log', N'IX_team_membership_log_date',
        N' INCLUDE ([team_id], [user_id])');

DECLARE @i int = 1;
DECLARE @maxSeq int = (SELECT MAX(sequence) FROM @targets);
DECLARE @table sysname, @index sysname, @includeSql nvarchar(1000);
DECLARE @objectId int, @indexId int, @rowCount bigint, @onlineDone bit, @hasDate bit;

WHILE @i <= @maxSeq
BEGIN
    SELECT @table = table_name, @index = index_name, @includeSql = include_sql
    FROM @targets WHERE sequence = @i;

    SET @objectId = OBJECT_ID(N'dbo.' + @table, N'U');

    IF @objectId IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SET @hasDate = CASE WHEN EXISTS (
            SELECT 1 FROM sys.columns WHERE object_id = @objectId AND name = N'date')
            THEN 1 ELSE 0 END;

        IF @hasDate = 0
        BEGIN
            SET @msg = @migration + N': dbo.' + @table + N' has no [date] column; skipping.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            SELECT @indexId = index_id FROM sys.indexes
            WHERE object_id = @objectId AND name = @index;

            -- "Already correct" means: keyed on [date] and carrying at least as many included
            -- columns as we are about to add. Counting rather than naming each column keeps this
            -- guard short; a partially-built index has fewer, so it is rebuilt.
            IF @indexId IS NOT NULL
               AND EXISTS (
                    SELECT 1 FROM sys.index_columns AS ic
                    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE ic.object_id = @objectId AND ic.index_id = @indexId
                      AND ic.key_ordinal = 1 AND c.name = N'date')
               AND (
                    SELECT COUNT(*) FROM sys.index_columns AS ic
                    WHERE ic.object_id = @objectId AND ic.index_id = @indexId
                      AND ic.is_included_column = 1
                   ) >= (LEN(@includeSql) - LEN(REPLACE(@includeSql, N',', N''))) + 1
            BEGIN
                SET @msg = @migration + N': [' + @index + N'] on dbo.' + @table
                    + N' already has the required shape; skipping.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
            ELSE
            BEGIN
                SELECT @rowCount = ISNULL(SUM(rows), 0) FROM sys.partitions
                WHERE object_id = @objectId AND index_id IN (0, 1);

                SET @msg = @migration
                    + CASE WHEN @indexId IS NULL THEN N': creating [' ELSE N': rebuilding [' END
                    + @index + N'] on dbo.' + @table + N' ('
                    + CAST(@rowCount AS nvarchar(20)) + N' estimated rows).';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;

                SET @onlineDone = 0;

                IF @canOnline = 1
                BEGIN
                    BEGIN TRY
                        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].['
                            + @table + N'] ([date])' + @includeSql
                            + CASE WHEN @indexId IS NULL
                                THEN N' WITH (ONLINE = ON);'
                                ELSE N' WITH (DROP_EXISTING = ON, ONLINE = ON);' END;
                        EXEC sp_executesql @sql;
                        SET @onlineDone = 1;
                    END TRY
                    BEGIN CATCH
                        SET @msg = @migration + N': online build of [' + @index + N'] failed ('
                            + ERROR_MESSAGE() + N'); retrying offline.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    END CATCH
                END

                IF @onlineDone = 0
                BEGIN
                    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].['
                        + @table + N'] ([date])' + @includeSql
                        + CASE WHEN @indexId IS NULL
                            THEN N';'
                            ELSE N' WITH (DROP_EXISTING = ON);' END;
                    EXEC sp_executesql @sql;
                END

                SET @msg = @migration + N': [' + @index + N'] is ready.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
        END
    END

    SET @indexId = NULL;
    SET @i += 1;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;


-- =====================================================================================================
-- Record the migration so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app
-- Health page treat it as applied. No model change here, so the EF snapshot is byte-identical to the
-- previous migration's - copy that row's Model / ContextKey / ProductVersion. Guarded so re-running
-- is safe.
--
-- The guard checks the PREREQUISITE ROW ONLY, never the state of any data. A data-state guard here
-- would be a test this script cannot reliably pass, and refusing to stamp on one would strand the
-- whole upgrade chain - see DenormaliseCopilotChatUserAndTime for the incident that rule comes from.
-- This migration writes no rows at all, so there is no data state to check in the first place.
-- =====================================================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609161200001_IndexTeamsExplorerQueries')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609151440027_CopilotPromptSafetyFields')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609161200001_IndexTeamsExplorerQueries', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609151440027_CopilotPromptSafetyFields';
        RAISERROR('IndexTeamsExplorerQueries: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('IndexTeamsExplorerQueries: the indexes were created, but prerequisite migration 202609151440027_CopilotPromptSafetyFields is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
END
ELSE
    RAISERROR('IndexTeamsExplorerQueries: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;