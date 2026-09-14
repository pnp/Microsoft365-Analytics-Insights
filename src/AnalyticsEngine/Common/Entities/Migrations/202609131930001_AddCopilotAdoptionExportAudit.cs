namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the audit table used by the Copilot Adoption export endpoints to record who attempted to
    /// download individual-level data, the filter/window/options that shaped the file and whether the
    /// export succeeded, failed or was truncated.
    ///
    /// The EF entity model is deliberately unchanged. This is a raw-SQL table used by a parameterised
    /// writer, so the .resx snapshot is a byte-identical copy of the previous migration
    /// (202609101200001_RetireImportDbHacks). Hand-editing the EF6 EDMX snapshot for a new entity would
    /// risk AutomaticDataLossException before the web app starts; avoiding an entity prevents that
    /// failure mode.
    ///
    /// NOT PERFORMANCE-MOTIVATED as a schema change. The table is empty on creation and is append-only
    /// at export time; the single date index exists for audit-retention review, not to optimise an
    /// existing production query.
    ///
    /// All customer/user-originated free text is nvarchar: actor, endpoint, query parameters, options
    /// JSON and failure reason can contain tenant-specific text at runtime and must not be corrupted by
    /// a code page conversion.
    /// </summary>
    public partial class AddCopilotAdoptionExportAudit : DbMigration
    {
        public const string Up_Sql = @"SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NULL
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: creating dbo.copilot_adoption_export_audit.', 0, 1) WITH NOWAIT;

    CREATE TABLE [dbo].[copilot_adoption_export_audit]
    (
        [id] bigint IDENTITY(1,1) NOT NULL,
        [occurred_utc] datetime2(3) NOT NULL CONSTRAINT [DF_copilot_adoption_export_audit_occurred_utc] DEFAULT SYSUTCDATETIME(),
        [actor] nvarchar(512) NULL,
        [endpoint] nvarchar(200) NOT NULL,
        [parameters] nvarchar(max) NULL,
        [window_days] int NULL,
        [options_json] nvarchar(max) NULL,
        [row_count] int NULL,
        [truncated] bit NOT NULL CONSTRAINT [DF_copilot_adoption_export_audit_truncated] DEFAULT (0),
        [succeeded] bit NOT NULL CONSTRAINT [DF_copilot_adoption_export_audit_succeeded] DEFAULT (0),
        [status_code] int NOT NULL,
        [failure_reason] nvarchar(1000) NULL,
        [pseudonymised] bit NOT NULL CONSTRAINT [DF_copilot_adoption_export_audit_pseudonymised] DEFAULT (1),
        [individual_data_disabled] bit NOT NULL CONSTRAINT [DF_copilot_adoption_export_audit_individual_data_disabled] DEFAULT (0),
        CONSTRAINT [PK_copilot_adoption_export_audit] PRIMARY KEY CLUSTERED ([id] ASC)
    );

    CREATE NONCLUSTERED INDEX [IX_copilot_adoption_export_audit_occurred_utc]
        ON [dbo].[copilot_adoption_export_audit] ([occurred_utc] ASC)
        INCLUDE ([endpoint], [actor], [succeeded], [status_code]);
END
ELSE
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: dbo.copilot_adoption_export_audit already exists; nothing to do.', 0, 1) WITH NOWAIT;
END";

        public override void Up()
        {
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Sql(@"
IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[copilot_adoption_export_audit];
END", suppressTransaction: true);
        }
    }
}
