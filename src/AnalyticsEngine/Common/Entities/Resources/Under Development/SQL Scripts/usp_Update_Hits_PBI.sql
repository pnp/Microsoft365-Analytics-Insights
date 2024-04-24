IF OBJECT_ID('usp_Update_Hits_PBI') IS NULL
  EXEC ('CREATE PROCEDURE usp_Update_Hits_PBI AS RETURN 0;')
GO

ALTER PROCEDURE usp_Update_Hits_PBI
AS

-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-07-28
-- Version 1.0
-- Description:	This Stored Procedure will update the new HITS_PBI table with most recent results and delete anything older than 6 months
-- =============================================	

BEGIN

SET NOCOUNT ON;




--Declare error variables
DECLARE @error_number AS INT, @error_message AS NVARCHAR(1000), @error_severity AS INT;

IF OBJECT_ID('Hits_PBI_Import_Output','u') IS NOT NULL
DROP TABLE Hits_PBI_Import_Output;

CREATE TABLE Hits_PBI_Import_Output(
[hits_ID] INT NOT NULL,
[hit_timestapmp] datetime not null,
Import_Action Varchar(20) NOT NULL,
Date_Import_Run datetime 
CONSTRAINT df_Hits_PBI_importDate DEFAULT (sysdatetime()) NOT NULL
);

--we might need to put an extra step to delete duplicates but so far this has not been required.

--Now update table with new rows and delete rows older than 6 months

BEGIN TRY
BEGIN TRANSACTION;

DROP INDEX IF EXISTS [NIX_Hits_PBI_Date] ON [dbo].[hits_pbi];

WITH NewRows AS (
SELECT distinct hits.ID, hits.session_id, hits.hit_timestamp AS 'Hit_Timestamp', 
hits.seconds_on_page AS 'Seconds_on_Page',
hits.page_load_time AS 'Load_Time', 
CASE WHEN CHARINDEX('/sites/', url_base) = 0 
THEN URL_BASE ELSE SUBSTRING(url_base, CHARINDEX('/sites/', url_base)+7, LEN(url_base)) END as 'Site_Name',
urls.full_url AS 'URL', browsers.browser_name AS 'Browser',  
provinces.province_name AS 'Region', page_titles.title AS 'PageTitle', sessions.user_id,
CASE WHEN devices.device_name = 'Laptop' THEN 'Laptop' WHEN devices.device_name = 'Other' THEN 'Workstation' 
ELSE 'Not Known' END AS 'Device_Name',
operating_systems.os_name AS 'OS_Name', cities.city_name AS 'City',
Countries.country_name AS 'Country',
dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear, dimtime.time, dimtime.hour, 
dimtime.[Period of Day], DimTime.PeriodSort
FROM hits
LEFT JOIN urls
ON hits.url_id = urls.id
LEFT JOIN browsers
ON hits.agent_id = browsers.id
LEFT JOIN provinces
ON hits.location_province_id = provinces.id
LEFT JOIN
sessions
ON hits.session_id = sessions.id
LEFT JOIN page_titles
ON hits.page_title_id = page_titles.id
LEFT JOIN devices
ON hits.device_id = devices.id
LEFT JOIN operating_systems
ON hits.os_id = operating_systems.id
LEFT JOIN cities
ON hits.city_id = cities.id
LEFT JOIN countries
ON hits.country_id = countries.id
LEFT JOIN webs
ON hits.web_id = webs.id
INNER JOIN dimDate AS dimdate
ON CONVERT(DATE, hits.hit_timestamp, 103) = dimdate.date
INNER JOIN DimTime as dimtime
ON FORMAT(hit_timestamp, 'HH:mm') = dimtime.time
WHERE hits.hit_timestamp <= getdate() 
AND hits.hit_timestamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))
AND hits.id NOT IN 
(SELECT hits_id from hits_pbi))

--merge statement to insert new rows
MERGE INTO Hits_PBI AS TGT
USING NewRows AS SRC
ON TGT.hits_id = SRC.id
WHEN NOT MATCHED THEN INSERT
([hits_id],[session_id],[hit_timestamp],[seconds_on_page],[page_load_time],
[site_name], [URL], [browser], [region],[page_title],[user_id],[Device_Name],
[os_name],[city],[country],[date], [dayname], [monthName],[monthNumber],[weekofyear],[quarter],[year],
[MonthYear],[time],[hour],[periodofDay],[PeriodSort])
VALUES ([src].[id],[src].[session_id],[src].[hit_timestamp],[src].[seconds_on_page],[src].[load_time],
[src].[site_name], [src].[URL], [src].[browser], [src].[region],[src].[pagetitle],[src].[user_id],[src].[Device_Name],
[src].[os_name],[src].[city],[src].[country],[src].[date], [src].[dayname],[src].[monthName],[src].[monthNumber],[src].[weekofyear],
[src].[quarter],[src].[year],
[src].[MonthYear],[src].[time],[src].[hour],[src].[period of Day],[src].[PeriodSort])
OUTPUT 
COALESCE (inserted.hits_id, deleted.hits_id) AS hits_id,
COALESCE(inserted.hit_timestamp, deleted.hit_timestamp) AS hit_timestamp,
$action AS the_action,
sysdatetime()
INTO hits_Pbi_Import_Output;
 
--Delete rows older than 6 months from start of month and insert these into output table

DELETE FROM hits_pbi
OUTPUT 
deleted.hits_id,
deleted.hit_timestamp AS hit_timestamp,
'deleted' AS the_action,
sysdatetime()
INTO hits_Pbi_Import_Output
WHERE hit_timestamp < DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0))


CREATE NONCLUSTERED INDEX [NIX_Hits_PBI_Date]
ON [dbo].[Hits_PBI] ([Date])
INCLUDE ([DayName],[WeekOfYear],[MonthName],[Quarter],[Year],[MonthYear])


COMMIT TRANSACTION;
END TRY

BEGIN CATCH
IF @@TRANCOUNT > 0
ROLLBACK TRANSACTION;
SELECT @error_number = ERROR_NUMBER(), @error_message = ERROR_MESSAGE(),
@error_severity = ERROR_SEVERITY();
raiserror ('usp_Update_Hits_PBI: %d: %s', 16, 1, @error_number, @error_message) ;
END CATCH

END


EXECUTE usp_Update_Hits_PBI

SELECT * FROM Hits_PBI
SELECT * FROM Hits_PBI_Import_Output