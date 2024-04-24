namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Fixes issue with non-standard file names not fitting into varchar field. 
    /// </summary>
    public partial class v104 : DbMigration
    {
        // Taken from Create DB.sql
        const string INDEX_CREATE =
            @"CREATE UNIQUE NONCLUSTERED INDEX IX_event_filenames 
                ON dbo.event_file_names ( [file_name]
	        ) WITH(STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) 
                ON[PRIMARY]";

        public override void Up()
        {
            Sql(migrateColumnSQL);
            Console.WriteLine("DB SCHEMA: Applied 'file-name patch' succesfully.");
        }

        public override void Down()
        {
            // This doesn't really migrate "down", but the "up" changes from varchar to nvarchar & that's all
            // EF assumes string is nvarchar anyway but initial schema was varchar.
            DropIndex("dbo.event_file_names", "IX_event_filenames");
            AlterColumn("dbo.event_file_names", "file_name", c => c.String());
            Sql(INDEX_CREATE);
        }

        // Convert file_name to nvarchar from original create script type of varchar
        const string migrateColumnSQL = @"
BEGIN TRANSACTION
SET QUOTED_IDENTIFIER ON
SET ARITHABORT ON
SET NUMERIC_ROUNDABORT OFF
SET CONCAT_NULL_YIELDS_NULL ON
SET ANSI_NULLS ON
SET ANSI_PADDING ON
SET ANSI_WARNINGS ON
COMMIT
BEGIN TRANSACTION
GO
CREATE TABLE dbo.Tmp_event_file_names
	(
	id int NOT NULL IDENTITY (1, 1),
	file_name nvarchar(250) NOT NULL
	)  ON [PRIMARY]
GO
ALTER TABLE dbo.Tmp_event_file_names SET (LOCK_ESCALATION = TABLE)
GO
SET IDENTITY_INSERT dbo.Tmp_event_file_names ON
GO
IF EXISTS(SELECT * FROM dbo.event_file_names)
	 EXEC('INSERT INTO dbo.Tmp_event_file_names (id, file_name)
		SELECT id, CONVERT(nvarchar(250), file_name) FROM dbo.event_file_names WITH (HOLDLOCK TABLOCKX)')
GO
SET IDENTITY_INSERT dbo.Tmp_event_file_names OFF
GO
ALTER TABLE dbo.audit_events
	DROP CONSTRAINT FK_events_event_filenames
GO
DROP TABLE dbo.event_file_names
GO
EXECUTE sp_rename N'dbo.Tmp_event_file_names', N'event_file_names', 'OBJECT' 
GO
ALTER TABLE dbo.event_file_names ADD CONSTRAINT
	PK_event_file_names PRIMARY KEY CLUSTERED 
	(
	id
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]

GO
CREATE UNIQUE NONCLUSTERED INDEX IX_event_filenames ON dbo.event_file_names
	(
	file_name
	) WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
COMMIT
BEGIN TRANSACTION
GO
ALTER TABLE dbo.audit_events ADD CONSTRAINT
	FK_events_event_filenames FOREIGN KEY
	(
	file_name_id
	) REFERENCES dbo.event_file_names
	(
	id
	) ON UPDATE  NO ACTION 
	 ON DELETE  NO ACTION 
	
GO
ALTER TABLE dbo.audit_events SET (LOCK_ESCALATION = TABLE)
GO
COMMIT

";
    }
}
