-- Merge staged Dataverse audit events (CreateRecord / UpdateRecord / DeleteRecord) into normalised tables.

-- 1. Upsert environments (shared lookup).
INSERT INTO power_app_environments (environment_id, [name])
SELECT DISTINCT i.environment_id, i.environment_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
WHERE i.environment_id IS NOT NULL AND env.environment_id IS NULL;


-- 2. Upsert Dataverse entity / table names.
INSERT INTO dataverse_entities ([name])
SELECT DISTINCT i.entity_name
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN dataverse_entities e ON e.[name] = i.entity_name
WHERE i.entity_name IS NOT NULL AND e.[name] IS NULL;


-- 3. Insert per-event metadata.
INSERT INTO event_meta_dataverse (event_id, environment_id, entity_id, record_id)
SELECT i.event_id, env.id, e.id, i.record_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_app_environments env ON env.environment_id = i.environment_id
LEFT JOIN dataverse_entities e ON e.[name] = i.entity_name
WHERE NOT EXISTS (SELECT 1 FROM event_meta_dataverse m WHERE m.event_id = i.event_id);
