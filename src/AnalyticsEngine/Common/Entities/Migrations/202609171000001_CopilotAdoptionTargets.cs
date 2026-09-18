namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds storage for customer-defined Copilot Adoption targets. The table stores the selected metric,
    /// scope, owner, target value/date and the frozen baseline period/value/hash captured when the target
    /// is created, so later retuning cannot move the goalposts retrospectively.
    ///
    /// Purely additive schema: a new empty table plus its lookup index. There is no before query to
    /// benchmark, so the performance-motivated schema benchmark gate does not apply.
    /// </summary>
    public partial class CopilotAdoptionTargets : DbMigration
    {
        public const string Up_Sql = @"SET NOCOUNT ON;

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
";

        public const string Down_Sql = @"SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_targets', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[copilot_adoption_targets];
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotAdoptionTargets'. Adds customer-defined Copilot Adoption target storage. Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotAdoptionTargets'. Drops target storage.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
