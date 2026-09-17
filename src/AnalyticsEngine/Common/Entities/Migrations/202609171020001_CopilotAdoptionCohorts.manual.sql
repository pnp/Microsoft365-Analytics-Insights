/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202609171020001_CopilotAdoptionCohorts
   =====================================================================================================
   Adds frozen Copilot Adoption cohort and membership tables. Purely additive schema.

   RUN ORDER
     Run after 202609171000001_CopilotAdoptionTargets. This script stamps __MigrationHistory by
     copying that predecessor's model blob because the EF model is unchanged.

   SAFETY
     Idempotent and guarded. The pre-stamp guard checks schema only, never row data.
   ===================================================================================================== */
SET NOCOUNT ON;
GO

SET NOCOUNT ON;
RAISERROR('CopilotAdoptionCohorts: creating additive frozen cohort tables for Copilot Adoption. Purely additive; no performance benchmark required.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_cohort', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_cohort]
    (
        [cohort_id] int IDENTITY(1,1) NOT NULL,
        [name] nvarchar(200) NOT NULL,
        [action_code] nvarchar(40) NOT NULL,
        [created_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_cohort_created_utc] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        [baseline_period_end] date NOT NULL,
        [baseline_period_days] int NOT NULL CONSTRAINT [DF_copilot_adoption_cohort_baseline_period_days] DEFAULT (28),
        [baseline_options_hash] nvarchar(64) NOT NULL,
        [closed_utc] datetime2(7) NULL,
        [closed_by] nvarchar(256) NULL,
        CONSTRAINT [PK_copilot_adoption_cohort] PRIMARY KEY CLUSTERED ([cohort_id] ASC),
        CONSTRAINT [CK_copilot_adoption_cohort_period_days] CHECK ([baseline_period_days] > 0),
        CONSTRAINT [CK_copilot_adoption_cohort_options_hash] CHECK (LEN([baseline_options_hash]) = 64)
    );
    RAISERROR('CopilotAdoptionCohorts: created dbo.copilot_adoption_cohort.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAdoptionCohorts: dbo.copilot_adoption_cohort already exists.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_cohort_member', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_cohort_member]
    (
        [cohort_id] int NOT NULL,
        [user_id] int NOT NULL,
        [baseline_band] int NOT NULL,
        [baseline_score] float NOT NULL,
        [baseline_active_days] int NOT NULL,
        [baseline_department] nvarchar(100) NULL,
        [holdout_control] bit NOT NULL CONSTRAINT [DF_copilot_adoption_cohort_member_holdout_control] DEFAULT (0),
        CONSTRAINT [PK_copilot_adoption_cohort_member] PRIMARY KEY CLUSTERED ([cohort_id] ASC, [user_id] ASC),
        CONSTRAINT [CK_copilot_adoption_cohort_member_band] CHECK ([baseline_band] BETWEEN 0 AND 5),
        CONSTRAINT [CK_copilot_adoption_cohort_member_score] CHECK ([baseline_score] >= 0 AND [baseline_score] <= 100),
        CONSTRAINT [CK_copilot_adoption_cohort_member_days] CHECK ([baseline_active_days] >= 0)
    );
    RAISERROR('CopilotAdoptionCohorts: created dbo.copilot_adoption_cohort_member.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAdoptionCohorts: dbo.copilot_adoption_cohort_member already exists.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_cohort_member', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.copilot_adoption_cohort_member', N'holdout_control') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_cohort_member]
        ADD [holdout_control] bit NOT NULL CONSTRAINT [DF_copilot_adoption_cohort_member_holdout_control] DEFAULT (0);
    RAISERROR('CopilotAdoptionCohorts: added copilot_adoption_cohort_member.holdout_control.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_cohort_member', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.copilot_adoption_cohort', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_cohort_member_cohort' AND parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_cohort_member'))
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_cohort_member] WITH CHECK ADD CONSTRAINT [FK_copilot_adoption_cohort_member_cohort]
        FOREIGN KEY ([cohort_id]) REFERENCES [dbo].[copilot_adoption_cohort] ([cohort_id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[copilot_adoption_cohort_member] CHECK CONSTRAINT [FK_copilot_adoption_cohort_member_cohort];
    RAISERROR('CopilotAdoptionCohorts: created FK_copilot_adoption_cohort_member_cohort.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_cohort_member', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_cohort_member_users' AND parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_cohort_member'))
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_cohort_member] WITH CHECK ADD CONSTRAINT [FK_copilot_adoption_cohort_member_users]
        FOREIGN KEY ([user_id]) REFERENCES [dbo].[users] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[copilot_adoption_cohort_member] CHECK CONSTRAINT [FK_copilot_adoption_cohort_member_users];
    RAISERROR('CopilotAdoptionCohorts: created FK_copilot_adoption_cohort_member_users.', 0, 1) WITH NOWAIT;
END

RAISERROR('CopilotAdoptionCohorts: finished.', 0, 1) WITH NOWAIT;

GO

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171020001_CopilotAdoptionCohorts')
BEGIN
    DECLARE @missing nvarchar(max) = N'';
    IF OBJECT_ID(N'dbo.copilot_adoption_cohort', N'U') IS NULL SET @missing += N'copilot_adoption_cohort; ';
    IF OBJECT_ID(N'dbo.copilot_adoption_cohort_member', N'U') IS NULL SET @missing += N'copilot_adoption_cohort_member; ';
    IF COL_LENGTH(N'dbo.copilot_adoption_cohort', N'baseline_period_days') IS NULL SET @missing += N'copilot_adoption_cohort.baseline_period_days; ';
    IF COL_LENGTH(N'dbo.copilot_adoption_cohort_member', N'holdout_control') IS NULL SET @missing += N'copilot_adoption_cohort_member.holdout_control; ';
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_cohort_member_cohort') SET @missing += N'FK_copilot_adoption_cohort_member_cohort; ';

    IF @missing <> N''
        RAISERROR(N'CopilotAdoptionCohorts: NOT stamped - schema work incomplete. Missing: %s', 16, 1, @missing) WITH NOWAIT;
    ELSE IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171000001_CopilotAdoptionTargets')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609171020001_CopilotAdoptionCohorts', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609171000001_CopilotAdoptionTargets';
        RAISERROR('CopilotAdoptionCohorts: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('CopilotAdoptionCohorts: prerequisite 202609171000001_CopilotAdoptionTargets is missing from __MigrationHistory, so the schema was not stamped.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAdoptionCohorts: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
GO
