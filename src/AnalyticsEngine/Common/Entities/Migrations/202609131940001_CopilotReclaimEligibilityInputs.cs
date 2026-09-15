namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the two persisted inputs needed to make Copilot Adoption reclaim recommendations safe:
    /// an account-age proxy for future seat tenure, and an admin-maintained exclusion list for cases
    /// the data cannot detect such as long-term leave, part-time roles, service accounts and shared
    /// mailboxes.
    ///
    /// This is a purely additive schema change: a nullable <c>dbo.users.created_utc</c> column and a new
    /// <c>dbo.copilot_adoption_reclaim_exclusions</c> table. No existing query is being tuned and no
    /// existing table is rewritten except for a metadata-only nullable column add, so the performance
    /// benchmark rule for performance-motivated schema changes does not apply.
    ///
    /// The EF entity model is deliberately unchanged. The user import writes <c>created_utc</c> through
    /// its bulk-copy SQL paths, and Copilot Adoption reads it with raw SQL. The exclusion table is also
    /// raw-SQL only for now. Because there is no model change, the .resx snapshot is a byte-identical
    /// copy of 202609101200001_RetireImportDbHacks, and the manual script stamps __MigrationHistory by copying that predecessor row.
    /// </summary>
    public partial class CopilotReclaimEligibilityInputs : DbMigration
    {
        /// <summary>
        /// Additive, guarded and re-runnable schema for account-age and reclaim-exclusion inputs.
        /// Exposed so the manual DBA script can embed exactly the same SQL that EF runs.
        /// </summary>
        public const string Up_Sql = @"SET NOCOUNT ON;

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
";

        public const string Down_Sql = @"SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_reclaim_exclusions', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[copilot_adoption_reclaim_exclusions];
END

IF OBJECT_ID(N'dbo.users', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.users') AND name = N'created_utc')
BEGIN
    ALTER TABLE [dbo].[users] DROP COLUMN [created_utc];
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotReclaimEligibilityInputs'. Adds nullable users.created_utc and dbo.copilot_adoption_reclaim_exclusions for safer Copilot Adoption reclaim recommendations. Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotReclaimEligibilityInputs'. Drops the reclaim-exclusion table and created_utc column.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
