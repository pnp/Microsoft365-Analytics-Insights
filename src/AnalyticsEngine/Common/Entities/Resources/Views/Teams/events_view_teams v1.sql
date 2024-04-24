
DROP VIEW if exists vwTeamsChatStats
GO


CREATE VIEW vwTeamsChatStats AS

	select teams.name, last_chat_date, sum(channel_chat_count) as total_chat_count, sum(channel_tab_count) as total_tab_count from teams
		inner join 
		(
			--Channel stats with IsNull check
			select team_id, last_chat_date, ISNULL(channel_chat_count, 0) as channel_chat_count, ISNULL(channel_tab_count, 0) as channel_tab_count from (
  				select *	--Channel stats raw
				,(
					select SUM([chats_count]) from teams_channel_stats_log where teams_channel_stats_log.channel_id = [teams_channels].id
				) as channel_chat_count 
				,(
					select SUM([tabs_count]) from teams_channel_stats_log where teams_channel_stats_log.channel_id = [teams_channels].id
				) as channel_tab_count 
				,(
					select [date] as last_chat_date from teams_channel_stats_log 
						where teams_channel_stats_log.channel_id = [teams_channels].id and chats_count > 0
				) as last_chat_date 
				from [dbo].[teams_channels]
			) channel_stats
		) channel_stats on channel_stats.team_id = teams.id
		group by teams.name, last_chat_date
GO


