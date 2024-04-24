
-- old table
IF OBJECT_ID (N'hit_imports', N'U') IS NOT NULL 
drop table [hit_imports]


IF OBJECT_ID (N'${STAGING_TABLE_HIT_IMPORTS}', N'U') IS NOT NULL 
drop table [${STAGING_TABLE_HIT_IMPORTS}]

CREATE TABLE [dbo].[${STAGING_TABLE_HIT_IMPORTS}](
	[id] [int] IDENTITY(1,1) NOT NULL primary key,
	[url] [nvarchar](max) NOT NULL,
	[hit_timestamp] [datetime] NOT NULL,
	[title] [nvarchar](250) NOT NULL,
	[user_name] [varchar](250) NOT NULL,
	[web_url] [nvarchar](500) NULL,
	[web_title] [nvarchar](500) NULL,
	[site_url] [nvarchar](500) NULL,
	[ai_session_id] [varchar](50) NOT NULL,
	[os_name] [varchar](200) NULL,
	[device_name] [varchar](200) NULL,
	[browser_name] [varchar](250) NULL,
	[country_name] [nvarchar](250) NULL,
	[city_name] [nvarchar](250) NULL,
	[province_name] [nvarchar](250) NULL,
	[page_request_id] [uniqueidentifier] NOT NULL,
	[sp_request_duration] [int] NULL,
	[page_load_time] [float] NULL
	);

-- Change collation to case-sensitive if needed
ALTER TABLE ${STAGING_TABLE_HIT_IMPORTS} ALTER COLUMN ai_session_id varchar(50) COLLATE SQL_Latin1_General_CP1_CS_AS;


