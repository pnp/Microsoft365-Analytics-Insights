-- Merge staged Power Apps share / permission-grant events into normalised tables.
-- One row per (event, recipient). Apps + recipients are upserted first.

-- 1. Make sure recipients exist as users.
INSERT INTO users (user_name)
SELECT DISTINCT i.shared_with_upn
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN users u ON u.user_name = i.shared_with_upn
WHERE i.shared_with_upn IS NOT NULL AND u.user_name IS NULL;


-- 2. Make sure the app exists (minimal placeholder if we've never seen it before).
INSERT INTO power_apps (app_id, [name])
SELECT DISTINCT i.app_id, i.app_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_apps pa ON pa.app_id = i.app_id
WHERE i.app_id IS NOT NULL AND pa.app_id IS NULL;


-- 3. Insert one share row per (event, recipient). The unique index on
--    (event_id, shared_with_user_id) means re-imports are idempotent.
INSERT INTO event_meta_power_app_share (event_id, power_app_id, shared_with_user_id, role_name)
SELECT i.event_id, pa.id, u.id, i.role_name
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_apps pa ON pa.app_id = i.app_id
LEFT JOIN users u ON u.user_name = i.shared_with_upn
WHERE u.id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM event_meta_power_app_share s
      WHERE s.event_id = i.event_id AND s.shared_with_user_id = u.id
  );
