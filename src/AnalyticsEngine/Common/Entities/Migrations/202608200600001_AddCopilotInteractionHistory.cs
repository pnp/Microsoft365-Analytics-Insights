namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Creates the tables for the optional Copilot AI interaction-history import (Microsoft Graph
    /// <c>getAllEnterpriseInteractions</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purely additive.</b> Ten new tables, no change to any existing table, so there is no table rewrite,
    /// no blocking schema lock and no maintenance window needed - the tables are created empty in seconds
    /// regardless of tenant size. Nothing here alters an existing query path, so there is no before/after
    /// benchmark to report: there is no "before".
    /// </para>
    /// <para>
    /// <b>No prompt or response text is stored.</b> The importer derives counts from each interaction body
    /// and discards it. The only free text these tables can lead to is Azure AI Language key phrases, which
    /// land in the shared <c>keywords</c> lookup and are only produced when cognitive services are enabled.
    /// </para>
    /// <para>
    /// <b>Two index choices worth knowing about.</b> The unique key on <c>copilot_interaction_sessions</c> is
    /// (<c>user_id</c>, <c>session_ref</c>) rather than <c>session_ref</c> alone, because a Copilot thread
    /// can be shared - a Teams meeting session appears in more than one participant's history - and a global
    /// unique constraint would make the second participant's insert collide. A separate non-unique index on
    /// <c>session_ref</c> keeps the join back to <c>copilot_chats.thread_id</c> a seek. The unique key on
    /// <c>copilot_interactions</c> (<c>session_id</c>, <c>graph_interaction_id</c>) is what makes the import
    /// idempotent: the query window deliberately overlaps the previous one, so re-read rows must collapse.
    /// </para>
    /// <para>
    /// <b>Why <c>copilot_interactions.user_id</c> does not cascade.</b> That column is denormalised from the
    /// parent session so per-user reporting doesn't need a join. Users already reach interactions through
    /// <c>users -&gt; copilot_interaction_sessions -&gt; copilot_interactions</c>, so cascading the
    /// denormalised FK as well would give SQL Server two cascade paths to the same table and it refuses the
    /// constraint outright ("may cause cycles or multiple cascade paths"). Interactions are still removed
    /// with their session, and <c>CleanDataByUser</c> deletes them explicitly when a user is purged.
    /// </para>
    /// </remarks>
    public partial class AddCopilotInteractionHistory : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.copilot_interaction_app_classes",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.copilot_interaction_conversation_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.copilot_interaction_devices",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.copilot_interaction_import_log",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        run_started_utc = c.DateTime(nullable: false),
                        run_finished_utc = c.DateTime(),
                        users_in_scope = c.Int(nullable: false),
                        users_scanned = c.Int(nullable: false),
                        users_skipped = c.Int(nullable: false),
                        users_failed = c.Int(nullable: false),
                        interactions_read = c.Int(nullable: false),
                        interactions_saved = c.Int(nullable: false),
                        cognitive_docs_scored = c.Int(nullable: false),
                        error = c.String(maxLength: 1000),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_interaction_keywords",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        interaction_id = c.Int(nullable: false),
                        keyword_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_interactions", t => t.interaction_id, cascadeDelete: true)
                .ForeignKey("dbo.keywords", t => t.keyword_id, cascadeDelete: true)
                .Index(t => new { t.interaction_id, t.keyword_id }, unique: true);
            
            CreateTable(
                "dbo.copilot_interactions",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        graph_interaction_id = c.String(nullable: false, maxLength: 200),
                        session_id = c.Int(nullable: false),
                        user_id = c.Int(nullable: false),
                        request_id = c.String(maxLength: 200),
                        interaction_type_id = c.Int(),
                        app_class_id = c.Int(),
                        conversation_type_id = c.Int(),
                        locale_id = c.Int(),
                        device_id = c.Int(),
                        created_utc = c.DateTime(nullable: false),
                        body_char_count = c.Int(nullable: false),
                        body_word_count = c.Int(nullable: false),
                        attachment_count = c.Int(nullable: false),
                        link_count = c.Int(nullable: false),
                        mention_count = c.Int(nullable: false),
                        context_count = c.Int(nullable: false),
                        response_latency_ms = c.Int(),
                        sentiment_score = c.Double(),
                        language_id = c.Int(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_interaction_app_classes", t => t.app_class_id)
                .ForeignKey("dbo.copilot_interaction_conversation_types", t => t.conversation_type_id)
                .ForeignKey("dbo.copilot_interaction_devices", t => t.device_id)
                .ForeignKey("dbo.copilot_interaction_types", t => t.interaction_type_id)
                .ForeignKey("dbo.languages", t => t.language_id)
                .ForeignKey("dbo.copilot_interaction_locales", t => t.locale_id)
                .ForeignKey("dbo.copilot_interaction_sessions", t => t.session_id, cascadeDelete: true)
                .ForeignKey("dbo.users", t => t.user_id)
                .Index(t => new { t.session_id, t.graph_interaction_id }, unique: true)
                .Index(t => new { t.user_id, t.created_utc })
                .Index(t => t.request_id)
                .Index(t => t.interaction_type_id)
                .Index(t => t.app_class_id)
                .Index(t => t.conversation_type_id)
                .Index(t => t.locale_id)
                .Index(t => t.device_id)
                .Index(t => t.created_utc)
                .Index(t => t.language_id);
            
            CreateTable(
                "dbo.copilot_interaction_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.copilot_interaction_locales",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.copilot_interaction_sessions",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        session_ref = c.String(nullable: false, maxLength: 450),
                        user_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => new { t.user_id, t.session_ref }, unique: true)
                .Index(t => t.session_ref);
            
            CreateTable(
                "dbo.copilot_interaction_user_watermarks",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        user_id = c.Int(nullable: false),
                        last_interaction_utc = c.DateTime(),
                        last_run_utc = c.DateTime(),
                        consecutive_empty_or_failed = c.Int(nullable: false),
                        skip_until_utc = c.DateTime(),
                        last_error = c.String(maxLength: 500),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id, unique: true);
            
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.copilot_interaction_user_watermarks", "user_id", "dbo.users");
            DropForeignKey("dbo.copilot_interaction_keywords", "keyword_id", "dbo.keywords");
            DropForeignKey("dbo.copilot_interaction_keywords", "interaction_id", "dbo.copilot_interactions");
            DropForeignKey("dbo.copilot_interactions", "user_id", "dbo.users");
            DropForeignKey("dbo.copilot_interactions", "session_id", "dbo.copilot_interaction_sessions");
            DropForeignKey("dbo.copilot_interaction_sessions", "user_id", "dbo.users");
            DropForeignKey("dbo.copilot_interactions", "locale_id", "dbo.copilot_interaction_locales");
            DropForeignKey("dbo.copilot_interactions", "language_id", "dbo.languages");
            DropForeignKey("dbo.copilot_interactions", "interaction_type_id", "dbo.copilot_interaction_types");
            DropForeignKey("dbo.copilot_interactions", "device_id", "dbo.copilot_interaction_devices");
            DropForeignKey("dbo.copilot_interactions", "conversation_type_id", "dbo.copilot_interaction_conversation_types");
            DropForeignKey("dbo.copilot_interactions", "app_class_id", "dbo.copilot_interaction_app_classes");
            DropIndex("dbo.copilot_interaction_user_watermarks", new[] { "user_id" });
            DropIndex("dbo.copilot_interaction_sessions", new[] { "session_ref" });
            DropIndex("dbo.copilot_interaction_sessions", new[] { "user_id", "session_ref" });
            DropIndex("dbo.copilot_interaction_locales", new[] { "name" });
            DropIndex("dbo.copilot_interaction_types", new[] { "name" });
            DropIndex("dbo.copilot_interactions", new[] { "language_id" });
            DropIndex("dbo.copilot_interactions", new[] { "created_utc" });
            DropIndex("dbo.copilot_interactions", new[] { "device_id" });
            DropIndex("dbo.copilot_interactions", new[] { "locale_id" });
            DropIndex("dbo.copilot_interactions", new[] { "conversation_type_id" });
            DropIndex("dbo.copilot_interactions", new[] { "app_class_id" });
            DropIndex("dbo.copilot_interactions", new[] { "interaction_type_id" });
            DropIndex("dbo.copilot_interactions", new[] { "request_id" });
            DropIndex("dbo.copilot_interactions", new[] { "user_id", "created_utc" });
            DropIndex("dbo.copilot_interactions", new[] { "session_id", "graph_interaction_id" });
            DropIndex("dbo.copilot_interaction_keywords", new[] { "interaction_id", "keyword_id" });
            DropIndex("dbo.copilot_interaction_devices", new[] { "name" });
            DropIndex("dbo.copilot_interaction_conversation_types", new[] { "name" });
            DropIndex("dbo.copilot_interaction_app_classes", new[] { "name" });
            DropTable("dbo.copilot_interaction_user_watermarks");
            DropTable("dbo.copilot_interaction_sessions");
            DropTable("dbo.copilot_interaction_locales");
            DropTable("dbo.copilot_interaction_types");
            DropTable("dbo.copilot_interactions");
            DropTable("dbo.copilot_interaction_keywords");
            DropTable("dbo.copilot_interaction_import_log");
            DropTable("dbo.copilot_interaction_devices");
            DropTable("dbo.copilot_interaction_conversation_types");
            DropTable("dbo.copilot_interaction_app_classes");
        }
    }
}

