-- Merge staged Power Apps audit events into normalised tables.
-- ${STAGING_TABLE_ACTIVITY} is replaced at runtime with the temp table name.

-- 1. Upsert environments (lookup is shared with flows)
INSERT INTO power_app_environments (environment_id, [name])
SELECT DISTINCT imports.environment_id, imports.environment_id
FROM [${STAGING_TABLE_ACTIVITY}] imports
LEFT JOIN power_app_environments env ON env.environment_id = imports.environment_id
WHERE imports.environment_id IS NOT NULL AND env.environment_id IS NULL;


-- 2. Insert apps we haven't seen before
INSERT INTO power_apps (app_id, [name], environment_id)
SELECT distinct_imports.app_id, distinct_imports.app_name, env.id
FROM (
    SELECT DISTINCT i.app_id, i.app_name, i.environment_id
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.app_id IS NOT NULL
) distinct_imports
LEFT JOIN power_app_environments env ON env.environment_id = distinct_imports.environment_id
WHERE NOT EXISTS (SELECT 1 FROM power_apps pa WHERE pa.app_id = distinct_imports.app_id);


-- 3. Backfill display-names on apps if a friendlier one has arrived
UPDATE pa
SET [name] = i.app_name
FROM power_apps pa
INNER JOIN [${STAGING_TABLE_ACTIVITY}] i ON i.app_id = pa.app_id
WHERE i.app_name IS NOT NULL
  AND (pa.[name] IS NULL OR pa.[name] = pa.app_id);


-- 4. Insert per-event metadata
INSERT INTO event_meta_power_app (event_id, power_app_id, app_session_id)
SELECT i.event_id, pa.id, i.app_session_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_apps pa ON pa.app_id = i.app_id
WHERE NOT EXISTS (SELECT 1 FROM event_meta_power_app m WHERE m.event_id = i.event_id);
