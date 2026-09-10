namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the Data Loss Prevention tables that back the "DLP impact on Copilot" report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Classification: purely additive.</b> Five new, initially-empty tables plus their foreign-key
    /// indexes. Nothing existing is altered, narrowed, dropped or re-indexed - in particular this does
    /// NOT touch <c>copilot_event_accessed_resources</c> or its measured de-duplication index
    /// (<c>IX_copilot_event_accessed_resources_dedup</c>, see <c>WidenCopilotAccessedResourceDedupIndex</c>).
    /// There is therefore no "before" query to benchmark: the repo's prove-it rule applies to
    /// performance-motivated schema changes, and this is not one.
    /// </para>
    /// <para>
    /// <b>Upgrade cost:</b> creating five empty tables is effectively instant regardless of tenant size
    /// (sub-second at 1M, 10M and 100M rows on the fact tables), because no existing table is read,
    /// rewritten or re-indexed. No maintenance window is required and the importer can keep running.
    /// </para>
    /// <para>
    /// <b>Why <c>copilot_dlp_events</c> is a separate table</b> rather than extra columns on
    /// <c>copilot_event_accessed_resources</c>: that table is the largest Copilot table (rows =
    /// interactions x resources) and its import-path de-duplication seeks on a benchmarked composite
    /// index. Widening its de-dup tuple would invalidate that benchmark and slow the hot path for every
    /// customer, to carry columns that would be NULL on the overwhelming majority of rows.
    /// Policy-affected resources are rare, so a sparse side table keyed on the chat is both cheaper and
    /// - unlike an index-shape change - purely additive.
    /// </para>
    /// </remarks>
    public partial class DlpCopilotImpact : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.copilot_dlp_events",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        copilot_chat_id = c.Guid(nullable: false),
                        dlp_policy_id = c.Int(),
                        dlp_rule_id = c.Int(),
                        dlp_action_id = c.Int(),
                        resource_name_id = c.Int(),
                        resource_type_id = c.Int(),
                        sensitivity_label_id = c.Int(),
                        is_blocked = c.Boolean(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.dlp_actions", t => t.dlp_action_id)
                .ForeignKey("dbo.dlp_policies", t => t.dlp_policy_id)
                .ForeignKey("dbo.copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .ForeignKey("dbo.copilot_event_accessed_resource_names", t => t.resource_name_id)
                .ForeignKey("dbo.copilot_event_accessed_resource_types", t => t.resource_type_id)
                .ForeignKey("dbo.dlp_rules", t => t.dlp_rule_id)
                .ForeignKey("dbo.sensitivity_labels", t => t.sensitivity_label_id)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.dlp_policy_id)
                .Index(t => t.dlp_rule_id)
                .Index(t => t.dlp_action_id)
                .Index(t => t.resource_name_id)
                .Index(t => t.resource_type_id)
                .Index(t => t.sensitivity_label_id);
            
            CreateTable(
                "dbo.dlp_actions",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.dlp_policies",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        policy_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 400),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.policy_id, unique: true);
            
            CreateTable(
                "dbo.dlp_rules",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        rule_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 400),
                        dlp_policy_id = c.Int(),
                        severity = c.String(maxLength: 50),
                        rule_mode = c.String(maxLength: 50),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.dlp_policies", t => t.dlp_policy_id)
                .Index(t => t.rule_id, unique: true)
                .Index(t => t.dlp_policy_id);
            
            CreateTable(
                "dbo.dlp_rule_matches",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        event_id = c.Guid(nullable: false),
                        dlp_policy_id = c.Int(),
                        dlp_rule_id = c.Int(),
                        dlp_action_id = c.Int(),
                        is_blocked = c.Boolean(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.dlp_actions", t => t.dlp_action_id)
                .ForeignKey("dbo.audit_events", t => t.event_id, cascadeDelete: true)
                .ForeignKey("dbo.dlp_policies", t => t.dlp_policy_id)
                .ForeignKey("dbo.dlp_rules", t => t.dlp_rule_id)
                .Index(t => t.event_id)
                .Index(t => t.dlp_policy_id)
                .Index(t => t.dlp_rule_id)
                .Index(t => t.dlp_action_id);
            
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.dlp_rule_matches", "dlp_rule_id", "dbo.dlp_rules");
            DropForeignKey("dbo.dlp_rule_matches", "dlp_policy_id", "dbo.dlp_policies");
            DropForeignKey("dbo.dlp_rule_matches", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.dlp_rule_matches", "dlp_action_id", "dbo.dlp_actions");
            DropForeignKey("dbo.copilot_dlp_events", "sensitivity_label_id", "dbo.sensitivity_labels");
            DropForeignKey("dbo.copilot_dlp_events", "dlp_rule_id", "dbo.dlp_rules");
            DropForeignKey("dbo.dlp_rules", "dlp_policy_id", "dbo.dlp_policies");
            DropForeignKey("dbo.copilot_dlp_events", "resource_type_id", "dbo.copilot_event_accessed_resource_types");
            DropForeignKey("dbo.copilot_dlp_events", "resource_name_id", "dbo.copilot_event_accessed_resource_names");
            DropForeignKey("dbo.copilot_dlp_events", "copilot_chat_id", "dbo.copilot_chats");
            DropForeignKey("dbo.copilot_dlp_events", "dlp_policy_id", "dbo.dlp_policies");
            DropForeignKey("dbo.copilot_dlp_events", "dlp_action_id", "dbo.dlp_actions");
            DropIndex("dbo.dlp_rule_matches", new[] { "dlp_action_id" });
            DropIndex("dbo.dlp_rule_matches", new[] { "dlp_rule_id" });
            DropIndex("dbo.dlp_rule_matches", new[] { "dlp_policy_id" });
            DropIndex("dbo.dlp_rule_matches", new[] { "event_id" });
            DropIndex("dbo.dlp_rules", new[] { "dlp_policy_id" });
            DropIndex("dbo.dlp_rules", new[] { "rule_id" });
            DropIndex("dbo.dlp_policies", new[] { "policy_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "sensitivity_label_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "resource_type_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "resource_name_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "dlp_action_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "dlp_rule_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "dlp_policy_id" });
            DropIndex("dbo.copilot_dlp_events", new[] { "copilot_chat_id" });
            DropTable("dbo.dlp_rule_matches");
            DropTable("dbo.dlp_rules");
            DropTable("dbo.dlp_policies");
            DropTable("dbo.dlp_actions");
            DropTable("dbo.copilot_dlp_events");
        }
    }
}
