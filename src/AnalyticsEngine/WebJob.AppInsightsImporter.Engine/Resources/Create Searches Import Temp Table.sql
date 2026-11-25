CREATE TABLE [${STAGING_TABLE_SEARCHES}](
	[id] [int] IDENTITY(1,1) NOT NULL primary key,
	[ai_session_id] [varchar](50) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
	[user_name] [nvarchar](250) COLLATE DATABASE_DEFAULT NOT NULL,
	[search_term] [nvarchar](250) COLLATE DATABASE_DEFAULT NOT NULL,
	[date_time] datetime NULL,
	[ingestion_hour] datetime NULL
	);

