/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202609221200001_UserOrganisations

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact schema the migration creates, followed by the __MigrationHistory stamp so EF
   (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as applied.

   WHAT IT DOES
     Creates five new tables for configurable user organisations - admin-defined grouping dimensions
     for users, populated either from a custom Entra attribute during the normal user-metadata import
     or from a CSV uploaded in the portal:

       dbo.user_org_types           the dimensions themselves (name, source, Entra attribute)
       dbo.user_org_values          the distinct values seen for each dimension
       dbo.user_org_assignments     which user is in which org (at most one value per type per user)
       dbo.user_org_import_jobs     one row per CSV upload, with status and row counts
       dbo.user_org_import_staging  the parsed rows of an upload, awaiting merge

     NOT TO BE CONFUSED WITH dbo.orgs / dbo.org_urls. Those are an unrelated and much older concept -
     mapping SharePoint URL bases so web-traffic hits can be scoped - and this script does not touch
     them. Everything created here is prefixed user_org_ for exactly that reason.

   CLASSIFICATION: PURELY ADDITIVE
     Five new, empty tables. No existing table is rewritten, no existing column is altered, and no
     existing query is being tuned. There is therefore no "before" state to benchmark, and the
     measurement rule that governs performance-motivated schema changes does not apply.

   UPGRADE TIME
     Effectively instantaneous on any tenant size. CREATE TABLE on an empty table is a metadata-only
     operation, and the indexes are built over zero rows. This is independent of how large
     dbo.users is - the only existing table referenced - because no existing row is read or written.
     No maintenance window is required and the importer does not need to be stopped.

   SAFETY
     * Idempotent / re-runnable: every object is created only if absent, so a second run is a no-op.
     * Creates nothing destructive - no DROP, no ALTER of an existing object, no backfill.
     * dbo.user_org_assignments is skipped (with a message) if dbo.users has no primary key yet, so
       the script is safe on a partially-created database and converges on re-run.
     * No wrapping transaction (matches suppressTransaction: true); an interrupted run converges on
       re-run.
     * Touches no existing rows, so there is nothing to reconcile afterwards.

   PREREQUISITE
     The database must already be on migration 202609201430001_DropCopilotAdoptionPeriodTables.
     The __MigrationHistory stamp copies that row's model snapshot, which is byte-identical to this
     one because this migration changes no EF entity model.

   Run against the Analytics database.
   ===================================================================================================== */
SET NOCOUNT ON;

RAISERROR('UserOrganisations: adding configurable user organisation tables. Purely additive schema, so no performance benchmark is required.', 0, 1) WITH NOWAIT;

-- ---------------------------------------------------------------------------
-- user_org_types - the admin-defined dimensions themselves.
--   source_kind: 1 = Entra attribute, 2 = CSV upload.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.user_org_types', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[user_org_types]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [name] nvarchar(100) NOT NULL,
        [source_kind] tinyint NOT NULL,
        [entra_attribute_name] nvarchar(200) NULL,
        [is_enabled] bit NOT NULL CONSTRAINT [DF_user_org_types_is_enabled] DEFAULT (1),
        [created_utc] datetime2(7) NOT NULL CONSTRAINT [DF_user_org_types_created_utc] DEFAULT SYSUTCDATETIME(),
        [modified_utc] datetime2(7) NULL,
        CONSTRAINT [PK_user_org_types] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_user_org_types_source_kind] CHECK ([source_kind] IN (1, 2))
    );

    RAISERROR('UserOrganisations: created dbo.user_org_types.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('UserOrganisations: dbo.user_org_types already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.user_org_types', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_org_types') AND name = N'UX_user_org_types_name')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_user_org_types_name] ON [dbo].[user_org_types] ([name] ASC);
    RAISERROR('UserOrganisations: created UX_user_org_types_name.', 0, 1) WITH NOWAIT;
END

