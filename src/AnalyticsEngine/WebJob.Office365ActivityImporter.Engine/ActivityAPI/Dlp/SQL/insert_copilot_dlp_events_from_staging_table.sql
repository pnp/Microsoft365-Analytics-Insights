-- Merge staged Copilot DLP policy matches into normalised tables.
--
-- Source: the PolicyDetails / Status carried on AccessedResources inside a CopilotInteraction audit
-- record. This is the ONLY place a DLP block can be attributed to a specific Copilot agent - DLP
-- policies scoped to the "Microsoft 365 Copilot and Copilot Chat" location do not emit standalone
-- DlpRuleMatch records on the DLP.All feed, and those that do emit carry no agent identity.
-- https://learn.microsoft.com/en-us/purview/audit-copilot
--
-- is_blocked arrives already decided (see CopilotDlpRules / CopilotDlpLogTempEntity). This script
-- deliberately contains NO classification logic, so there is exactly one implementation of
-- "what counts as a block" and it is the unit-tested C# one.
--
-- Guarded on table existence so the importer keeps working against a database that has not yet had the
-- DLP migration applied - the staged rows are simply discarded that cycle, and the rolling look-back
-- window re-imports them once the schema is upgraded.
IF OBJECT_ID('dbo.copilot_dlp_events', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.dlp_policies', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.dlp_rules', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.dlp_actions', 'U') IS NOT NULL
BEGIN

-- 1. Upsert policies. LEFT() to the column widths so an unexpectedly long name truncates rather than
--    failing the whole batch: losing the tail of a policy name is recoverable, losing the import is not.
INSERT INTO dlp_policies (policy_id, [name])
SELECT d.policy_id, d.policy_name
FROM (
    SELECT LEFT(i.policy_id, 200) AS policy_id,
           MAX(LEFT(i.policy_name, 400)) AS policy_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.policy_id IS NOT NULL
    GROUP BY LEFT(i.policy_id, 200)
) d
WHERE NOT EXISTS (SELECT 1 FROM dlp_policies p WHERE p.policy_id = d.policy_id);


-- 2. Backfill a policy display-name that arrived empty the first time we saw the policy.
UPDATE p
SET [name] = d.policy_name
FROM dlp_policies p
INNER JOIN (
    SELECT LEFT(i.policy_id, 200) AS policy_id,
           MAX(LEFT(i.policy_name, 400)) AS policy_name
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.policy_id IS NOT NULL AND i.policy_name IS NOT NULL
    GROUP BY LEFT(i.policy_id, 200)
) d ON d.policy_id = p.policy_id
WHERE p.[name] IS NULL;


-- 3. Upsert rules, linked to their policy.
INSERT INTO dlp_rules (rule_id, [name], dlp_policy_id, severity, rule_mode)
SELECT d.rule_id, d.rule_name, p.id, d.severity, d.rule_mode
FROM (
    SELECT LEFT(i.rule_id, 200) AS rule_id,
           MAX(LEFT(i.rule_name, 400)) AS rule_name,
           MAX(LEFT(i.policy_id, 200)) AS policy_id,
           MAX(LEFT(i.severity, 50)) AS severity,
           MAX(LEFT(i.rule_mode, 50)) AS rule_mode
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.rule_id IS NOT NULL
    GROUP BY LEFT(i.rule_id, 200)
) d
LEFT JOIN dlp_policies p ON p.policy_id = d.policy_id
WHERE NOT EXISTS (SELECT 1 FROM dlp_rules r WHERE r.rule_id = d.rule_id);


-- 4. Keep rule metadata current. An admin can retune a rule - most importantly switching it between
--    "Audit only" and "Enforce" - and the newest observation is the truthful one.
UPDATE r
SET [name] = COALESCE(d.rule_name, r.[name]),
    severity = COALESCE(d.severity, r.severity),
    rule_mode = COALESCE(d.rule_mode, r.rule_mode),
    dlp_policy_id = COALESCE(r.dlp_policy_id, p.id)
FROM dlp_rules r
INNER JOIN (
    SELECT LEFT(i.rule_id, 200) AS rule_id,
           MAX(LEFT(i.rule_name, 400)) AS rule_name,
           MAX(LEFT(i.policy_id, 200)) AS policy_id,
           MAX(LEFT(i.severity, 50)) AS severity,
           MAX(LEFT(i.rule_mode, 50)) AS rule_mode
    FROM [${STAGING_TABLE_ACTIVITY}] i
    WHERE i.rule_id IS NOT NULL
    GROUP BY LEFT(i.rule_id, 200)
) d ON d.rule_id = r.rule_id
LEFT JOIN dlp_policies p ON p.policy_id = d.policy_id;


-- 5. Upsert the small action lookup ("BlockAccess", "NotifyUser", ...).
INSERT INTO dlp_actions ([name])
SELECT DISTINCT LEFT(i.action_name, 100)
FROM [${STAGING_TABLE_ACTIVITY}] i
WHERE i.action_name IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dlp_actions a WHERE a.[name] = LEFT(i.action_name, 100));


