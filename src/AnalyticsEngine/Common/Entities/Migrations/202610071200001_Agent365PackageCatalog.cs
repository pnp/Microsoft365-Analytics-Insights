namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Creates additive snapshot tables for the Agent 365 Package Management API catalog.
    /// </summary>
    public partial class Agent365PackageCatalog : DbMigration
    {
        /// <summary>
        /// Guarded additive schema, also embedded verbatim in the manual DBA upgrade script.
        /// </summary>
        public const string Up_Sql = @"SET QUOTED_IDENTIFIER ON;
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
END";

        public override void Up()
        {
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Sql(@"IF OBJECT_ID(N'dbo.copilot_agent_package_elements', N'U') IS NOT NULL DROP TABLE dbo.copilot_agent_package_elements;
IF OBJECT_ID(N'dbo.copilot_agent_packages', N'U') IS NOT NULL DROP TABLE dbo.copilot_agent_packages;
IF OBJECT_ID(N'dbo.copilot_agent_catalog_import_log', N'U') IS NOT NULL DROP TABLE dbo.copilot_agent_catalog_import_log;", suppressTransaction: true);
        }
    }
}
