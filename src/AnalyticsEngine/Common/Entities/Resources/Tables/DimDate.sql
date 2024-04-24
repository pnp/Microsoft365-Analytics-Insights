DECLARE @StartDate DATE, @NumberOfYears INT
SET @StartDate = '20180101' 
 SET @NumberOfYears = 30;

-- prevent set or regional settings from interfering with 
-- interpretation of dates / literals

SET DATEFIRST 7;
SET DATEFORMAT mdy;
--SET LANGUAGE UK_ENGLISH;

DECLARE @CutoffDate DATE = DATEADD(YEAR, @NumberOfYears, @StartDate);

If Object_ID('DimDate','u') IS NOT NULL
BEGIN
DROP Table DimDate
END

-- this is just a holding table for intermediate calculations:
CREATE TABLE #dim
(
  [date]       DATE PRIMARY KEY, 
  [day]        AS DATEPART(DAY,      [date]),
  [month]      AS DATEPART(MONTH,    [date]),
  FirstOfMonth AS CONVERT(DATE, DATEADD(MONTH, DATEDIFF(MONTH, 0, [date]), 0)),
  [MonthName]  AS DATENAME(MONTH,    [date]),
  [week]       AS DATEPART(WEEK,     [date]),
  [DayOfWeek]  AS DATEPART(WEEKDAY,  [date]),
  [quarter]    AS CONVERT(CHAR(1),DATEPART(QUARTER,  [date])),
  [year]       AS DATEPART(YEAR,     [date]),
  [MonthYear] AS DATENAME(MONTH,    [date]) + '-' + CONVERT(VARCHAR, DATEPART(YEAR,     [date]),0)
);

-- use the catalog views to generate as many rows as we need

INSERT #dim([date]) 
SELECT d
FROM
(
  SELECT d = DATEADD(DAY, rn - 1, @StartDate)
  FROM 
  (
    SELECT TOP (DATEDIFF(DAY, @StartDate, @CutoffDate)) 
      rn = ROW_NUMBER() OVER (ORDER BY s1.[object_id])
    FROM sys.all_objects AS s1
    CROSS JOIN sys.all_objects AS s2
    -- on my system this would support > 5 million days
    ORDER BY s1.[object_id]
  ) AS x
) AS y;

SELECT * FROM #dim

CREATE TABLE dbo.DimDate
(
  --DateKey           INT         NOT NULL PRIMARY KEY,
  [Date]              DATE        NOT NULL,
  [Day]               TINYINT     NOT NULL,
  WeekDayName         VARCHAR(10) NOT NULL,
  [DayOfYear]         SMALLINT    NOT NULL,
  [WeekOfYear]        TINYINT     NOT NULL,
  [Month]             TINYINT     NOT NULL,
  [MonthName]         VARCHAR(10) NOT NULL,
  [Quarter]           VARCHAR(10) NOT NULL,
  [Year]              INT         NOT NULL,
  [MonthYear]         VARCHAR(14)     NOT NULL,
);
GO

INSERT dbo.DimDate WITH (TABLOCKX)
SELECT
  [Date]        = [date],
  [Day]         = CONVERT(TINYINT, [day]),
  [WeekDayName] = CONVERT(VARCHAR(10), DATENAME(WEEKDAY, [date])),
  [DayOfYear]   = CONVERT(SMALLINT, DATEPART(DAYOFYEAR, [date])),
  --WeekOfMonth   = CONVERT(TINYINT, DENSE_RANK() OVER 
    --              (PARTITION BY [year], [month] ORDER BY [week])),
  WeekOfYear    = CONVERT(TINYINT, [week]),
  [Month]       = CONVERT(TINYINT, [month]),
  [MonthName]   = CONVERT(VARCHAR(10), [MonthName]),
  --[Quarter] = CASE WHEN [quarter] = '1' THEN 'Q1 ' + CONVERT(CHAR(4),[YEAR]) ELSE 'Nowt' END,
  [Quarter]     = 'Q' + CAST([quarter] AS CHAR(1)) + ' ' + CAST([YEAR] AS CHAR(4)),
  [Year]        = [year],
  [MonthYear] = [MonthYear]
FROM #dim
OPTION (MAXDOP 1);

SELECT * FROM DimDate