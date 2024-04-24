namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v1010 : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.o365_client_applications",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    client_application_id = c.Guid(nullable: false),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.client_application_id, unique: true);

            CreateTable(
                "dbo.event_meta_stream",
                c => new
                {
                    event_id = c.Guid(nullable: false),
                    o365_client_application_id = c.Int(nullable: false),
                    video_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.o365_client_applications", t => t.o365_client_application_id, cascadeDelete: false)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .ForeignKey("dbo.stream_videos", t => t.video_id, cascadeDelete: false)
                .Index(t => t.event_id)
                .Index(t => t.o365_client_application_id)
                .Index(t => t.video_id);

            CreateTable(
                "dbo.stream_videos",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    stream_id = c.Guid(nullable: false),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.stream_id, unique: true);

            CreateTable(
                "dbo.yammer_messages",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    sender_id = c.Int(nullable: false),
                    created = c.DateTime(nullable: false),
                    yammer_msg_id = c.Long(nullable: false),
                    reply_to_yammer_msg_id = c.Long(),
                    likes_count = c.Int(nullable: false),
                    followers_count = c.Int(nullable: false),
                    parent_msg_id = c.Int(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.yammer_messages", t => t.parent_msg_id)
                .ForeignKey("dbo.users", t => t.sender_id, cascadeDelete: false)
                .Index(t => t.sender_id)
                .Index(t => t.yammer_msg_id, unique: true)
                .Index(t => t.parent_msg_id);

            CreateTable(
                "dbo.yammer_msg_to_stream",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    message_id = c.Int(nullable: false),
                    stream_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.yammer_messages", t => t.message_id, cascadeDelete: false)
                .ForeignKey("dbo.stream_videos", t => t.stream_id, cascadeDelete: false)
                .Index(t => t.message_id)
                .Index(t => t.stream_id);


            // Insert hard-coded client-app IDs
            // https://techcommunity.microsoft.com/t5/microsoft-stream-blog/global-admin-pro-tip-learn-how-to-build-video-analytics/ba-p/365267
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('cf53fce8-def6-4aeb-8d30-b158e7b1cf83', 'Stream Portal')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('844cca35-0656-46ce-b636-13f48b0eecbd', 'Stream Mobile')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('574df941-661b-4bfc-acb0-0a07de7de341', 'Dynamics Talent')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('00000005-0000-0ff1-ce00-000000000000', 'Yammer')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('4580fd1d-e5a3-4f56-9ad1-aab0e3bf8f76', 'Microsoft Teams Exporter')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('cc15fd57-2c6c-4117-a88c-83b1d56b4bbe', 'Microsoft Teams Middle tier')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('1fec8e78-bce4-4aaf-ab1b-5451cc387264', 'Microsoft Teams Native Client')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('5e3ce6c0-2b1f-4285-8d4b-75ee78787346', 'Microsoft Teams Web Client')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('7557eb47-c689-4224-abcf-aef9bd7573df', 'Microsoft Teams Scheduling and Broadcast Service')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('e478694e-c391-4a61-a6be-c953d2372058', 'OfficeMix.com')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('d3590ed6-52b3-4102-aeff-aad2292ab01c', 'Office First Party native app')");
            Sql("INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('2acd2ef7-b80e-46d2-87f8-90e10adc3fca', 'Microsoft Learning 3rd party app')");
            Sql($"INSERT INTO [o365_client_applications] ([client_application_id] ,[name]) VALUES ('{O365ClientApplication.UNKNOWN_CLIENT_APP_ID}', 'Unknown')");


            // Stream events view
            Sql(@"
                CREATE VIEW [dbo].[events_view_stream] AS

	                SELECT audit_events.id
			                ,[user_id]
			                ,[users].[user_name]
			                ,event_operations.operation_name as operation
			                ,stream_videos.[name] 
							,o365_client_applications.[name] as o365_client_application_name
			                ,time_stamp
		                FROM [dbo].audit_events
		                inner join users on 
			                audit_events.[user_id] = users.id
		                inner join event_meta_stream on
			                event_meta_stream.event_id = audit_events.id
		                inner join event_operations on
			                audit_events.operation_id = event_operations.id
		                inner join stream_videos on
			                event_meta_stream.video_id = stream_videos.id
						inner join o365_client_applications on
							event_meta_stream.o365_client_application_id = o365_client_applications.id

                GO
");

            Console.WriteLine("DB SCHEMA: Applied 'Yammer & Stream v1' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
