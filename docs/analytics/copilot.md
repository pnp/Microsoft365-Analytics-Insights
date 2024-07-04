# Copilot Audit logs

**[WIP]**

Copilot related event logs can be captured from the Audit logs.

Additional required **application** permissions:

* Files.Read.All
* OnlineMeetings.Read.All

## SQL stuff

### List of app hosts
```SQL
SELECT [app_host]
FROM dbo.event_copilot_chats
GROUP BY [app_host];
```

### App host metrics
```SQL
DECLARE
  @StartDate DATETIME = '2024-04-29',
  @EndDate DATETIME = '2024-05-06';

WITH copilot_pivoted AS (
  SELECT * FROM (
    SELECT
      [app_host]
      ,[user_id]
      ,[profiling].[udf_GetMonday](au.time_stamp) AS [date]
      ,[event_id]
    FROM dbo.event_copilot_chats AS c
      JOIN dbo.audit_events AS au ON c.event_id = au.id
    WHERE @StartDate <= au.time_stamp AND au.time_stamp <= @EndDate
  ) t
  PIVOT (
    COUNT(event_id)
    FOR app_host IN  (
      -- As more hosts appear, they need to be added here as they are in the JSON
      [Bing]
      ,[bizchat]
      ,[Excel]
      ,[Loop]
      ,[Office]
      ,[PowerPoint]
      ,[Teams]
      ,[Word]
    )
  ) AS pivoted
)
SELECT
  [user_id], [date]
  -- M365 Chat experience on Bing and Teams
  ,[bizchat] AS [Copilot M365 Chat]
  ,[Bing] AS [Copilot Bing]
  ,[Loop] AS [Copilot Loop]
  ,[Office] AS [Copilot Office]
  ,[Excel] AS [Copilot Excel]
  ,[PowerPoint] AS [Copilot PowerPoint]
  ,[Word] AS [Copilot Word]
  ,[Teams] AS [Copilot Teams]
FROM copilot_pivoted
```

### App host metrics pivoted
```SQL
DECLARE
  @StartDate DATETIME = '2024-04-29',
  @EndDate DATETIME = '2024-05-06';
WITH
copilot_rows AS (
  SELECT
    user_id,
    [profiling].[udf_GetMonday](au.time_stamp) AS [MetricDate],
    'Copilot ' + (
      CASE
        WHEN app_host = 'bizchat' THEN 'M365 Chat'
        ELSE app_host
      END
    ) AS [Metric]
  FROM
    dbo.event_copilot_chats AS c
    JOIN dbo.audit_events AS au ON c.event_id = au.id
)
SELECT
  [user_id], [MetricDate], [Metric]
  ,COUNT(user_id) AS [Sum]
FROM copilot_rows
GROUP BY user_id, MetricDate, Metric
```

```SQL
WITH copilot_events AS (
  SELECT
    [user_id]
    ,@StartDate AS [date]
    ,[app_host]
    ,c.event_id AS [chat_id]
    ,f.copilot_chat_id AS [has_file]
    ,m.copilot_chat_id AS [has_meeting]
  FROM dbo.event_copilot_chats AS c
    JOIN dbo.audit_events AS au ON c.event_id = au.id
    LEFT JOIN dbo.event_copilot_files AS f ON c.event_id = f.copilot_chat_id
    LEFT JOIN dbo.event_copilot_meetings AS m ON c.event_id = m.copilot_chat_id
  WHERE @StartDate <= au.time_stamp AND au.time_stamp <= @EndDate
)
SELECT
  [user_id], [date]
  ,count(chat_id) AS [chat_count]
  ,count(has_file) AS [file_count]
  ,count(has_meeting) AS [meeting_count]
FROM copilot_events
GROUP BY [user_id], [date]
```

```SQL
```

## Notes

* If the events have already been imported and need to be updated, use the following to clean up the tables and force their processing again. **Used only during development**:

```SQL
BEGIN TRY
  BEGIN TRANSACTION
  TRUNCATE TABLE dbo.event_copilot_files
  TRUNCATE TABLE dbo.event_copilot_meetings
  DELETE FROM  dbo.event_copilot_chats
  TRUNCATE TABLE [dbo].[event_meta_general]
  DELETE FROM dbo.audit_events WHERE operation_id = 1
  COMMIT TRANSACTION
END TRY
BEGIN CATCH
  PRINT error_message()
  IF @@trancount > 0 ROLLBACK TRANSACTION
END CATCH
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


* Example of TeamsChat interaction originating from a meeting chat

```JSON
{"AISystemPlugin":[],"AccessedResources":[],"AppHost":"Teams","Contexts":[{"Id":"https://teams.microsoft.com/_#/conversations/19:meeting_MGRjZWI3ODMtM2ZkOS00ZDI4LTk3M2QtOGFhMWUyNzcwMzZl@thread.v2?ctx=chat","Type":"TeamsChat"}],"MessageIds":[],"Messages":[{"Id":"1715255844384","isPrompt":true},{"Id":"1715255844522","isPrompt":false}],"ModelTransparencyDetails":[],"ThreadId":"19:aFR29tYtsi-OFqenKd2KDbou0dc0q0NO0JxAzbHZA-k1@thread.v2"}
```