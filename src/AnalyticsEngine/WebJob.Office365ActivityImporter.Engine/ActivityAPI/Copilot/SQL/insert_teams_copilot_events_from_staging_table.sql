INSERT INTO online_meetings(created, meeting_id, name)
	SELECT distinct meeting_created_utc, imports.meeting_id, imports.meeting_name 
	FROM ${STAGING_TABLE_ACTIVITY} imports
	left join 
		online_meetings on online_meetings.meeting_id = imports.meeting_id
	where 
		not exists(select top 1 created, meeting_id, name from online_meetings 
			where created = imports.meeting_created_utc 
				and created = imports.meeting_created_utc 
				and imports.meeting_name = [name]
		)

insert into [event_copilot_chats] (event_id, app_host)
	SELECT imports.event_id,app_host FROM [${STAGING_TABLE_ACTIVITY}] imports

insert into event_copilot_meetings (copilot_chat_id, meeting_id)
	SELECT imports.event_id
		  ,online_meetings.id as meetingId
	  FROM ${STAGING_TABLE_ACTIVITY} imports
	  inner join online_meetings on online_meetings.meeting_id = imports.meeting_id
		and online_meetings.name = imports.meeting_name
		and online_meetings.created = CAST(imports.meeting_created_utc AS datetime)		 
