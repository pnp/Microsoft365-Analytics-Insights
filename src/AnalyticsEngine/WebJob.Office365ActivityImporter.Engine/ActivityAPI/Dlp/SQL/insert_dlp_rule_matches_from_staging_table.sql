-- Merge staged DLP rule matches from the standalone DLP.All activity feed into normalised tables.
--
-- These records cover Exchange Online, SharePoint/OneDrive and Endpoint DLP. They carry the human
-- UserId and NO agent identity, so they answer "which policies are firing across the tenant and who is
-- hitting them" - not "which Copilot agent was blocked". The agent-attributable source is
-- copilot_dlp_events. The two are deliberately never joined on user+time to guess an agent.
-- https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#dlp-schema
--
-- Shares the dlp_policies / dlp_rules / dlp_actions dimensions with the Copilot-embedded source so the
-- same policy is one row however it was observed. is_blocked arrives already decided by CopilotDlpRules,
-- the same classifier both sources use.
IF OBJECT_ID('dbo.dlp_rule_matches', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.dlp_policies', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.dlp_rules', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.dlp_actions', 'U') IS NOT NULL
BEGIN

-- 1. Upsert policies.
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


-- 4. Keep rule metadata current (an admin can switch a rule between "Audit only" and "Enforce").
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


-- 5. Upsert the action lookup.
INSERT INTO dlp_actions ([name])
SELECT DISTINCT LEFT(i.action_name, 100)
FROM [${STAGING_TABLE_ACTIVITY}] i
WHERE i.action_name IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dlp_actions a WHERE a.[name] = LEFT(i.action_name, 100));


-- 6. Insert the match rows, de-duplicated on the full tuple and keyed on event_id so a re-imported
--    look-back window does not accumulate duplicates. INNER JOIN audit_events for the same reason as
--    the Copilot script: satisfy the foreign key rather than fail the batch if the base event was
--    filtered out upstream.
;WITH staged AS (
    SELECT DISTINCT
        i.event_id,
        p.id AS dlp_policy_id,
        r.id AS dlp_rule_id,
        a.id AS dlp_action_id,
        i.is_blocked
    FROM [${STAGING_TABLE_ACTIVITY}] i
    INNER JOIN audit_events e ON e.id = i.event_id
    LEFT JOIN dlp_policies p ON p.policy_id = LEFT(i.policy_id, 200)
    LEFT JOIN dlp_rules r ON r.rule_id = LEFT(i.rule_id, 200)
    LEFT JOIN dlp_actions a ON a.[name] = LEFT(i.action_name, 100)
)
INSERT INTO dlp_rule_matches (event_id, dlp_policy_id, dlp_rule_id, dlp_action_id, is_blocked)
SELECT s.event_id, s.dlp_policy_id, s.dlp_rule_id, s.dlp_action_id, s.is_blocked
FROM staged s
WHERE NOT EXISTS (
    SELECT 1
    FROM dlp_rule_matches x
    WHERE x.event_id = s.event_id
      AND EXISTS (
          SELECT x.dlp_policy_id, x.dlp_rule_id, x.dlp_action_id, x.is_blocked
          INTERSECT
          SELECT s.dlp_policy_id, s.dlp_rule_id, s.dlp_action_id, s.is_blocked
      )
);

END
