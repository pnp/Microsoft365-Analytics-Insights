using Common.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Dlp
{
    /// <summary>
    /// Generates fake Microsoft Purview Data Loss Prevention activity so the "DLP impact on Copilot"
    /// report has something to show.
    ///
    /// Attaches policy matches to Copilot interactions that ALREADY exist in the database, rather than
    /// inventing its own interactions. That is deliberate: the report's whole value is per-agent and
    /// per-user attribution, so the generated blocks have to hang off the same agents and users the
    /// Copilot reports show, or the two pages tell contradictory stories.
    /// </summary>
    /// <remarks>
    /// Produces both DLP sources so the page can be exercised end to end:
    /// <list type="bullet">
    /// <item><c>copilot_dlp_events</c> - policy detail carried inside a Copilot interaction record.
    /// This is the only source that can name the agent.</item>
    /// <item><c>dlp_rule_matches</c> - the tenant-wide DLP.All feed, which has no agent identity.</item>
    /// </list>
    /// The blocked/audited split is not random noise: a realistic tenant has a mixture of enforcing
    /// rules and rules still in simulation, and the report's central distinction is exactly that. One
    /// seeded policy is deliberately left in "Audit only" mode so the page always demonstrates a
    /// matching-but-not-blocking policy.
    /// </remarks>
    public class DlpActivityGenerator
    {
        private readonly string _connectionString;
        private readonly Random _random;

        public DlpActivityGenerator(string connectionString, int? seed = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }

            _connectionString = connectionString;
            _random = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        /// <summary>
        /// Synthetic DLP policies. Names are obviously fictional and describe what an admin would
        /// recognise: label-based Copilot restrictions, a sensitive-information-type rule, and one
        /// policy still being trialled in simulation.
        /// </summary>
        internal static readonly DemoPolicy[] Policies =
        {
            new DemoPolicy("Contoso - Copilot: Confidential content", "Confidential label", "High", "Enforce", "BlockAccess"),
            new DemoPolicy("Contoso - Copilot: Payment card data", "Credit card number", "High", "Enforce", "BlockAccess"),
            new DemoPolicy("Contoso - Copilot: HR records", "Employee record", "Medium", "Enforce", "BlockAccess"),
            // Left in simulation on purpose: proves the report separates "matched" from "blocked".
            new DemoPolicy("Contoso - Copilot: Draft policy (simulation)", "Project codename", "Low", "Audit only", "NotifyUser"),
        };

        /// <summary>Synthetic sensitivity labels the affected content carries.</summary>
        internal static readonly string[] LabelIds =
        {
            "00000000-0000-0000-0000-00000000c001",
            "00000000-0000-0000-0000-00000000c002",
        };

        /// <summary>Stable synthetic policy id for the Nth seeded policy.</summary>
        internal static string PolicyIdFor(int index) => $"00000000-0000-0000-0000-0000000000{(0xd0 + index):x2}";

        /// <summary>Stable synthetic rule id for the Nth seeded policy.</summary>
        internal static string RuleIdFor(int index) => $"00000000-0000-0000-0000-0000000000{(0xe0 + index):x2}";

        /// <summary>
        /// Mirrors the importer's classification (<c>CopilotDlpRules</c>): only an enforcing rule taking
        /// a blocking action actually denies content. Kept in step so demo data says what the importer
        /// would have said for the same payload.
        /// </summary>
        internal static bool IsBlocking(DemoPolicy policy) =>
            string.Equals(policy.RuleMode, "Enforce", StringComparison.OrdinalIgnoreCase)
            && string.Equals(policy.ActionName, "BlockAccess", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Attaches DLP activity to existing data.
        /// </summary>
        /// <param name="copilotAffectedPercent">Percentage of existing Copilot interactions to attach a policy match to (0-100).</param>
        /// <param name="tenantMatchCount">Number of tenant-wide DLP.All rule matches to attach to existing audit events.</param>
        public void GenerateDlpActivity(int copilotAffectedPercent = 8, int tenantMatchCount = 500)
        {
            if (copilotAffectedPercent < 0 || copilotAffectedPercent > 100) throw new ArgumentOutOfRangeException(nameof(copilotAffectedPercent));
            if (tenantMatchCount < 0) throw new ArgumentOutOfRangeException(nameof(tenantMatchCount));

            Console.WriteLine("Generating fake DLP policy activity...");
            Console.WriteLine($"- {copilotAffectedPercent}% of existing Copilot interactions will be affected by a policy");
            Console.WriteLine($"- up to {tenantMatchCount} tenant-wide (DLP.All) rule match(es)");
            Console.WriteLine();

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var seeded = EnsureDimensions(db);

                int copilotRows = GenerateCopilotPolicyMatches(db, seeded, copilotAffectedPercent);
                int tenantRows = GenerateTenantRuleMatches(db, seeded, tenantMatchCount);

                Console.WriteLine();
                Console.WriteLine($"Done. {copilotRows} Copilot policy match(es) and {tenantRows} tenant-wide rule match(es) written.");
                if (copilotRows == 0 && copilotAffectedPercent > 0)
                {
                    Console.WriteLine("No un-affected Copilot interactions were found - generate Copilot activity first, "
                        + "or the existing interactions already carry policy matches.");
                }
            }
        }

        /// <summary>Creates the shared policy / rule / action / label dimensions if they aren't there.</summary>
        private SeededDimensions EnsureDimensions(AnalyticsEntitiesContext db)
        {
            var seeded = new SeededDimensions();

            foreach (var actionName in Policies.Select(p => p.ActionName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var action = db.dlp_actions.FirstOrDefault(a => a.Name == actionName);
                if (action == null)
                {
                    action = new DlpAction { Name = actionName };
                    db.dlp_actions.Add(action);
                }
                seeded.Actions[actionName] = action;
            }
            db.SaveChanges();

            for (int i = 0; i < Policies.Length; i++)
            {
                var definition = Policies[i];
                string policyId = PolicyIdFor(i);
                string ruleId = RuleIdFor(i);

                var policy = db.dlp_policies.FirstOrDefault(p => p.PolicyId == policyId);
                if (policy == null)
                {
                    policy = new DlpPolicy { PolicyId = policyId, Name = definition.PolicyName };
                    db.dlp_policies.Add(policy);
                    db.SaveChanges();
                }

                var rule = db.dlp_rules.FirstOrDefault(r => r.RuleId == ruleId);
                if (rule == null)
                {
                    rule = new DlpRule
                    {
                        RuleId = ruleId,
                        Name = definition.RuleName,
                        DlpPolicyId = policy.ID,
                        Severity = definition.Severity,
                        RuleMode = definition.RuleMode,
                    };
                    db.dlp_rules.Add(rule);
                    db.SaveChanges();
                }

                seeded.Matches.Add(new SeededMatch
                {
                    Policy = policy,
                    Rule = rule,
                    Action = seeded.Actions[definition.ActionName],
                    IsBlocked = IsBlocking(definition),
                });
            }

            foreach (var labelId in LabelIds)
            {
                var label = db.SensitivityLabels.FirstOrDefault(l => l.LabelId == labelId);
                if (label == null)
                {
                    label = new SensitivityLabel { LabelId = labelId };
                    db.SensitivityLabels.Add(label);
                }
                seeded.Labels.Add(label);
            }
            db.SaveChanges();

            return seeded;
        }

        private int GenerateCopilotPolicyMatches(AnalyticsEntitiesContext db, SeededDimensions seeded, int affectedPercent)
        {
            if (affectedPercent == 0) return 0;

            // Only interactions that don't already carry a match, so a repeat run tops the data up
            // instead of doubling every count on the report.
            var chatIds = db.CopilotChats
                .Where(c => !db.copilot_dlp_events.Any(e => e.ChatId == c.EventID))
                .Select(c => c.EventID)
                .ToList();

            if (chatIds.Count == 0)
            {
                return 0;
            }

            var resourceNames = db.CopilotAccessedResourceNames.OrderBy(n => n.ID).Take(20).ToList();
            var resourceTypes = db.CopilotAccessedResourceTypes.OrderBy(t => t.ID).Take(20).ToList();

            int written = 0;
            foreach (var chatId in chatIds)
            {
                if (_random.Next(100) >= affectedPercent) continue;

                var match = seeded.Matches[_random.Next(seeded.Matches.Count)];

                db.copilot_dlp_events.Add(new CopilotDlpEvent
                {
                    ChatId = chatId,
                    DlpPolicyId = match.Policy.ID,
                    DlpRuleId = match.Rule.ID,
                    DlpActionId = match.Action.ID,
                    ResourceNameId = Pick(resourceNames)?.ID,
                    ResourceTypeId = Pick(resourceTypes)?.ID,
                    SensitivityLabelId = seeded.Labels[_random.Next(seeded.Labels.Count)].ID,
                    IsBlocked = match.IsBlocked,
                });

                written++;
                if (written % 500 == 0)
                {
                    db.SaveChanges();
                    Console.WriteLine($"  ...{written} Copilot policy match(es) written");
                }
            }

            db.SaveChanges();
            return written;
        }

        private int GenerateTenantRuleMatches(AnalyticsEntitiesContext db, SeededDimensions seeded, int count)
        {
            if (count == 0) return 0;

            var eventIds = db.AuditEventsCommon
                .Where(e => !db.dlp_rule_matches.Any(m => m.EventId == e.Id))
                .OrderByDescending(e => e.TimeStamp)
                .Select(e => e.Id)
                .Take(count)
                .ToList();

            int written = 0;
            foreach (var eventId in eventIds)
            {
                var match = seeded.Matches[_random.Next(seeded.Matches.Count)];

                db.dlp_rule_matches.Add(new DlpRuleMatch
                {
                    EventId = eventId,
                    DlpPolicyId = match.Policy.ID,
                    DlpRuleId = match.Rule.ID,
                    DlpActionId = match.Action.ID,
                    IsBlocked = match.IsBlocked,
                });

                written++;
                if (written % 500 == 0)
                {
                    db.SaveChanges();
                    Console.WriteLine($"  ...{written} tenant-wide rule match(es) written");
                }
            }

            db.SaveChanges();
            return written;
        }

        private T Pick<T>(IReadOnlyList<T> items) where T : class
        {
            return items == null || items.Count == 0 ? null : items[_random.Next(items.Count)];
        }

        internal sealed class DemoPolicy
        {
            public string PolicyName { get; }
            public string RuleName { get; }
            public string Severity { get; }
            public string RuleMode { get; }
            public string ActionName { get; }

            public DemoPolicy(string policyName, string ruleName, string severity, string ruleMode, string actionName)
            {
                PolicyName = policyName;
                RuleName = ruleName;
                Severity = severity;
                RuleMode = ruleMode;
                ActionName = actionName;
            }
        }

        private sealed class SeededMatch
        {
            public DlpPolicy Policy { get; set; }
            public DlpRule Rule { get; set; }
            public DlpAction Action { get; set; }
            public bool IsBlocked { get; set; }
        }

        private sealed class SeededDimensions
        {
            public Dictionary<string, DlpAction> Actions { get; } = new Dictionary<string, DlpAction>(StringComparer.OrdinalIgnoreCase);
            public List<SeededMatch> Matches { get; } = new List<SeededMatch>();
            public List<SensitivityLabel> Labels { get; } = new List<SensitivityLabel>();
        }
    }
}
