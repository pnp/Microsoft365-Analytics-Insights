namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the schema for configurable user organisations ("orgs"): admin-defined grouping dimensions
    /// for users, populated either from a custom Entra attribute during the normal user-metadata import
    /// or from a CSV uploaded in the portal.
    ///
    /// <para>
    /// <b>Classification: purely additive.</b> Five new tables; no existing table is rewritten, no
    /// existing column is altered and no existing query is being tuned. The benchmark gate that applies
    /// to <i>performance-motivated</i> schema changes therefore does not apply here - there is no
    /// "before" query to measure.
    /// </para>
    ///
    /// <para>
    /// <b>Not to be confused with <c>dbo.orgs</c> / <c>dbo.org_urls</c>.</b> Those are an unrelated,
    /// much older concept: mapping SharePoint URL bases so web-traffic hits can be scoped.
    /// <c>dbo.org_urls</c> is still live (it drives the CORS allow-list and SharePoint URL filtering).
    /// Everything added here is prefixed <c>user_org_</c> precisely so the two cannot be confused.
    /// </para>
    ///
    /// <para>
    /// <b>No EF model change.</b> These tables are read and written with raw SQL, exactly as
    /// <c>copilot_adoption_reclaim_exclusions</c> is. The hot path is a bulk merge over a
    /// 200,000-user tenant, which would not use EF anyway. Because the entity model is untouched, the
    /// <c>.resx</c> snapshot is a byte-identical copy of the predecessor migration's, so EF sees
    /// <c>model == latest snapshot</c>, never auto-migrates, and cannot throw
    /// <c>AutomaticDataLossException</c>.
    /// </para>
    /// </summary>
    public partial class UserOrganisations : DbMigration
    {
        /// <summary>
        /// Additive, guarded and re-runnable schema for user organisations. Exposed so the manual DBA
        /// script can embed exactly the same SQL that EF runs.
        /// </summary>
        public const string Up_Sql = @"SET NOCOUNT ON;

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
";

        public const string Down_Sql = @"SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.user_org_import_staging', N'U') IS NOT NULL
    DROP TABLE [dbo].[user_org_import_staging];

IF OBJECT_ID(N'dbo.user_org_import_jobs', N'U') IS NOT NULL
    DROP TABLE [dbo].[user_org_import_jobs];

IF OBJECT_ID(N'dbo.user_org_assignments', N'U') IS NOT NULL
    DROP TABLE [dbo].[user_org_assignments];

IF OBJECT_ID(N'dbo.user_org_values', N'U') IS NOT NULL
    DROP TABLE [dbo].[user_org_values];

IF OBJECT_ID(N'dbo.user_org_types', N'U') IS NOT NULL
    DROP TABLE [dbo].[user_org_types];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'UserOrganisations'. Adds the configurable user organisation tables (types, values, assignments, CSV import jobs and staging). Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'UserOrganisations'. Drops the user organisation tables.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
