/* =====================================================================================================
   MANUAL DATABASE UPGRADE SCRIPT
   Migration: 202609131940001_CopilotReclaimEligibilityInputs

   Applies the migration's Up SQL verbatim, then stamps dbo.__MigrationHistory so EF and the Health
   page treat it as applied.

   PREREQUISITE
     202609131930001_AddCopilotAdoptionExportAudit must already be stamped - it is the migration
     immediately before this one. Run the release's manual scripts in migration-id order.

   WHAT IT DOES
     Adds dbo.users.created_utc as the Graph user.createdDateTime account-age proxy for reclaim grace
     periods, and creates dbo.copilot_adoption_reclaim_exclusions for reviewed false positives. Free
     text columns are nvarchar so non-Latin notes and admin display names are preserved.

   PERFORMANCE CLASSIFICATION
     Purely additive schema: one nullable column and one new empty table/index. There is no before
     query to benchmark, so the performance-motivated schema benchmark gate does not apply.

   SAFE TO RE-RUN: every schema step is guarded and idempotent. The stamp guard checks schema only.
   ===================================================================================================== */

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('CopilotReclaimEligibilityInputs: dbo.__MigrationHistory does not exist - this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609131930001_AddCopilotAdoptionExportAudit')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609131940001_CopilotReclaimEligibilityInputs')
BEGIN
    RAISERROR('CopilotReclaimEligibilityInputs: prerequisite migration 202609131930001_AddCopilotAdoptionExportAudit is not stamped in __MigrationHistory. Run the manual scripts in migration-id order - this one is LAST. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

RAISERROR('CopilotReclaimEligibilityInputs: pre-flight checks passed.', 0, 1) WITH NOWAIT;
GO

SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'CopilotReclaimEligibilityInputs';
DECLARE @msg nvarchar(2000);

RAISERROR('CopilotReclaimEligibilityInputs: adding account-age and reclaim-exclusion inputs. This is additive schema, so no performance benchmark is required.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.users', N'U') IS NULL
BEGIN
    RAISERROR('CopilotReclaimEligibilityInputs: dbo.users does not exist; cannot add created_utc.', 16, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.users') AND name = N'created_utc')
BEGIN
    ALTER TABLE [dbo].[users] ADD [created_utc] datetime2(7) NULL;
    RAISERROR('CopilotReclaimEligibilityInputs: added dbo.users.created_utc from Graph user.createdDateTime.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotReclaimEligibilityInputs: dbo.users.created_utc already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_reclaim_exclusions]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [user_id] int NOT NULL,
        [reason] nvarchar(100) NOT NULL,
        [note] nvarchar(1000) NULL,
        [excluded_by] nvarchar(256) NOT NULL,
        [excluded_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_reclaim_exclusions_excluded_utc] DEFAULT SYSUTCDATETIME(),
        [review_after_utc] datetime2(7) NULL,
        CONSTRAINT [PK_copilot_adoption_reclaim_exclusions] PRIMARY KEY CLUSTERED ([id] ASC)
    );

    RAISERROR('CopilotReclaimEligibilityInputs: created dbo.copilot_adoption_reclaim_exclusions.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotReclaimEligibilityInputs: dbo.copilot_adoption_reclaim_exclusions already exists.', 0, 1) WITH NOWAIT;
END


IF OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_reclaim_exclusions_users' AND parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.users') AND type = N'PK')
    BEGIN
        ALTER TABLE [dbo].[copilot_adoption_reclaim_exclusions] WITH CHECK ADD CONSTRAINT [FK_copilot_adoption_reclaim_exclusions_users]
            FOREIGN KEY ([user_id]) REFERENCES [dbo].[users] ([id]) ON DELETE CASCADE;
        ALTER TABLE [dbo].[copilot_adoption_reclaim_exclusions] CHECK CONSTRAINT [FK_copilot_adoption_reclaim_exclusions_users];
        RAISERROR('CopilotReclaimEligibilityInputs: created FK_copilot_adoption_reclaim_exclusions_users.', 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        RAISERROR('CopilotReclaimEligibilityInputs: dbo.users has no primary key yet; leaving the exclusion table without its FK until the normal schema has been created.', 0, 1) WITH NOWAIT;
    END
END

IF OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions') AND name = N'IX_copilot_adoption_reclaim_exclusions_user_review')
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_copilot_adoption_reclaim_exclusions_user_review]
            ON [dbo].[copilot_adoption_reclaim_exclusions] ([user_id] ASC, [review_after_utc] ASC, [excluded_utc] DESC)
            INCLUDE ([reason], [excluded_by]);
        RAISERROR('CopilotReclaimEligibilityInputs: created IX_copilot_adoption_reclaim_exclusions_user_review.', 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        RAISERROR('CopilotReclaimEligibilityInputs: exclusion lookup index already exists.', 0, 1) WITH NOWAIT;
    END
END

RAISERROR('CopilotReclaimEligibilityInputs: finished.', 0, 1) WITH NOWAIT;
GO

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.users', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.users') AND name = N'created_utc')
   AND OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions') AND name = N'IX_copilot_adoption_reclaim_exclusions_user_review')
   AND EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_reclaim_exclusions_users' AND parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions'))
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609131940001_CopilotReclaimEligibilityInputs')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609131940001_CopilotReclaimEligibilityInputs', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609131930001_AddCopilotAdoptionExportAudit';
        RAISERROR('CopilotReclaimEligibilityInputs: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        RAISERROR('CopilotReclaimEligibilityInputs: __MigrationHistory is already stamped.', 0, 1) WITH NOWAIT;
    END
END
ELSE
BEGIN
    RAISERROR('CopilotReclaimEligibilityInputs: NOT stamped - required schema objects are missing.', 16, 1) WITH NOWAIT;
END
GO

SET NOEXEC OFF;
GO
