-- Meeting enrichment for Copilot interactions in a Teams meeting. It runs after the shared Copilot merge, so
-- the interactions themselves (copilot_chats) are already saved, whatever happens here.
--
-- A meeting is matched on its id, its creation time as online_meetings stores it (datetime: Graph's 7-digit
-- datetime2 value is rounded to it) and its subject trimmed to online_meetings.name's 100 characters. The
-- subject is trimmed when it is staged too, without splitting a surrogate pair; LEFT() is the backstop. The
-- old check compared created with itself and the raw datetime2 value, so it never found the stored row: each
-- batch stored the meeting again, the link below then matched every copy and violated
-- copilot_event_meetings' primary key, and a subject over 100 characters failed the insert - every retry.
;WITH staged AS (
	SELECT imports.meeting_id,
		   TRY_CONVERT(datetime, imports.meeting_created_utc) AS created,
		   CAST(LEFT(imports.meeting_name, 100) AS nvarchar(100)) AS name
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE imports.meeting_id IS NOT NULL AND imports.meeting_name IS NOT NULL AND imports.meeting_created_utc IS NOT NULL
)
INSERT INTO online_meetings (created, meeting_id, name)
	SELECT DISTINCT staged.created, staged.meeting_id, staged.name
	FROM staged
	WHERE staged.created IS NOT NULL
	  AND NOT EXISTS (
		SELECT 1
		FROM online_meetings existing
		WHERE existing.meeting_id = staged.meeting_id
		  AND existing.created = staged.created
		  AND existing.name = staged.name
	  );

-- Link each interaction to exactly one meeting row. A meeting that older builds stored more than once links to
-- its oldest copy, so the duplicates already in a database can't fan one interaction out into several rows.
;WITH staged AS (
	SELECT imports.event_id,
		   imports.meeting_id,
		   TRY_CONVERT(datetime, imports.meeting_created_utc) AS created,
		   CAST(LEFT(imports.meeting_name, 100) AS nvarchar(100)) AS name
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE imports.meeting_id IS NOT NULL AND imports.meeting_name IS NOT NULL AND imports.meeting_created_utc IS NOT NULL
), matched AS (
	SELECT staged.event_id,
		   online_meetings.id AS meeting_row_id,
		   ROW_NUMBER() OVER (PARTITION BY staged.event_id ORDER BY online_meetings.id) AS pick
	FROM staged
	INNER JOIN online_meetings
		ON online_meetings.meeting_id = staged.meeting_id
		AND online_meetings.created = staged.created
		AND online_meetings.name = staged.name
)
INSERT INTO copilot_event_meetings (copilot_chat_id, meeting_id)
	SELECT matched.event_id, matched.meeting_row_id
	FROM matched
	WHERE matched.pick = 1
	  AND NOT EXISTS (
		SELECT 1
		FROM copilot_event_meetings existing
		WHERE existing.copilot_chat_id = matched.event_id
	  );