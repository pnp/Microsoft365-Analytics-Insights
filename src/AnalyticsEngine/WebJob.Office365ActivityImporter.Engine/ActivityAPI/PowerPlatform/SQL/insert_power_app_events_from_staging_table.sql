-- Merge staged Power Apps audit events into normalised tables.
-- ${STAGING_TABLE_ACTIVITY} is replaced at runtime with the temp table name.

-- 1. Upsert environments (lookup shared with flows / dataverse / copilot studio)
--    Dedupe by environment_id; pick the best display name we saw for it this batch
--    (fall back to the GUID when only the legacy schema is in play).
INSERT INTO power_app_environments (environment_id, [name])
SELECT d.environment_id, COALESCE(d.environment_name, d.environment_id)
FROM (
    SELECT i.environment_id,
           MAX(i.environment_name) AS environment_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.environment_id IS NOT NULL
    GROUP BY i.environment_id
) d
WHERE NOT EXISTS (SELECT 1 FROM power_app_environments env WHERE env.environment_id = d.environment_id);


-- 1b. Backfill the environment friendly name if a later event provides one.
UPDATE env
SET [name] = d.environment_name
FROM power_app_environments env
INNER JOIN (
    SELECT i.environment_id,
           MAX(i.environment_name) AS environment_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.environment_id IS NOT NULL AND i.environment_name IS NOT NULL
    GROUP BY i.environment_id
) d ON d.environment_id = env.environment_id
WHERE env.[name] IS NULL OR env.[name] = env.environment_id;


-- 2. Upsert app types (canvas / model-driven / Teams / portal)
INSERT INTO power_app_types ([name])
SELECT DISTINCT i.app_type
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_app_types t ON t.[name] = i.app_type
WHERE i.app_type IS NOT NULL AND t.[name] IS NULL;


-- 3. Upsert client types (Teams / Mobile / Desktop / Web)
INSERT INTO power_platform_client_types ([name])
SELECT DISTINCT i.client_type
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_platform_client_types c ON c.[name] = i.client_type
WHERE i.client_type IS NOT NULL AND c.[name] IS NULL;


-- 4. Insert apps we haven't seen before (set first_seen_at to the earliest staged event)
INSERT INTO power_apps (app_id, [name], environment_id, app_type_id, first_seen_at)
SELECT d.app_id, d.app_name, env.id, t.id, d.first_seen_at
FROM (
    SELECT i.app_id,
           MAX(i.app_name) AS app_name,
           MAX(i.environment_id) AS environment_id,
           MAX(i.app_type) AS app_type,
           MIN(i.event_time) AS first_seen_at
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.app_id IS NOT NULL
    GROUP BY i.app_id
) d
LEFT JOIN power_app_environments env ON env.environment_id = d.environment_id
LEFT JOIN power_app_types t ON t.[name] = d.app_type
WHERE NOT EXISTS (SELECT 1 FROM power_apps pa WHERE pa.app_id = d.app_id);


-- 5. Backfill friendlier app metadata if it arrives later
UPDATE pa
SET [name] = COALESCE(NULLIF(i.app_name, pa.app_id), pa.[name]),
    app_type_id = COALESCE(pa.app_type_id, t.id),
    environment_id = COALESCE(pa.environment_id, env.id)
FROM power_apps pa
INNER JOIN [${STAGING_TABLE_ACTIVITY}] i ON i.app_id = pa.app_id
LEFT JOIN power_app_types t ON t.[name] = i.app_type
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
WHERE (pa.[name] IS NULL OR pa.[name] = pa.app_id OR pa.app_type_id IS NULL OR pa.environment_id IS NULL);


-- 6. Insert per-event metadata
INSERT INTO event_meta_power_app (event_id, power_app_id, app_session_id, client_type_id)
SELECT i.event_id, pa.id, i.app_session_id, ct.id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_apps pa ON pa.app_id = i.app_id
LEFT JOIN power_platform_client_types ct ON ct.[name] = i.client_type
WHERE NOT EXISTS (SELECT 1 FROM event_meta_power_app m WHERE m.event_id = i.event_id);


-- 7. Refresh connector bindings emitted on publish events.
--    connectors_csv is pipe-delimited; split and upsert into the connector lookup + junction.
WITH split_connectors AS (
    SELECT DISTINCT
        i.app_id,
        LTRIM(RTRIM(value)) AS connector_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    CROSS APPLY STRING_SPLIT(i.connectors_csv, '|')
    WHERE i.connectors_csv IS NOT NULL AND LTRIM(RTRIM(value)) <> ''
)
INSERT INTO power_platform_connectors ([name])
SELECT DISTINCT sc.connector_name
FROM split_connectors sc
WHERE NOT EXISTS (SELECT 1 FROM power_platform_connectors c WHERE c.[name] = sc.connector_name);

WITH split_connectors AS (
    SELECT DISTINCT
        i.app_id,
        LTRIM(RTRIM(value)) AS connector_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    CROSS APPLY STRING_SPLIT(i.connectors_csv, '|')
    WHERE i.connectors_csv IS NOT NULL AND LTRIM(RTRIM(value)) <> ''
)
INSERT INTO power_app_connectors (power_app_id, connector_id)
SELECT pa.id, c.id
FROM split_connectors sc
INNER JOIN power_apps pa ON pa.app_id = sc.app_id
INNER JOIN power_platform_connectors c ON c.[name] = sc.connector_name
WHERE NOT EXISTS (
    SELECT 1 FROM power_app_connectors j WHERE j.power_app_id = pa.id AND j.connector_id = c.id
);
