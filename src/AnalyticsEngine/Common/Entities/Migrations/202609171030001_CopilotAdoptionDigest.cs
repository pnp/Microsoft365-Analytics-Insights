namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the outbox/state table for the scheduled Copilot Adoption digest. The digest is an email
    /// notification channel, so retries must not double-send a period; the unique key and claimed/sent
    /// statuses make a period + recipient set idempotent and leave failures visible to the Health page.
    ///
    /// Purely additive schema: a new empty operational table plus lookup indexes. There is no before
    /// query to benchmark, so the performance-motivated schema benchmark gate does not apply.
    /// </summary>
    public partial class CopilotAdoptionDigest : DbMigration
    {
        public const string Up_Sql = @"SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

RAISERROR('CopilotAdoptionDigest: adding scheduled digest send-state storage. This is additive schema, so no performance benchmark is required.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[copilot_adoption_digest_run]
    (
        [id] int IDENTITY(1,1) NOT NULL,
        [period_end] date NULL,
        [period_days] int NULL,
        [recipients_hash] nvarchar(64) NULL,
        [subject] nvarchar(256) NULL,
        [portal_url] nvarchar(2048) NULL,
        [status] nvarchar(32) NOT NULL,
        [phase] nvarchar(32) NULL,
        [claimed_utc] datetime2(7) NULL,
        [sent_utc] datetime2(7) NULL,
        [completed_utc] datetime2(7) NULL,
        [error] nvarchar(max) NULL,
        [created_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_digest_run_created_utc] DEFAULT SYSUTCDATETIME(),
        [updated_utc] datetime2(7) NOT NULL CONSTRAINT [DF_copilot_adoption_digest_run_updated_utc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_copilot_adoption_digest_run] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_copilot_adoption_digest_run_status] CHECK ([status] IN (N'Sending', N'Sent', N'Failed')),
        CONSTRAINT [CK_copilot_adoption_digest_run_period_days] CHECK ([period_days] IS NULL OR [period_days] > 0),
        CONSTRAINT [CK_copilot_adoption_digest_run_hash] CHECK ([recipients_hash] IS NULL OR LEN([recipients_hash]) = 64)
    );
    RAISERROR('CopilotAdoptionDigest: created dbo.copilot_adoption_digest_run.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionDigest: dbo.copilot_adoption_digest_run already exists.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'UX_copilot_adoption_digest_run_period_recipients')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_copilot_adoption_digest_run_period_recipients]
        ON [dbo].[copilot_adoption_digest_run] ([period_end] ASC, [period_days] ASC, [recipients_hash] ASC)
        WHERE [period_end] IS NOT NULL AND [period_days] IS NOT NULL AND [recipients_hash] IS NOT NULL;
    RAISERROR('CopilotAdoptionDigest: created UX_copilot_adoption_digest_run_period_recipients.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionDigest: period/recipient idempotency index already exists or table is missing.', 0, 1) WITH NOWAIT;
END

IF OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'IX_copilot_adoption_digest_run_updated')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_adoption_digest_run_updated]
        ON [dbo].[copilot_adoption_digest_run] ([updated_utc] DESC, [status] ASC)
        INCLUDE ([period_end], [period_days], [completed_utc]);
    RAISERROR('CopilotAdoptionDigest: created IX_copilot_adoption_digest_run_updated.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionDigest: latest-status index already exists or table is missing.', 0, 1) WITH NOWAIT;
END

RAISERROR('CopilotAdoptionDigest: finished.', 0, 1) WITH NOWAIT;
";

        public const string Down_Sql = @"SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[copilot_adoption_digest_run];
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotAdoptionDigest'. Adds scheduled digest send-state storage. Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotAdoptionDigest'. Drops digest send-state storage.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
