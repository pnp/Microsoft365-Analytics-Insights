namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v109 : DbMigration
    {
        public override void Up()
        {

            CreateTable(
                "dbo.call_failures",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    reason = c.String(),
                    stage = c.String(),
                    call_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.call_records", t => t.call_id, cascadeDelete: false)
                .Index(t => t.call_id);

            CreateTable(
                "dbo.call_records",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    organizer_id = c.Int(nullable: false),
                    call_type_id = c.Int(nullable: false),
                    graph_id = c.String(maxLength: 100),
                    start = c.DateTime(nullable: false),
                    end = c.DateTime(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.call_types", t => t.call_type_id, cascadeDelete: false)
                .ForeignKey("dbo.users", t => t.organizer_id, cascadeDelete: false)
                .Index(t => t.organizer_id)
                .Index(t => t.call_type_id)
                .Index(t => t.graph_id, unique: true);

            CreateTable(
                "dbo.call_types",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.call_sessions",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    attendee_user_id = c.Int(nullable: false),
                    start = c.DateTime(nullable: false),
                    end = c.DateTime(nullable: false),
                    call_record_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.attendee_user_id, cascadeDelete: false)
                .ForeignKey("dbo.call_records", t => t.call_record_id, cascadeDelete: false)
                .Index(t => t.attendee_user_id)
                .Index(t => t.call_record_id);

            CreateTable(
                "dbo.call_session_call_modalities",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    call_modality_id = c.Int(nullable: false),
                    call_session_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.call_modalities", t => t.call_modality_id, cascadeDelete: false)
                .ForeignKey("dbo.call_sessions", t => t.call_session_id, cascadeDelete: false)
                .Index(t => new { t.call_modality_id, t.call_session_id }, unique: true);

            CreateTable(
                "dbo.call_modalities",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            CreateTable(
                "dbo.call_feedback",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    rating = c.String(),
                    text = c.String(),
                    user_id = c.Int(nullable: false),
                    call_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.call_records", t => t.call_id, cascadeDelete: false)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: false)
                .Index(t => t.user_id)
                .Index(t => t.call_id);

            CreateTable(
                "dbo.teams_channel_tabs_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    tab_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    channel_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_channels", t => t.channel_id, cascadeDelete: false)
                .ForeignKey("dbo.teams_tabs", t => t.tab_id, cascadeDelete: false)
                .Index(t => t.tab_id)
                .Index(t => t.channel_id);

            CreateTable(
                "dbo.teams_channels",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    graph_id = c.String(nullable: false, maxLength: 100),
                    name = c.String(maxLength: 100),
                    team_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams", t => t.team_id, cascadeDelete: false)
                .Index(t => t.graph_id, unique: true)
                .Index(t => t.team_id);

            CreateTable(
                "dbo.teams_channel_stats_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    chats_count = c.Int(),
                    sentiment_score = c.Double(),
                    date = c.DateTime(nullable: false),
                    channel_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_channels", t => t.channel_id, cascadeDelete: false)
                .Index(t => t.channel_id);

            CreateTable(
                "dbo.teams_channel_stats_log_keywords",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    keyword_id = c.Int(nullable: false),
                    channel_stats_log_id = c.Int(nullable: false),
                    keyword_count = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_channel_stats_log", t => t.channel_stats_log_id, cascadeDelete: false)
                .ForeignKey("dbo.keywords", t => t.keyword_id, cascadeDelete: false)
                .Index(t => t.keyword_id)
                .Index(t => t.channel_stats_log_id);

            CreateTable(
                "dbo.keywords",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.teams_channel_stats_log_langs",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    language_id = c.Int(nullable: false),
                    channel_stats_log_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_channel_stats_log", t => t.channel_stats_log_id, cascadeDelete: false)
                .ForeignKey("dbo.languages", t => t.language_id, cascadeDelete: false)
                .Index(t => t.language_id)
                .Index(t => t.channel_stats_log_id);

            CreateTable(
                "dbo.languages",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.teams",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    discovered = c.DateTime(nullable: false),
                    has_refresh_token = c.Boolean(nullable: false),
                    last_refresh = c.DateTime(),
                    graph_id = c.String(nullable: false, maxLength: 100),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.graph_id, unique: true);

            CreateTable(
                "dbo.teams_addons_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    addon_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    team_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_addons", t => t.addon_id, cascadeDelete: false)
                .ForeignKey("dbo.teams", t => t.team_id, cascadeDelete: false)
                .Index(t => t.addon_id)
                .Index(t => t.team_id);

            CreateTable(
                "dbo.teams_addons",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    addon_type = c.Int(nullable: false),
                    published_state = c.String(maxLength: 50),
                    graph_id = c.String(nullable: false, maxLength: 100),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.graph_id, unique: true);

            CreateTable(
                "dbo.team_owners",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    owner_id = c.Int(nullable: false),
                    discovered = c.DateTime(nullable: false),
                    team_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.owner_id, cascadeDelete: false)
                .ForeignKey("dbo.teams", t => t.team_id, cascadeDelete: false)
                .Index(t => t.owner_id)
                .Index(t => t.team_id);

            CreateTable(
                "dbo.teams_tabs",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    url = c.String(),
                    teams_addon_id = c.Int(),
                    graph_id = c.String(nullable: false, maxLength: 100),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_addons", t => t.teams_addon_id)
                .Index(t => t.teams_addon_id)
                .Index(t => t.graph_id, unique: true);


            CreateTable(
                "dbo.sys_configs",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    config_json = c.String(),
                    date_applied = c.DateTime(nullable: false),
                    installed_by_username = c.String(),
                    messages = c.String(),
                })
                .PrimaryKey(t => t.id);


            CreateTable(
                "dbo.team_membership_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    team_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams", t => t.team_id, cascadeDelete: false)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: false)
                .Index(t => t.user_id)
                .Index(t => t.team_id);

            CreateTable(
                "dbo.teams_user_device_usage_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    used_web = c.Boolean(),
                    used_win_phone = c.Boolean(),
                    used_ios = c.Boolean(),
                    used_android = c.Boolean(),
                    used_mac = c.Boolean(),
                    used_windows = c.Boolean(),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: false)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.teams_user_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    private_chat_count = c.Long(),
                    team_chat_count = c.Long(),
                    calls_count = c.Long(),
                    meetings_count = c.Long(),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: false)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.sys_telemetry_reports",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    submitted = c.DateTime(nullable: false),
                    report = c.String(),
                })
                .PrimaryKey(t => t.id);

            Sql(Properties.Resources.TeamsViewsV1);

            Console.WriteLine("DB SCHEMA: Applied 'Teams v1' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
