/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202609201430001_DropCopilotAdoptionPeriodTables

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact schema change the migration performs, followed by the __MigrationHistory stamp so
   EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as
   applied.

   WHAT IT DOES
     Drops the seven Copilot Adoption closed-period tables, children before parents:

         copilot_adoption_intervention
         copilot_adoption_cohort_member
         copilot_adoption_cohort
         copilot_adoption_user_period
         copilot_adoption_period_run
         copilot_adoption_targets
         copilot_adoption_digest_run

   WHY
     Comparison over time has been removed from the Copilot Adoption tool. It now does two things:
     it shows adoption as it is now, and it exports a complete Excel workbook. Comparing two points
     in time is done by exporting the workbook twice and diffing the two files - the "Snapshot facts"
     sheet exists for exactly that - rather than by storing period history in the database.

     No code in the product reads or writes these tables any more. The scheduled Copilot Adoption
     digest email, which was the only thing that ever caused a period to be published, has been
     removed with them.

   *** THIS IS DESTRUCTIVE AND IRREVERSIBLE ***
     Any stored period history, customer-defined target, frozen cohort, intervention record or
     digest send record is deleted and cannot be recovered except from a database backup. Take one
     before running this if any of that history matters to you.

     WHAT MAY ACTUALLY BE IN THEM - read this before assuming there is nothing to lose:

       * copilot_adoption_period_run, copilot_adoption_user_period, copilot_adoption_targets and
         copilot_adoption_digest_run only ever populated on a deployment that had explicitly
         configured the Copilot Adoption digest email (recipients AND a sender), because publishing
         a period was a side effect of sending that mail, and a target could not be created without
         a published period. On every other deployment these four are empty.

       * copilot_adoption_cohort, copilot_adoption_cohort_member and copilot_adoption_intervention
         are NOT subject to that. The portal's "Start intervention" button on the enablement plan
         built them from the live analysis and required neither the digest nor a published period,
         so these three can hold records on ANY deployment. If anyone has used that button, this
         script deletes the frozen groups and the intervention notes they recorded.

     Each drop prints the table name and the number of rows it is about to destroy, so you can run
     it, read the output, and stop before the next release if the counts surprise you.

   SAFETY
     * Idempotent / re-runnable: each drop is guarded by OBJECT_ID(...) IS NOT NULL, so a database
       that never had these tables, or a second run, is a clean no-op.
     * Drop order is foreign-key safe: copilot_adoption_intervention and copilot_adoption_cohort_member
       both cascade to copilot_adoption_cohort, and copilot_adoption_user_period and
       copilot_adoption_cohort_member cascade to dbo.users. No constraint has to be dropped separately.
     * DROP TABLE is a metadata operation, so this is fast at any row count. No maintenance window is
       required and the importer does not need to be stopped.
     * Each drop logs the table name and the row count being destroyed via RAISERROR ... WITH NOWAIT,
       so the session output is a record of exactly what was removed.

   RUN ORDER
     The manual upgrade scripts form a strict chain: run them in migration-id order. This one's
     immediate predecessor is 202609190900001_IndexPlatformUserActivityLogDate, and the stamp below
     hard-fails if that row is not already present in __MigrationHistory.
   ===================================================================================================== */

SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DropCopilotAdoptionPeriodTables';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @table sysname;
DECLARE @rows bigint;
DECLARE @sql nvarchar(max);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121)
    + N' UTC. Copilot Adoption no longer stores period history; comparison is done by diffing two '
    + N'exported workbooks. The tables below are dropped and their contents are NOT recoverable.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Children before parents, so the cascading foreign keys to copilot_adoption_cohort and dbo.users
-- never block a drop and no constraint has to be dropped on its own.
DECLARE @drop TABLE (seq int IDENTITY(1,1) NOT NULL, name sysname NOT NULL);
INSERT INTO @drop (name) VALUES
    (N'copilot_adoption_intervention'),
    (N'copilot_adoption_cohort_member'),
    (N'copilot_adoption_cohort'),
    (N'copilot_adoption_user_period'),
    (N'copilot_adoption_period_run'),
    (N'copilot_adoption_targets'),
    (N'copilot_adoption_digest_run');

DECLARE tables CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @drop ORDER BY seq;
OPEN tables;
FETCH NEXT FROM tables INTO @table;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist, nothing to do.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        -- Reported before the drop so the session log records what was destroyed. Read from
        -- sys.partitions rather than COUNT(*) because it is O(1) and this is a log line, not a
        -- decision - nothing branches on the value.
        SET @rows = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions AS p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @table) AND p.index_id IN (0, 1));
        SET @msg = @migration + N': dropping dbo.' + @table + N' (' + CAST(@rows AS nvarchar(20))
            + N' row(s)). This is irreversible.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;

        SET @sql = N'DROP TABLE [dbo].[' + @table + N'];';
        EXEC sp_executesql @sql;
    END

    FETCH NEXT FROM tables INTO @table;
END

CLOSE tables;
DEALLOCATE tables;

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

/* ---------------------------------------------------------------------------------------------------
   Record the migration as applied.

   The guard checks SCHEMA only - that the tables this script removes are actually gone. It
   deliberately does NOT check any data state: a data-state guard is the shape that has previously
   refused to stamp a successfully-completed migration and stranded the rest of the chain behind it.

   The check matters because a severity-16 RAISERROR does not abort a batch - sqlcmd and SSMS carry on
   to the next one - so an unguarded stamp would record a failed apply as complete, after which EF
   never retries it.

   The Model blob is copied verbatim from the predecessor row: this migration changes only the SQL
   schema and not the EF entity model (none of these tables ever had an entity or a DbSet), so the
   two snapshots are byte-identical.
   --------------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
               WHERE MigrationId = N'202609201430001_DropCopilotAdoptionPeriodTables')
BEGIN
    IF OBJECT_ID(N'dbo.copilot_adoption_period_run', N'U') IS NOT NULL
        OR OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NOT NULL
        OR OBJECT_ID(N'dbo.copilot_adoption_targets', N'U') IS NOT NULL
        OR OBJECT_ID(N'dbo.copilot_adoption_cohort', N'U') IS NOT NULL
        OR OBJECT_ID(N'dbo.copilot_adoption_cohort_member', N'U') IS NOT NULL
        OR OBJECT_ID(N'dbo.copilot_adoption_intervention', N'U') IS NOT NULL
        OR OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NOT NULL
        RAISERROR('DropCopilotAdoptionPeriodTables: NOT stamped - at least one Copilot Adoption period table still exists, so the drops did not complete. Re-run this script, or run the installer to reconcile.', 16, 1);
    ELSE IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
                        WHERE MigrationId = N'202609190900001_IndexPlatformUserActivityLogDate')
        RAISERROR('DropCopilotAdoptionPeriodTables: the tables were dropped, but prerequisite migration 202609190900001_IndexPlatformUserActivityLogDate is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
    ELSE
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609201430001_DropCopilotAdoptionPeriodTables', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609190900001_IndexPlatformUserActivityLogDate';
        RAISERROR('DropCopilotAdoptionPeriodTables: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
END
ELSE
    RAISERROR('DropCopilotAdoptionPeriodTables: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;