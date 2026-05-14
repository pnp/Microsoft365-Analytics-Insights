-- Merge staged Power Platform admin audit events into normalised tables.

-- 1. Upsert environments (shared lookup)
INSERT INTO power_app_environments (environment_id, [name])
SELECT DISTINCT imports.environment_id, imports.environment_id
FROM [${STAGING_TABLE_ACTIVITY}] imports
LEFT JOIN power_app_environments env ON env.environment_id = imports.environment_id
WHERE imports.environment_id IS NOT NULL AND env.environment_id IS NULL;


-- 2. Insert per-event metadata
INSERT INTO event_meta_power_platform_admin (event_id, environment_id, [json])
SELECT i.event_id, env.id, i.event_json
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
WHERE NOT EXISTS (SELECT 1 FROM event_meta_power_platform_admin m WHERE m.event_id = i.event_id);
