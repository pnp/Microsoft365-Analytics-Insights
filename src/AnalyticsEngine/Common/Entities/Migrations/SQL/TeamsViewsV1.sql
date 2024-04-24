/****** Object:  View [dbo].[vwTeamsAddOns_Log]    Script Date: 27/10/2020 12:34:04 ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO


CREATE OR ALTER VIEW [dbo].[vwTeamsUsers] AS
	SELECT teams_user_activity_log.user_id, teams_user_activity_log.meetings_count, teams_user_activity_log.private_chat_count, 
		teams_user_activity_log.team_chat_count, teams_user_activity_log.calls_count,
		dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
		dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear,
		(teams_user_activity_log.meetings_count * 3) + (teams_user_activity_log.private_chat_count * 1) + 
		(teams_user_activity_log.team_chat_count * 2) + (teams_user_activity_log.calls_count * 2) AS 'Weighted Score'
	FROM teams_user_activity_log
	INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, teams_user_activity_log.date, 103) = dimdate.date
GO



CREATE OR ALTER VIEW [dbo].[vwTeamsAddOns_Log] AS

	SELECT teams.name as 'Team', teams_addons.name as 'Add-on',
		dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
		dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear
	FROM teams 
	INNER JOIN teams_addons_log
		on teams.id = teams_addons_log.team_id
	INNER JOIN teams_addons 
		ON teams_addons.id = teams_addons_log.addon_id
	INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, teams_addons_log.date, 103) = dimdate.date

GO

CREATE OR ALTER VIEW [dbo].[vwTeamsCalls]
AS
	SELECT        call_records.organizer_id AS Organizer, call_records.start, call_records.[end], CONVERT(char(8), DATEADD(s, DATEDIFF(s, call_records.start, call_records.[end]), '1900-1-1'), 8) AS [Call Duration], call_feedback.rating, 
							 call_feedback.text, dimdate.Date, dimdate.WeekDayName AS dayName, dimdate.MonthName, dimdate.Month AS monthNumber, dimdate.WeekOfYear, dimdate.Quarter, dimdate.Year, dimdate.MonthYear, dimtime.Time, 
							 dimtime.Hour, dimtime.[Period of Day], dimtime.PeriodSort
	FROM            dbo.call_records LEFT OUTER JOIN
							 dbo.call_feedback ON call_records.id = call_feedback.call_id INNER JOIN
							 dbo.DimDate AS dimdate ON CONVERT(DATE, call_records.start, 103) = dimdate.Date INNER JOIN
							 dbo.DimTime AS dimtime ON FORMAT(call_records.start, 'HH:mm') = dimtime.Time
GO



CREATE OR ALTER VIEW [dbo].[vwTeamsChannels] AS
	Select Distinct teams.id, teams.name as 'Team Name', teams_channels.name 'Channel Name', COALESCE(teams_channel_stats_log.chats_count,0) as 'Chats_Count',
		COALESCE(teams_channel_stats_log.sentiment_score,0) AS 'Sentiment_Score', COALESCE(teams_channel_stats_log_keywords.keyword_count,0) AS 'Keyword_Count',
		dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
		dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear
	FROM teams
	INNER JOIN teams_channels
		on teams.id = teams_channels.team_id
	INNER JOIN teams_channel_stats_log
		ON teams_channels.id = teams_channel_stats_log.channel_id
	LEFT JOIN teams_channel_stats_log_keywords
		on teams_channel_stats_log_keywords.channel_stats_log_id = teams_channel_stats_log.id
	INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, teams_channel_stats_log.date, 103) = dimdate.date
GO


CREATE OR ALTER VIEW [dbo].[vwTeamsKeywords] AS
	Select teams.id, teams.name as 'Team', teams_channels.name 'Channel Name', keywords.name as 'Keyword',
		dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
		dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear
	FROM teams
	INNER JOIN teams_channels
		on teams.id = teams_channels.team_id
	INNER JOIN teams_channel_stats_log
		ON teams_channels.id = teams_channel_stats_log.channel_id
	LEFT JOIN teams_channel_stats_log_keywords
		on teams_channel_stats_log_keywords.channel_stats_log_id = teams_channel_stats_log.id
	LEFT JOIN keywords 
		ON teams_channel_stats_log_keywords.id = keywords.id
	INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, teams_channel_stats_log.date, 103) = dimdate.date
GO

CREATE OR ALTER VIEW [dbo].[vwTeamsMembers] AS

	WITH LastActive AS (
	SELECT user_id, max(vwTeamsUsers.date) As 'LastActive' FROM vwTeamsUsers WHERE [Weighted Score] <> 0
	GROUP BY user_id)
	  SELECT DISTINCT teams.name as 'Team', team_membership_log.user_id,  LastActive.LastActive AS 'LatestUserActivity'
	  FROM teams 
	  INNER JOIN team_membership_log
	  on teams.id = team_membership_log.team_id
	  LEFT JOIN LastActive 
	  ON team_membership_log.user_id = LastActive.user_id
GO


CREATE OR ALTER VIEW [dbo].[vwTeamsOwners] AS

  SELECT teams.name, team_owners.owner_id, team_owners.discovered,
	  dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
	  dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear
  FROM teams 
	INNER JOIN team_owners
  ON teams.id = team_owners.team_id
	INNER JOIN dimDate AS dimdate
  ON CONVERT(DATE, team_owners.discovered, 103) = dimdate.date

GO


CREATE OR ALTER VIEW [dbo].[vwTeamsStats] AS
	WITH 
	Members AS (
		select teams.name , COUNT(DISTINCT team_membership_log.user_id) as 'Member Count'
		FROM teams
		Left JOIN team_membership_log 
			on teams.id = team_membership_log.team_id
		group by teams.id, teams.name
	),
	Channels AS (
		Select teams.name, COUNT(teams_channels.name) as 'Channel Count'
		FROM teams
		INNER JOIN teams_channels
		on teams.id = teams_channels.team_id
		group by teams.name
	),
	AddOns AS (
		SELECT Teams.name, COUNT(DISTINCT teams_addons.name) as 'Add-On Count'
		FROM teams
		INNER JOIN teams_addons_log
		ON teams.id = teams_addons_log.team_id
		INNER JOIN teams_addons
		ON teams_addons_log.id = teams_addons.id
		GROUP BY teams.name
	),
	tabs AS
	(
		Select Teams.name, Count(DISTINCT teams_tabs.name) as 'Active Tab Count'
		FROM teams
		INNER JOIN teams_channels
		ON teams.id = teams_channels.team_id
		Inner JOIN teams_channel_tabs_log
		ON teams_channels.id = teams_channel_tabs_log.channel_id
		INNER JOIN teams_tabs
		ON teams_channel_tabs_log.tab_id = teams_tabs.id
		Group by teams.name
	)
	SELECT Members.name as 'Team', COALESCE(Members.[Member Count],0) AS 'Members', COALESCE(Channels.[Channel Count],0) as 'Channels', 
	COALESCE(addons.[Add-On Count],0) As 'Add-Ons - Active Only', COALESCE(tabs.[Active Tab Count],0)  as 'Tabs - Active Only'
	FROM Members
	Left JOIN
	Channels
		ON Members.name = Channels.name
	LEFT JOIN Addons
		ON Members.name = AddOns.name
	LEFT JOIN tabs 
		ON members.name = tabs.name
GO

CREATE OR ALTER VIEW [dbo].[vwTeamsTabs_Log] AS
	SELECT teams.name as 'Team', teams_channels.name as 'Channel', teams_tabs.name AS 'Tab',
		dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
		dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear
	FROM teams 
	INNER JOIN teams_channels
		ON teams.id = teams_channels.team_id
	INNER JOIN teams_channel_tabs_log
		ON teams_channel_tabs_log.channel_id = teams_channels.id
	INNER JOIN teams_tabs
		ON teams_channel_tabs_log.tab_id = teams_tabs.id
	INNER JOIN dimDate AS dimdate
		ON CONVERT(DATE, teams_channel_tabs_log.date, 103) = dimdate.date
GO

