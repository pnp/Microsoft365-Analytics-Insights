namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class PowerPlatformAuditLogging : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.power_app_environments",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        environment_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.environment_id, unique: true);
            
            CreateTable(
                "dbo.copilot_studio_bots",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        bot_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                        environment_id = c.Int(),
                        first_seen_at = c.DateTime(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_app_environments", t => t.environment_id)
                .Index(t => t.bot_id, unique: true)
                .Index(t => t.environment_id);
            
            CreateTable(
                "dbo.event_meta_copilot_studio",
                c => new
                    {
                        event_id = c.Guid(nullable: false),
                        bot_id = c.Int(),
                    })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .ForeignKey("dbo.copilot_studio_bots", t => t.bot_id)
                .Index(t => t.event_id)
                .Index(t => t.bot_id);
            
            CreateTable(
                "dbo.dataverse_entities",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.event_meta_dataverse",
                c => new
                    {
                        event_id = c.Guid(nullable: false),
                        environment_id = c.Int(),
                        entity_id = c.Int(),
                        record_id = c.String(maxLength: 200),
                    })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .ForeignKey("dbo.dataverse_entities", t => t.entity_id)
                .ForeignKey("dbo.power_app_environments", t => t.environment_id)
                .Index(t => t.event_id)
                .Index(t => t.environment_id)
                .Index(t => t.entity_id);
            
            CreateTable(
                "dbo.flow_recurrence_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.power_app_connectors",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        power_app_id = c.Int(nullable: false),
                        connector_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_platform_connectors", t => t.connector_id, cascadeDelete: true)
                .ForeignKey("dbo.power_apps", t => t.power_app_id, cascadeDelete: true)
                .Index(t => new { t.power_app_id, t.connector_id }, unique: true);
            
            CreateTable(
                "dbo.power_platform_connectors",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 255),
                        publisher = c.String(maxLength: 255),
                        is_premium = c.Boolean(),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.power_apps",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        app_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                        environment_id = c.Int(),
                        app_type_id = c.Int(),
                        first_seen_at = c.DateTime(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_app_types", t => t.app_type_id)
                .ForeignKey("dbo.power_app_environments", t => t.environment_id)
                .Index(t => t.app_id, unique: true)
                .Index(t => t.environment_id)
                .Index(t => t.app_type_id);
            
            CreateTable(
                "dbo.power_app_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.event_meta_power_app",
                c => new
                    {
                        event_id = c.Guid(nullable: false),
                        power_app_id = c.Int(),
                        app_session_id = c.String(maxLength: 200),
                        client_type_id = c.Int(),
                    })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .ForeignKey("dbo.power_platform_client_types", t => t.client_type_id)
                .ForeignKey("dbo.power_apps", t => t.power_app_id)
                .Index(t => t.event_id)
                .Index(t => t.power_app_id)
                .Index(t => t.client_type_id);
            
            CreateTable(
                "dbo.power_platform_client_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);
            
            CreateTable(
                "dbo.event_meta_power_app_share",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        event_id = c.Guid(nullable: false),
                        power_app_id = c.Int(),
                        shared_with_user_id = c.Int(),
                        role_name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.audit_events", t => t.event_id, cascadeDelete: true)
                .ForeignKey("dbo.power_apps", t => t.power_app_id)
                .ForeignKey("dbo.users", t => t.shared_with_user_id)
                .Index(t => new { t.event_id, t.shared_with_user_id }, unique: true)
                .Index(t => t.power_app_id);
            
            CreateTable(
                "dbo.power_automate_flow_connectors",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        flow_id = c.Int(nullable: false),
                        connector_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_platform_connectors", t => t.connector_id, cascadeDelete: true)
                .ForeignKey("dbo.power_automate_flows", t => t.flow_id, cascadeDelete: true)
                .Index(t => new { t.flow_id, t.connector_id }, unique: true);
            
            CreateTable(
                "dbo.power_automate_flows",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        flow_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                        environment_id = c.Int(),
                        first_seen_at = c.DateTime(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_app_environments", t => t.environment_id)
                .Index(t => t.flow_id, unique: true)
                .Index(t => t.environment_id);
            
            CreateTable(
                "dbo.event_meta_power_automate_flow",
                c => new
                    {
                        event_id = c.Guid(nullable: false),
                        flow_id = c.Int(),
                        run_id = c.String(maxLength: 200),
                        recurrence_type_id = c.Int(),
                    })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .ForeignKey("dbo.power_automate_flows", t => t.flow_id)
                .ForeignKey("dbo.flow_recurrence_types", t => t.recurrence_type_id)
                .Index(t => t.event_id)
                .Index(t => t.flow_id)
                .Index(t => t.recurrence_type_id);
            
            CreateTable(
                "dbo.event_meta_power_automate_flow_share",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        event_id = c.Guid(nullable: false),
                        flow_id = c.Int(),
                        shared_with_user_id = c.Int(),
                        role_name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.audit_events", t => t.event_id, cascadeDelete: true)
                .ForeignKey("dbo.power_automate_flows", t => t.flow_id)
                .ForeignKey("dbo.users", t => t.shared_with_user_id)
                .Index(t => new { t.event_id, t.shared_with_user_id }, unique: true)
                .Index(t => t.flow_id);
            
            CreateTable(
                "dbo.power_bi_dashboards",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        dashboard_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                        workspace_id = c.Int(),
                        first_seen_at = c.DateTime(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_bi_workspaces", t => t.workspace_id)
                .Index(t => t.dashboard_id, unique: true)
                .Index(t => t.workspace_id);
            
            CreateTable(
                "dbo.power_bi_workspaces",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        workspace_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.workspace_id, unique: true);
            
            CreateTable(
                "dbo.event_meta_power_bi",
                c => new
                    {
                        event_id = c.Guid(nullable: false),
                        workspace_id = c.Int(),
                        report_id = c.Int(),
                        dashboard_id = c.Int(),
                    })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .ForeignKey("dbo.power_bi_dashboards", t => t.dashboard_id)
                .ForeignKey("dbo.power_bi_reports", t => t.report_id)
                .ForeignKey("dbo.power_bi_workspaces", t => t.workspace_id)
                .Index(t => t.event_id)
                .Index(t => t.workspace_id)
                .Index(t => t.report_id)
                .Index(t => t.dashboard_id);
            
            CreateTable(
                "dbo.power_bi_reports",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        report_id = c.String(maxLength: 200),
                        name = c.String(maxLength: 255),
                        report_type = c.String(maxLength: 100),
                        workspace_id = c.Int(),
                        first_seen_at = c.DateTime(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.power_bi_workspaces", t => t.workspace_id)
                .Index(t => t.report_id, unique: true)
                .Index(t => t.workspace_id);
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.event_meta_power_bi", "workspace_id", "dbo.power_bi_workspaces");
            DropForeignKey("dbo.event_meta_power_bi", "report_id", "dbo.power_bi_reports");
            DropForeignKey("dbo.power_bi_reports", "workspace_id", "dbo.power_bi_workspaces");
            DropForeignKey("dbo.event_meta_power_bi", "dashboard_id", "dbo.power_bi_dashboards");
            DropForeignKey("dbo.event_meta_power_bi", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.power_bi_dashboards", "workspace_id", "dbo.power_bi_workspaces");
            DropForeignKey("dbo.event_meta_power_automate_flow_share", "shared_with_user_id", "dbo.users");
            DropForeignKey("dbo.event_meta_power_automate_flow_share", "flow_id", "dbo.power_automate_flows");
            DropForeignKey("dbo.event_meta_power_automate_flow_share", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.event_meta_power_automate_flow", "recurrence_type_id", "dbo.flow_recurrence_types");
            DropForeignKey("dbo.event_meta_power_automate_flow", "flow_id", "dbo.power_automate_flows");
            DropForeignKey("dbo.event_meta_power_automate_flow", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.power_automate_flow_connectors", "flow_id", "dbo.power_automate_flows");
            DropForeignKey("dbo.power_automate_flows", "environment_id", "dbo.power_app_environments");
            DropForeignKey("dbo.power_automate_flow_connectors", "connector_id", "dbo.power_platform_connectors");
            DropForeignKey("dbo.event_meta_power_app_share", "shared_with_user_id", "dbo.users");
            DropForeignKey("dbo.event_meta_power_app_share", "power_app_id", "dbo.power_apps");
            DropForeignKey("dbo.event_meta_power_app_share", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.event_meta_power_app", "power_app_id", "dbo.power_apps");
            DropForeignKey("dbo.event_meta_power_app", "client_type_id", "dbo.power_platform_client_types");
            DropForeignKey("dbo.event_meta_power_app", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.power_app_connectors", "power_app_id", "dbo.power_apps");
            DropForeignKey("dbo.power_apps", "environment_id", "dbo.power_app_environments");
            DropForeignKey("dbo.power_apps", "app_type_id", "dbo.power_app_types");
            DropForeignKey("dbo.power_app_connectors", "connector_id", "dbo.power_platform_connectors");
            DropForeignKey("dbo.event_meta_dataverse", "environment_id", "dbo.power_app_environments");
            DropForeignKey("dbo.event_meta_dataverse", "entity_id", "dbo.dataverse_entities");
            DropForeignKey("dbo.event_meta_dataverse", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.event_meta_copilot_studio", "bot_id", "dbo.copilot_studio_bots");
            DropForeignKey("dbo.event_meta_copilot_studio", "event_id", "dbo.audit_events");
            DropForeignKey("dbo.copilot_studio_bots", "environment_id", "dbo.power_app_environments");
            DropIndex("dbo.power_bi_reports", new[] { "workspace_id" });
            DropIndex("dbo.power_bi_reports", new[] { "report_id" });
            DropIndex("dbo.event_meta_power_bi", new[] { "dashboard_id" });
            DropIndex("dbo.event_meta_power_bi", new[] { "report_id" });
            DropIndex("dbo.event_meta_power_bi", new[] { "workspace_id" });
            DropIndex("dbo.event_meta_power_bi", new[] { "event_id" });
            DropIndex("dbo.power_bi_workspaces", new[] { "workspace_id" });
            DropIndex("dbo.power_bi_dashboards", new[] { "workspace_id" });
            DropIndex("dbo.power_bi_dashboards", new[] { "dashboard_id" });
            DropIndex("dbo.event_meta_power_automate_flow_share", new[] { "flow_id" });
            DropIndex("dbo.event_meta_power_automate_flow_share", new[] { "event_id", "shared_with_user_id" });
            DropIndex("dbo.event_meta_power_automate_flow", new[] { "recurrence_type_id" });
            DropIndex("dbo.event_meta_power_automate_flow", new[] { "flow_id" });
            DropIndex("dbo.event_meta_power_automate_flow", new[] { "event_id" });
            DropIndex("dbo.power_automate_flows", new[] { "environment_id" });
            DropIndex("dbo.power_automate_flows", new[] { "flow_id" });
            DropIndex("dbo.power_automate_flow_connectors", new[] { "flow_id", "connector_id" });
            DropIndex("dbo.event_meta_power_app_share", new[] { "power_app_id" });
            DropIndex("dbo.event_meta_power_app_share", new[] { "event_id", "shared_with_user_id" });
            DropIndex("dbo.power_platform_client_types", new[] { "name" });
            DropIndex("dbo.event_meta_power_app", new[] { "client_type_id" });
            DropIndex("dbo.event_meta_power_app", new[] { "power_app_id" });
            DropIndex("dbo.event_meta_power_app", new[] { "event_id" });
            DropIndex("dbo.power_app_types", new[] { "name" });
            DropIndex("dbo.power_apps", new[] { "app_type_id" });
            DropIndex("dbo.power_apps", new[] { "environment_id" });
            DropIndex("dbo.power_apps", new[] { "app_id" });
            DropIndex("dbo.power_platform_connectors", new[] { "name" });
            DropIndex("dbo.power_app_connectors", new[] { "power_app_id", "connector_id" });
            DropIndex("dbo.flow_recurrence_types", new[] { "name" });
            DropIndex("dbo.event_meta_dataverse", new[] { "entity_id" });
            DropIndex("dbo.event_meta_dataverse", new[] { "environment_id" });
            DropIndex("dbo.event_meta_dataverse", new[] { "event_id" });
            DropIndex("dbo.dataverse_entities", new[] { "name" });
            DropIndex("dbo.event_meta_copilot_studio", new[] { "bot_id" });
            DropIndex("dbo.event_meta_copilot_studio", new[] { "event_id" });
            DropIndex("dbo.power_app_environments", new[] { "environment_id" });
            DropIndex("dbo.copilot_studio_bots", new[] { "environment_id" });
            DropIndex("dbo.copilot_studio_bots", new[] { "bot_id" });
            DropTable("dbo.power_bi_reports");
            DropTable("dbo.event_meta_power_bi");
            DropTable("dbo.power_bi_workspaces");
            DropTable("dbo.power_bi_dashboards");
            DropTable("dbo.event_meta_power_automate_flow_share");
            DropTable("dbo.event_meta_power_automate_flow");
            DropTable("dbo.power_automate_flows");
            DropTable("dbo.power_automate_flow_connectors");
            DropTable("dbo.event_meta_power_app_share");
            DropTable("dbo.power_platform_client_types");
            DropTable("dbo.event_meta_power_app");
            DropTable("dbo.power_app_types");
            DropTable("dbo.power_apps");
            DropTable("dbo.power_platform_connectors");
            DropTable("dbo.power_app_connectors");
            DropTable("dbo.flow_recurrence_types");
            DropTable("dbo.event_meta_dataverse");
            DropTable("dbo.dataverse_entities");
            DropTable("dbo.event_meta_copilot_studio");
            DropTable("dbo.copilot_studio_bots");
            DropTable("dbo.power_app_environments");
        }
    }
}
