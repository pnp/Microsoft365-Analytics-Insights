/****** Object:  View [dbo].[Usage]    Script Date: 12/03/2020 14:10:25 ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

drop view if exists [dbo].[Usage]
Go

CREATE VIEW [dbo].[Usage] AS




SELECT distinct hits.ID, hits.session_id, urls.full_url AS 'URL', browsers.browser_name AS 'Browser Name', provinces.province_name AS 'Region',users.user_Name AS 'User Name', page_titles.title AS 'Page Title', hits.seconds_on_page AS 'Seconds on Page',
CASE WHEN devices.device_name = 'Laptop' THEN 'Laptop' WHEN devices.device_name = 'Other' THEN 'Workstation' ELSE 'Not Known' END AS 'Device Name' /*case -- change 'other' to 'workstation */, 
operating_systems.os_name AS 'OS Name', cities.city_name AS 'City',
Countries.country_name AS 'Country',
hits.page_load_time AS 'Page Load Time', CASE WHEN CHARINDEX('/sites/', url_base) = 0 THEN URL_BASE ELSE SUBSTRING(url_base, CHARINDEX('/sites/', url_base)+7, LEN(url_base)) END as 'Site Name',
dimdate.date, dimdate.WeekDayName AS [Day],  dimdate.MonthName AS 'Month Name', dimdate.Month AS monthNumber, dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear AS 'Month-Year', dimtime.time, dimtime.hour, dimtime.[Period of Day], DimTime.PeriodSort
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
INNER JOIN Users
ON sessions.user_id = users.id


GO


