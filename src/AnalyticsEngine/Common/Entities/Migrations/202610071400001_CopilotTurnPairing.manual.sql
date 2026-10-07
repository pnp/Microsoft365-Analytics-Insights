/* =====================================================================================================
   MANUAL DATABASE UPGRADE - 202610071400001_CopilotTurnPairing

   For DBAs who upgrade the Analytics database by hand instead of running the installer.
   This is the exact schema the migration creates, followed by the __MigrationHistory stamp so EF
   (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health page treat it as applied.

   RUN ORDER
     Run the release's manual scripts in migration-id order. This one requires
     202610021200001_LicenceHistory to be stamped in __MigrationHistory first, and refuses to stamp
     itself otherwise.

   WHAT IT DOES (issue #699)
     Lets a Microsoft 365 Copilot turn with a Copilot Studio agent count once. Each such turn is audited
     twice - once by the Copilot Studio runtime and once by Microsoft 365 Copilot - and the two records
     share only their conversation id.
       dbo.copilot_chats.conversation_id   the interaction's conversation id (NULLable nvarchar(450))
       dbo.copilot_chat_duplicates         one row per audit record that is an extra record of a turn
                                           counted on another record; filled by the importer

   CLASSIFICATION: PURELY ADDITIVE
     A NULLable column with no default and a new, empty table with its own indexes. No index is built
     on an existing table, nothing is backfilled, no data moves. There is no "before" query being
     tuned, so the benchmark gate for performance-motivated schema changes does not apply.

   UPGRADE TIME
     Effectively instant on any tenant size and any SQL Server edition. Adding a NULLable column with no
     default is a metadata-only change; it needs a brief schema-modification lock on dbo.copilot_chats,
     so it waits for (and briefly blocks) queries that are running against that table. The new table is
     empty. No ONLINE operations are needed. You do not need to stop the importer, though running the
     script while it is idle avoids the brief lock wait.

   SAFE TO RE-RUN
     Every step is guarded. Re-running the script against an upgraded database changes nothing.
   ===================================================================================================== */

SET NOCOUNT ON;

RAISERROR('CopilotTurnPairing: starting. Purely additive: one NULLable column on dbo.copilot_chats and one new, empty table. No backfill and no index on dbo.copilot_chats.', 0, 1) WITH NOWAIT;

-------------------------------------------------------------------------------------------------
-- 1. copilot_chats.conversation_id. A NULLable column with no default is a metadata-only change,
--    so this is instant even on a very large table, on every edition.
-------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.copilot_chats', 'conversation_id') IS NULL
BEGIN
    RAISERROR('CopilotTurnPairing: adding dbo.copilot_chats.conversation_id (metadata-only)...', 0, 1) WITH NOWAIT;
    ALTER TABLE [dbo].[copilot_chats] ADD [conversation_id] nvarchar(450) NULL;
    RAISERROR('CopilotTurnPairing: added dbo.copilot_chats.conversation_id.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotTurnPairing: dbo.copilot_chats.conversation_id already exists; skipping.', 0, 1) WITH NOWAIT;

-------------------------------------------------------------------------------------------------
-- 2. copilot_chat_duplicates: the audit records that are an extra record of a turn counted on
--    another record. Created empty; the importer fills it.
-------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.copilot_chat_duplicates', N'U') IS NULL
BEGIN
    RAISERROR('CopilotTurnPairing: creating dbo.copilot_chat_duplicates...', 0, 1) WITH NOWAIT;
    CREATE TABLE [dbo].[copilot_chat_duplicates]
    (
        [event_id] uniqueidentifier NOT NULL,
        [counted_event_id] uniqueidentifier NOT NULL,
        [reason] tinyint NOT NULL,
        CONSTRAINT [PK_copilot_chat_duplicates] PRIMARY KEY CLUSTERED ([event_id] ASC),
        CONSTRAINT [FK_copilot_chat_duplicates_copilot_chats] FOREIGN KEY ([event_id])
            REFERENCES [dbo].[copilot_chats] ([event_id]) ON DELETE CASCADE,
        CONSTRAINT [CK_copilot_chat_duplicates_reason] CHECK ([reason] IN (1, 2)),
        CONSTRAINT [CK_copilot_chat_duplicates_not_self] CHECK ([counted_event_id] <> [event_id])
    );
    RAISERROR('CopilotTurnPairing: created dbo.copilot_chat_duplicates.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotTurnPairing: dbo.copilot_chat_duplicates already exists; skipping.', 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_chat_duplicates', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.copilot_chat_duplicates')
                     AND name = N'IX_copilot_chat_duplicates_counted_event_id')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_chat_duplicates_counted_event_id]
        ON [dbo].[copilot_chat_duplicates] ([counted_event_id] ASC)
        INCLUDE ([reason]);
    RAISERROR('CopilotTurnPairing: created IX_copilot_chat_duplicates_counted_event_id.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotTurnPairing: IX_copilot_chat_duplicates_counted_event_id already exists; skipping.', 0, 1) WITH NOWAIT;

RAISERROR('CopilotTurnPairing: schema is present.', 0, 1) WITH NOWAIT;
GO

-------------------------------------------------------------------------------------------------
-- Record the migration in __MigrationHistory.
--
-- The guard checks SCHEMA only: the column, the table and its index must exist, and the
-- predecessor must be stamped. It never checks data. Reached on every path, including a re-run
-- against a database that is already up to date. The model blob is copied from the predecessor,
-- because this migration does not change the EF model.
-------------------------------------------------------------------------------------------------
RAISERROR('CopilotTurnPairing: stamping __MigrationHistory.', 0, 1) WITH NOWAIT;

IF COL_LENGTH('dbo.copilot_chats', 'conversation_id') IS NULL
   OR OBJECT_ID(N'dbo.copilot_chat_duplicates', N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM sys.indexes
                  WHERE object_id = OBJECT_ID(N'dbo.copilot_chat_duplicates')
                    AND name = N'IX_copilot_chat_duplicates_counted_event_id')
BEGIN
    RAISERROR('CopilotTurnPairing: NOT stamped - the schema work did not complete: dbo.copilot_chats.conversation_id, dbo.copilot_chat_duplicates or IX_copilot_chat_duplicates_counted_event_id is missing. Re-run this script and read the messages above.', 16, 1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM [dbo].[__MigrationHistory] WHERE [MigrationId] = N'202610021200001_LicenceHistory')
BEGIN
    RAISERROR('CopilotTurnPairing: NOT stamped - prerequisite migration 202610021200001_LicenceHistory is not stamped in __MigrationHistory. Run its manual script first.', 16, 1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM [dbo].[__MigrationHistory] WHERE [MigrationId] = N'202610071400001_CopilotTurnPairing')
BEGIN
    INSERT INTO [dbo].[__MigrationHistory] ([MigrationId], [ContextKey], [Model], [ProductVersion])
    SELECT N'202610071400001_CopilotTurnPairing', [ContextKey], [Model], [ProductVersion]
    FROM [dbo].[__MigrationHistory]
    WHERE [MigrationId] = N'202610021200001_LicenceHistory';
    RAISERROR('CopilotTurnPairing: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('CopilotTurnPairing: __MigrationHistory already stamped.', 0, 1) WITH NOWAIT;
END
