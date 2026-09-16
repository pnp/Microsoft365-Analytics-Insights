namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the Copilot Adoption period-history foundation: one run row per published closed period and
    /// one threshold-independent raw fact row per Copilot-licensed user in that period.
    ///
    /// CLASSIFICATION: PURELY ADDITIVE. These are new empty tables plus their table-local constraints and
    /// indexes. No existing table is rewritten and no existing query is being tuned, so the performance
    /// benchmark rule for performance-motivated schema changes does not apply. The clustered period/user
    /// index is additive with the new table and unmeasured because there is no before-plan to compare.
    ///
    /// The EF model is deliberately unchanged: the tables are written and read through raw SQL so stored
    /// history can be populated by the scheduled/off-request publish path without extending the interactive
    /// DbContext surface. Because there is no model change, the .resx snapshot is a byte-identical copy of
    /// 202609151440027_CopilotPromptSafetyFields and the manual script stamps by copying that predecessor.
    /// </summary>
    public partial class CopilotAdoptionPeriodFacts : DbMigration
    {
        public const string Up_Sql = @"SET NOCOUNT ON;
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
";

        public const string Down_Sql = @"SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_adoption_user_period', N'U') IS NOT NULL DROP TABLE [dbo].[copilot_adoption_user_period];
IF OBJECT_ID(N'dbo.copilot_adoption_period_run', N'U') IS NOT NULL DROP TABLE [dbo].[copilot_adoption_period_run];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotAdoptionPeriodFacts'. Adds raw period-fact history tables for Copilot Adoption. Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotAdoptionPeriodFacts'. Drops the Copilot Adoption period-history tables.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
