
-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-08-04
-- Version 1.0
-- Description:	This script will create a new table for the PBI reports called 'Events_PBI'
-- =============================================	


DROP TABLE  IF EXISTS  [dbo].[Events_PBI]
GO

CREATE TABLE [dbo].[Events_PBI]
(
id uniqueidentifier not null PRIMARY KEY,
user_id int null,
user_name varchar(250) NOT NULL,
url_id int,
full_url nvarchar(max),
operation_id INT, 
operation varchar(250),
item_type_id int,
type_name varchar(250), 
file_extension_id int, 
file_extension varchar(250), 
file_name_id int, 
file_name nvarchar(250), 
time_stamp datetime NOT NULL,
date date not null,
dayName varchar(10) NOT NULL, 
MonthName varchar(10) NOT NULL,
MonthNumber tinyint NOT NULL, 
WeekOfYear tinyint not null,
Quarter varchar(10) NOT NULL,
Year INT NOT NULL,
MonthYear Varchar(14) NOT NULL, 
time varchar(50) NOT NULL,
hour varchar(50) NOT NULL,
PeriodOfDay Varchar(50) NOT NULL,
PeriodSort INT NOT NULL
)

INSERT INTO [dbo].[Events_PBI]
([id],[user_id],[user_name],[url_id],[full_url],[operation_id],[operation], [item_type_id],
[type_name],[file_extension_id],[file_extension],[file_name_id],[file_name],[time_stamp],
[date],[dayName],[MonthName],[monthNumber],[WeekOfYear],[quarter],[Year],[MonthYear],
[time],[hour],[PeriodofDay],[PeriodSort])
SELECT audit_events.id
		,[user_id]
		,[users].[user_name]
		,url_id
		,[urls].full_url
		,operation_id
		,event_operations.operation_name as operation
		,item_type_id
		,event_types.[type_name] 
		,file_extension_id
		,event_file_ext.extension_name as file_extention
		,file_name_id
		,event_file_names.[file_name]
		,time_stamp
		,dimdate.date
		,dimdate.WeekDayName AS dayName
		,dimdate.MonthName
		,dimdate.Month AS monthNumber
		,dimDate.WeekOfYear
		,dimdate.quarter
		,dimdate.Year
		,dimdate.MonthYear
		,dimtime.time
		,dimtime.hour
		,dimtime.[Period of Day]
		,dimtime.PeriodSort
	FROM [dbo].audit_events
	inner join users on 
	audit_events.[user_id] = users.id
	inner join event_meta_sharepoint on
	event_meta_sharepoint.event_id = audit_events.id
	left join urls on 
	event_meta_sharepoint.url_id = urls.id
	left join event_file_ext on -- Not always a file 
	event_meta_sharepoint.file_extension_id = event_file_ext.id
	left join event_file_names on -- Not always a file
	event_meta_sharepoint.file_name_id = event_file_names.id
	left join event_types on 
	event_meta_sharepoint.item_type_id = event_types.id
	inner join event_operations on
		audit_events.operation_id = event_operations.id
	INNER JOIN dimDate AS dimdate
	ON CONVERT(DATE, audit_events.time_stamp, 103) = dimdate.date
	INNER JOIN DimTime as dimtime
	ON FORMAT(audit_events.time_stamp, 'HH:mm') = dimtime.time
	--WHERE (DateAdd(MM, -6, GetDate()) < audit_events.time_stamp)
	WHERE audit_events.time_stamp <= getdate() 
	AND audit_events.time_stamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))