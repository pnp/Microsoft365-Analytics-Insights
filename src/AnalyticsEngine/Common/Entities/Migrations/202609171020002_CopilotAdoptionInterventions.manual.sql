/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202609171020002_CopilotAdoptionInterventions
   =====================================================================================================
   Adds Copilot Adoption intervention records and intended reinvestment fields. Purely additive schema.

   RUN ORDER
     Run after 202609171020001_CopilotAdoptionCohorts. This script stamps __MigrationHistory by
     copying that predecessor's model blob because the EF model is unchanged.

   SAFETY
     Idempotent and guarded. The pre-stamp guard checks schema only, never row data.
   ===================================================================================================== */
SET NOCOUNT ON;
GO

SET NOCOUNT ON;
RAISERROR('CopilotAdoptionInterventions: creating additive intervention table for Copilot Adoption. Purely additive; no performance benchmark required.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_intervention', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_intervention]
    (
        [intervention_id] int IDENTITY(1,1) NOT NULL,
        [cohort_id] int NOT NULL,
        [owner] nvarchar(256) NULL,
        [intervention_type] nvarchar(40) NOT NULL,
        [guidance_resource] nvarchar(200) NULL,
        [started_utc] datetime2(7) NULL,
        [due_utc] datetime2(7) NULL,
        [completed_utc] datetime2(7) NULL,
        [status] nvarchar(40) NOT NULL CONSTRAINT [DF_copilot_adoption_intervention_status] DEFAULT (N'planned'),
        [intended_outcome] nvarchar(1000) NULL,
        [notes] nvarchar(max) NULL,
        [intended_reinvestment_type] nvarchar(80) NOT NULL CONSTRAINT [DF_copilot_adoption_intervention_reinvestment_type] DEFAULT (N'other'),
        [intended_reinvestment_description] nvarchar(1000) NULL,
        [created_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_intervention_created_utc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_copilot_adoption_intervention] PRIMARY KEY CLUSTERED ([intervention_id] ASC),
        CONSTRAINT [CK_copilot_adoption_intervention_status] CHECK ([status] IN (N'planned', N'started', N'completed', N'cancelled')),
        CONSTRAINT [CK_copilot_adoption_intervention_type] CHECK ([intervention_type] IN (N'briefing', N'scenario workshop', N'champion session', N'comms', N'one-to-one', N'licence reassignment'))
    );
    RAISERROR('CopilotAdoptionInterventions: created dbo.copilot_adoption_intervention.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAdoptionInterventions: dbo.copilot_adoption_intervention already exists.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_intervention', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.copilot_adoption_intervention', N'intended_reinvestment_type') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_intervention]
        ADD [intended_reinvestment_type] nvarchar(80) NOT NULL CONSTRAINT [DF_copilot_adoption_intervention_reinvestment_type] DEFAULT (N'other'),
            [intended_reinvestment_description] nvarchar(1000) NULL;
    RAISERROR('CopilotAdoptionInterventions: added intended reinvestment columns.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_intervention', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.copilot_adoption_cohort', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_intervention_cohort' AND parent_object_id = OBJECT_ID(N'dbo.copilot_adoption_intervention'))
BEGIN
    ALTER TABLE [dbo].[copilot_adoption_intervention] WITH CHECK ADD CONSTRAINT [FK_copilot_adoption_intervention_cohort]
        FOREIGN KEY ([cohort_id]) REFERENCES [dbo].[copilot_adoption_cohort] ([cohort_id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[copilot_adoption_intervention] CHECK CONSTRAINT [FK_copilot_adoption_intervention_cohort];
    RAISERROR('CopilotAdoptionInterventions: created FK_copilot_adoption_intervention_cohort.', 0, 1) WITH NOWAIT;
END

RAISERROR('CopilotAdoptionInterventions: finished.', 0, 1) WITH NOWAIT;

GO

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171020002_CopilotAdoptionInterventions')
BEGIN
    DECLARE @missing nvarchar(max) = N'';
    IF OBJECT_ID(N'dbo.copilot_adoption_intervention', N'U') IS NULL SET @missing += N'copilot_adoption_intervention; ';
    IF COL_LENGTH(N'dbo.copilot_adoption_intervention', N'intended_outcome') IS NULL SET @missing += N'copilot_adoption_intervention.intended_outcome; ';
    IF COL_LENGTH(N'dbo.copilot_adoption_intervention', N'intended_reinvestment_type') IS NULL SET @missing += N'copilot_adoption_intervention.intended_reinvestment_type; ';
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_copilot_adoption_intervention_cohort') SET @missing += N'FK_copilot_adoption_intervention_cohort; ';

    IF @missing <> N''
        RAISERROR(N'CopilotAdoptionInterventions: NOT stamped - schema work incomplete. Missing: %s', 16, 1, @missing) WITH NOWAIT;
    ELSE IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171020001_CopilotAdoptionCohorts')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609171020002_CopilotAdoptionInterventions', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609171020001_CopilotAdoptionCohorts';
        RAISERROR('CopilotAdoptionInterventions: recorded in __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR('CopilotAdoptionInterventions: prerequisite 202609171020001_CopilotAdoptionCohorts is missing from __MigrationHistory, so the schema was not stamped.', 16, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAdoptionInterventions: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
GO
