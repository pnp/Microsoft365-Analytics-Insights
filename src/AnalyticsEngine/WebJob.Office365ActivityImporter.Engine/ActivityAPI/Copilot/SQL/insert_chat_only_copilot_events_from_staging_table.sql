
insert into [event_copilot_chats] (event_id, app_host)
	SELECT imports.event_id,app_host FROM [${STAGING_TABLE_ACTIVITY}] imports