-- ---------------------------------------------------------------------------
-- user_org_values - the distinct values seen for each org type.
-- nvarchar, never varchar: org names come from a customer tenant and routinely
-- contain non-Latin scripts. 200 chars keeps the indexed column well inside
-- SQL Server's 1700-byte index key limit.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.user_org_values', N'U') IS NULL
   AND OBJECT_ID(N'dbo.user_org_types', N'U') IS NOT NULL
BEGIN
    CREATE TABLE [dbo].[user_org_values]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [org_type_id] int NOT NULL,
        [name] nvarchar(200) NOT NULL,
        CONSTRAINT [PK_user_org_values] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [FK_user_org_values_type] FOREIGN KEY ([org_type_id])
            REFERENCES [dbo].[user_org_types] ([id])
    );

    RAISERROR('UserOrganisations: created dbo.user_org_values.', 0, 1) WITH NOWAIT;
END
ELSE IF OBJECT_ID(N'dbo.user_org_values', N'U') IS NOT NULL
BEGIN
    RAISERROR('UserOrganisations: dbo.user_org_values already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.user_org_values', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_org_values') AND name = N'UX_user_org_values_type_name')
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_user_org_values_type_name]
            ON [dbo].[user_org_values] ([org_type_id] ASC, [name] ASC);
        RAISERROR('UserOrganisations: created UX_user_org_values_type_name.', 0, 1) WITH NOWAIT;
    END

    -- Target of the composite foreign key from user_org_assignments. Without this, SQL Server has
    -- nothing to point that key at, and a user could be assigned a value belonging to a different
    -- org type entirely.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_org_values') AND name = N'UX_user_org_values_id_type')
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_user_org_values_id_type]
            ON [dbo].[user_org_values] ([id] ASC, [org_type_id] ASC);
        RAISERROR('UserOrganisations: created UX_user_org_values_id_type.', 0, 1) WITH NOWAIT;
    END
END

-- ---------------------------------------------------------------------------
-- user_org_assignments - which user is in which org.
-- The primary key (user_id, org_type_id) is what enforces the product rule that
-- a user has AT MOST ONE value per org type. The composite foreign key on
-- (org_value_id, org_type_id) makes it structurally impossible to assign a value
-- that belongs to a different org type.
-- Exactly one cascade path (from users), because SQL Server rejects multiple
-- cascade paths to the same table; deleting an org type clears its children
-- explicitly, in one transaction.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.user_org_assignments', N'U') IS NULL
   AND OBJECT_ID(N'dbo.user_org_values', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.users', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.users') AND type = N'PK')
BEGIN
    CREATE TABLE [dbo].[user_org_assignments]
    (
        [user_id] int NOT NULL,
        [org_type_id] int NOT NULL,
        [org_value_id] int NOT NULL,
        [last_updated_utc] datetime2(7) NOT NULL CONSTRAINT [DF_user_org_assignments_last_updated_utc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_user_org_assignments] PRIMARY KEY CLUSTERED ([user_id] ASC, [org_type_id] ASC),
        CONSTRAINT [FK_user_org_assignments_users] FOREIGN KEY ([user_id])
            REFERENCES [dbo].[users] ([id]) ON DELETE CASCADE,
        CONSTRAINT [FK_user_org_assignments_value] FOREIGN KEY ([org_value_id], [org_type_id])
            REFERENCES [dbo].[user_org_values] ([id], [org_type_id])
    );

    RAISERROR('UserOrganisations: created dbo.user_org_assignments.', 0, 1) WITH NOWAIT;
END
ELSE IF OBJECT_ID(N'dbo.user_org_assignments', N'U') IS NOT NULL
BEGIN
    RAISERROR('UserOrganisations: dbo.user_org_assignments already exists.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('UserOrganisations: skipped dbo.user_org_assignments - dbo.users or dbo.user_org_values is not ready yet.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.user_org_assignments', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_org_assignments') AND name = N'IX_user_org_assignments_value')
