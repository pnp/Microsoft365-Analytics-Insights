
-- =============================================
-- Author:		Glenn Pepper
-- Create date: 2020-07-28
-- Version 1.0
-- Description:	This script will create a new table for the PBI reports called 'Hits_PBI'
-- =============================================	



DROP TABLE  IF EXISTS  dbo.hits_PBI
GO

CREATE TABLE dbo.Hits_PBI
([ID] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
[hits_ID] INT NOT NULL,
[session_ID] int null,
[hit_timestamp] datetime,
[seconds_on_page] float null,
[page_load_time] float null,
[site_name] nvarchar(500) NULL,
[URL] nvarchar(max),
[browser] varchar(250) null,
[region] nvarchar(250) null,
[user_id] int,
[page_title] nvarchar(250),
[Device_Name]  varchar(200),
[OS_Name] varchar(200),
[city] nvarchar(250), 
[country] nvarchar(250),
[date] date,
[dayName] varchar(10),
[monthName] varchar(10),
[MonthNumber] tinyint,
[WeekofYear] tinyint,
[quarter] varchar(10),
[year] int,
[monthYear] varchar(14),
[time] varchar(50),
[hour] varchar(50),
[PeriodofDay] varchar(50),
[PeriodSort] int
)

INSERT INTO dbo.hits_PBI ([hits_id],[session_id],[hit_timestamp],[seconds_on_page],[page_load_time],
[site_name], [URL], [browser], [region],[page_title],[user_id],[Device_Name],
[os_name],[city],[country],[date], [dayname], [monthName],[monthNumber],[weekofyear],[quarter],[year],
[MonthYear],[time],[hour],[periodofDay],[PeriodSort])
SELECT distinct hits.ID, hits.session_id, hits.hit_timestamp AS 'Hit Timestamp', 
hits.seconds_on_page AS 'Seconds on Page',
hits.page_load_time AS 'Load Time', 
CASE WHEN CHARINDEX('/sites/', url_base) = 0 
THEN URL_BASE ELSE SUBSTRING(url_base, CHARINDEX('/sites/', url_base)+7, LEN(url_base)) END as 'Site Name',
urls.full_url AS 'URL', browsers.browser_name AS 'Browser',  
provinces.province_name AS 'Region', page_titles.title AS 'Page Title', sessions.user_id,
CASE WHEN devices.device_name = 'Laptop' THEN 'Laptop' WHEN devices.device_name = 'Other' THEN 'Workstation' 
ELSE 'Not Known' END AS 'Device Name',
operating_systems.os_name AS 'OS Name', cities.city_name AS 'City',
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

CREATE NONCLUSTERED INDEX [NIX_Hits_PBI_Date]
ON [dbo].[Hits_PBI] ([Date])
INCLUDE ([DayName],[WeekOfYear],[MonthName],[Quarter],[Year],[MonthYear])
GO

SELECT * FROM dbo.Hits_PBI