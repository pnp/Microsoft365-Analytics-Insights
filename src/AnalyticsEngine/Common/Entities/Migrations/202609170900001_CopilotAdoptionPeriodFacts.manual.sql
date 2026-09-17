/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202609170900001_CopilotAdoptionPeriodFacts
   =====================================================================================================
   Adds the raw Copilot Adoption period-fact history tables. Purely additive schema: new empty tables,
   constraints and indexes only. No backfill is performed and no existing user metadata is copied into
   past periods, so history starts accruing only when the scheduled publish path runs.

   RUN ORDER
     Run after 202609151440027_CopilotPromptSafetyFields. This script stamps __MigrationHistory by
     copying that predecessor's model blob because the EF model is unchanged.

   SAFETY
     Idempotent and guarded. The pre-stamp guard checks schema only, never row data.
   ===================================================================================================== */
SET NOCOUNT ON;
GO

SET NOCOUNT ON;
RAISERROR('CopilotAdoptionPeriodFacts: creating additive raw period-fact history tables for Copilot Adoption. Purely additive; no performance benchmark required.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_period_run', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_period_run]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [period_end] date NOT NULL,
        [period_days] int NOT NULL,
        [options_hash] nvarchar(64) NOT NULL,
        [audit_available] bit NOT NULL,
        [report_obfuscated] bit NOT NULL,
        [report_period_days] int NOT NULL CONSTRAINT [DF_copilot_adoption_period_run_report_period_days] DEFAULT (0),
        [licensed_users] int NOT NULL,
        [scored_users] int NOT NULL,
        [published_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_period_run_published_utc] DEFAULT SYSUTCDATETIME(),
        [data_cutoff_utc] datetime2(7) NOT NULL,
        [coverage_status] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_copilot_adoption_period_run] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_copilot_adoption_period_run_days] CHECK ([period_days] > 0),
        CONSTRAINT [CK_copilot_adoption_period_run_options_hash] CHECK (LEN([options_hash]) = 64)
    );
    RAISERROR('CopilotAdoptionPeriodFacts: created dbo.copilot_adoption_period_run.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionPeriodFacts: dbo.copilot_adoption_period_run already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_period_run', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.copilot_adoption_period_run', N'report_period_days') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_period_run]
        ADD [report_period_days] int NOT NULL CONSTRAINT [DF_copilot_adoption_period_run_report_period_days] DEFAULT (0);
    RAISERROR('CopilotAdoptionPeriodFacts: added copilot_adoption_period_run.report_period_days.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_period_run', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_period_run') AND name = N'UX_copilot_adoption_period_run_period')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_copilot_adoption_period_run_period]
        ON [dbo].[copilot_adoption_period_run] ([period_end] ASC, [period_days] ASC);
    RAISERROR('CopilotAdoptionPeriodFacts: created UX_copilot_adoption_period_run_period.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_user_period]
    (
        [period_end] date NOT NULL,
        [period_days] int NOT NULL,
        [data_cutoff_utc] datetime2(7) NOT NULL,
        [user_id] int NOT NULL,
        [seat_licence_type_ids] nvarchar(850) NOT NULL,
        [account_enabled] bit NULL,
        [department_id] int NULL,
        [country_id] int NULL,
        [manager_id] int NULL,
        [seat_first_observed_utc] datetime2(7) NULL,
        [account_created_utc] datetime2(7) NULL,
        [active_days] int NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_active_days] DEFAULT (0),
        [interactions] bigint NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_interactions] DEFAULT (0),
        [apps_used] int NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_apps_used] DEFAULT (0),
        [agents_used] int NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_agents_used] DEFAULT (0),
        [cowork_interactions] bigint NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_cowork_interactions] DEFAULT (0),
        [active_weeks] int NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_active_weeks] DEFAULT (0),
        [first_interaction_utc] datetime2(7) NULL,
        [last_interaction_utc] datetime2(7) NULL,
        [prior_interactions] bigint NOT NULL CONSTRAINT [DF_copilot_adoption_user_period_prior_interactions] DEFAULT (0),
        [signal_source] nvarchar(32) NOT NULL,
        [report_prompts] int NULL,
        [report_active_days] int NULL,
        [report_apps_used] int NULL,
        [report_last_activity_utc] datetime2(7) NULL,
        [report_agent_last_activity_utc] datetime2(7) NULL,
        [coverage_status] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_copilot_adoption_user_period] PRIMARY KEY NONCLUSTERED ([period_end] ASC, [period_days] ASC, [user_id] ASC),
        CONSTRAINT [CK_copilot_adoption_user_period_days] CHECK ([period_days] > 0),
        CONSTRAINT [CK_copilot_adoption_user_period_counts] CHECK ([active_days] >= 0 AND [interactions] >= 0 AND [apps_used] >= 0 AND [agents_used] >= 0 AND [cowork_interactions] >= 0 AND [active_weeks] >= 0 AND [prior_interactions] >= 0)
    );
    RAISERROR('CopilotAdoptionPeriodFacts: created dbo.copilot_adoption_user_period.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionPeriodFacts: dbo.copilot_adoption_user_period already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.copilot_adoption_user_period', N'account_created_utc') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_user_period]
        ADD [account_created_utc] datetime2(7) NULL;
    RAISERROR('CopilotAdoptionPeriodFacts: added copilot_adoption_user_period.account_created_utc.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_user_period') AND name = N'CX_copilot_adoption_user_period_period_user')
