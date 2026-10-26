/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202610221500001_CopilotAgentNameAndFlagRecency

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact schema the migration creates, followed by the __MigrationHistory stamp so EF
   (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as applied.

   RUN ORDER
     Run the release's manual scripts in migration-id order. This one requires
     202610080900001_AddPromptCategories to be stamped in __MigrationHistory first, and refuses to stamp
     itself otherwise. The next script in the chain is 202610221500002_EventFileExtensionUnicode.

   WHAT IT DOES
     The importer commits Copilot interactions newest-first, in batches, so an older batch could be
     committed after a newer one and put an agent's older name or custom-agent flag back. These columns
     record how recent the stored name and flag are, so an older batch no longer replaces them:
       dbo.copilot_agents.name_seen_utc      audit time of the newest record carrying the stored name
       dbo.copilot_agents.flag_seen_utc      audit time of the newest record carrying the stored flag
       dbo.copilot_agents.name_is_fallback   1 when the stored name is a Copilot Studio schema name
     All three start NULL ("not known"), which behaves exactly like before the upgrade: the next batch
     that agrees on a name or carries a flag sets it, and records its time.

   CLASSIFICATION: PURELY ADDITIVE
     Three NULLable columns with no default. No index, no backfill, no data moves. There is no "before"
     query being tuned, so the benchmark gate for performance-motivated schema changes does not apply.

   UPGRADE TIME
     Effectively instant on any tenant size and any SQL Server edition: dbo.copilot_agents holds one row
     per agent and each column add is a metadata-only change. Each needs a brief schema-modification lock
     on dbo.copilot_agents. No ONLINE operations are needed. You do not need to stop the importer, but the
     new importer build needs these columns, so run this script before starting it.

   SAFE TO RE-RUN
     Every step is guarded. Re-running the script against an upgraded database changes nothing.
   ===================================================================================================== */

SET NOCOUNT ON;

RAISERROR('CopilotAgentNameAndFlagRecency: starting. Purely additive: three NULLable columns on dbo.copilot_agents. No backfill, no index, no data moves.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_agents', N'U') IS NULL
BEGIN
    RAISERROR('CopilotAgentNameAndFlagRecency: dbo.copilot_agents does not exist, so its prerequisite migration 202510231511508_CopilotAgents has not been applied.', 16, 1);
    RETURN;
END

-- NULLable columns with no default: metadata-only, so instant on every edition.
IF COL_LENGTH(N'dbo.copilot_agents', N'name_seen_utc') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_agents] ADD [name_seen_utc] datetime2(3) NULL;
    RAISERROR('CopilotAgentNameAndFlagRecency: added dbo.copilot_agents.name_seen_utc.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAgentNameAndFlagRecency: dbo.copilot_agents.name_seen_utc already exists; skipping.', 0, 1) WITH NOWAIT;

IF COL_LENGTH(N'dbo.copilot_agents', N'flag_seen_utc') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_agents] ADD [flag_seen_utc] datetime2(3) NULL;
    RAISERROR('CopilotAgentNameAndFlagRecency: added dbo.copilot_agents.flag_seen_utc.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAgentNameAndFlagRecency: dbo.copilot_agents.flag_seen_utc already exists; skipping.', 0, 1) WITH NOWAIT;

IF COL_LENGTH(N'dbo.copilot_agents', N'name_is_fallback') IS NULL
BEGIN
    ALTER TABLE [dbo].[copilot_agents] ADD [name_is_fallback] bit NULL;
    RAISERROR('CopilotAgentNameAndFlagRecency: added dbo.copilot_agents.name_is_fallback.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotAgentNameAndFlagRecency: dbo.copilot_agents.name_is_fallback already exists; skipping.', 0, 1) WITH NOWAIT;

RAISERROR('CopilotAgentNameAndFlagRecency: schema is present.', 0, 1) WITH NOWAIT;
GO

-------------------------------------------------------------------------------------------------
-- Record the migration in __MigrationHistory.
--
-- The guard checks SCHEMA only: the three columns must exist, and the predecessor must be stamped.
-- It never checks data. Reached on every path, including a re-run against a database that is
-- already up to date. The model blob is copied from the predecessor, because this migration does
-- not change the EF model.
-------------------------------------------------------------------------------------------------
RAISERROR('CopilotAgentNameAndFlagRecency: stamping __MigrationHistory.', 0, 1) WITH NOWAIT;

IF COL_LENGTH(N'dbo.copilot_agents', N'name_seen_utc') IS NULL
   OR COL_LENGTH(N'dbo.copilot_agents', N'flag_seen_utc') IS NULL
   OR COL_LENGTH(N'dbo.copilot_agents', N'name_is_fallback') IS NULL
BEGIN
    RAISERROR('CopilotAgentNameAndFlagRecency: NOT stamped - the schema work did not complete: dbo.copilot_agents.name_seen_utc, flag_seen_utc or name_is_fallback is missing. Re-run this script and read the messages above.', 16, 1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM [dbo].[__MigrationHistory] WHERE [MigrationId] = N'202610080900001_AddPromptCategories')
BEGIN
    RAISERROR('CopilotAgentNameAndFlagRecency: NOT stamped - prerequisite migration 202610080900001_AddPromptCategories is not stamped in __MigrationHistory. Run its manual script first.', 16, 1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM [dbo].[__MigrationHistory] WHERE [MigrationId] = N'202610221500001_CopilotAgentNameAndFlagRecency')
BEGIN
    INSERT INTO [dbo].[__MigrationHistory] ([MigrationId], [ContextKey], [Model], [ProductVersion])
    SELECT N'202610221500001_CopilotAgentNameAndFlagRecency', [ContextKey], [Model], [ProductVersion]
    FROM [dbo].[__MigrationHistory]
    WHERE [MigrationId] = N'202610080900001_AddPromptCategories';
    RAISERROR('CopilotAgentNameAndFlagRecency: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotAgentNameAndFlagRecency: __MigrationHistory already stamped.', 0, 1) WITH NOWAIT;
END
