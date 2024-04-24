IF OBJECT_ID('usp_Update_sessions_PBI') IS NULL
  EXEC ('CREATE PROCEDURE usp_Update_sessions_PBI AS RETURN 0;')
GO

ALTER PROCEDURE usp_Update_Sessions_PBI
AS

-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-08-04
-- Version 1.0
-- Description:	This Stored Procedure will update the new sessions_PBI table with most recent results and delete anything older than 6 months
-- =============================================	

BEGIN

SET NOCOUNT ON;




--Declare error variables
DECLARE @error_number AS INT, @error_message AS NVARCHAR(1000), @error_severity AS INT;

IF OBJECT_ID('sessions_PBI_Import_Output','u') IS NOT NULL
DROP TABLE sessions_PBI_Import_Output;

CREATE TABLE sessions_PBI_Import_Output(
[ID] int NOT NULL,
[sessions_timestamp] datetime not null,
Import_Action Varchar(20) NOT NULL,
Date_Import_Run datetime 
CONSTRAINT df_sessions_PBI_importDate DEFAULT (sysdatetime()) NOT NULL
);

BEGIN TRY
BEGIN TRANSACTION;

WITH CTE AS
(SELECT 
	s.id
	,ai_session_id
	-- ENTRANCE
	,(
		select top 1 h.id		-- ID
		from hits h
		where h.session_id = s.id
		order by [hit_timestamp] asc
	) as entrance_hit_id
	,(
		select top 1 page_titles.title	-- Page Title
		from hits h
			inner join page_titles on h.page_title_id = page_titles.id
		where h.session_id = s.id
		order by [hit_timestamp] asc
	) as entrance_hit_title
	,(
		select top 1 h.hit_timestamp	-- Hit timestamp
		from hits h
			inner join urls on h.url_id = urls.id
		where h.session_id = s.id
	    --AND (DateAdd(MM, -6, GetDate()) < h.hit_timestamp)
		AND h.hit_timestamp <= getdate()
		AND h.hit_timestamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))
		order by [hit_timestamp] asc
	) as entrance_hit_time_stamp
	-- EXIT
	,(
		select top 1 h.id		-- ID
		from hits h
		where h.session_id = s.id
		order by [hit_timestamp] desc
	) as exit_hit_id
	,(
		select top 1 page_titles.title		--Title
		from hits h
			inner join page_titles on h.page_title_id = page_titles.id
		where h.session_id = s.id
		order by [hit_timestamp] desc
	) as exit_hit_title
	,(
		select top 1 h.hit_timestamp	--Timestamp
		from hits h
			inner join urls on h.url_id = urls.id
		where h.session_id = s.id
		--AND (DateAdd(MM, -6, GetDate()) < h.hit_timestamp)
		AND h.hit_timestamp <= getdate()
		AND h.hit_timestamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))
		order by [hit_timestamp] desc
	) as exit_hit_time_stamp
from [sessions] s
),

NewRows AS
(SELECT
s.id
,s.ai_session_id
,s.entrance_hit_id
,s.entrance_hit_title
,s.entrance_hit_time_stamp
,s.exit_hit_id
,s.exit_hit_title
,s.exit_hit_time_stamp
,dimdate.date
,dimdate.WeekDayName AS dayName
,dimdate.MonthName
,dimdate.Month AS monthNumber
,dimDate.WeekOfYear
,dimdate.quarter
,dimdate.Year
,dimDate.MonthYear
,dimtime.time
,dimtime.hour
,dimtime.[Period of Day]
,dimTime.[PeriodSort]
FROM CTE s
INNER JOIN dimDate AS dimdate
ON CONVERT(DATE, s.entrance_hit_time_stamp, 103) = dimdate.date
INNER JOIN DimTime as dimtime
ON FORMAT(s.entrance_hit_time_stamp, 'HH:mm') = dimtime.time
)

--merge statement to insert new rows
MERGE INTO Sessions_PBI AS TGT
USING NewRows AS SRC
ON TGT.id = SRC.id
WHEN NOT MATCHED THEN INSERT
(			[id]
           ,[ai_session_id]
           ,[entrance_hit_id]
           ,[entrance_hit_title]
           ,[entrance_hit_time_stamp]
           ,[exit_hit_id]
           ,[exit_hit_title]
           ,[exit_hit_time_stamp]
           ,[date]
           ,[dayName]
           ,[MonthName]
           ,[monthNumber]
           ,[WeekOfYear]
           ,[quarter]
           ,[Year]
           ,[MonthYear]
           ,[time]
           ,[hour]
           ,[PeriodofDay]
           ,[PeriodSort]
)
VALUES
([src].[id], [src].[ai_session_id], [src].[entrance_hit_id],[src].[entrance_hit_title],[src].[entrance_hit_time_stamp],
[src].[exit_hit_id],[src].[exit_hit_title], [src].[exit_hit_time_stamp], [src].[date],
[src].[dayName],[src].[MonthName], [src].[monthNumber],[src].[WeekOfYear], [src].[quarter], [src].[Year],
[src].[MonthYear], [src].[time],[src].[hour], [src].[Period of Day], [src].[PeriodSort])
OUTPUT 
COALESCE (inserted.id, deleted.id) AS sessions_id,
COALESCE(inserted.entrance_hit_time_stamp, deleted.entrance_hit_time_stamp) AS sessions_timestamp,
$action AS the_action,
sysdatetime()
INTO sessions_Pbi_Import_Output
 ;

--Delete rows older than 6 months
DELETE FROM sessions_pbi
OUTPUT 
deleted.id,
deleted.entrance_hit_time_stamp AS sessions_timestamp,
'deleted' AS the_action,
sysdatetime()
INTO sessions_Pbi_Import_Output
WHERE entrance_hit_time_stamp < DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0))


COMMIT TRANSACTION;
END TRY

BEGIN CATCH
IF @@TRANCOUNT > 0
ROLLBACK TRANSACTION;
SELECT @error_number = ERROR_NUMBER(), @error_message = ERROR_MESSAGE(),
@error_severity = ERROR_SEVERITY();
raiserror ('usp_Update_sessions_PBI: %d: %s', 16, 1, @error_number, @error_message) ;
END CATCH

END


--EXECUTE usp_update_sessions_pbi