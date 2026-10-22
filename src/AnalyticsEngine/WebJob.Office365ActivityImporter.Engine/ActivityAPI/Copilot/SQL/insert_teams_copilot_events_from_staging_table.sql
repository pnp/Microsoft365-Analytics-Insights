-- Meeting enrichment for Copilot interactions in a Teams meeting. It runs after the shared Copilot merge, so
-- the interactions themselves (copilot_chats) are already saved, whatever happens here.
--
-- A meeting is its meeting_id ({user object id}_{meeting thread id}): online_meetings holds one row per id, and
-- UX_online_meetings_meeting_id (migration UniqueOnlineMeetingIds) enforces it. The id is trimmed and cast to that
-- column's nvarchar(850), so both lookups below are index seeks with nothing applied to the column. A stored
-- meeting is never stored again, whatever its creation time or subject now say. The old check compared created
-- with itself and the raw datetime2 value and never meeting_id, so each batch stored the meeting again, and the
-- link below then matched every copy and violated copilot_event_meetings' primary key.
--
-- The creation time is stored as datetime (Graph's 7-digit datetime2 value is rounded to it) and the subject is
-- trimmed to online_meetings.name's 100 characters: it is trimmed when staged too, without splitting a surrogate
-- pair; LEFT() is the backstop.
;WITH staged AS (
	SELECT CAST(LEFT(imports.meeting_id, 850) AS nvarchar(850)) AS meeting_id,
		   TRY_CONVERT(datetime, imports.meeting_created_utc) AS created,
		   CAST(LEFT(imports.meeting_name, 100) AS nvarchar(100)) AS name
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE imports.meeting_id IS NOT NULL AND imports.meeting_name IS NOT NULL AND imports.meeting_created_utc IS NOT NULL
), candidates AS (
	-- One row per meeting, even when a batch saw it twice with different subjects.
	SELECT staged.meeting_id, staged.created, staged.name,
		   ROW_NUMBER() OVER (PARTITION BY staged.meeting_id ORDER BY staged.created, staged.name) AS pick
	FROM staged
	WHERE staged.created IS NOT NULL
)
INSERT INTO online_meetings (created, meeting_id, name)
	SELECT candidates.created, candidates.meeting_id, candidates.name
	FROM candidates
	WHERE candidates.pick = 1
	  AND NOT EXISTS (
		SELECT 1
		FROM online_meetings existing
		WHERE existing.meeting_id = candidates.meeting_id
	  );

-- Link each interaction to its meeting's row. A database the migration hasn't run on yet can still hold several
-- copies of a meeting: the oldest is used, so they can't fan one interaction out into several rows.
;WITH staged AS (
	SELECT imports.event_id,
		   CAST(LEFT(imports.meeting_id, 850) AS nvarchar(850)) AS meeting_id
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE imports.meeting_id IS NOT NULL AND imports.meeting_name IS NOT NULL AND imports.meeting_created_utc IS NOT NULL
), matched AS (
	SELECT staged.event_id,
		   online_meetings.id AS meeting_row_id,
		   ROW_NUMBER() OVER (PARTITION BY staged.event_id ORDER BY online_meetings.id) AS pick
	FROM staged
	INNER JOIN online_meetings
		ON online_meetings.meeting_id = staged.meeting_id
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