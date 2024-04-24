/****** Object:  View [dbo].[Content]    Script Date: 12/03/2020 15:47:28 ******/
DROP VIEW if exists [dbo].[Content]
GO


CREATE VIEW [dbo].[Content] AS
	SELECT audit_events.id
			,[users].[user_name]
			,[urls].full_url
			,event_operations.operation_name as 'Event'
			,event_types.[type_name] AS 'Event Type'
			,event_file_ext.extension_name as 'File Type'
			,event_file_names.[file_name] as 'File Name'
			,CASE WHEN CHARINDEX('/sites/', webs.url_base) = 0 THEN URL_BASE ELSE SUBSTRING(webs.url_base, CHARINDEX('/sites/', webs.url_base)+7, LEN(webs.url_base)) END as 'Site Name' 
			,time_stamp
			,dimdate.date
			,dimdate.WeekDayName AS 'Day Name'
			,dimdate.MonthName
			,dimdate.Month AS monthNumber
			,dimDate.WeekOfYear
			,dimdate.quarter
			,dimdate.Year
			,dimdate.MonthYear as 'Month-Year'
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
		INNER JOIN webs
		ON webs.id = event_meta_sharepoint.related_web_id
		INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, audit_events.time_stamp, 103) = dimdate.date
		INNER JOIN DimTime as dimtime
		ON FORMAT(audit_events.time_stamp, 'HH:mm') = dimtime.time
GO


