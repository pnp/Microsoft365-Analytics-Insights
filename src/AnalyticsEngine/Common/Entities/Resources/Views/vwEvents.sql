
-- ============================================
-- Author: Glenn Pepper
-- Create Date: 04/12/2019
-- Version: 1.0
-- Description: These view display event information
-- =============================================

IF OBJECT_ID('vwEvents','v') IS NOT NULL
BEGIN
DROP VIEW vwEvents
END
GO

CREATE VIEW [dbo].[vwEvents] AS
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
	GO

IF OBJECT_ID('vwEvents_6months','v') IS NOT NULL
BEGIN
DROP VIEW vwEvents_6months
END
GO

CREATE VIEW [dbo].[vwEvents_6months] AS

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
	GO





