/* =====================================================================================================
   MANUAL DATABASE UPGRADE SCRIPT
   Migration: 202609131930001_AddCopilotAdoptionExportAudit

   Applies the migration's Up SQL verbatim, then stamps dbo.__MigrationHistory by copying the previous
   migration's model snapshot.

   RUN ORDER
     This is the FIRST of the two manual scripts in this release. Run it before
     202609131940001_CopilotReclaimEligibilityInputs.manual.sql, which hard-fails if this one has not
     been stamped.

   PREREQUISITE
     202609101200001_RetireImportDbHacks must already be stamped.

   SAFE TO RE-RUN: every schema step is guarded and idempotent. The stamp guard checks schema only.
   ===================================================================================================== */

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: dbo.__MigrationHistory does not exist - this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609101200001_RetireImportDbHacks')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609131930001_AddCopilotAdoptionExportAudit')
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: prerequisite migration 202609101200001_RetireImportDbHacks is not stamped in __MigrationHistory. Run the manual scripts in migration-id order. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

RAISERROR('AddCopilotAdoptionExportAudit: pre-flight checks passed.', 0, 1) WITH NOWAIT;
GO
SET NOCOUNT ON;

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
END
ELSE
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: dbo.copilot_adoption_export_audit already exists.', 0, 1) WITH NOWAIT;
END

-- Guarded separately from the table, not nested inside its "table does not exist" branch. This runs
-- with suppressTransaction, so SQL Server commits each statement independently: a failure between the
-- two would leave the table present and the index missing, and a re-run keyed only off the table would
-- then skip the index and stamp the migration as complete.
IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_export_audit') AND name = N'IX_copilot_adoption_export_audit_occurred_utc')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_adoption_export_audit_occurred_utc]
        ON [dbo].[copilot_adoption_export_audit] ([occurred_utc] ASC)
        INCLUDE ([endpoint], [actor], [succeeded], [status_code]);
    RAISERROR('AddCopilotAdoptionExportAudit: created IX_copilot_adoption_export_audit_occurred_utc.', 0, 1) WITH NOWAIT;
END
GO

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NULL
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: schema verification failed; dbo.copilot_adoption_export_audit is missing, so the migration will not be stamped.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_export_audit') AND name = N'actor' AND system_type_id = TYPE_ID(N'nvarchar'))
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: schema verification failed; actor is not nvarchar, so the migration will not be stamped.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_export_audit') AND name = N'IX_copilot_adoption_export_audit_occurred_utc')
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: schema verification failed; IX_copilot_adoption_export_audit_occurred_utc is missing, so the migration will not be stamped.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_export_audit') AND name = N'PK_copilot_adoption_export_audit')
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: schema verification failed; PK_copilot_adoption_export_audit is missing, so the migration will not be stamped.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609131930001_AddCopilotAdoptionExportAudit')
BEGIN
    INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
    SELECT N'202609131930001_AddCopilotAdoptionExportAudit', ContextKey, Model, ProductVersion
    FROM dbo.__MigrationHistory
    WHERE MigrationId = N'202609101200001_RetireImportDbHacks';

    RAISERROR('AddCopilotAdoptionExportAudit: stamped dbo.__MigrationHistory.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('AddCopilotAdoptionExportAudit: dbo.__MigrationHistory is already stamped; nothing to do.', 0, 1) WITH NOWAIT;
END

SET NOEXEC OFF;