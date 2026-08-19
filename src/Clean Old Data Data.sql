declare @archiveDateMax datetime

--Archive date: one month before "now". All records will use this value to delete from
set @archiveDateMax = dateadd(month, -1, GETDATE())

--IMPORTANT: strongly recommend you run this in a transaction first and rollback to test

--begin transaction archive

--Delete hits & activity from before achive date
delete from hits where [hit_timestamp] < @archiveDateMax

-- Delete searches that have sessions that have now no hits
delete from searches where exists (
	select id from [sessions] s where not exists (select * from hits where hits.session_id = s.id)
		and id = searches.session_id
)

--Delete sessions that have no hits
declare @count int
set @count = (select count(*) from sessions)
print 'session count before session clean: ' + cast(@count as nvarchar)
delete s from [sessions] s where not exists (select * from hits where hits.session_id = s.id)
set @count = (select count(*) from sessions)
print 'session count after session clean: ' + cast(@count as nvarchar)

-- Azure AD event props
delete audit_event_azure_ad_props from audit_event_azure_ad_props
	inner join event_meta_azure_ad on event_meta_azure_ad.event_id = audit_event_azure_ad_props.event_id
		inner join audit_events on audit_events.id = event_meta_azure_ad.event_id
			where audit_events.[time_stamp] < @archiveDateMax
-- Azure AD
delete event_meta_azure_ad from event_meta_azure_ad
	inner join audit_events on audit_events.id = event_meta_azure_ad.event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- Exchange event props
delete audit_event_exchange_props from audit_event_exchange_props
	inner join event_meta_exchange on event_meta_exchange.event_id = audit_event_exchange_props.event_id
		inner join audit_events on audit_events.id = event_meta_exchange.event_id
			where audit_events.[time_stamp] < @archiveDateMax
-- Exchange
delete event_meta_exchange from event_meta_exchange
	inner join audit_events on audit_events.id = event_meta_exchange.event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- SharePoint
delete [event_meta_sharepoint] from [event_meta_sharepoint]
	inner join audit_events on audit_events.id = [event_meta_sharepoint].event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- General events
delete [event_meta_general] from [event_meta_general]
	inner join audit_events on audit_events.id = [event_meta_general].event_id
	where audit_events.[time_stamp] < @archiveDateMax


-- Teams
-- Deprecated Teams add-on tracking: these tables are no longer written to and are dropped by the
-- DeprecateTeamsAddons migration once empty, so only prune them when they are still present.
-- Dynamic SQL keeps this batch parseable on a database where they have already gone.
IF OBJECT_ID('dbo.teams_addons_log', 'U') IS NOT NULL
	EXEC sp_executesql N'delete from teams_addons_log where [date] < @archiveDateMax', N'@archiveDateMax datetime', @archiveDateMax = @archiveDateMax
IF OBJECT_ID('dbo.teams_addons_user_installed_log', 'U') IS NOT NULL
	EXEC sp_executesql N'delete from teams_addons_user_installed_log where [date] < @archiveDateMax', N'@archiveDateMax datetime', @archiveDateMax = @archiveDateMax
delete teams_channel_stats_log_keywords from teams_channel_stats_log_keywords 
	inner join teams_channel_stats_log on teams_channel_stats_log.id = teams_channel_stats_log_keywords.channel_stats_log_id
	where teams_channel_stats_log.[date] < @archiveDateMax 
delete teams_channel_stats_log_langs from teams_channel_stats_log_langs 
	inner join teams_channel_stats_log on teams_channel_stats_log.id = teams_channel_stats_log_langs.channel_stats_log_id
	where teams_channel_stats_log.[date] < @archiveDateMax 

delete from teams_channel_stats_log where [date] < @archiveDateMax 
delete from teams_channel_tabs_log where [date] < @archiveDateMax 
delete from team_membership_log where [date] < @archiveDateMax 


--Activity clean-up
delete from onedrive_usage_activity_log where [date] < @archiveDateMax
delete from onedrive_user_activity_log where [date] < @archiveDateMax
delete from outlook_user_activity_log where [date] < @archiveDateMax
delete from sharepoint_user_activity_log where [date] < @archiveDateMax
delete from teams_user_activity_log where [date] < @archiveDateMax
delete from yammer_device_activity_log where [date] < @archiveDateMax
delete from yammer_group_activity_log where [date] < @archiveDateMax
delete from yammer_user_activity_log where [date] < @archiveDateMax

-- commit/rollback
--rollback transaction archive

--commit transaction archive
