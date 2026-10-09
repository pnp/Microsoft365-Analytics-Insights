/* =====================================================================================================

   MANUAL DATABASE UPGRADE - 202610071200001_Agent365PackageCatalog

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   The schema below is the migration's guarded, additive Up SQL, followed by the __MigrationHistory stamp.

   CLASSIFICATION: PURELY ADDITIVE
     Creates empty Agent 365 catalog snapshot tables and indexes only. No existing data is changed.

   UPGRADE TIME
     Metadata-only table creation and empty-index creation. No maintenance window is expected; the importer
     need not be stopped.

   SAFETY
     * Every object is guarded and the script is safe to re-run.
     * No transaction wraps the schema operations, so an interrupted run converges on re-run.
     * The pre-stamp guard checks schema only, never data state.
     * Package definition JSON is not stored by the importer.

   PREREQUISITE
     Run after migration 202610021200001_LicenceHistory. The EF model is unchanged, so the history stamp
     copies the predecessor's model snapshot.

   ===================================================================================================== */

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

RAISERROR('Agent365PackageCatalog: creating the Agent 365 package catalog snapshot tables.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_agent_catalog_import_log', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_agent_catalog_import_log]
    (
        [run_id] uniqueidentifier NOT NULL,
        [started_utc] datetime2(7) NOT NULL,
        [completed_utc] datetime2(7) NULL,
        [is_success] bit NOT NULL CONSTRAINT [DF_copilot_agent_catalog_import_log_is_success] DEFAULT (0),
        [package_count] int NULL,
        [element_count] int NULL,
        [error] nvarchar(1000) NULL,
        CONSTRAINT [PK_copilot_agent_catalog_import_log] PRIMARY KEY CLUSTERED ([run_id] ASC)
    );
    RAISERROR('Agent365PackageCatalog: created dbo.copilot_agent_catalog_import_log.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_agent_catalog_import_log', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_agent_catalog_import_log') AND name = N'IX_copilot_agent_catalog_import_log_started_utc')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_agent_catalog_import_log_started_utc]
        ON [dbo].[copilot_agent_catalog_import_log] ([started_utc] DESC, [run_id] DESC)
        INCLUDE ([completed_utc], [is_success], [error]);
END

IF OBJECT_ID(N'dbo.copilot_agent_packages', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_agent_packages]
    (
        [import_run_id] uniqueidentifier NOT NULL,
        [package_id] nvarchar(450) NOT NULL,
        [agent_identity_id] nvarchar(450) NULL,
        [display_name] nvarchar(1000) NULL,
        [package_type] nvarchar(100) NULL,
        [platform] nvarchar(200) NULL,
        [publisher] nvarchar(1000) NULL,
        [manifest_id] nvarchar(450) NULL,
        [version] nvarchar(100) NULL,
        [is_blocked] bit NULL,
        [last_modified_utc] datetime2(0) NULL,
        [last_used_utc] datetime2(0) NULL,
        [last_used_datetime_provided] bit NOT NULL,
        [active_users] int NULL,
        [total_sessions] int NULL,
        [total_run_time_hours] float NULL,
        [exception_rate] float NULL,
        [supported_hosts_json] nvarchar(max) NULL,
        CONSTRAINT [PK_copilot_agent_packages] PRIMARY KEY CLUSTERED ([import_run_id] ASC, [package_id] ASC),
        CONSTRAINT [FK_copilot_agent_packages_import_log] FOREIGN KEY ([import_run_id])
            REFERENCES [dbo].[copilot_agent_catalog_import_log] ([run_id])
    );
    RAISERROR('Agent365PackageCatalog: created dbo.copilot_agent_packages.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_agent_packages', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_packages') AND name = N'FK_copilot_agent_packages_import_log')
BEGIN
    ALTER TABLE [dbo].[copilot_agent_packages]
        ADD CONSTRAINT [FK_copilot_agent_packages_import_log] FOREIGN KEY ([import_run_id])
            REFERENCES [dbo].[copilot_agent_catalog_import_log] ([run_id]);
END

IF OBJECT_ID(N'dbo.copilot_agent_packages', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_agent_packages') AND name = N'IX_copilot_agent_packages_never_used')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_agent_packages_never_used]
        ON [dbo].[copilot_agent_packages] ([import_run_id] ASC, [last_used_datetime_provided] ASC, [last_used_utc] ASC);
END

IF OBJECT_ID(N'dbo.copilot_agent_package_elements', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_agent_package_elements]
    (
        [id] bigint IDENTITY(1,1) NOT NULL,
        [import_run_id] uniqueidentifier NOT NULL,
        [package_id] nvarchar(450) NOT NULL,
        [element_type] nvarchar(100) NULL,
        [element_id] nvarchar(450) NULL,
        CONSTRAINT [PK_copilot_agent_package_elements] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [FK_copilot_agent_package_elements_package] FOREIGN KEY ([import_run_id], [package_id])
            REFERENCES [dbo].[copilot_agent_packages] ([import_run_id], [package_id])
    );
    RAISERROR('Agent365PackageCatalog: created dbo.copilot_agent_package_elements.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_agent_package_elements', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_package_elements') AND name = N'FK_copilot_agent_package_elements_package')
BEGIN
    ALTER TABLE [dbo].[copilot_agent_package_elements]
        ADD CONSTRAINT [FK_copilot_agent_package_elements_package] FOREIGN KEY ([import_run_id], [package_id])
            REFERENCES [dbo].[copilot_agent_packages] ([import_run_id], [package_id]);
END

IF OBJECT_ID(N'dbo.copilot_agent_package_elements', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_agent_package_elements') AND name = N'IX_copilot_agent_package_elements_package')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_agent_package_elements_package]
        ON [dbo].[copilot_agent_package_elements] ([import_run_id] ASC, [package_id] ASC)
        INCLUDE ([element_type], [element_id]);
END

IF OBJECT_ID(N'dbo.copilot_agent_catalog_import_log', N'U') IS NULL
   OR OBJECT_ID(N'dbo.copilot_agent_packages', N'U') IS NULL
   OR OBJECT_ID(N'dbo.copilot_agent_package_elements', N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_agent_catalog_import_log') AND name = N'IX_copilot_agent_catalog_import_log_started_utc')
   OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_agent_packages') AND name = N'IX_copilot_agent_packages_never_used')
   OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_agent_package_elements') AND name = N'IX_copilot_agent_package_elements_package')
   OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_packages') AND name = N'FK_copilot_agent_packages_import_log')
   OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_package_elements') AND name = N'FK_copilot_agent_package_elements_package')
   OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_catalog_import_log') AND name = N'PK_copilot_agent_catalog_import_log')
   OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_packages') AND name = N'PK_copilot_agent_packages')
   OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_package_elements') AND name = N'PK_copilot_agent_package_elements')
   OR NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_agent_catalog_import_log') AND name = N'DF_copilot_agent_catalog_import_log_is_success')
   OR EXISTS
   (
       SELECT 1
       FROM (VALUES
           (N'copilot_agent_catalog_import_log', N'run_id'),
           (N'copilot_agent_catalog_import_log', N'started_utc'),
           (N'copilot_agent_catalog_import_log', N'completed_utc'),
           (N'copilot_agent_catalog_import_log', N'is_success'),
           (N'copilot_agent_catalog_import_log', N'package_count'),
           (N'copilot_agent_catalog_import_log', N'element_count'),
           (N'copilot_agent_catalog_import_log', N'error'),
           (N'copilot_agent_packages', N'import_run_id'),
           (N'copilot_agent_packages', N'package_id'),
           (N'copilot_agent_packages', N'agent_identity_id'),
           (N'copilot_agent_packages', N'display_name'),
           (N'copilot_agent_packages', N'package_type'),
           (N'copilot_agent_packages', N'platform'),
           (N'copilot_agent_packages', N'publisher'),
           (N'copilot_agent_packages', N'manifest_id'),
           (N'copilot_agent_packages', N'version'),
           (N'copilot_agent_packages', N'is_blocked'),
           (N'copilot_agent_packages', N'last_modified_utc'),
           (N'copilot_agent_packages', N'last_used_utc'),
           (N'copilot_agent_packages', N'last_used_datetime_provided'),
           (N'copilot_agent_packages', N'active_users'),
           (N'copilot_agent_packages', N'total_sessions'),
           (N'copilot_agent_packages', N'total_run_time_hours'),
           (N'copilot_agent_packages', N'exception_rate'),
           (N'copilot_agent_packages', N'supported_hosts_json'),
           (N'copilot_agent_package_elements', N'import_run_id'),
           (N'copilot_agent_package_elements', N'id'),
           (N'copilot_agent_package_elements', N'package_id'),
           (N'copilot_agent_package_elements', N'element_type'),
           (N'copilot_agent_package_elements', N'element_id')
       ) AS required_columns(table_name, column_name)
       WHERE NOT EXISTS
       (
           SELECT 1
           FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.' + required_columns.table_name)
             AND name = required_columns.column_name
       )
   )
BEGIN
    RAISERROR('Agent365PackageCatalog: schema validation failed; migration was not stamped.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610021200001_LicenceHistory')
BEGIN
    RAISERROR('Agent365PackageCatalog: predecessor migration 202610021200001_LicenceHistory is not stamped; migration was not stamped.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610071200001_Agent365PackageCatalog')
BEGIN
    INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
    SELECT N'202610071200001_Agent365PackageCatalog', ContextKey, Model, ProductVersion
    FROM dbo.__MigrationHistory
    WHERE MigrationId = N'202610021200001_LicenceHistory';
END
