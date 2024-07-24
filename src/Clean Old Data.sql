declare @archiveDateMax datetime

--Archive date: one month before "now". All records will use this value to delete from
set @archiveDateMax = dateadd(month, -1, GETDATE())

--IMPORTANT: by default this script does not commit the transaction.
--It's recommended to stop web-jobs before running to ensure no locks.
--Test once in rollback mode and once no errors are seen, change "rollback" to "commit" below and run again.

begin transaction archive

-- Delete clicks
delete from hits_clicked_elements where [timestamp] < @archiveDateMax

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

-- Audit-log cleanup
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

-- General/misc events
delete [event_meta_general] from [event_meta_general]
	inner join audit_events on audit_events.id = [event_meta_general].event_id
	where audit_events.[time_stamp] < @archiveDateMax
	
-- Copilot
delete event_copilot_files from event_copilot_files
	inner join event_copilot_chats on event_copilot_files.copilot_chat_id = event_copilot_chats.event_id
	inner join audit_events on audit_events.id = event_copilot_chats.event_id
	where audit_events.[time_stamp] < @archiveDateMax

delete event_copilot_meetings from event_copilot_meetings
	inner join event_copilot_chats on event_copilot_meetings.copilot_chat_id = event_copilot_chats.event_id
	inner join audit_events on audit_events.id = event_copilot_chats.event_id
	where audit_events.[time_stamp] < @archiveDateMax

delete event_copilot_chats from event_copilot_chats
	inner join audit_events on audit_events.id = event_copilot_chats.event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- Delete common events and ignored
delete from audit_events where [time_stamp] < @archiveDateMax
delete from ignored_audit_events where processed_timestamp < @archiveDateMax

-- Teams
delete from teams_addons_log where [date] < @archiveDateMax

delete teams_channel_stats_log_keywords from teams_channel_stats_log_keywords
	inner join teams_channel_stats_log on teams_channel_stats_log.id = teams_channel_stats_log_keywords.channel_stats_log_id
	where teams_channel_stats_log.[date] < @archiveDateMax 

delete teams_channel_stats_log_langs from teams_channel_stats_log_langs
	inner join teams_channel_stats_log on teams_channel_stats_log.id = teams_channel_stats_log_langs.channel_stats_log_id
	where teams_channel_stats_log.[date] < @archiveDateMax 

delete from teams_channel_stats_log where [date] < @archiveDateMax 

delete from teams_channel_tabs_log where [date] < @archiveDateMax 
delete from team_membership_log where [date] < @archiveDateMax 

-- Calls
delete call_session_call_modalities from call_session_call_modalities
	inner join call_sessions on call_sessions.id = call_session_call_modalities.call_session_id
	inner join call_records on call_sessions.call_record_id = call_records.id
	where call_records.[end] < @archiveDateMax  

delete call_sessions from call_sessions
	inner join call_records on call_sessions.call_record_id = call_records.id
	where call_records.[end] < @archiveDateMax
	
delete call_feedback from call_feedback
	inner join call_records on call_feedback.call_id = call_records.id
	where call_records.[end] < @archiveDateMax

delete from call_records
	where call_records.[end] < @archiveDateMax


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
rollback transaction archive

--commit transaction archive

