
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO


CREATE OR ALTER VIEW [dbo].[vwTeamsDevices] AS
SELECT 
	   users.user_name
      ,teams_user_device_usage_log.[used_web]
      ,teams_user_device_usage_log.[used_win_phone]
      ,teams_user_device_usage_log.[used_ios]
      ,teams_user_device_usage_log.[used_android]
      ,teams_user_device_usage_log.[used_mac]
      ,teams_user_device_usage_log.[used_windows]
	  ,dimdate.date
	  ,dimdate.WeekDayName AS dayName
	  ,dimdate.MonthName
	  ,dimdate.Month AS monthNumber
	  ,dimDate.WeekOfYear
	  ,dimdate.quarter
	  ,dimdate.Year
	  ,dimdate.monthYear
  FROM [dbo].[teams_user_device_usage_log]
  INNER JOIN users
  ON users.id = teams_user_device_usage_log.[user_id]
  INNER JOIN dimDate AS dimdate
  ON CONVERT(DATE,teams_user_device_usage_log.[Date] , 103) = dimdate.date
GO

