-- Keep resources unique to either constituent of a counted turn, attributed to that turn.
-- The full null-safe tuple matches CopilotTurnSql.ResourceCountedOnce, including action/list item.
SELECT r.id, COALESCE(d.counted_event_id, c.event_id) AS copilot_chat_id,
       r.resource_id_id, r.resource_name_id, r.resource_site_url_id,
       r.resource_type_id, r.sensitivity_label_id
FROM dbo.copilot_event_accessed_resources AS r
JOIN dbo.copilot_chats AS c ON c.event_id = r.copilot_chat_id
LEFT JOIN dbo.copilot_chat_duplicates AS d ON d.time_stamp = c.time_stamp AND d.event_id = c.event_id
WHERE (c.app_host IS NULL OR c.app_host <> N'Copilot Studio')
  AND (d.event_id IS NULL OR NOT EXISTS (
      SELECT 1
      FROM (SELECT d.counted_event_id AS event_id
            UNION ALL
            SELECT earlier.event_id FROM dbo.copilot_chat_duplicates AS earlier
            WHERE earlier.counted_event_id = d.counted_event_id AND earlier.event_id < d.event_id) AS turn_record
      JOIN dbo.copilot_event_accessed_resources AS tr ON tr.copilot_chat_id = turn_record.event_id
      WHERE EXISTS (SELECT tr.resource_id_id, tr.resource_name_id, tr.resource_site_url_id,
                           tr.resource_type_id, tr.sensitivity_label_id, tr.action_id, tr.list_item_unique_id_id
                    INTERSECT
                    SELECT r.resource_id_id, r.resource_name_id, r.resource_site_url_id,
                           r.resource_type_id, r.sensitivity_label_id, r.action_id, r.list_item_unique_id_id)))
