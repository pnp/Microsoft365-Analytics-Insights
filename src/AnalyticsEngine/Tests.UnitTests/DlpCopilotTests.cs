using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Common.Entities;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for Data Loss Prevention reporting.
    ///
    /// The load-bearing claim of this feature is that a DLP policy which blocks Microsoft 365 Copilot
    /// reports itself INSIDE the CopilotInteraction record (AccessedResources[].Status / PolicyDetails),
    /// not on the DLP.All feed - so the Copilot-embedded path is the only one that can name the agent.
    /// These tests pin the parsing and the block/audit classification that path depends on.
    /// </summary>
    [TestClass]
    public class DlpCopilotTests
    {
        private static ILogger Logger => new LoggerFactory().CreateLogger("DlpCopilotTests");

        #region Parsing the Copilot-embedded DLP fields

        /// <summary>
        /// The DLP fields on AccessedResources are documented in prose on the audit-copilot page and are
        /// absent from the published OData schema, so this pins the exact JSON property names the
        /// importer depends on. Getting one wrong loses every block silently rather than loudly.
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_ParsesAccessedResourceStatusAndPolicyDetails()
        {
            var json = @"{
                ""Workload"": ""Copilot"",
                ""Operation"": ""CopilotInteraction"",
                ""RecordType"": 261,
                ""AgentId"": ""CopilotStudio.Declarative.00000000-0000-0000-0000-000000000001"",
                ""AgentName"": ""Contoso HR Agent"",
                ""CopilotEventData"": {
                    ""AppHost"": ""BizChat"",
                    ""DLPEvaluationDeferred"": 5,
                    ""DLPEvaluationDeferredReason"": ""Timeout"",
                    ""AccessedResources"": [{
                        ""Action"": ""Read"",
                        ""Id"": ""00000000-0000-0000-0000-000000000002"",
                        ""Name"": ""Καλημέρα κόσμε.docx"",
                        ""Type"": ""docx"",
                        ""SensitivityLabelId"": ""00000000-0000-0000-0000-000000000003"",
                        ""SiteUrl"": ""https://contoso.sharepoint.com/sites/hr"",
                        ""Status"": ""failure"",
                        ""XPIADetected"": false,
                        ""PolicyDetails"": [{
                            ""PolicyId"": ""00000000-0000-0000-0000-000000000004"",
                            ""PolicyName"": ""Block Copilot on Confidential"",
                            ""Rules"": [{
                                ""RuleId"": ""00000000-0000-0000-0000-000000000005"",
                                ""RuleName"": ""Confidential label"",
                                ""Actions"": [""BlockAccess"", ""NotifyUser""],
                                ""Severity"": ""High"",
                                ""RuleMode"": ""Enforce""
                            }]
                        }]
                    }]
                }
            }";

            var parsed = CopilotAuditLogContent.FromJson(json);

            Assert.IsNotNull(parsed.CopilotEventData, "CopilotEventData must deserialise.");
            Assert.AreEqual(5, parsed.CopilotEventData.DlpEvaluationDeferred, "DLPEvaluationDeferred must bind by its exact schema name.");
            Assert.AreEqual("Timeout", parsed.CopilotEventData.DlpEvaluationDeferredReason);

            var resource = parsed.CopilotEventData.AccessedResources.Single();
            Assert.AreEqual("failure", resource.Status, "AccessedResources[].Status must bind.");
            Assert.AreEqual("Καλημέρα κόσμε.docx", resource.Name, "A non-Latin file name must survive parsing intact.");
            Assert.IsFalse(resource.XPIADetected.Value);

            var policy = resource.PolicyDetails.Single();
            Assert.AreEqual("00000000-0000-0000-0000-000000000004", policy.PolicyId);
            Assert.AreEqual("Block Copilot on Confidential", policy.PolicyName);

            var rule = policy.Rules.Single();
            Assert.AreEqual("00000000-0000-0000-0000-000000000005", rule.RuleId);
            Assert.AreEqual("Confidential label", rule.RuleName);
            Assert.AreEqual("High", rule.Severity);
            Assert.AreEqual("Enforce", rule.RuleMode);
            CollectionAssert.AreEqual(new[] { "BlockAccess", "NotifyUser" }, rule.Actions.ToArray());
        }

        /// <summary>
        /// CopilotAuditLogContent deserialises the payload twice - once into CopilotEventData and once,
        /// via EventRaw, into CopilotAuditEvent for cost estimation. Both expose AccessedResources, so
        /// this proves the DLP fields are reachable on BOTH and a future reader cannot silently pick the
        /// model that happens to lack them.
        /// </summary>
        [TestMethod]
        public void ParsedAuditEvent_SharesTheSameAccessedResourceShape()
        {
            var json = @"{
                ""Workload"": ""Copilot"",
                ""CopilotEventData"": {
                    ""AccessedResources"": [{
                        ""Name"": ""budget.xlsx"",
                        ""Status"": ""failure"",
                        ""PolicyDetails"": [{ ""PolicyId"": ""p1"", ""PolicyName"": ""P"" }]
                    }]
                }
            }";

            var parsed = CopilotAuditLogContent.FromJson(json);

            Assert.AreEqual("failure", parsed.ParsedAuditEvent.AccessedResources.Single().Status,
                "The cost-estimation model must see the same Status as CopilotEventData.");
            Assert.AreEqual("p1", parsed.ParsedAuditEvent.AccessedResources.Single().PolicyDetails.Single().PolicyId);
        }

        #endregion

        #region Block vs audit classification

        private static AccessedResource Resource(string status, params AccessedResourcePolicyRule[] rules)
        {
            return new AccessedResource
            {
                Name = "doc.docx",
                Type = "docx",
                Status = status,
                PolicyDetails = new List<AccessedResourcePolicyDetail>
                {
                    new AccessedResourcePolicyDetail
                    {
                        PolicyId = "policy-1",
                        PolicyName = "Policy One",
                        Rules = rules.Length == 0 ? null : rules.ToList(),
                    }
                }
            };
        }

        private static AccessedResourcePolicyRule Rule(string mode, params string[] actions)
        {
            return new AccessedResourcePolicyRule
            {
                RuleId = "rule-1",
                RuleName = "Rule One",
                RuleMode = mode,
                Actions = actions.ToList(),
            };
        }

        [TestMethod]
        public void Classify_EnforcedBlockAccess_IsABlock()
        {
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked,
                CopilotDlpRules.Classify(Resource(null, Rule("Enforce", "BlockAccess"))));
        }

        /// <summary>
        /// The single most important negative case. A rule can list BlockAccess while running in
        /// simulation ("Audit only"), in which case NOTHING was blocked. Counting it as a block would
        /// tell an admin their users are being denied content when they are not.
        /// </summary>
        [TestMethod]
        public void Classify_AuditOnlyRuleListingBlockAccess_IsNotABlock()
        {
            Assert.AreEqual(CopilotDlpOutcome.PolicyAudited,
                CopilotDlpRules.Classify(Resource(null, Rule("Audit only", "BlockAccess"))),
                "A rule in 'Audit only' mode reports but does not enforce, so it must not count as a block.");
        }

        [TestMethod]
        public void Classify_NotifyOnlyRule_IsNotABlock()
        {
            Assert.AreEqual(CopilotDlpOutcome.PolicyAudited,
                CopilotDlpRules.Classify(Resource(null, Rule("Enforce", "NotifyUser", "GenerateIncidentReport"))),
                "Notify / incident-report actions warn the user; they do not deny the content.");
        }

        /// <summary>
        /// Status is the service's own verdict that Copilot did not get the resource, so it wins even
        /// when the rule metadata alone would not imply a block.
        /// </summary>
        [TestMethod]
        public void Classify_StatusFailureWithPolicyDetail_IsABlock()
        {
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked,
                CopilotDlpRules.Classify(Resource("failure", Rule("Audit only", "NotifyUser"))));
        }

        /// <summary>
        /// The other important negative case: a failure with NO policy detail is an access failure of
        /// some other kind (deleted file, permissions), and attributing it to DLP would invent blocks.
        /// </summary>
        [TestMethod]
        public void Classify_StatusFailureWithoutPolicyDetail_IsNotDlpAtAll()
        {
            var resource = new AccessedResource { Name = "doc.docx", Status = "failure" };

            Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource),
                "A bare access failure is not evidence of a DLP policy.");
            Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData { AccessedResources = new List<AccessedResource> { resource } }
            }).Count, "It must also stage nothing.");
        }

        [TestMethod]
        public void Classify_IsCaseInsensitive()
        {
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked,
                CopilotDlpRules.Classify(Resource("FAILURE", Rule("audit only", "NotifyUser"))),
                "Status casing is not guaranteed by the schema.");
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked,
                CopilotDlpRules.Classify(Resource(null, Rule("enforce", "blockaccess"))),
                "RuleMode / action casing is not guaranteed by the schema.");
        }

        [TestMethod]
        public void Classify_UnrecognisedAction_IsTreatedAsNonBlocking()
        {
            Assert.AreEqual(CopilotDlpOutcome.PolicyAudited,
                CopilotDlpRules.Classify(Resource(null, Rule("Enforce", "SomeFutureAction"))),
                "An action we do not recognise must under-report rather than invent a block.");
        }

        [TestMethod]
        public void ExtractMatches_PerRuleVerdict_ReportsEachRuleHonestly()
        {
            var resource = Resource(null, Rule("Enforce", "BlockAccess"), new AccessedResourcePolicyRule
            {
                RuleId = "rule-2",
                RuleName = "Rule Two",
                RuleMode = "Audit only",
                Actions = new List<string> { "BlockAccess" },
            });

            var matches = CopilotDlpRules.ExtractMatches(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData { AccessedResources = new List<AccessedResource> { resource } }
            });

            Assert.AreEqual(2, matches.Count);
            Assert.IsTrue(matches.Single(m => m.RuleId == "rule-1").IsBlocked, "The enforcing rule blocked.");
            Assert.IsFalse(matches.Single(m => m.RuleId == "rule-2").IsBlocked, "The audit-only rule did not.");
        }

        [TestMethod]
        public void ExtractMatches_PolicyWithNoRules_StillRecordsThePolicy()
        {
            var matches = CopilotDlpRules.ExtractMatches(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    AccessedResources = new List<AccessedResource> { Resource("failure") }
                }
            });

            var match = matches.Single();
            Assert.AreEqual("policy-1", match.PolicyId);
            Assert.IsNull(match.RuleId, "There was no rule detail to record.");
            Assert.IsTrue(match.IsBlocked);
        }

        [TestMethod]
        public void PrimaryAction_PrefersTheBlockingActionAndIsDeterministic()
        {
            Assert.AreEqual("BlockAccess",
                CopilotDlpRules.PrimaryAction(Rule("Enforce", "NotifyUser", "BlockAccess", "GenerateIncidentReport")));

            // Equally-ranked (unknown) actions must not depend on payload ordering.
            Assert.AreEqual("Aaa", CopilotDlpRules.PrimaryAction(Rule("Enforce", "Zzz", "Aaa")));
            Assert.IsNull(CopilotDlpRules.PrimaryAction(Rule("Enforce")));
        }

        [TestMethod]
        public void DecodeDeferredStages_DecodesTheDocumentedBitmask()
        {
            CollectionAssert.AreEqual(new[] { "Prompt", "Grounding" },
                CopilotDlpRules.DecodeDeferredStages(5).ToArray(), "5 = 1 (Prompt) | 4 (Grounding).");
            CollectionAssert.AreEqual(new[] { "Prompt", "Response", "Grounding", "WebGrounding" },
                CopilotDlpRules.DecodeDeferredStages(15).ToArray());
            Assert.AreEqual(0, CopilotDlpRules.DecodeDeferredStages(0).Count);
            Assert.AreEqual(0, CopilotDlpRules.DecodeDeferredStages(null).Count);
        }

        #endregion

        #region DLP.All routing

        /// <summary>
        /// The routing trap this feature had to avoid: a DLP record names the workload where the match
        /// was DETECTED, so routing by Workload hands it to the SharePoint deserialiser and every policy
        /// field is silently lost. It must be claimed by RecordType first.
        /// </summary>
        [TestMethod]
        public void Dispatch_SharePointDlpRecord_RoutesToDlpNotSharePoint()
        {
            var json = JToken.Parse(@"{
                ""Workload"": ""SharePoint"",
                ""RecordType"": 11,
                ""Operation"": ""DlpRuleMatch"",
                ""UserKey"": ""DlpAgent"",
                ""PolicyDetails"": [{ ""PolicyId"": ""p1"", ""PolicyName"": ""Finance"" }]
            }");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            var mapped = AuditLogContentDispatcher.Dispatch(json, logBase, Logger);

            Assert.IsInstanceOfType(mapped, typeof(DlpAuditLogContent),
                "A DLP record on the SharePoint workload must be routed by RecordType, not workload.");
            Assert.AreEqual("Finance", ((DlpAuditLogContent)mapped).PolicyDetails.Single().PolicyName);
        }

        [TestMethod]
        public void Dispatch_EveryDocumentedDlpRecordType_IsRouted()
        {
            foreach (var recordType in new[] { 11, 13, 33, 63, 107 })
            {
                var json = JToken.Parse($@"{{ ""Workload"": ""Exchange"", ""RecordType"": {recordType} }}");
                var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

                Assert.IsInstanceOfType(AuditLogContentDispatcher.Dispatch(json, logBase, Logger),
                    typeof(DlpAuditLogContent), $"RecordType {recordType} is a documented DLP record type.");
            }
        }

        [TestMethod]
        public void Dispatch_DlpDisabled_DropsDlpRecords()
        {
            var json = JToken.Parse(@"{ ""Workload"": ""SharePoint"", ""RecordType"": 11 }");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            Assert.IsNull(AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importDlp: false),
                "DLP records must be dropped when the DLP import toggle is off.");
        }

        /// <summary>
        /// The DLP route must not become a catch-all: an ordinary SharePoint event carries no RecordType
        /// in some payloads, and claiming those would break the existing SharePoint import.
        /// </summary>
        [TestMethod]
        public void Dispatch_NonDlpRecordTypes_AreUnaffected()
        {
            var noRecordType = JToken.Parse(@"{ ""Workload"": ""SharePoint"" }");
            Assert.IsInstanceOfType(
                AuditLogContentDispatcher.Dispatch(noRecordType, noRecordType.ToObject<WorkloadOnlyAuditLogContent>(), Logger),
                typeof(SharePointAuditLogContent),
                "A SharePoint event with no RecordType must still deserialise as SharePoint.");

            var spFileOp = JToken.Parse(@"{ ""Workload"": ""SharePoint"", ""RecordType"": 6 }");
            Assert.IsInstanceOfType(
                AuditLogContentDispatcher.Dispatch(spFileOp, spFileOp.ToObject<WorkloadOnlyAuditLogContent>(), Logger),
                typeof(SharePointAuditLogContent),
                "SharePointFileOperation (6) is not a DLP record type.");
        }

        #endregion

        #region Import toggle and optional subscription

        [TestMethod]
        public void ImportDlp_AddsTheDlpContentTypeAndUsesTheActivityApi()
        {
            var settings = new ImportTaskSettings { ImportDlp = true };

            Assert.IsTrue(settings.UsesActivityApi, "DLP alone must run the Activity API import loop.");
            Assert.AreEqual(ImportTaskSettings.CONTENT_TYPE_DLP_ALL, settings.ToActivityApiContentTypesString());

            var both = new ImportTaskSettings { ActivityLog = true, ImportDlp = true };
            CollectionAssert.AreEquivalent(
                new[] { ImportTaskSettings.CONTENT_TYPE_AUDIT_SHAREPOINT, ImportTaskSettings.CONTENT_TYPE_DLP_ALL },
                both.ToActivityApiContentTypesString().Split(';'));
        }

        /// <summary>
        /// DLP.All needs its own ActivityFeed.ReadDlp consent, so its subscription must be optional -
        /// otherwise ticking the DLP box before consent takes down SharePoint, Copilot and Power
        /// Platform importing too. The mandatory feeds must stay mandatory.
        /// </summary>
        [TestMethod]
        public void OnlyTheDlpContentTypeIsOptional()
        {
            Assert.IsTrue(ImportTaskSettings.IsOptionalContentType(ImportTaskSettings.CONTENT_TYPE_DLP_ALL));
            Assert.IsTrue(ImportTaskSettings.IsOptionalContentType("dlp.all"), "Comparison must be case-insensitive.");
            Assert.IsFalse(ImportTaskSettings.IsOptionalContentType(ImportTaskSettings.CONTENT_TYPE_AUDIT_SHAREPOINT));
            Assert.IsFalse(ImportTaskSettings.IsOptionalContentType(ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL));
        }

        /// <summary>
        /// Copilot DLP reporting rides on the Copilot interaction records, so it must work with the DLP
        /// toggle OFF. If this ever fails, the feature has been wired to the wrong permission.
        /// </summary>
        [TestMethod]
        public void CopilotDlpReporting_DoesNotRequireTheDlpToggle()
        {
            var settings = new ImportTaskSettings { Copilot = true, ImportDlp = false };

            Assert.AreEqual(ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL, settings.ToActivityApiContentTypesString(),
                "Copilot interactions - which carry the DLP block signal - arrive on Audit.General.");

            var json = JToken.Parse(@"{
                ""Workload"": ""Copilot"",
                ""RecordType"": 261,
                ""CopilotEventData"": { ""AccessedResources"": [{
                    ""Name"": ""doc.docx"", ""Status"": ""failure"",
                    ""PolicyDetails"": [{ ""PolicyId"": ""p1"" }] }] }
            }");
            var mapped = AuditLogContentDispatcher.Dispatch(
                json, json.ToObject<WorkloadOnlyAuditLogContent>(), Logger, importCopilot: true, importDlp: false)
                as CopilotAuditLogContent;

            Assert.IsNotNull(mapped, "A Copilot interaction is not a DLP record type and must not be dropped by the DLP toggle.");
            Assert.AreEqual(1, CopilotDlpRules.ExtractMatches(mapped).Count,
                "Its embedded DLP detail must still be extracted with the DLP.All import disabled.");
        }

        #endregion

        #region Staging

        private sealed class RecordingDlpStagingWriter : IDlpStagingWriter
        {
            public List<CopilotDlpLogTempEntity> CopilotRows { get; } = new List<CopilotDlpLogTempEntity>();
            public List<DlpRuleMatchLogTempEntity> RuleMatchRows { get; } = new List<DlpRuleMatchLogTempEntity>();

            public void StageCopilotDlp(CopilotDlpLogTempEntity row) => CopilotRows.Add(row);
            public void StageDlpRuleMatch(DlpRuleMatchLogTempEntity row) => RuleMatchRows.Add(row);
            public System.Threading.Tasks.Task CommitAllChanges() => System.Threading.Tasks.Task.CompletedTask;
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Manager_StagesCopilotBlocksWithTheirClassification()
        {
            var writer = new RecordingDlpStagingWriter();
            var manager = new DlpAuditEventManager(writer, Logger);
            var baseEvent = new CommonAuditEvent { Id = System.Guid.NewGuid() };

            await manager.SaveCopilotDlpMatchesToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    AccessedResources = new List<AccessedResource>
                    {
                        Resource("failure", Rule("Enforce", "BlockAccess")),
                        // Not policy-affected: must stage nothing, keeping the table sparse.
                        new AccessedResource { Name = "other.docx", Status = "success" },
                    }
                }
            }, baseEvent);

            var row = writer.CopilotRows.Single();
            Assert.AreEqual(baseEvent.Id, row.EventId);
            Assert.AreEqual("policy-1", row.PolicyId);
            Assert.AreEqual("BlockAccess", row.ActionName);
            Assert.IsTrue(row.IsBlocked);
            Assert.AreEqual(1, manager.StagedCopilotMatchCount);
            Assert.AreEqual(1, manager.StagedCopilotBlockedCount);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Manager_SkipsAMatchThatNamesNeitherPolicyNorRule()
        {
            var writer = new RecordingDlpStagingWriter();
            var manager = new DlpAuditEventManager(writer, Logger);

            await manager.SaveCopilotDlpMatchesToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    AccessedResources = new List<AccessedResource>
                    {
                        new AccessedResource
                        {
                            Name = "doc.docx",
                            Status = "failure",
                            PolicyDetails = new List<AccessedResourcePolicyDetail> { new AccessedResourcePolicyDetail() },
                        }
                    }
                }
            }, new CommonAuditEvent { Id = System.Guid.NewGuid() });

            Assert.AreEqual(0, writer.CopilotRows.Count,
                "An anonymous match cannot be grouped or named in the report, so it must not be stored.");
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Manager_StagesDlpAllRuleMatches()
        {
            var writer = new RecordingDlpStagingWriter();
            var manager = new DlpAuditEventManager(writer, Logger);
            var baseEvent = new CommonAuditEvent { Id = System.Guid.NewGuid() };

            await manager.SaveSingleDlpEventToSqlStaging(new DlpAuditLogContent
            {
                Operation = "DlpRuleMatch",
                PolicyDetails = new List<AccessedResourcePolicyDetail>
                {
                    new AccessedResourcePolicyDetail
                    {
                        PolicyId = "p1",
                        PolicyName = "Finance",
                        Rules = new List<AccessedResourcePolicyRule> { Rule("Enforce", "BlockAccess") },
                    }
                }
            }, baseEvent);

            var row = writer.RuleMatchRows.Single();
            Assert.AreEqual(baseEvent.Id, row.EventId);
            Assert.AreEqual("Finance", row.PolicyName);
            Assert.IsTrue(row.IsBlocked, "The same classifier decides 'blocked' for both DLP sources.");
        }

        #endregion
    }
}
