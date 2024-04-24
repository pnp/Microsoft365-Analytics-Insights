
IF OBJECT_ID (N'${STAGING_TABLE_SEARCHES}', N'U') IS NOT NULL 
drop table [${STAGING_TABLE_SEARCHES}]

CREATE TABLE [dbo].[${STAGING_TABLE_SEARCHES}](
	[id] [int] IDENTITY(1,1) NOT NULL primary key,
	[ai_session_id] [varchar](50) NOT NULL,
	[user_name] [varchar](250) NOT NULL,
	[search_term] [nvarchar](250) NOT NULL,
	[date_time] datetime NULL
	);

-- Change collation to case-sensitive if needed
ALTER TABLE [${STAGING_TABLE_SEARCHES}] ALTER COLUMN ai_session_id varchar(50) COLLATE SQL_Latin1_General_CP1_CS_AS;

