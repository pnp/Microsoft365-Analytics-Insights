-- Merge staged Copilot Studio audit events into normalised tables.

-- 1. Upsert environments (shared lookup).
INSERT INTO power_app_environments (environment_id, [name])
SELECT DISTINCT i.environment_id, i.environment_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
WHERE i.environment_id IS NOT NULL AND env.environment_id IS NULL;


-- 2. Insert bots we haven't seen before.
INSERT INTO copilot_studio_bots (bot_id, [name], environment_id, first_seen_at)
SELECT d.bot_id, d.bot_name, env.id, d.first_seen_at
FROM (
    SELECT i.bot_id,
           MAX(COALESCE(i.bot_name, i.bot_id)) AS bot_name,
           MAX(i.environment_id) AS environment_id,
           MIN(i.event_time) AS first_seen_at
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.bot_id IS NOT NULL
    GROUP BY i.bot_id
) d
LEFT JOIN power_app_environments env ON env.environment_id = d.environment_id
WHERE NOT EXISTS (SELECT 1 FROM copilot_studio_bots b WHERE b.bot_id = d.bot_id);


-- 3. Backfill bot display-names when friendlier data arrives.
UPDATE b
SET [name] = COALESCE(NULLIF(i.bot_name, b.bot_id), b.[name]),
    environment_id = COALESCE(b.environment_id, env.id)
FROM copilot_studio_bots b
INNER JOIN [${STAGING_TABLE_ACTIVITY}] i ON i.bot_id = b.bot_id
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
WHERE (b.[name] IS NULL OR b.[name] = b.bot_id OR b.environment_id IS NULL);


-- 4. Insert per-event metadata.
INSERT INTO event_meta_copilot_studio (event_id, bot_id)
SELECT i.event_id, b.id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN copilot_studio_bots b ON b.bot_id = i.bot_id
WHERE NOT EXISTS (SELECT 1 FROM event_meta_copilot_studio m WHERE m.event_id = i.event_id);