BEGIN
    -- Serves the reverse question: which users are in this org? The clustered key answers
    -- per-user lookups; this one answers per-org ones.
    CREATE NONCLUSTERED INDEX [IX_user_org_assignments_value]
        ON [dbo].[user_org_assignments] ([org_type_id] ASC, [org_value_id] ASC)
        INCLUDE ([user_id]);
    RAISERROR('UserOrganisations: created IX_user_org_assignments_value.', 0, 1) WITH NOWAIT;
END

-- ---------------------------------------------------------------------------
-- user_org_import_jobs - one row per CSV upload.
--   mode:   1 = Replace, 2 = Merge
--   status: 1 = Pending, 2 = Running, 3 = Succeeded, 4 = Failed, 5 = Cancelled
-- Persisted in SQL rather than held in memory so a job survives an App Service
-- recycle: an interrupted import stays visible (stale heartbeat) instead of
-- vanishing silently, and the row doubles as the audit trail.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.user_org_import_jobs', N'U') IS NULL
   AND OBJECT_ID(N'dbo.user_org_types', N'U') IS NOT NULL
BEGIN
    CREATE TABLE [dbo].[user_org_import_jobs]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [org_type_id] int NOT NULL,
        [mode] tinyint NOT NULL,
        [status] tinyint NOT NULL,
        [file_name] nvarchar(260) NULL,
        [started_by] nvarchar(256) NOT NULL,
        [queued_utc] datetime2(7) NOT NULL CONSTRAINT [DF_user_org_import_jobs_queued_utc] DEFAULT SYSUTCDATETIME(),
        [started_utc] datetime2(7) NULL,
        [finished_utc] datetime2(7) NULL,
        [heartbeat_utc] datetime2(7) NULL,
        [rows_total] int NOT NULL CONSTRAINT [DF_user_org_import_jobs_rows_total] DEFAULT (0),
        [rows_applied] int NOT NULL CONSTRAINT [DF_user_org_import_jobs_rows_applied] DEFAULT (0),
        [rows_cleared] int NOT NULL CONSTRAINT [DF_user_org_import_jobs_rows_cleared] DEFAULT (0),
        [rows_unknown_upn] int NOT NULL CONSTRAINT [DF_user_org_import_jobs_rows_unknown_upn] DEFAULT (0),
        [rows_invalid] int NOT NULL CONSTRAINT [DF_user_org_import_jobs_rows_invalid] DEFAULT (0),
        [error_message] nvarchar(2000) NULL,
        CONSTRAINT [PK_user_org_import_jobs] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [FK_user_org_import_jobs_type] FOREIGN KEY ([org_type_id])
            REFERENCES [dbo].[user_org_types] ([id]),
        CONSTRAINT [CK_user_org_import_jobs_mode] CHECK ([mode] IN (1, 2)),
        CONSTRAINT [CK_user_org_import_jobs_status] CHECK ([status] IN (1, 2, 3, 4, 5))
    );

    RAISERROR('UserOrganisations: created dbo.user_org_import_jobs.', 0, 1) WITH NOWAIT;
