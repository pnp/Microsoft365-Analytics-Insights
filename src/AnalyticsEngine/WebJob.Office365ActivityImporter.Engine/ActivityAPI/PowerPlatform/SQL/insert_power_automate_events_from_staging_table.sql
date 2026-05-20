-- Merge staged Power Automate (MicrosoftFlow) audit events into normalised tables.

-- 1. Upsert environments (shared lookup).
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


-- 2. Insert flows we haven't seen before (with earliest first_seen_at).
INSERT INTO power_automate_flows (flow_id, [name], environment_id, first_seen_at)
SELECT d.flow_id, d.flow_name, env.id, d.first_seen_at
FROM (
    SELECT i.flow_id,
           MAX(i.flow_name) AS flow_name,
           MAX(i.environment_id) AS environment_id,
           MIN(i.event_time) AS first_seen_at
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.flow_id IS NOT NULL
    GROUP BY i.flow_id
) d
LEFT JOIN power_app_environments env ON env.environment_id = d.environment_id
WHERE NOT EXISTS (SELECT 1 FROM power_automate_flows pf WHERE pf.flow_id = d.flow_id);


-- 3. Backfill flow display-names + environment if friendlier data arrives later.
UPDATE pf
SET [name] = COALESCE(NULLIF(i.flow_name, pf.flow_id), pf.[name]),
    environment_id = COALESCE(pf.environment_id, env.id)
FROM power_automate_flows pf
INNER JOIN [${STAGING_TABLE_ACTIVITY}] i ON i.flow_id = pf.flow_id
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
WHERE (pf.[name] IS NULL OR pf.[name] = pf.flow_id OR pf.environment_id IS NULL);


-- 4. Insert per-event metadata.
INSERT INTO event_meta_power_automate_flow (event_id, flow_id, run_id)
SELECT i.event_id, pf.id, i.run_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_automate_flows pf ON pf.flow_id = i.flow_id
WHERE NOT EXISTS (SELECT 1 FROM event_meta_power_automate_flow m WHERE m.event_id = i.event_id);


-- 5. Refresh connector bindings (pipe-delimited connectors_csv from save / publish events).
WITH split_connectors AS (
    SELECT DISTINCT
        i.flow_id,
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
        i.flow_id,
        LTRIM(RTRIM(value)) AS connector_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    CROSS APPLY STRING_SPLIT(i.connectors_csv, '|')
    WHERE i.connectors_csv IS NOT NULL AND LTRIM(RTRIM(value)) <> ''
)
INSERT INTO power_automate_flow_connectors (flow_id, connector_id)
SELECT pf.id, c.id
FROM split_connectors sc
INNER JOIN power_automate_flows pf ON pf.flow_id = sc.flow_id
INNER JOIN power_platform_connectors c ON c.[name] = sc.connector_name
WHERE NOT EXISTS (
    SELECT 1 FROM power_automate_flow_connectors j WHERE j.flow_id = pf.id AND j.connector_id = c.id
);