BEGIN
    CREATE CLUSTERED INDEX [CX_copilot_adoption_user_period_period_user]
        ON [dbo].[copilot_adoption_user_period] ([period_end] ASC, [user_id] ASC);
    RAISERROR('CopilotAdoptionPeriodFacts: created clustered index CX_copilot_adoption_user_period_period_user.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_user_period_users' AND parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_user_period'))
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_user_period] WITH CHECK ADD CONSTRAINT [FK_copilot_adoption_user_period_users]
        FOREIGN KEY ([user_id]) REFERENCES [dbo].[users] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[copilot_adoption_user_period] CHECK CONSTRAINT [FK_copilot_adoption_user_period_users];
    RAISERROR('CopilotAdoptionPeriodFacts: created FK_copilot_adoption_user_period_users.', 0, 1) WITH NOWAIT;
END

RAISERROR('CopilotAdoptionPeriodFacts: finished.', 0, 1) WITH NOWAIT;

GO

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170900001_CopilotAdoptionPeriodFacts')
BEGIN
    DECLARE @missing nvarchar(max) = N'';
    IF OBJECT_ID(N'dbo.copilot_adoption_period_run', N'U') IS NULL SET @missing += N'copilot_adoption_period_run; ';
    IF OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NULL SET @missing += N'copilot_adoption_user_period; ';
    IF COL_LENGTH(N'dbo.copilot_adoption_period_run', N'report_period_days') IS NULL SET @missing += N'copilot_adoption_period_run.report_period_days; ';
    IF COL_LENGTH(N'dbo.copilot_adoption_user_period', N'account_created_utc') IS NULL SET @missing += N'copilot_adoption_user_period.account_created_utc; ';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_user_period') AND name = N'CX_copilot_adoption_user_period_period_user') SET @missing += N'CX_copilot_adoption_user_period_period_user; ';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_period_run') AND name = N'UX_copilot_adoption_period_run_period') SET @missing += N'UX_copilot_adoption_period_run_period; ';

    IF @missing <> N''
    BEGIN
        RAISERROR(N'CopilotAdoptionPeriodFacts: NOT stamped - schema work incomplete. Missing: %s', 16, 1, @missing) WITH NOWAIT;
    END
    ELSE IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609151440027_CopilotPromptSafetyFields')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609170900001_CopilotAdoptionPeriodFacts', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609151440027_CopilotPromptSafetyFields';
        RAISERROR('CopilotAdoptionPeriodFacts: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('CopilotAdoptionPeriodFacts: prerequisite 202609151440027_CopilotPromptSafetyFields is missing from __MigrationHistory, so the schema was not stamped.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAdoptionPeriodFacts: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
GO
