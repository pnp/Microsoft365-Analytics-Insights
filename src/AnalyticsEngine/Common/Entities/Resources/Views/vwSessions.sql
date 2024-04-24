
-- ============================================
-- Author: Glenn Pepper
-- Create Date: 04/12/2019
-- Version: 1.0
-- Description: This view displays session information
-- =============================================


IF OBJECT_ID('vwSessions','v') IS NOT NULL
BEGIN
DROP VIEW vwSessions
END
GO
CREATE VIEW vwSessions AS

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
		order by [hit_timestamp] desc
	) as exit_hit_time_stamp
from [sessions] s
)

SELECT	 cte.id
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
		,dimTime.PeriodSort
		FROM CTE
		INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, CTE.entrance_hit_time_stamp, 103) = dimdate.date
		INNER JOIN DimTime as dimtime
		ON FORMAT(cte.entrance_hit_time_stamp, 'HH:mm') = dimtime.time

GO



IF OBJECT_ID('vwSessions_6Months','v') IS NOT NULL
BEGIN
DROP VIEW vwSessions_6Months
END
GO
CREATE VIEW vwSessions_6Months AS
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

SELECT	 cte.id
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

GO
