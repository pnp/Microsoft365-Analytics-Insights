
-- ============================================
-- Author: Glenn Pepper
-- Create Date: 04/12/2019
-- Modified Date: 15/07/2020
-- Version: 2.0
-- Description: This view displays hit information
-- Changes: Change column order, added a coalesce statement for NULLS in 'seconds_on_page' column. 
-- Index added to DimDate table to speed up query
-- =============================================


IF OBJECT_ID('vwHits_6Months','v') IS NOT NULL
BEGIN
DROP VIEW vwHits_6Months
END
GO

CREATE VIEW vwHits_6Months AS
SELECT distinct hits.ID, hits.session_id, hits.hit_timestamp AS 'Hit Timestamp', 
COALESCE(hits.seconds_on_page,0) AS 'Seconds on Page',
hits.page_load_time AS 'Load Time', CASE WHEN CHARINDEX('/sites/', url_base) = 0 
THEN URL_BASE ELSE SUBSTRING(url_base, CHARINDEX('/sites/', url_base)+7, LEN(url_base)) END as 'Site Name',
urls.full_url AS 'URL', browsers.browser_name AS 'Browser',  
provinces.province_name AS 'Region', sessions.user_id, users.user_name as 'User Name', page_titles.title AS 'Page Title', 
CASE WHEN devices.device_name = 'Laptop' THEN 'Laptop' WHEN devices.device_name = 'Other' THEN 'Workstation' 
ELSE 'Not Known' END AS 'Device Name' /*case -- change 'other' to 'workstation */, 
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
--ON CAST(hits.hit_timestamp AS DATE) = dimDate.Date
INNER JOIN DimTime as dimtime
ON FORMAT(hit_timestamp, 'HH:mm') = dimtime.time
INNER JOIN Users
ON sessions.user_id = users.id
--WHERE (DateAdd(MM, -6, GetDate()) < hits.hit_timestamp)
WHERE hits.hit_timestamp <= getdate() 
AND hits.hit_timestamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))
GO



IF OBJECT_ID('vwHits','v') IS NOT NULL
BEGIN
DROP View vwHits
END
GO

CREATE VIEW vwHits AS
SELECT distinct hits.ID, hits.session_id, hits.hit_timestamp AS 'Hit Timestamp', 
COALESCE(hits.seconds_on_page,0) AS 'Seconds on Page',
hits.page_load_time AS 'Load Time', CASE WHEN CHARINDEX('/sites/', url_base) = 0 
THEN URL_BASE ELSE SUBSTRING(url_base, CHARINDEX('/sites/', url_base)+7, LEN(url_base)) END as 'Site Name',
urls.full_url AS 'URL', browsers.browser_name AS 'Browser',  
provinces.province_name AS 'Region',sessions.user_id, users.user_name as 'User Name', page_titles.title AS 'Page Title', 
CASE WHEN devices.device_name = 'Laptop' THEN 'Laptop' WHEN devices.device_name = 'Other' THEN 'Workstation' 
ELSE 'Not Known' END AS 'Device Name' /*case -- change 'other' to 'workstation */, 
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
INNER JOIN Users
ON sessions.user_id = users.id

GO
