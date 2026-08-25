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
    ///   * The two foreign keys on copilot_event_accessed_resources are the only step that touches the
    ///     existing data, and they are NOT free. SQL Server validates each checked constraint by scanning
    ///     the table while holding a schema-modification (Sch-M) lock, which blocks all access including
    ///     reads - and there is no ONLINE form of foreign-key validation, so no edition avoids it.
    ///     The scan happens even though every existing row has NULL in both columns and there is
    ///     therefore nothing to verify: measured, 300,000 all-NULL rows still cost 8,139 logical reads and
    ///     a Sch-M lock per constraint. On a 3,000,000-row junction table that was about 7,800 logical
    ///     reads and ~0.6-0.7 s per constraint; extrapolating, roughly 0.4-0.5 s at 1M rows, 4-5 s at 10M
    ///     and 40-50 s at 100M for the pair. Large databases should therefore run this in a maintenance
    ///     window with the importer stopped, on EVERY edition.
    ///   * The two foreign-key INDEXES that used to be built here are no longer part of this migration.
    ///     They now belong to <see cref="IndexCopilotAccessedResourceFkColumns"/>, together with their
    ///     measured build times and their ONLINE/offline edition handling.
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

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotDroppedAuditFields'. Adds the Copilot audit fields the importer parsed but never stored (all interaction contexts, AI system plugins, accessed-resource action / listItemUniqueId, message size / isPrompt, model provider / version, thread id, client region, log version). New tables and NULLable columns are instant. The two foreign-key indexes on copilot_event_accessed_resources are built separately by IndexCopilotAccessedResourceFkColumns.");

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
