namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Persists the Copilot interaction audit fields that the importer already deserialised and then
    /// silently discarded. Everything here comes from the CopilotInteractionAuditRecord schema
    /// (https://learn.microsoft.com/office/office-365-management-api/copilot-schema) and is available in
    /// payloads the importer already downloads - no new API, permission or import-cycle cost.
    ///
    /// SCHEMA CHANGES
    ///   New tables
    ///     * copilot_event_contexts           - EVERY interaction context (Id / Type / ContainerId).
    ///                                          The importer only ever resolves the FIRST file or meeting
    ///                                          context into copilot_event_files / copilot_event_meetings,
    ///                                          so the rest of this (unordered) collection was lost.
    ///     * copilot_event_context_types      - lookup for Contexts[].Type ("docx", "TeamsMeeting", ...).
    ///     * copilot_ai_system_plugins        - lookup for AISystemPlugin (Id / Name / Version).
    ///     * copilot_event_ai_system_plugins  - junction: which plugins grounded which interaction.
    ///     * copilot_event_accessed_resource_actions - lookup for AccessedResources[].Action ("Read", ...).
    ///   New columns
    ///     * copilot_chats.thread_id / client_region / copilot_log_version
    ///     * copilot_event_accessed_resources.action_id / list_item_unique_id_id
    ///     * copilot_event_messages.size / is_prompt
    ///     * copilot_ai_models.provider_name / version
    ///
    /// All text columns are nvarchar so non-Latin content (e.g. a Greek file name in a context URL)
    /// round-trips intact. Widths are bounded rather than nvarchar(max) so nothing here is a LOB.
    ///
    /// NO BACKFILL. Management Activity API content is only retrievable for 7 days, so these columns are
    /// populated for newly imported interactions only; historic rows keep NULL / no child rows.
    ///
    /// COST / RUNTIME
    ///   * The five CreateTable operations are instant (new, empty tables).
    ///   * All nine AddColumn operations add NULLable columns with no default, which SQL Server applies as
    ///     a metadata-only change - instant even on a 100M-row copilot_event_accessed_resources.
    ///   * The only heavy step is the two foreign-key indexes on copilot_event_accessed_resources
    ///     (IX_..._action_id, IX_..._list_item_unique_id_id), which follow that table's existing
    ///     convention (it already carries one index per FK column). They are NOT performance-motivated
    ///     indexes: they exist so the new dimensions behave like the five that are already there.
    ///     They are therefore built here through guarded raw SQL rather than EF's CreateIndex, so they can
    ///     attempt an ONLINE (non-blocking) build on Enterprise (3) / Azure SQL DB (5) / MI (8) and fall
    ///     back to offline elsewhere, run outside the EF transaction, report live progress and no-op on
    ///     re-run.
    ///
    ///     Measured at synthetic scale (LocalDB, offline build, buffer pool dropped before each build,
    ///     medians of 3 runs) on a 3,000,000-row junction table:
    ///
    ///       index                             build time   size
    ///       ---------------------------------------------------
    ///       IX_..._action_id                      2.5 s    40.7 MB
    ///       IX_..._list_item_unique_id_id         3.1 s    40.7 MB
    ///       pair                                  5.6 s    81 MB
    ///
    ///     Extrapolating as O(n log n), the pair costs roughly 2 s / 27 MB at 1M rows, 20 s / 0.3 GB at
    ///     10M rows and 4 min / 2.7 GB at 100M rows. Real timings vary with SQL tier, IO throughput and
    ///     memory grant. Where ONLINE is unavailable each build briefly locks the table: run the upgrade
    ///     in a maintenance window with the importer stopped.
    ///
    /// The de-duplication tuple used by the Copilot merge's accessed-resource anti-join was originally left
    /// un-widened by action_id / list_item_unique_id_id, so that the composite index added by
    /// <see cref="CoverCopilotAccessedResourceDedup"/> still covered it exactly. That decision was REVERSED
    /// before release by <see cref="WidenCopilotAccessedResourceDedupIndex"/> (issue #287): treating the two
    /// new columns as payload rather than identity dropped distinct actions (the same document Read AND
    /// Written in one interaction collapsed to one row) and could fabricate action / list-item pairings via
    /// two independent MIN()s. The merge now de-duplicates on the full tuple and that migration widens the
    /// index to match, which measured as free on a small commit batch and ~10% slower on a large one.
    /// See common_upsert_copilot_agents.sql and that migration's doc comment for the detail.
    ///
    /// This migration DOES change the EF entity model, so its .resx snapshot is freshly scaffolded (it is
    /// not a copy of the predecessor's). The manual upgrade script therefore has to stamp
    /// __MigrationHistory with this new model blob rather than copying the previous row.
    ///
    /// RESUMABILITY - why the index build is NOT in this migration
    ///   It used to end with Sql(JunctionIndexes_Sql, suppressTransaction: true). EF commits everything
    ///   before a transaction-suppressed statement, so a failed or interrupted index build left all five
    ///   tables, nine columns and the foreign keys COMMITTED while the migration stayed unstamped - and the
    ///   retry then hit the unconditional CreateTable and failed on objects that already existed. The
    ///   upgrade could not converge without hand repair, which is exactly what the repo's "idempotent,
    ///   guarded and resumable" rule exists to prevent.
    ///
    ///   The index build now lives in <see cref="IndexCopilotAccessedResourceFkColumns"/>, a later
    ///   index-only migration. This migration is therefore a single atomic transaction: it either applies
    ///   and stamps, or rolls back entirely, and a retry starts from a clean state either way.
    /// </summary>
    public partial class CopilotDroppedAuditFields : DbMigration
    {
        /// <summary>
        /// Builds the two foreign-key indexes on the (potentially huge) accessed-resource junction table.
        /// Exposed as a constant so the manual upgrade script and unit tests use the exact same SQL.
        /// Idempotent, guarded, edition-aware (ONLINE attempt via sp_executesql inside TRY/CATCH - which is
        /// what makes the "ONLINE is Enterprise only" error catchable - with an offline fallback).
        /// </summary>
        public const string JunctionIndexes_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'CopilotDroppedAuditFields';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;
DECLARE @rowCount bigint;
DECLARE @sql nvarchar(max);
DECLARE @ix sysname;
DECLARE @col sysname;
DECLARE @i int = 1;

DECLARE @targets table (seq int NOT NULL PRIMARY KEY, ix sysname NOT NULL, col sysname NOT NULL);
INSERT INTO @targets (seq, ix, col) VALUES
    (1, N'IX_copilot_event_accessed_resources_action_id', N'action_id'),
    (2, N'IX_copilot_event_accessed_resources_list_item_unique_id_id', N'list_item_unique_id_id');

SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index builds '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).'
           ELSE N'are not supported on this edition - each build briefly locks the table, so run large upgrades in a maintenance window with the importer stopped.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist; skipping the junction indexes.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    WHILE @i <= 2
    BEGIN
        SELECT @ix = ix, @col = col FROM @targets WHERE seq = @i;

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
        BEGIN
            SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' does not exist; skipping [' + @ix + N'].';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
        BEGIN
            SET @msg = @migration + N': [' + @ix + N'] already exists; nothing to do.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            SET @stepStart = SYSUTCDATETIME();
            SET @onlineDone = 0;

            IF @canOnline = 1
            BEGIN
                BEGIN TRY
                    SET @msg = @migration + N': creating [' + @ix + N'] WITH (ONLINE = ON)...';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']) WITH (ONLINE = ON);';
                    EXEC sp_executesql @sql;
                    SET @onlineDone = 1;
                END TRY
                BEGIN CATCH
                    SET @msg = @migration + N': ONLINE build of [' + @ix + N'] unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END CATCH
            END

            IF @onlineDone = 0
            BEGIN
                SET @msg = @migration + N': creating [' + @ix + N'] (offline)...';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
                SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']);';
                EXEC sp_executesql @sql;
            END

            SET @msg = @migration + N': [' + @ix + N'] created in '
                + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms ('
                + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END

        SET @i += 1;
    END
END

SET @msg = @migration + N': junction indexes finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/> for the junction indexes. Guarded and idempotent.
        /// </summary>
        public const string JunctionIndexesDown_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_event_accessed_resources') AND name = N'IX_copilot_event_accessed_resources_action_id')
        DROP INDEX [IX_copilot_event_accessed_resources_action_id] ON [dbo].[copilot_event_accessed_resources];
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_event_accessed_resources') AND name = N'IX_copilot_event_accessed_resources_list_item_unique_id_id')
        DROP INDEX [IX_copilot_event_accessed_resources_list_item_unique_id_id] ON [dbo].[copilot_event_accessed_resources];
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotDroppedAuditFields'. Adds the Copilot audit fields the importer parsed but never stored (all interaction contexts, AI system plugins, accessed-resource action / listItemUniqueId, message size / isPrompt, model provider / version, thread id, client region, log version). New tables and NULLable columns are instant; the two foreign-key indexes on copilot_event_accessed_resources can take time on Copilot-heavy tenants - check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");

            CreateTable(
                "dbo.copilot_event_accessed_resource_actions",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.copilot_ai_system_plugins",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    plugin_id = c.String(maxLength: 255),
                    name = c.String(maxLength: 255),
                    version = c.String(maxLength: 50),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.copilot_event_context_types",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.copilot_event_ai_system_plugins",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    copilot_chat_id = c.Guid(nullable: false),
                    ai_system_plugin_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_ai_system_plugins", t => t.ai_system_plugin_id, cascadeDelete: true)
                .ForeignKey("dbo.copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.ai_system_plugin_id);

            CreateTable(
                "dbo.copilot_event_contexts",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    copilot_chat_id = c.Guid(nullable: false),
                    context_ref = c.String(maxLength: 850),
                    context_type_id = c.Int(),
                    container_id = c.String(maxLength: 450),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_event_context_types", t => t.context_type_id)
                .ForeignKey("dbo.copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.context_type_id);

            AddColumn("dbo.copilot_ai_models", "provider_name", c => c.String(maxLength: 100));
            AddColumn("dbo.copilot_ai_models", "version", c => c.String(maxLength: 100));
            AddColumn("dbo.copilot_chats", "thread_id", c => c.String(maxLength: 450));
            AddColumn("dbo.copilot_chats", "client_region", c => c.String(maxLength: 50));
            AddColumn("dbo.copilot_chats", "copilot_log_version", c => c.String(maxLength: 50));
            AddColumn("dbo.copilot_event_accessed_resources", "action_id", c => c.Int());
            AddColumn("dbo.copilot_event_accessed_resources", "list_item_unique_id_id", c => c.Int());
            AddColumn("dbo.copilot_event_messages", "size", c => c.Long());
            AddColumn("dbo.copilot_event_messages", "is_prompt", c => c.Boolean());

            // Cheap: every existing row has NULL in both columns, so SQL Server's WITH CHECK validation
            // finds nothing to verify. These do NOT require the FK-column indexes to exist first - those
            // are built by the separate IndexCopilotAccessedResourceFkColumns migration (see the remarks
            // on this class for why the index build had to leave this migration).
            AddForeignKey("dbo.copilot_event_accessed_resources", "action_id", "dbo.copilot_event_accessed_resource_actions", "id");
            AddForeignKey("dbo.copilot_event_accessed_resources", "list_item_unique_id_id", "dbo.copilot_event_accessed_resource_ids", "id");
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotDroppedAuditFields'.");

            DropForeignKey("dbo.copilot_event_contexts", "copilot_chat_id", "dbo.copilot_chats");
            DropForeignKey("dbo.copilot_event_contexts", "context_type_id", "dbo.copilot_event_context_types");
            DropForeignKey("dbo.copilot_event_ai_system_plugins", "copilot_chat_id", "dbo.copilot_chats");
            DropForeignKey("dbo.copilot_event_ai_system_plugins", "ai_system_plugin_id", "dbo.copilot_ai_system_plugins");
            DropForeignKey("dbo.copilot_event_accessed_resources", "list_item_unique_id_id", "dbo.copilot_event_accessed_resource_ids");
            DropForeignKey("dbo.copilot_event_accessed_resources", "action_id", "dbo.copilot_event_accessed_resource_actions");
            DropIndex("dbo.copilot_event_contexts", new[] { "context_type_id" });
            DropIndex("dbo.copilot_event_contexts", new[] { "copilot_chat_id" });
            DropIndex("dbo.copilot_event_ai_system_plugins", new[] { "ai_system_plugin_id" });
            DropIndex("dbo.copilot_event_ai_system_plugins", new[] { "copilot_chat_id" });
            DropColumn("dbo.copilot_event_messages", "is_prompt");
            DropColumn("dbo.copilot_event_messages", "size");
            DropColumn("dbo.copilot_event_accessed_resources", "list_item_unique_id_id");
            DropColumn("dbo.copilot_event_accessed_resources", "action_id");
            DropColumn("dbo.copilot_chats", "copilot_log_version");
            DropColumn("dbo.copilot_chats", "client_region");
            DropColumn("dbo.copilot_chats", "thread_id");
            DropColumn("dbo.copilot_ai_models", "version");
            DropColumn("dbo.copilot_ai_models", "provider_name");
            DropTable("dbo.copilot_event_contexts");
            DropTable("dbo.copilot_event_ai_system_plugins");
            DropTable("dbo.copilot_event_context_types");
            DropTable("dbo.copilot_ai_system_plugins");
            DropTable("dbo.copilot_event_accessed_resource_actions");
        }
    }
}
