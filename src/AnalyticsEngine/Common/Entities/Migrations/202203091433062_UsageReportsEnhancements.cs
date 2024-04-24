namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class UsageReportsEnhancements : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.user_departments",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.user_job_titles",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.user_license_type_lookups",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    user_id = c.Int(nullable: false),
                    license_type_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.license_types", t => t.license_type_id, cascadeDelete: true)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id)
                .Index(t => t.license_type_id);

            CreateTable(
                "dbo.license_types",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    sku_id = c.String(),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.user_office_locations",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.user_usage_locations",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.onedrive_usage_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    storage_used_bytes = c.Long(nullable: false),
                    active_file_count = c.Long(nullable: false),
                    file_count = c.Long(nullable: false),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.onedrive_user_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    viewed_or_edited = c.Long(nullable: false),
                    synced = c.Long(nullable: false),
                    shared_internally = c.Long(nullable: false),
                    shared_externally = c.Long(nullable: false),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.outlook_user_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    email_send_count = c.Long(nullable: false),
                    email_receive_count = c.Long(nullable: false),
                    email_read_count = c.Long(nullable: false),
                    meeting_created_count = c.Long(nullable: false),
                    meeting_interacted_count = c.Long(nullable: false),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.sharepoint_user_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    viewed_or_edited = c.Long(nullable: false),
                    synced = c.Long(nullable: false),
                    shared_internally = c.Long(nullable: false),
                    shared_externally = c.Long(nullable: false),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.yammer_device_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    used_web = c.Boolean(),
                    used_win_phone = c.Boolean(),
                    used_android = c.Boolean(),
                    used_ipad = c.Boolean(),
                    used_iphone = c.Boolean(),
                    used_others = c.Boolean(),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.yammer_group_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    posted_count = c.Int(nullable: false),
                    read_count = c.Int(nullable: false),
                    liked_count = c.Int(nullable: false),
                    member_count = c.Int(nullable: false),
                    yammer_group_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.yammer_groups", t => t.yammer_group_id, cascadeDelete: true)
                .Index(t => t.yammer_group_id);

            CreateTable(
                "dbo.yammer_groups",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.yammer_user_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    posted_count = c.Int(nullable: false),
                    read_count = c.Int(nullable: false),
                    liked_count = c.Int(nullable: false),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            AddColumn("dbo.users", "last_updated", c => c.DateTime());
            AddColumn("dbo.users", "office_location_id", c => c.Int());
            AddColumn("dbo.users", "usage_location_id", c => c.Int());
            AddColumn("dbo.users", "department_id", c => c.Int());
            AddColumn("dbo.users", "job_title_id", c => c.Int());
            AddColumn("dbo.users", "azure_ad_id", c => c.String());
            AddColumn("dbo.users", "account_enabled", c => c.Boolean());
            AddColumn("dbo.teams_user_device_usage_log", "last_activity_date", c => c.DateTime());
            AddColumn("dbo.teams_user_activity_log", "adhoc_meetings_attended_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "adhoc_meetings_organized_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "meetings_attended_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "meetings_organized_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "scheduled_onetime_meetings_attended_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "scheduled_onetime_meetings_organized_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "scheduled_recurring_meetings_attended_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "scheduled_recurring_meetings_organized_count", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "audio_duration_seconds", c => c.Int(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "video_duration_seconds", c => c.Int(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "screenshare_duration_seconds", c => c.Int(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "last_activity_date", c => c.DateTime());
            AlterColumn("dbo.teams_user_activity_log", "private_chat_count", c => c.Long(nullable: false));
            AlterColumn("dbo.teams_user_activity_log", "team_chat_count", c => c.Long(nullable: false));
            AlterColumn("dbo.teams_user_activity_log", "calls_count", c => c.Long(nullable: false));
            AlterColumn("dbo.teams_user_activity_log", "meetings_count", c => c.Long(nullable: false));
            CreateIndex("dbo.users", "office_location_id");
            CreateIndex("dbo.users", "usage_location_id");
            CreateIndex("dbo.users", "department_id");
            CreateIndex("dbo.users", "job_title_id");
            AddForeignKey("dbo.users", "department_id", "dbo.user_departments", "id");
            AddForeignKey("dbo.users", "job_title_id", "dbo.user_job_titles", "id");
            AddForeignKey("dbo.users", "office_location_id", "dbo.user_office_locations", "id");
            AddForeignKey("dbo.users", "usage_location_id", "dbo.user_usage_locations", "id");
            Console.WriteLine("DB SCHEMA: Applied 'Usage reports enhancements' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
