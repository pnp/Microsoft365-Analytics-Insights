IF OBJECT_ID('usp_Update_Events_PBI') IS NULL
  EXEC ('CREATE PROCEDURE usp_Update_Events_PBI AS RETURN 0;')
GO

ALTER PROCEDURE usp_Update_Events_PBI
AS

-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-08-04
-- Version 1.0
-- Description:	This Stored Procedure will update the new Events_PBI table with most recent results and delete anything older than 6 months
-- =============================================	

BEGIN

SET NOCOUNT ON;




--Declare error variables
DECLARE @error_number AS INT, @error_message AS NVARCHAR(1000), @error_severity AS INT;

IF OBJECT_ID('Events_PBI_Import_Output','u') IS NOT NULL
DROP TABLE Events_PBI_Import_Output;

CREATE TABLE Events_PBI_Import_Output(
[ID] uniqueIdentifier NOT NULL,
[events_timestamp] datetime not null,
Import_Action Varchar(20) NOT NULL,
Date_Import_Run datetime 
CONSTRAINT df_events_PBI_importDate DEFAULT (sysdatetime()) NOT NULL
);

--we might need to put an extra step to delete duplicates but so far this has not been required.

--Now update table with new rows and delete rows older than 6 months

BEGIN TRY
BEGIN TRANSACTION;

WITH NewRows AS (
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
		,event_file_ext.extension_name as file_extension
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
)

--merge statement to insert new rows
MERGE INTO Events_PBI AS TGT
USING NewRows AS SRC
ON TGT.id = SRC.id
WHEN NOT MATCHED THEN INSERT
([id],[user_id],[user_name],[url_id],[full_url],[operation_id],[operation], [item_type_id],
[type_name],[file_extension_id],[file_extension],[file_name_id],[file_name],[time_stamp],
[date],[dayName],[MonthName],[monthNumber],[WeekOfYear],[quarter],[Year],[MonthYear],
[time],[hour],[PeriodofDay],[PeriodSort])
VALUES 
([src].[id],[src].[user_id],[src].[user_name],[src].[url_id],[src].[full_url],[src].[operation_id], [src].[operation],
[src].[item_type_id],[src].[type_name], [src].[file_extension_id],[src].[file_extension],[src].[file_name_id],[src].[file_name],
[src].[time_stamp], [src].[date], [src].[dayName], [src].[MonthName], [src].[MonthNumber], [src].[WeekOfYear],[src].[Quarter],
[src].[Year], [src].[MonthYear], [src].[time], [src].[hour], [src].[Period Of Day], [src].[PeriodSort])
OUTPUT 
COALESCE (inserted.id, deleted.id) AS events_id,
COALESCE(inserted.time_stamp, deleted.time_stamp) AS events_timestamp,
$action AS the_action,
sysdatetime()
INTO events_Pbi_Import_Output
 ;
--Delete rows older than 6 months from start of month and insert these into output table

DELETE FROM events_pbi
OUTPUT 
deleted.id,
deleted.time_stamp AS events_timestamp,
'deleted' AS the_action,
sysdatetime()
INTO events_Pbi_Import_Output
WHERE time_stamp < DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0))


COMMIT TRANSACTION;
END TRY

BEGIN CATCH
IF @@TRANCOUNT > 0
ROLLBACK TRANSACTION;
SELECT @error_number = ERROR_NUMBER(), @error_message = ERROR_MESSAGE(),
@error_severity = ERROR_SEVERITY();
raiserror ('usp_Update_events_PBI: %d: %s', 16, 1, @error_number, @error_message) ;
END CATCH

END


--EXECUTE usp_Update_events_PBI