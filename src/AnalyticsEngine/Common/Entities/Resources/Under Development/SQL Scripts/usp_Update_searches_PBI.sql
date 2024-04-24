IF OBJECT_ID('usp_Update_searches_PBI') IS NULL
  EXEC ('CREATE PROCEDURE usp_Update_searches_PBI AS RETURN 0;')
GO

ALTER PROCEDURE usp_Update_searches_PBI
AS

-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-08-04
-- Version 1.0
-- Description:	This Stored Procedure will update the new searches_PBI table with most recent results and delete anything older than 6 months
-- =============================================	

BEGIN

SET NOCOUNT ON;




--Declare error variables
DECLARE @error_number AS INT, @error_message AS NVARCHAR(1000), @error_severity AS INT;

IF OBJECT_ID('Searches_PBI_Import_Output','u') IS NOT NULL
DROP TABLE searches_PBI_Import_Output;

CREATE TABLE searches_PBI_Import_Output(
[ID] int NOT NULL,
[searches_timestamp] datetime not null,
Import_Action Varchar(20) NOT NULL,
Date_Import_Run datetime 
CONSTRAINT df_searches_PBI_importDate DEFAULT (sysdatetime()) NOT NULL
);

--we might need to put an extra step to delete duplicates but so far this has not been required.

--Now update table with new rows and delete rows older than 6 months

BEGIN TRY
BEGIN TRANSACTION;

WITH NewRows AS (
SELECT searches.id, searches.session_id, search_terms.search_term, users.id as 'User_ID'
,hits.hit_timestamp as time_stamp
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
FROM searches
LEFT JOIN search_terms
ON searches.id = search_terms.id
LEFT JOIN sessions 
ON searches.session_id = sessions.id
LEFT JOIN users 
ON sessions.user_id =users.id
LEFT JOIN hits 
on searches.id = hits.id
INNER JOIN dimDate AS dimdate
ON CONVERT(DATE, hits.hit_timestamp, 103) = dimdate.date
INNER JOIN DimTime as dimtime
ON FORMAT(hits.hit_timestamp, 'HH:mm') = dimtime.time
WHERE hits.hit_timestamp <= getdate() 
AND hits.hit_timestamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))
)

--merge statement to insert new rows
MERGE INTO searches_PBI AS TGT
USING NewRows AS SRC
ON TGT.id = SRC.id
WHEN NOT MATCHED THEN INSERT
([id], [session_id], [search_term], [user_id], [time_stamp],[date],[dayName],[MonthName],[monthNumber],[WeekOfYear],[quarter],[Year],[MonthYear],
[time],[hour],[PeriodofDay],[PeriodSort])
VALUES 
([src].[id],[src].[session_id],[src].[search_term],[src].[user_id],[src].[time_stamp],
[src].[date], [src].[dayName], [src].[MonthName], [src].[MonthNumber], [src].[WeekOfYear],[src].[Quarter],
[src].[Year], [src].[MonthYear], [src].[time], [src].[hour], [src].[Period Of Day], [src].[PeriodSort])
OUTPUT 
COALESCE (inserted.id, deleted.id) AS searches_id,
COALESCE(inserted.time_stamp, deleted.time_stamp) AS timestamp,
$action AS the_action,
sysdatetime()
INTO searches_Pbi_Import_Output
 ;

--Delete rows older than 6 months from start of month and insert these into output table
DELETE FROM searches_pbi
OUTPUT 
deleted.id,
deleted.time_stamp AS timestamp,
'deleted' AS the_action,
sysdatetime()
INTO searches_Pbi_Import_Output
WHERE time_stamp < DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0))


COMMIT TRANSACTION;
END TRY

BEGIN CATCH
IF @@TRANCOUNT > 0
ROLLBACK TRANSACTION;
SELECT @error_number = ERROR_NUMBER(), @error_message = ERROR_MESSAGE(),
@error_severity = ERROR_SEVERITY();
raiserror ('usp_Update_searches_PBI: %d: %s', 16, 1, @error_number, @error_message) ;
END CATCH

END
