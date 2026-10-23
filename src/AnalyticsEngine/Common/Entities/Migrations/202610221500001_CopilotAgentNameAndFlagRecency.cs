namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Lets the Copilot agents upsert keep the NEWEST name and custom-agent flag of an agent, rather than whichever
    /// batch happened to be committed last.
    ///
    /// <para>
    /// <b>Why.</b> The importer reads audit content newest-first and commits it in batches of about 2,000 events, and
    /// each commit runs the agents upsert (<c>common_upsert_copilot_agents.sql</c>) three times: once for each of the
    /// chat-only, file and meeting staging tables. So an older batch can be committed after a newer one, and it then
    /// put the agent's older name or flag back. Nothing stored said how recent the stored name or flag was, and
    /// <c>copilot_chats</c> cannot say it either: it records an agent interaction, not whether that interaction
    /// carried a name. Inferring recency from it held back renames, and left a stored schema-name fallback
    /// (for example <c>cr123_contosoHelpdesk</c>) in place for good once a newer unnamed interaction was stored.
    /// </para>
    ///
    /// <para><b>Schema changes</b>, all on <c>dbo.copilot_agents</c>:</para>
    /// <list type="bullet">
    /// <item><c>name_seen_utc datetime2(3) NULL</c>: the audit time of the newest record that carried the stored
    /// display name, as far as the upsert knows. NULL when unknown: a row stored before this migration, a name taken
    /// from a batch whose records disagreed, or a schema-name fallback.</item>
    /// <item><c>flag_seen_utc datetime2(3) NULL</c>: the same, for <c>is_custom_agent</c>.</item>
    /// <item><c>name_is_fallback bit NULL</c>: 1 when the stored name is a Copilot Studio schema name rather than a
    /// display name, 0 when it is a display name, NULL when unknown (every row stored before this migration).</item>
    /// </list>
    /// <para>
    /// A stored time of NULL counts as older than any batch, so a row stored before this migration behaves exactly as
    /// it did: the next batch that agrees on a name replaces it, and only from then on is its time recorded. That is
    /// also why <c>name_is_fallback</c> is not backfilled: whether a stored name is a schema name can only be proven
    /// from the raw audit payload, and a legacy row's NULL time already lets any display name replace it.
    /// </para>
    ///
    /// <para>
    /// <b>Classification: purely additive.</b> Three NULLable columns with no default, which is a metadata-only change
    /// on every SQL Server edition. No index, no backfill, no data moves, so there is no "before" query to measure and
    /// the benchmark gate for performance-motivated schema changes does not apply. The upsert's new predicates run only
    /// on the rows of <c>copilot_agents</c> it already joins to the batch's agents, so it adds no scan; the audit time
    /// it compares comes from a clustered-index seek on <c>audit_events</c> per staged agent interaction, the same seek
    /// the interaction insert that follows it makes.
    /// </para>
    ///
    /// <para>
    /// <b>Upgrade time.</b> Effectively instant whatever the size of the database, on any edition: <c>copilot_agents</c>
    /// holds one row per agent, and the column adds are metadata-only. Each needs a brief schema-modification lock on
    /// <c>copilot_agents</c>. No ONLINE operation is needed.
    /// </para>
    ///
    /// <para>
    /// <b>EF model.</b> The columns are deliberately not mapped on <c>CopilotAgent</c>: only
    /// the upsert's SQL reads or writes them. EF lists the columns it maps in every statement it generates, so the
    /// NULLable extra columns are invisible to it (an EF insert leaves them NULL, which is the "unknown" state). The
    /// model is unchanged and this migration reuses its predecessor's snapshot byte for byte.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> Each step is guarded and re-runnable, and runs with <c>suppressTransaction: true</c>, so a
    /// partial apply converges on re-run. Progress is reported with <c>RAISERROR ... WITH NOWAIT</c>.
    /// </para>
    /// </summary>
    public partial class CopilotAgentNameAndFlagRecency : DbMigration
    {
        /// <summary>
        /// The whole migration as one guarded, idempotent script. A <c>public const</c> so the shipped
        /// <c>202610221500001_CopilotAgentNameAndFlagRecency.manual.sql</c> can embed it verbatim.
        /// </summary>
        public const string Up_Sql = @"SET NOCOUNT ON;

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

RAISERROR('CopilotAgentNameAndFlagRecency: schema is present.', 0, 1) WITH NOWAIT;";

        /// <summary>The reverse, kept as a <c>public const</c> so tests can replay it.</summary>
        public const string Down_Sql = @"IF COL_LENGTH(N'dbo.copilot_agents', N'name_is_fallback') IS NOT NULL
    ALTER TABLE [dbo].[copilot_agents] DROP COLUMN [name_is_fallback];

IF COL_LENGTH(N'dbo.copilot_agents', N'flag_seen_utc') IS NOT NULL
    ALTER TABLE [dbo].[copilot_agents] DROP COLUMN [flag_seen_utc];

IF COL_LENGTH(N'dbo.copilot_agents', N'name_seen_utc') IS NOT NULL
    ALTER TABLE [dbo].[copilot_agents] DROP COLUMN [name_seen_utc];";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotAgentNameAndFlagRecency'. Adds three NULLable columns to dbo.copilot_agents so an older import batch can no longer put back an agent's older name or custom-agent flag. Metadata-only and instant; no data moves.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotAgentNameAndFlagRecency'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
