namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the Copilot Adoption intervention table. CLASSIFICATION: PURELY ADDITIVE. New empty table,
    /// constraints and table-local key only; no existing table is rewritten and no performance-motivated
    /// index is added, so no before/after benchmark is required.
    /// </summary>
    public partial class CopilotAdoptionInterventions : DbMigration
    {
        public const string Up_Sql = @"SET NOCOUNT ON;
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
";

        public const string Down_Sql = @"SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_adoption_intervention', N'U') IS NOT NULL DROP TABLE [dbo].[copilot_adoption_intervention];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotAdoptionInterventions'. Adds intervention records. Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotAdoptionInterventions'. Drops intervention records.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