-- 6. Insert the match rows.
--
--    INNER JOIN copilot_chats: the FK requires the interaction to exist. It normally does - this script
--    runs after the shared Copilot merge that creates the chat rows from the same batch - but joining
--    rather than assuming means a chat that was filtered out upstream (out-of-scope user, say) drops its
--    DLP rows quietly instead of failing the batch on a foreign-key violation.
--
--    The resource dimensions are LEFT JOINed, never inserted here: the same accessed resources were
--    already dimensioned by the shared Copilot merge for this batch, so re-inserting them would be a
--    second, divergent implementation of that upsert.
--
--    De-duplicated on the full tuple with a NULL-safe INTERSECT, keyed on copilot_chat_id so the check
--    seeks the handful of rows for one interaction rather than scanning the table. The importer re-reads
--    a rolling look-back window, so the same interaction comes back through this merge on later cycles
--    and MUST NOT accumulate duplicate rows - every count on this table is a COUNT of these rows.
;WITH staged AS (
    SELECT DISTINCT
        i.event_id,
        p.id AS dlp_policy_id,
        r.id AS dlp_rule_id,
        a.id AS dlp_action_id,
        rname.id AS resource_name_id,
        rtype.id AS resource_type_id,
        slabel.id AS sensitivity_label_id,
        i.is_blocked
    FROM [${STAGING_TABLE_ACTIVITY}] i
    INNER JOIN copilot_chats c ON c.event_id = i.event_id
    LEFT JOIN dlp_policies p ON p.policy_id = LEFT(i.policy_id, 200)
    LEFT JOIN dlp_rules r ON r.rule_id = LEFT(i.rule_id, 200)
    LEFT JOIN dlp_actions a ON a.[name] = LEFT(i.action_name, 100)
    LEFT JOIN copilot_event_accessed_resource_names rname ON rname.[name] = i.resource_name
    LEFT JOIN copilot_event_accessed_resource_types rtype ON rtype.[name] = i.resource_type
    LEFT JOIN sensitivity_labels slabel ON slabel.label_id = i.sensitivity_label_id
)
INSERT INTO copilot_dlp_events
    (copilot_chat_id, dlp_policy_id, dlp_rule_id, dlp_action_id,
     resource_name_id, resource_type_id, sensitivity_label_id, is_blocked)
SELECT s.event_id, s.dlp_policy_id, s.dlp_rule_id, s.dlp_action_id,
       s.resource_name_id, s.resource_type_id, s.sensitivity_label_id, s.is_blocked
FROM staged s
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_dlp_events x
    WHERE x.copilot_chat_id = s.event_id
      AND EXISTS (
          SELECT x.dlp_policy_id, x.dlp_rule_id, x.dlp_action_id,
                 x.resource_name_id, x.resource_type_id, x.sensitivity_label_id, x.is_blocked
          INTERSECT
          SELECT s.dlp_policy_id, s.dlp_rule_id, s.dlp_action_id,
                 s.resource_name_id, s.resource_type_id, s.sensitivity_label_id, s.is_blocked
      )
);

END
