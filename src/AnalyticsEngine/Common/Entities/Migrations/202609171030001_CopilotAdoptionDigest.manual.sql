/* =====================================================================================================
   MANUAL DATABASE UPGRADE SCRIPT
   Migration: 202609171030001_CopilotAdoptionDigest

   Applies the migration's Up SQL verbatim, then stamps dbo.__MigrationHistory so EF and the Health
   page treat it as applied.

   PREREQUISITE
     202609171020002_CopilotAdoptionInterventions must already be stamped. This branch is stacked
     after the adoption interventions migration; run those manual scripts first.

   WHAT IT DOES
     Adds dbo.copilot_adoption_digest_run, an operational outbox/status table used to make scheduled
     aggregate digest sends idempotent by period + recipient set and to surface generation/send
     failures in Health. Text columns are nvarchar for Unicode-safe subjects and portal URLs.

   PERFORMANCE CLASSIFICATION
     Purely additive schema: one new empty table and two indexes. There is no before query to
     benchmark, so the performance-motivated schema benchmark gate does not apply.

   SAFE TO RE-RUN: every schema step is guarded and idempotent. The stamp guard checks schema only.
   ===================================================================================================== */

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('CopilotAdoptionDigest: dbo.__MigrationHistory does not exist - this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171020002_CopilotAdoptionInterventions')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171030001_CopilotAdoptionDigest')
BEGIN
    RAISERROR('CopilotAdoptionDigest: prerequisite migration 202609171020002_CopilotAdoptionInterventions is not stamped in __MigrationHistory. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

RAISERROR('CopilotAdoptionDigest: pre-flight checks passed.', 0, 1) WITH NOWAIT;
GO

SET QUOTED_IDENTIFIER ON;
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

GO

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'period_end')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'recipients_hash')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'error')
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'UX_copilot_adoption_digest_run_period_recipients')
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_digest_run') AND name = N'IX_copilot_adoption_digest_run_updated')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609171030001_CopilotAdoptionDigest')
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609171030001_CopilotAdoptionDigest', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609171020002_CopilotAdoptionInterventions';
        RAISERROR('CopilotAdoptionDigest: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        RAISERROR('CopilotAdoptionDigest: __MigrationHistory is already stamped.', 0, 1) WITH NOWAIT;
    END
END
ELSE
BEGIN
    RAISERROR('CopilotAdoptionDigest: NOT stamped - required schema objects are missing.', 16, 1) WITH NOWAIT;
END
GO

SET NOEXEC OFF;
GO
