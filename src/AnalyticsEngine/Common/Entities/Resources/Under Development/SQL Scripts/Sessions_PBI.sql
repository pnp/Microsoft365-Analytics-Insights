
-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-08-04
-- Version 1.0
-- Description:	This script will create a new table for the PBI reports called 'sessions_PBI' to hold the last 6 months of data
-- =============================================	

DROP TABLE  IF EXISTS  [dbo].[sessions_PBI]
GO

CREATE TABLE [dbo].[sessions_PBI]
(
id INT NOT NULL PRIMARY KEY,
ai_session_id varchar(50),
entrance_hit_id int, 
entrance_hit_title nvarchar(250),
entrance_hit_time_stamp datetime,
exit_hit_id INT,
exit_hit_title nvarchar(250), 
exit_hit_time_stamp datetime,
date date NOT NULL,
dayName varchar(10) NOT NULL, 
MonthName Varchar(10) NOT NULL, 
MonthNumber tinyint NOT NULL,
weekOfYear tinyInt NOT NULL,
Quarter varchar(10) NOT NULL, 
Year int NOT NULL, 
MonthYear varchar(14) NOT NULL, 
time varchar(50) NOT NULL, 
hour varchar(50) NOT NULL,
PeriodOfDay varchar(50) NOT NULL,
PeriodSort int NOT NULL
)
;

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
)

INSERT INTO [dbo].[sessions_PBI]
           ([id]
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
           ,[PeriodSort])
 SELECT cte.id
		,cte.ai_session_id
		,Cte.entrance_hit_id
		,cte.entrance_hit_title
		,CTE.entrance_hit_time_stamp
		,CTE.exit_hit_id
		,CTE.exit_hit_title
		,CTE.exit_hit_time_stamp
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
		FROM CTE
		INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, CTE.entrance_hit_time_stamp, 103) = dimdate.date
		INNER JOIN DimTime as dimtime
		ON FORMAT(cte.entrance_hit_time_stamp, 'HH:mm') = dimtime.time
