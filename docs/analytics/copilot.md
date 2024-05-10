# Copilot Audit logs

**[WIP]**

Copilot related event logs can be captured.

Additional required **application** permissions:

* Files.Read.All
* OnlineMeetings.Read.All

## SQL aggregation

TODO:
* Needs aggregation by date

![img](copilot1.png)

```SQL
select app_host
from dbo.event_copilot_chats
group by app_host;

DECLARE
	@monday DATETIME = '2024-04-29',
	@sunday DATETIME = '2024-05-06';

WITH copilot_pivoted AS (
	SELECT * FROM (
		SELECT
			app_host,
			user_id,
			@monday AS [date],
			event_id
		FROM
			dbo.event_copilot_chats c
			JOIN dbo.audit_events au ON c.event_id = au.id
			WHERE @monday <= au.time_stamp AND au.time_stamp <= @sunday
	) t
	PIVOT (
		COUNT(event_id)
		for app_host IN  (
			[Bing],
			[bizchat],
			[Excel],
			[Loop],
			[Office],
			[PowerPoint],
			[Teams],
			[Word]
		)
	) AS pivoted
)
SELECT
	user_id, date,
	[Bing],
	[bizchat] AS [M365 Chat] , -- M365 Chat experience on Bing and Teams
	[Excel],
	[Loop],
	[Office],
	[PowerPoint],
	[Teams],
	[Word]
FROM copilot_pivoted
```

```SQL

```

## Notes

* when apphost == Teams, events are not imported. These are interactions with copilot in the chat in teams

Check `SaveSingleCopilotEventToSql`.

* Once the events are imported, they are not updated in the database. To clean the Copilot events use this:

```SQL
begin try
begin transaction
truncate table dbo.event_copilot_files
truncate table dbo.event_copilot_meetings
delete FROM  dbo.event_copilot_chats
truncate table [dbo].[event_meta_general]
delete FROM dbo.audit_events WHERE operation_id = 1
commit transaction
end try
begin catch
print error_message()
if @@trancount > 0
rollback transaction
end catch
```

* OneDrive files info seems to fail to load:

    >Files might have been renamed.

    ```
    Error getting file info for copilotDocContextId https://contoso-my.sharepoint.com/personal/billy_contoso_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=[...]

    No file info found for copilot context type 'docx' with ID https://contoso-my.sharepoint.com/personal/billy_contoso_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=[...]
```

* Whiteboard events are not recorded:
    * whiteboard are considered SPO events -> wrong

    ```
    No file info found for copilot context type 'whiteboard' with ID whiteboard.office.com/me/whiteboards/p/c3BvOmh0dH[...]
    ```