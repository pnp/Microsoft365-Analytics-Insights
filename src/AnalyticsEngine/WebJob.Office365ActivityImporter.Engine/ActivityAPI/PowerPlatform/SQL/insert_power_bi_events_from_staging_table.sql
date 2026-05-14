-- Merge staged Power BI audit events into normalised tables (workspaces, reports, dashboards, per-event metadata).

-- 1. Upsert workspaces. Dedupe by workspace_id (a single batch can carry the same
--    workspace_id with different display-names; we pick one to satisfy the unique index).
INSERT INTO power_bi_workspaces (workspace_id, [name])
SELECT d.workspace_id, d.workspace_name
FROM (
    SELECT i.workspace_id,
           MAX(COALESCE(i.workspace_name, i.workspace_id)) AS workspace_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.workspace_id IS NOT NULL
    GROUP BY i.workspace_id
) d
WHERE NOT EXISTS (SELECT 1 FROM power_bi_workspaces w WHERE w.workspace_id = d.workspace_id);


-- 2. Upsert reports (with earliest first_seen_at).
INSERT INTO power_bi_reports (report_id, [name], report_type, workspace_id, first_seen_at)
SELECT d.report_id, d.report_name, d.report_type, w.id, d.first_seen_at
FROM (
    SELECT i.report_id,
           MAX(COALESCE(i.report_name, i.report_id)) AS report_name,
           MAX(i.report_type) AS report_type,
           MAX(i.workspace_id) AS workspace_id,
           MIN(i.event_time) AS first_seen_at
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.report_id IS NOT NULL
    GROUP BY i.report_id
) d
LEFT JOIN power_bi_workspaces w ON w.workspace_id = d.workspace_id
WHERE NOT EXISTS (SELECT 1 FROM power_bi_reports r WHERE r.report_id = d.report_id);


-- 3. Backfill report metadata when friendlier data arrives.
UPDATE r
SET [name] = COALESCE(NULLIF(i.report_name, r.report_id), r.[name]),
    report_type = COALESCE(r.report_type, i.report_type),
    workspace_id = COALESCE(r.workspace_id, w.id)
FROM power_bi_reports r
INNER JOIN [${STAGING_TABLE_ACTIVITY}] i ON i.report_id = r.report_id
LEFT JOIN power_bi_workspaces w ON w.workspace_id = i.workspace_id
WHERE (r.[name] IS NULL OR r.[name] = r.report_id OR r.report_type IS NULL OR r.workspace_id IS NULL);


-- 4. Upsert dashboards.
INSERT INTO power_bi_dashboards (dashboard_id, [name], workspace_id, first_seen_at)
SELECT d.dashboard_id, d.dashboard_name, w.id, d.first_seen_at
FROM (
    SELECT i.dashboard_id,
           MAX(COALESCE(i.dashboard_name, i.dashboard_id)) AS dashboard_name,
           MAX(i.workspace_id) AS workspace_id,
           MIN(i.event_time) AS first_seen_at
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.dashboard_id IS NOT NULL
    GROUP BY i.dashboard_id
) d
LEFT JOIN power_bi_workspaces w ON w.workspace_id = d.workspace_id
WHERE NOT EXISTS (SELECT 1 FROM power_bi_dashboards dash WHERE dash.dashboard_id = d.dashboard_id);


-- 5. Insert per-event metadata.
INSERT INTO event_meta_power_bi (event_id, workspace_id, report_id, dashboard_id)
SELECT i.event_id, w.id, r.id, dash.id
FROM [${STAGING_TABLE_ACTIVITY}] i
LEFT JOIN power_bi_workspaces w ON w.workspace_id = i.workspace_id
LEFT JOIN power_bi_reports r ON r.report_id = i.report_id
LEFT JOIN power_bi_dashboards dash ON dash.dashboard_id = i.dashboard_id
WHERE NOT EXISTS (SELECT 1 FROM event_meta_power_bi m WHERE m.event_id = i.event_id);
