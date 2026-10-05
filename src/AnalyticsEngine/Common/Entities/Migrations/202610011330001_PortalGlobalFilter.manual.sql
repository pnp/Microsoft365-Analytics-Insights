/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202610011330001_PortalGlobalFilter

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact schema the migration creates, followed by the __MigrationHistory stamp so EF
   (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as applied.

   WHAT IT DOES
     Creates one new table for the portal's global report filter - the conditions a portal
     administrator sets that every insights report applies on top of a reader's own filters:

       dbo.portal_global_filters    at most one row: the filter's definition, its revision, and when
                                    and by whom it was last changed

   CLASSIFICATION: PURELY ADDITIVE
     One new, empty table. No existing table is rewritten, no existing column is altered, and no
     existing query is being tuned. There is therefore no "before" state to benchmark, and the
     measurement rule that governs performance-motivated schema changes does not apply.

   UPGRADE TIME
     Effectively instantaneous on any tenant size. CREATE TABLE on an empty table is a metadata-only
     operation, and no existing row is read or written. No maintenance window is required and the
     importer does not need to be stopped.

   SAFETY
     * Idempotent / re-runnable: the table is created only if absent, so a second run is a no-op.
     * Creates nothing destructive - no DROP, no ALTER of an existing object, no backfill.
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on
       re-run.
     * Touches no existing rows, so there is nothing to reconcile afterwards.

   PREREQUISITE
     The database must already be on migration 202609221200001_UserOrganisations.
     The __MigrationHistory stamp copies that row's model snapshot, which is byte-identical to this
     one because this migration changes no EF entity model.

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

RAISERROR('PortalGlobalFilter: adding the portal global report filter table. Purely additive schema, so no performance benchmark is required.', 0, 1) WITH NOWAIT;

-- ---------------------------------------------------------------------------
-- portal_global_filters - the administrator's global report filter.
--   One row at most (id = 1). filter_json is the filter's wire form: an array of
--   conditions, each optionally naming an attribute of the person viewing the
--   report (see GlobalFilterCodec). An empty string means no filter.
--   nvarchar, never varchar: the values are department names, cost centres and
--   other text from a customer tenant, routinely in non-Latin scripts.
--   revision moves on every save, so a save from a page opened before somebody
--   else's change is refused rather than quietly putting back what they changed.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.portal_global_filters', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[portal_global_filters]
    (
        [id] int NOT NULL,
        [filter_json] nvarchar(max) NOT NULL CONSTRAINT [DF_portal_global_filters_filter_json] DEFAULT (N''),
        [revision] int NOT NULL CONSTRAINT [DF_portal_global_filters_revision] DEFAULT (1),
        [modified_utc] datetime2(7) NOT NULL CONSTRAINT [DF_portal_global_filters_modified_utc] DEFAULT SYSUTCDATETIME(),
        [modified_by] nvarchar(256) NULL,
        CONSTRAINT [PK_portal_global_filters] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_portal_global_filters_single_row] CHECK ([id] = 1)
    );

    RAISERROR('PortalGlobalFilter: created dbo.portal_global_filters.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('PortalGlobalFilter: dbo.portal_global_filters already exists.', 0, 1) WITH NOWAIT;
END

RAISERROR('PortalGlobalFilter: finished.', 0, 1) WITH NOWAIT;

/* ---------------------------------------------------------------------------------------------------
   Record the migration as applied.

   The guard checks SCHEMA only - that the table this script creates exists with this release's
   columns. It deliberately does NOT check any data state: this migration writes no rows, and a
   data-state guard is the shape that has previously refused to stamp a successfully-completed
   migration and stranded the rest of the chain behind it.

   The schema check still matters, because most severity-16 errors end only their own statement - the
   statements after it run, and sqlcmd and SSMS carry on to the next batch - so an unguarded stamp could
   record a failed apply as complete, after which EF never retries it.
   --------------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
               WHERE MigrationId = N'202610011330001_PortalGlobalFilter')
BEGIN
    IF OBJECT_ID(N'dbo.portal_global_filters', N'U') IS NULL
       OR COL_LENGTH(N'dbo.portal_global_filters', N'filter_json') IS NULL
       OR COL_LENGTH(N'dbo.portal_global_filters', N'revision') IS NULL
       OR COL_LENGTH(N'dbo.portal_global_filters', N'modified_utc') IS NULL
       OR COL_LENGTH(N'dbo.portal_global_filters', N'modified_by') IS NULL
        RAISERROR('PortalGlobalFilter: NOT stamped - dbo.portal_global_filters is missing or has an older shape, so the schema work did not complete. Re-run this script, or run the installer to reconcile.', 16, 1);
    ELSE IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
                        WHERE MigrationId = N'202609221200001_UserOrganisations')
        RAISERROR('PortalGlobalFilter: the table was created, but prerequisite migration 202609221200001_UserOrganisations is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
    ELSE
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202610011330001_PortalGlobalFilter', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609221200001_UserOrganisations';
        RAISERROR('PortalGlobalFilter: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
END
ELSE
    RAISERROR('PortalGlobalFilter: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
