namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// The unified Power Platform admin activity feed (Workload="PowerPlatform", RecordType=256)
    /// does not carry an AppType property on LaunchPowerApp events nor a RecurrenceType property
    /// on FlowRunStarted events, so the power_app_types and flow_recurrence_types lookup tables
    /// can never be populated from the current import path. Remove them along with their FK columns.
    /// </summary>
    public partial class RemovePowerAppTypesAndFlowRecurrenceTypes : DbMigration
    {
        public override void Up()
        {
            DropForeignKey("dbo.event_meta_power_automate_flow", "recurrence_type_id", "dbo.flow_recurrence_types");
            DropForeignKey("dbo.power_apps", "app_type_id", "dbo.power_app_types");
            DropIndex("dbo.event_meta_power_automate_flow", new[] { "recurrence_type_id" });
            DropIndex("dbo.power_apps", new[] { "app_type_id" });
            DropIndex("dbo.power_app_types", new[] { "name" });
            DropIndex("dbo.flow_recurrence_types", new[] { "name" });
            DropColumn("dbo.event_meta_power_automate_flow", "recurrence_type_id");
            DropColumn("dbo.power_apps", "app_type_id");
            DropTable("dbo.power_app_types");
            DropTable("dbo.flow_recurrence_types");

            Console.WriteLine("DB SCHEMA: Dropped power_app_types and flow_recurrence_types lookup tables succesfully.");
        }

        public override void Down()
        {
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
                "dbo.power_app_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            AddColumn("dbo.power_apps", "app_type_id", c => c.Int());
            AddColumn("dbo.event_meta_power_automate_flow", "recurrence_type_id", c => c.Int());

            CreateIndex("dbo.power_apps", "app_type_id");
            CreateIndex("dbo.event_meta_power_automate_flow", "recurrence_type_id");

            AddForeignKey("dbo.power_apps", "app_type_id", "dbo.power_app_types", "id");
            AddForeignKey("dbo.event_meta_power_automate_flow", "recurrence_type_id", "dbo.flow_recurrence_types", "id");
        }
    }
}
