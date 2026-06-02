-- Merge staged Power Automate flow share / permission-grant events.

-- 1. Make sure recipients exist as users.
INSERT INTO users (user_name)
SELECT DISTINCT i.shared_with_upn
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN users u ON u.user_name = i.shared_with_upn
WHERE i.shared_with_upn IS NOT NULL AND u.user_name IS NULL;


-- 2. Make sure the flow exists (placeholder if new).
INSERT INTO power_automate_flows (flow_id, [name])
SELECT DISTINCT i.flow_id, i.flow_id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_automate_flows pf ON pf.flow_id = i.flow_id
WHERE i.flow_id IS NOT NULL AND pf.flow_id IS NULL;


-- 3. Insert one share row per (event, recipient). Idempotent via unique index.
INSERT INTO event_meta_power_automate_flow_share (event_id, flow_id, shared_with_user_id, role_name)
SELECT i.event_id, pf.id, u.id, i.role_name
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_automate_flows pf ON pf.flow_id = i.flow_id
LEFT JOIN users u ON u.user_name = i.shared_with_upn
WHERE u.id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM event_meta_power_automate_flow_share s
      WHERE s.event_id = i.event_id AND s.shared_with_user_id = u.id
  );