END
ELSE IF OBJECT_ID(N'dbo.user_org_import_jobs', N'U') IS NOT NULL
BEGIN
    RAISERROR('UserOrganisations: dbo.user_org_import_jobs already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.user_org_import_jobs', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_org_import_jobs') AND name = N'IX_user_org_import_jobs_type_queued')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_user_org_import_jobs_type_queued]
        ON [dbo].[user_org_import_jobs] ([org_type_id] ASC, [queued_utc] DESC)
        INCLUDE ([status]);
    RAISERROR('UserOrganisations: created IX_user_org_import_jobs_type_queued.', 0, 1) WITH NOWAIT;
END

-- ---------------------------------------------------------------------------
-- user_org_import_staging - the parsed rows of an upload, awaiting merge.
-- upn is nvarchar even though dbo.users.user_name is varchar(250): this is a
-- transient table holding whatever the admin's file contained, and a non-ASCII
-- value must be reported back to them verbatim rather than silently corrupted
-- to question marks at rest. It simply will not match a user, and is counted as
-- an unknown UPN. The merge joins these two once per import as a hash join, so
-- the type difference costs nothing measurable.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.user_org_import_staging', N'U') IS NULL
   AND OBJECT_ID(N'dbo.user_org_import_jobs', N'U') IS NOT NULL
BEGIN
    CREATE TABLE [dbo].[user_org_import_staging]
    (
        [job_id] int NOT NULL,
        [line_number] int NOT NULL,
        [upn] nvarchar(250) NOT NULL,
        [org_value] nvarchar(200) NULL,
        CONSTRAINT [PK_user_org_import_staging] PRIMARY KEY CLUSTERED ([job_id] ASC, [line_number] ASC),
        CONSTRAINT [FK_user_org_import_staging_job] FOREIGN KEY ([job_id])
            REFERENCES [dbo].[user_org_import_jobs] ([id]) ON DELETE CASCADE
    );

    RAISERROR('UserOrganisations: created dbo.user_org_import_staging.', 0, 1) WITH NOWAIT;
END
ELSE IF OBJECT_ID(N'dbo.user_org_import_staging', N'U') IS NOT NULL
BEGIN
    RAISERROR('UserOrganisations: dbo.user_org_import_staging already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.user_org_import_staging', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_org_import_staging') AND name = N'IX_user_org_import_staging_job_upn')
BEGIN
    -- The merge joins staging to dbo.users on the UPN, per job.
    CREATE NONCLUSTERED INDEX [IX_user_org_import_staging_job_upn]
        ON [dbo].[user_org_import_staging] ([job_id] ASC, [upn] ASC)
        INCLUDE ([org_value]);
    RAISERROR('UserOrganisations: created IX_user_org_import_staging_job_upn.', 0, 1) WITH NOWAIT;
END

RAISERROR('UserOrganisations: finished.', 0, 1) WITH NOWAIT;

/* ---------------------------------------------------------------------------------------------------
   Record the migration as applied.

   The guard checks SCHEMA only - that the five tables this script creates actually exist. It
   deliberately does NOT check any data state: this migration writes no rows, and a data-state guard is
   the shape that has previously refused to stamp a successfully-completed migration and stranded the
   rest of the chain behind it.

   The schema check still matters, because a severity-16 RAISERROR does not abort a batch - sqlcmd and
   SSMS carry on to the next one - so an unguarded stamp would record a failed apply as complete, after
   which EF never retries it.
   --------------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
               WHERE MigrationId = N'202609221200001_UserOrganisations')
BEGIN
    IF OBJECT_ID(N'dbo.user_org_types', N'U') IS NULL
       OR OBJECT_ID(N'dbo.user_org_values', N'U') IS NULL
       OR OBJECT_ID(N'dbo.user_org_assignments', N'U') IS NULL
       OR OBJECT_ID(N'dbo.user_org_import_jobs', N'U') IS NULL
       OR OBJECT_ID(N'dbo.user_org_import_staging', N'U') IS NULL
        RAISERROR('UserOrganisations: NOT stamped - one or more of the user_org_* tables is missing, so the schema work did not complete. If dbo.users has no primary key the assignments table is skipped by design; create the normal schema first. Re-run this script, or run the installer to reconcile.', 16, 1);
    ELSE IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory
                        WHERE MigrationId = N'202609201430001_DropCopilotAdoptionPeriodTables')
        RAISERROR('UserOrganisations: the tables were created, but prerequisite migration 202609201430001_DropCopilotAdoptionPeriodTables is missing from __MigrationHistory, so it was NOT stamped. Upgrade to the previous release first, or run the installer to reconcile.', 16, 1);
    ELSE
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609221200001_UserOrganisations', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609201430001_DropCopilotAdoptionPeriodTables';
        RAISERROR('UserOrganisations: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
END
ELSE
    RAISERROR('UserOrganisations: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
