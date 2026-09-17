/* =====================================================================================================
   MANUAL DATABASE UPGRADE SCRIPT
   Migration: 202609171000001_CopilotAdoptionTargets

   Applies the migration's Up SQL verbatim, then stamps dbo.__MigrationHistory so EF and the Health
   page treat it as applied.

   PREREQUISITE
     202609170940001_CoworkUsageReportTables must already be stamped. This branch is stacked on
     PR #579; run that release's manual script before this one.

   WHAT IT DOES
     Adds dbo.copilot_adoption_targets for customer-defined internal targets. Baseline period, value
     and options hashes are written once at creation and never updated, so target progress cannot be
     silently moved by retuning CopilotAdoptionOptions. Text columns are nvarchar for Unicode owners
     and department/cohort names.

   PERFORMANCE CLASSIFICATION
     Purely additive schema: one new empty table and one index. There is no before query to benchmark,
     so the performance-motivated schema benchmark gate does not apply.

   SAFE TO RE-RUN: every schema step is guarded and idempotent. The stamp guard checks schema only.
   ===================================================================================================== */

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('CopilotAdoptionTargets: dbo.__MigrationHistory does not exist - this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170940001_CoworkUsageReportTables')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171000001_CopilotAdoptionTargets')
BEGIN
    RAISERROR('CopilotAdoptionTargets: prerequisite migration 202609170940001_CoworkUsageReportTables is not stamped in __MigrationHistory. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

RAISERROR('CopilotAdoptionTargets: pre-flight checks passed.', 0, 1) WITH NOWAIT;
GO

SET NOCOUNT ON;

RAISERROR('CopilotAdoptionTargets: adding customer-defined Copilot Adoption target storage. This is additive schema, so no performance benchmark is required.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_targets', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_targets]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [metric] nvarchar(64) NOT NULL,
        [scope_type] nvarchar(32) NOT NULL CONSTRAINT [DF_copilot_adoption_targets_scope_type] DEFAULT N'tenant',
        [scope_value] nvarchar(256) NULL,
        [target_value] decimal(18,4) NOT NULL,
        [owner] nvarchar(256) NOT NULL,
        [baseline_period_end] date NOT NULL,
        [baseline_period_days] int NOT NULL,
        [baseline_value] decimal(18,4) NOT NULL,
        [baseline_options_hash] nvarchar(64) NOT NULL,
        [baseline_scoring_options_hash] nvarchar(64) NOT NULL,
        [target_date] date NOT NULL,
        [created_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_targets_created_utc] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NULL,
        [is_active] bit NOT NULL CONSTRAINT [DF_copilot_adoption_targets_is_active] DEFAULT 1,
        CONSTRAINT [PK_copilot_adoption_targets] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_copilot_adoption_targets_metric] CHECK ([metric] IN (N'adoptionRatePct', N'habitRatePct', N'reclaimableSeats', N'reclaimCertainSeats', N'reclaimProbableSeats', N'reclaimReviewSeats', N'neverUsedUsers', N'dormantUsers', N'averageAdoptionScore', N'medianAdoptionScore', N'unlicensedActiveUsers', N'recommendedForLicence')),
        CONSTRAINT [CK_copilot_adoption_targets_scope] CHECK ([scope_type] IN (N'tenant', N'department', N'cohort')),
        CONSTRAINT [CK_copilot_adoption_targets_days] CHECK ([baseline_period_days] > 0),
        CONSTRAINT [CK_copilot_adoption_targets_hashes] CHECK (LEN([baseline_options_hash]) = 64 AND LEN([baseline_scoring_options_hash]) = 64),
        CONSTRAINT [CK_copilot_adoption_targets_scope_value] CHECK (([scope_type] = N'tenant' AND [scope_value] IS NULL) OR ([scope_type] <> N'tenant' AND [scope_value] IS NOT NULL))
    );
    RAISERROR('CopilotAdoptionTargets: created dbo.copilot_adoption_targets.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionTargets: dbo.copilot_adoption_targets already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_targets', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_targets') AND name = N'IX_copilot_adoption_targets_active_scope')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_adoption_targets_active_scope]
        ON [dbo].[copilot_adoption_targets] ([is_active] ASC, [scope_type] ASC, [scope_value] ASC, [metric] ASC, [target_date] ASC)
        INCLUDE ([owner], [target_value], [baseline_period_end], [baseline_period_days], [baseline_value]);
    RAISERROR('CopilotAdoptionTargets: created IX_copilot_adoption_targets_active_scope.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionTargets: active target lookup index already exists or table is missing.', 0, 1) WITH NOWAIT;
END


IF OBJECT_ID(N'dbo.TR_copilot_adoption_targets_freeze_baseline', N'TR') IS NULL
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_copilot_adoption_targets_freeze_baseline]
ON [dbo].[copilot_adoption_targets]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(baseline_period_end) OR UPDATE(baseline_period_days) OR UPDATE(baseline_value) OR UPDATE(baseline_options_hash) OR UPDATE(baseline_scoring_options_hash)
    BEGIN
        RAISERROR(''Copilot Adoption target baselines are immutable. Create a new target instead of changing the baseline.'', 16, 1);
        ROLLBACK TRANSACTION;
    END
END');
    RAISERROR('CopilotAdoptionTargets: created baseline immutability trigger.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionTargets: baseline immutability trigger already exists.', 0, 1) WITH NOWAIT;
END

RAISERROR('CopilotAdoptionTargets: finished.', 0, 1) WITH NOWAIT;

GO

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_targets', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_targets') AND name = N'baseline_value')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_targets') AND name = N'baseline_scoring_options_hash')
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_targets') AND name = N'IX_copilot_adoption_targets_active_scope')
   AND OBJECT_ID(N'dbo.TR_copilot_adoption_targets_freeze_baseline', N'TR') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171000001_CopilotAdoptionTargets')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609171000001_CopilotAdoptionTargets', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609170940001_CoworkUsageReportTables';
        RAISERROR('CopilotAdoptionTargets: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        RAISERROR('CopilotAdoptionTargets: __MigrationHistory is already stamped.', 0, 1) WITH NOWAIT;
    END
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionTargets: NOT stamped - required schema objects are missing.', 16, 1) WITH NOWAIT;
END
GO

SET NOEXEC OFF;
GO
