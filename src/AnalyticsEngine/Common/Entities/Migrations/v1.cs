namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v1 : DbMigration
    {
        public override void Up()
        {
            // Create schema from script
            Console.WriteLine("DB SCHEMA: Upgrading to v1 schema...if this fails, manual intervention will be needed...");
            Sql(Properties.Resources.Create_DB);
        }

        public override void Down()
        {
            DropForeignKey("dbo.searches", "session_id", "dbo.sessions");
            DropForeignKey("dbo.searches", "search_term_id", "dbo.search_terms");
            DropForeignKey("dbo.org_urls", "org_id", "dbo.orgs");
            DropForeignKey("dbo.hits", "url_id", "dbo.urls");
            DropForeignKey("dbo.hits", "session_id", "dbo.sessions");
            DropForeignKey("dbo.sessions", "user_id", "dbo.users");
            DropForeignKey("dbo.hits", "page_title_id", "dbo.page_titles");
            DropForeignKey("dbo.hits", "os_id", "dbo.operating_systems");
            DropForeignKey("dbo.hits", "location_province_id", "dbo.provinces");
            DropForeignKey("dbo.hits", "device_id", "dbo.devices");
            DropForeignKey("dbo.hits", "country_id", "dbo.countries");
            DropForeignKey("dbo.hits", "city_id", "dbo.cities");
            DropForeignKey("dbo.hits", "agent_id", "dbo.browsers");
            DropForeignKey("dbo.audit_events", "workload_id", "dbo.event_workloads");
            DropForeignKey("dbo.audit_events", "user_id", "dbo.users");
            DropForeignKey("dbo.audit_events", "url_id", "dbo.urls");
            DropForeignKey("dbo.audit_events", "operation_id", "dbo.event_operations");
            DropForeignKey("dbo.audit_events", "item_type_id", "dbo.event_types");
            DropForeignKey("dbo.audit_events", "file_name_id", "dbo.event_file_names");
            DropForeignKey("dbo.audit_events", "file_extension_id", "dbo.event_file_ext");
            DropIndex("dbo.searches", new[] { "session_id" });
            DropIndex("dbo.searches", new[] { "search_term_id" });
            DropIndex("dbo.org_urls", new[] { "org_id" });
            DropIndex("dbo.sessions", new[] { "user_id" });
            DropIndex("dbo.hits", new[] { "url_id" });
            DropIndex("dbo.hits", new[] { "session_id" });
            DropIndex("dbo.hits", new[] { "page_title_id" });
            DropIndex("dbo.hits", new[] { "os_id" });
            DropIndex("dbo.hits", new[] { "location_province_id" });
            DropIndex("dbo.hits", new[] { "device_id" });
            DropIndex("dbo.hits", new[] { "country_id" });
            DropIndex("dbo.hits", new[] { "city_id" });
            DropIndex("dbo.hits", new[] { "agent_id" });
            DropIndex("dbo.audit_events", new[] { "workload_id" });
            DropIndex("dbo.audit_events", new[] { "user_id" });
            DropIndex("dbo.audit_events", new[] { "url_id" });
            DropIndex("dbo.audit_events", new[] { "operation_id" });
            DropIndex("dbo.audit_events", new[] { "item_type_id" });
            DropIndex("dbo.audit_events", new[] { "file_name_id" });
            DropIndex("dbo.audit_events", new[] { "file_extension_id" });
            DropTable("dbo.searches");
            DropTable("dbo.search_terms");
            DropTable("dbo.processed_audit_events");
            DropTable("dbo.orgs");
            DropTable("dbo.org_urls");
            DropTable("dbo.import_log");
            DropTable("dbo.sessions");
            DropTable("dbo.page_titles");
            DropTable("dbo.operating_systems");
            DropTable("dbo.provinces");
            DropTable("dbo.hits");
            DropTable("dbo.devices");
            DropTable("dbo.countries");
            DropTable("dbo.cities");
            DropTable("dbo.browsers");
            DropTable("dbo.event_workloads");
            DropTable("dbo.users");
            DropTable("dbo.urls");
            DropTable("dbo.event_operations");
            DropTable("dbo.event_types");
            DropTable("dbo.event_file_names");
            DropTable("dbo.event_file_ext");
            DropTable("dbo.audit_events");
        }
    }
}
