-- Merge staged Power Automate (MicrosoftFlow) audit events into normalised tables.

-- 1. Upsert environments
INSERT INTO power_app_environments (environment_id, [name])
SELECT DISTINCT imports.environment_id, imports.environment_id
FROM [${STAGING_TABLE_ACTIVITY}] imports
LEFT JOIN power_app_environments env ON env.environment_id = imports.environment_id
WHERE imports.environment_id IS NOT NULL AND env.environment_id IS NULL;


-- 2. Upsert recurrence types
INSERT INTO flow_recurrence_types ([name])
SELECT DISTINCT imports.recurrence_type
FROM [${STAGING_TABLE_ACTIVITY}] imports
LEFT JOIN flow_recurrence_types rt ON rt.[name] = imports.recurrence_type
WHERE imports.recurrence_type IS NOT NULL AND rt.[name] IS NULL;


-- 3. Insert flows we haven't seen before
INSERT INTO power_automate_flows (flow_id, [name], environment_id)
SELECT distinct_imports.flow_id, distinct_imports.flow_name, env.id
FROM (
    SELECT DISTINCT i.flow_id, i.flow_name, i.environment_id
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.flow_id IS NOT NULL
) distinct_imports
LEFT JOIN power_app_environments env ON env.environment_id = distinct_imports.environment_id
WHERE NOT EXISTS (SELECT 1 FROM power_automate_flows pf WHERE pf.flow_id = distinct_imports.flow_id);


-- 4. Backfill flow display-names if a friendlier one arrives later
UPDATE pf
SET [name] = i.flow_name
FROM power_automate_flows pf
INNER JOIN [${STAGING_TABLE_ACTIVITY}] i ON i.flow_id = pf.flow_id
WHERE i.flow_name IS NOT NULL
  AND (pf.[name] IS NULL OR pf.[name] = pf.flow_id);


-- 5. Insert per-event metadata
INSERT INTO event_meta_power_automate_flow (event_id, flow_id, run_id, recurrence_type_id)
SELECT i.event_id, pf.id, i.run_id, rt.id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_automate_flows pf ON pf.flow_id = i.flow_id
LEFT JOIN flow_recurrence_types rt ON rt.[name] = i.recurrence_type
WHERE NOT EXISTS (SELECT 1 FROM event_meta_power_automate_flow m WHERE m.event_id = i.event_id);
