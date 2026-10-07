namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Lets a Microsoft 365 Copilot turn with a Copilot Studio agent count once (issue #699, phase 2).
    ///
    /// <para>
    /// <b>Why.</b> Each Microsoft 365 Copilot turn with a published Copilot Studio agent is audited twice: the
    /// Copilot Studio runtime logs one record (<c>AppHost</c> <c>m365copilot</c>, no messages) and Microsoft 365
    /// Copilot logs another (<c>AppHost</c> <c>Office</c>, with the agent's name and the messages). Phase 1 put
    /// both on one agent row, but each is still a <c>copilot_chats</c> row, so every agent figure counted the
    /// turn twice. The two records share only the conversation id (<c>CopilotEventData.ConversationId</c>, not
    /// in Microsoft's published schema; the documented <c>ThreadId</c> differs between them), which the importer
    /// threw away.
    /// </para>
    ///
    /// <para><b>Schema changes.</b></para>
    /// <list type="bullet">
    /// <item><c>copilot_chats.conversation_id nvarchar(450) NULL</c>. Every value seen so far is a GUID. It is
    /// sized like <c>thread_id</c>, the documented conversation-thread id, so any other id shape fits, and it is
    /// still narrow enough to be an index key (900 bytes, under the 1,700-byte limit) if a report ever needs one.
    /// The merge trims it with <c>LEFT(..., 450)</c>. No index: the pairing finds a turn's other records through
    /// the existing <c>IX_copilot_chats_time_stamp_user_id</c> (see <c>common_upsert_copilot_agents.sql</c>).</item>
    /// <item><c>copilot_chat_duplicates</c>, a new and empty table: one row per audit record that is an
    /// additional record of a turn counted on another record. <c>event_id</c> is the extra record (primary key,
    /// cascading foreign key to <c>copilot_chats</c>), <c>time_stamp</c> its <c>copilot_chats.time_stamp</c>,
    /// <c>counted_event_id</c> the record the turn is counted on and <c>reason</c> why: 1 for the runtime twin
    /// of a Microsoft 365 Copilot record, 2 for an extra runtime record of the same turn. Both rows stay in
    /// <c>copilot_chats</c>. A report counts a turn once by leaving out the rows listed here; matching on
    /// <c>time_stamp</c> as well as <c>event_id</c> lets it read only its window's slice of this table, through
    /// <c>IX_copilot_chat_duplicates_time_stamp</c>, rather than all of it.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why a table rather than a column on <c>copilot_chats</c>.</b> Every Copilot Adoption agent query reads
    /// <c>copilot_chats</c> through the covering index <c>IX_copilot_chats_time_stamp_user_id</c>
    /// <c>(time_stamp, user_id) INCLUDE (app_host, agent_id)</c>. A flag column outside that index would turn
    /// each of those seeks into a key lookup per agent interaction, and adding it to the INCLUDE list would
    /// rebuild that index, offline on Standard and Express, on every upgrade. <c>event_id</c> is the clustering
    /// key, so every non-clustered index already carries it, and an anti-join on this table's primary key keeps
    /// every existing plan covered. The table only holds the extra records, so it stays small.
    /// </para>
    ///
    /// <para>
    /// <b>Classification: purely additive.</b> A NULLable column with no default (metadata-only, so instant on
    /// any table size and any SQL Server edition) and a new, empty table with its own indexes. No index is built
    /// on an existing table, nothing is backfilled and no data moves, so there is no "before" query to measure
    /// and the benchmark gate for performance-motivated schema changes does not apply. The pairing that writes
    /// the new table runs in the importer and was measured separately (see the pull request).
    /// </para>
    ///
    /// <para>
    /// <b>Upgrade time.</b> Effectively instant whatever the size of <c>copilot_chats</c>: the column add is a
    /// metadata change that needs a brief schema-modification lock on <c>copilot_chats</c>, and the new table is
    /// empty. No ONLINE operations are needed on any edition. Interactions imported before the upgrade keep a NULL
    /// conversation id, so they are not paired; the importer fills it in for any event it re-reads in its
    /// look-back window.
    /// </para>
    ///
    /// <para>
    /// <b>EF model.</b> Neither the column nor the table is in the EF model: every read and write is SQL (the
    /// importer's merge and the report queries). So the model is unchanged and this migration reuses its
    /// predecessor's snapshot byte for byte, and the manual script's stamp copies the predecessor's model blob.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> Each step is guarded and re-runnable, and runs with <c>suppressTransaction: true</c>, so a
    /// partial apply converges on re-run. Progress is reported with <c>RAISERROR ... WITH NOWAIT</c>.
    /// </para>
    /// </summary>
    public partial class CopilotTurnPairing : DbMigration
    {
        /// <summary>
        /// The whole migration as one guarded, idempotent script. A <c>public const</c> so the shipped
        /// <c>202610071400001_CopilotTurnPairing.manual.sql</c> can embed it verbatim.
        /// </summary>
        public const string Up_Sql = @"SET NOCOUNT ON;

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
        [time_stamp] datetime NOT NULL,
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

-- Lets a report read only its window's slice of this table (see CopilotTurnSql.CountedTurn).
IF OBJECT_ID(N'dbo.copilot_chat_duplicates', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.copilot_chat_duplicates')
                     AND name = N'IX_copilot_chat_duplicates_time_stamp')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_copilot_chat_duplicates_time_stamp]
        ON [dbo].[copilot_chat_duplicates] ([time_stamp] ASC);
    RAISERROR('CopilotTurnPairing: created IX_copilot_chat_duplicates_time_stamp.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotTurnPairing: IX_copilot_chat_duplicates_time_stamp already exists; skipping.', 0, 1) WITH NOWAIT;

RAISERROR('CopilotTurnPairing: schema is present.', 0, 1) WITH NOWAIT;";

        /// <summary>The reverse, kept as a <c>public const</c> so tests can replay it.</summary>
        public const string Down_Sql = @"IF OBJECT_ID(N'dbo.copilot_chat_duplicates', N'U') IS NOT NULL
    DROP TABLE [dbo].[copilot_chat_duplicates];

IF COL_LENGTH('dbo.copilot_chats', 'conversation_id') IS NOT NULL
    ALTER TABLE [dbo].[copilot_chats] DROP COLUMN [conversation_id];";

        public override void Up()
        {
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
