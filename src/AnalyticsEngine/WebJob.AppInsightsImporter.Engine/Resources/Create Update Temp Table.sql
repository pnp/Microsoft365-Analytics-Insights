
IF OBJECT_ID (N'${STAGING_TABLE_UPDATES}', N'U') IS NOT NULL 
drop table [${STAGING_TABLE_UPDATES}]

CREATE TABLE [dbo].[${STAGING_TABLE_UPDATES}](
	[id] [int] IDENTITY(1,1) NOT NULL primary key,
	[page_request_id] [uniqueidentifier] NOT NULL,
	[seconds_on_page] [float] NULL
	);
