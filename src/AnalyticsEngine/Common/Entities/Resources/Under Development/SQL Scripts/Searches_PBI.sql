
-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-08-05
-- Version 1.0
-- Description:	This script will create a new table for the PBI reports called 'searches_PBI' to hold the last 6 months of data
-- =============================================	

DROP TABLE  IF EXISTS  [dbo].[searches_PBI]
GO

CREATE TABLE [dbo].[searches_PBI]
(
id int not null PRIMARY KEY,
session_id int NOT NULL,
search_term nvarchar(250) NULL,
user_id int null,
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
GO

INSERT INTO [dbo].[searches_PBI]
(id, session_id, search_term, user_id, time_stamp, date,dayName, MonthName,MonthNumber, WeekOfYear,
Quarter, Year,MonthYear,time,hour,PeriodOfDay, PeriodSort)
SELECT searches.id, searches.session_id, search_terms.search_term, users.id as 'User_ID'
,hits.hit_timestamp
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
GO

