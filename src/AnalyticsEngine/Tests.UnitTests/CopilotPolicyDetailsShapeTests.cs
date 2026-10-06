using Common.Entities;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #659: Copilot interaction records whose <c>AccessedResources[].PolicyDetails</c> arrives as a
    /// JSON-encoded string of undocumented <c>PolicyType</c> / <c>PolicyOutcomes</c> / <c>AuditLog</c>
    /// entries used to throw in <see cref="CopilotAuditLogContent.FromJson"/>, and the loader then dropped
    /// the whole interaction. These tests pin that both shapes import, that a policy evaluation with no
    /// effect is not reported as DLP, and that a record which still cannot be read is counted.
    /// </summary>
    /// <remarks>Synthetic fixtures only: Contoso, zeroed GUIDs, made-up names.</remarks>
    [TestClass]
    public class CopilotPolicyDetailsShapeTests
    {
        private const string GreekFileName = "Καλημέρα κόσμε.docx";

        /// <summary>Example 1 of #659: one Purview entry, as the JSON string it arrives as.</summary>
        private const string Example1PolicyDetails =
            @"""[{\""PolicyType\"":\""Purview\"",\""PolicyOutcomes\"":[\""None\""],\""AuditLog\"":\""\""}]""";

        /// <summary>
        /// Example 2 of #659, exactly as it sits on the wire: three entries, and on the Purview entry an
        /// AuditLog that is itself a JSON document with its quotes escaped as <c>\u0022</c>.
        /// </summary>
        private const string Example2PolicyDetailsOnTheWire =
            @"""[{\""PolicyType\"":\""ConditionalAccess\"",\""PolicyOutcomes\"":[\""None\""],\""AuditLog\"":\""\""},{\""PolicyType\"":\""RightsManagementService\"",\""PolicyOutcomes\"":[\""None\""],\""AuditLog\"":\""\""},{\""PolicyType\"":\""Purview\"",\""PolicyOutcomes\"":[\""None\""],\""AuditLog\"":\""{\\u0022PolicyDetails\\u0022:[],\\u0022AssociatedAdminUnits\\u0022:[]}\""}]""";

        /// <summary>
        /// A complete minimal CopilotInteraction record (Example 1's shape). <paramref name="policyDetailsJson"/>
        /// is the raw JSON value of PolicyDetails, or null to leave the property out.
        /// </summary>
        private static string Record(string policyDetailsJson, string status = null, string id = "00000000-0000-0000-0000-000000000001")
        {
            var statusProperty = status == null ? string.Empty : $@",
        ""Status"": ""{status}""";
            var policyDetailsProperty = policyDetailsJson == null ? string.Empty : $@",
        ""PolicyDetails"": {policyDetailsJson}";

            return $@"{{
  ""CreationTime"": ""2026-01-01T09:00:00"",
  ""Id"": ""{id}"",
  ""Operation"": ""CopilotInteraction"",
  ""OrganizationId"": ""00000000-0000-0000-0000-000000000000"",
  ""RecordType"": 261,
  ""UserId"": ""user@contoso.com"",
  ""Workload"": ""Copilot"",
  ""CopilotEventData"": {{
    ""AppHost"": ""BizChat"",
    ""Contexts"": [],
    ""AccessedResources"": [
      {{
        ""Action"": ""Read"",
        ""Id"": ""00000000-0000-0000-0000-000000000002"",
        ""Name"": ""{GreekFileName}"",
        ""Type"": ""docx"",
        ""SiteUrl"": ""https://contoso.sharepoint.com/sites/example/Shared Documents/{GreekFileName}""{statusProperty}{policyDetailsProperty}
      }}
    ],
    ""Messages"": [
      {{ ""Id"": ""1700000000001"", ""isPrompt"": true }},
      {{ ""Id"": ""1700000000002"", ""isPrompt"": false }}
    ]
  }}
}}";
        }

        /// <summary>A record whose PolicyDetails is the given .NET string, so the fixture's escaping cannot be wrong.</summary>
        private static string RecordWithSerialisedPolicyDetails(string policyDetails, string status = null)
        {
            var record = JObject.Parse(Record(null, status));
            record["CopilotEventData"]["AccessedResources"][0]["PolicyDetails"] = policyDetails; // a string, not an array
            return record.ToString();
        }

        /// <summary>Both of FromJson's deserialisations must see the same resource.</summary>
        private static IEnumerable<AccessedResource> BothResources(CopilotAuditLogContent parsed)
        {
            yield return parsed.CopilotEventData.AccessedResources.Single();
            yield return parsed.ParsedAuditEvent.AccessedResources.Single();
        }

        private static CopilotAuditLogContent Wrap(AccessedResource resource)
        {
            return new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData { AccessedResources = new List<AccessedResource> { resource } }
            };
        }

        #region Parsing

        [TestMethod]
        public void Example1_StringEncodedPolicyDetails_ImportsWithTheEntryParsed()
        {
            var parsed = CopilotAuditLogContent.FromJson(Record(Example1PolicyDetails));

            Assert.AreEqual("BizChat", parsed.CopilotEventData.AppHost, "The interaction itself must survive.");
            foreach (var resource in BothResources(parsed))
            {
                Assert.AreEqual(GreekFileName, resource.Name, "A non-Latin file name must survive intact.");

                var entry = resource.PolicyDetails.Single();
                Assert.AreEqual("Purview", entry.PolicyType);
                CollectionAssert.AreEqual(new[] { "None" }, entry.PolicyOutcomes.ToArray());
                Assert.IsNull(entry.AuditLog, "An empty AuditLog decodes to nothing.");
                Assert.IsNull(entry.PolicyId);
                Assert.IsNull(entry.PolicyName);
                Assert.IsNull(entry.Rules);
            }
        }

        /// <summary>The literal <c>\u0022</c> fixture: Example 2 byte-for-byte as production sends it.</summary>
        [TestMethod]
        public void Example2_LiteralWireFormat_ImportsAndDecodesTheAuditLog()
        {
            AssertExample2(CopilotAuditLogContent.FromJson(Record(Example2PolicyDetailsOnTheWire)));
        }

        /// <summary>Example 2 built by serialising, as #659 suggests, so its escaping cannot be wrong.</summary>
        [TestMethod]
        public void Example2_BuiltBySerialising_ImportsAndDecodesTheAuditLog()
        {
            var auditLog = JsonConvert.SerializeObject(new { PolicyDetails = new object[0], AssociatedAdminUnits = new object[0] });
            var policyDetails = JsonConvert.SerializeObject(new object[]
            {
                new { PolicyType = "ConditionalAccess", PolicyOutcomes = new[] { "None" }, AuditLog = "" },
                new { PolicyType = "RightsManagementService", PolicyOutcomes = new[] { "None" }, AuditLog = "" },
                new { PolicyType = "Purview", PolicyOutcomes = new[] { "None" }, AuditLog = auditLog },
            });

            AssertExample2(CopilotAuditLogContent.FromJson(RecordWithSerialisedPolicyDetails(policyDetails)));
        }

        private static void AssertExample2(CopilotAuditLogContent parsed)
        {
            foreach (var resource in BothResources(parsed))
            {
                Assert.AreEqual(GreekFileName, resource.Name);
                CollectionAssert.AreEqual(
                    new[] { "ConditionalAccess", "RightsManagementService", "Purview" },
                    resource.PolicyDetails.Select(p => p.PolicyType).ToArray(),
                    "Every entry must be kept, in order.");

                foreach (var entry in resource.PolicyDetails)
                {
                    CollectionAssert.AreEqual(new[] { "None" }, entry.PolicyOutcomes.ToArray());
                }

                Assert.IsNull(resource.PolicyDetails[0].AuditLog);
                Assert.IsNull(resource.PolicyDetails[1].AuditLog);

                var purviewLog = resource.PolicyDetails[2].AuditLog;
                Assert.IsNotNull(purviewLog, "The Purview entry's AuditLog must be decoded.");
                Assert.IsNotNull(purviewLog.PolicyDetails);
                Assert.AreEqual(0, purviewLog.PolicyDetails.Count, "It decodes to {\"PolicyDetails\":[],...}.");

                Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource));
            }

            Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(parsed).Count);
        }

        [TestMethod]
        public void PolicyDetails_AbsentNullOrEmpty_IsUnchanged()
        {
            foreach (var value in new[] { null, "null", @"""""" })
            {
                var parsed = CopilotAuditLogContent.FromJson(Record(value));
                foreach (var resource in BothResources(parsed))
                {
                    Assert.AreEqual(GreekFileName, resource.Name);
                    Assert.IsNull(resource.PolicyDetails, $"PolicyDetails {value ?? "(absent)"} must stay null.");
                }
            }
        }

        [TestMethod]
        public void PolicyDetails_UnexpectedShapes_ImportWithNoPolicyDetail()
        {
            var unexpected = new[]
            {
                @"""not json""",
                @"""[not json""",
                @"""{\""PolicyType\"":\""Purview\""}""",
                @"{ ""PolicyType"": ""Purview"", ""PolicyOutcomes"": [""None""] }",
                "42",
                "4.2",
                "true",
            };

            foreach (var value in unexpected)
            {
                CopilotAuditLogContent parsed = null;
                try
                {
                    parsed = CopilotAuditLogContent.FromJson(Record(value, status: "failure"));
                }
                catch (Exception ex)
                {
                    Assert.Fail($"PolicyDetails {value} must not throw, but threw {ex.GetType().Name}: {ex.Message}");
                }

                foreach (var resource in BothResources(parsed))
                {
                    Assert.AreEqual(GreekFileName, resource.Name, $"The resource must survive PolicyDetails {value}.");
                    Assert.IsNull(resource.PolicyDetails, $"PolicyDetails {value} must become null.");
                    Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource));
                }
            }
        }

        [TestMethod]
        public void PolicyDetails_ArrayWithAnUnreadableEntry_KeepsTheRest()
        {
            var parsed = CopilotAuditLogContent.FromJson(Record(
                @"[ 42, { ""PolicyId"": ""p1"", ""Rules"": ""not a list"" }, { ""PolicyId"": ""p2"", ""PolicyName"": ""Ελληνική πολιτική"" } ]"));

            foreach (var resource in BothResources(parsed))
            {
                var entry = resource.PolicyDetails.Single();
                Assert.AreEqual("p2", entry.PolicyId);
                Assert.AreEqual("Ελληνική πολιτική", entry.PolicyName, "Policy names are tenant-authored free text.");
            }
        }

        /// <summary>
        /// The documented array must bind exactly as before #659 - this is the payload
        /// DlpCopilotTests.CopilotAuditLogContent_ParsesAccessedResourceStatusAndPolicyDetails pins - and keep
        /// its DLP classification.
        /// </summary>
        [TestMethod]
        public void DocumentedArray_BindsAndClassifiesAsBefore()
        {
            const string documented = @"[{
                ""PolicyId"": ""00000000-0000-0000-0000-000000000004"",
                ""PolicyName"": ""Block Copilot on Confidential"",
                ""Rules"": [{
                    ""RuleId"": ""00000000-0000-0000-0000-000000000005"",
                    ""RuleName"": ""Confidential label"",
                    ""Actions"": [""BlockAccess"", ""NotifyUser""],
                    ""Severity"": ""High"",
                    ""RuleMode"": ""Enforce""
                }]
            }]";

            var parsed = CopilotAuditLogContent.FromJson(Record(documented, status: "failure"));

            foreach (var resource in BothResources(parsed))
            {
                var policy = resource.PolicyDetails.Single();
                Assert.AreEqual("00000000-0000-0000-0000-000000000004", policy.PolicyId);
                Assert.AreEqual("Block Copilot on Confidential", policy.PolicyName);
                Assert.IsNull(policy.PolicyType);
                Assert.IsNull(policy.PolicyOutcomes);
                Assert.IsNull(policy.AuditLog);
                Assert.IsFalse(CopilotDlpRules.IsPolicyEvaluation(policy));

                var rule = policy.Rules.Single();
                Assert.AreEqual("Enforce", rule.RuleMode);
                CollectionAssert.AreEqual(new[] { "BlockAccess", "NotifyUser" }, rule.Actions.ToArray());

                Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked, CopilotDlpRules.Classify(resource));
            }

            var match = CopilotDlpRules.ExtractMatches(parsed).Single();
            Assert.AreEqual("Block Copilot on Confidential", match.PolicyName);
            Assert.AreEqual("BlockAccess", match.Action);
            Assert.IsTrue(match.IsBlocked);

            // The documented shape still takes a failed Status as the verdict, even for an audit-only rule.
            var auditOnly = JObject.Parse(Record(documented, status: "failure"));
            auditOnly["CopilotEventData"]["AccessedResources"][0]["PolicyDetails"][0]["Rules"][0]["RuleMode"] = "Audit only";
            var auditOnlyParsed = CopilotAuditLogContent.FromJson(auditOnly.ToString());
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked,
                CopilotDlpRules.Classify(auditOnlyParsed.CopilotEventData.AccessedResources.Single()));
            Assert.IsTrue(CopilotDlpRules.ExtractMatches(auditOnlyParsed).Single().IsBlocked);
        }

        /// <summary>
        /// Only PolicyType / PolicyOutcomes mark the new shape. A documented entry that happens to carry an
        /// AuditLog key too is classified exactly as before #659: a failed Status is still its verdict.
        /// </summary>
        [TestMethod]
        public void DocumentedEntry_WithAnAuditLogButNoPolicyTypeOrOutcomes_IsClassifiedAsBefore()
        {
            const string documentedWithAuditLog = @"[{
                ""PolicyId"": ""00000000-0000-0000-0000-000000000004"",
                ""PolicyName"": ""Contoso policy"",
                ""Rules"": [{ ""RuleId"": ""rule-1"", ""Actions"": [""NotifyUser""], ""RuleMode"": ""Audit only"" }],
                ""AuditLog"": ""{\""PolicyDetails\"":[]}""
            }]";

            var parsed = CopilotAuditLogContent.FromJson(Record(documentedWithAuditLog, status: "failure"));
            var resource = parsed.CopilotEventData.AccessedResources.Single();

            Assert.IsNotNull(resource.PolicyDetails.Single().AuditLog, "The AuditLog is decoded on the model.");
            Assert.IsFalse(CopilotDlpRules.IsPolicyEvaluation(resource.PolicyDetails.Single()));
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked, CopilotDlpRules.Classify(resource));
            Assert.IsTrue(CopilotDlpRules.ExtractMatches(parsed).Single().IsBlocked);
        }

        [TestMethod]
        public void AuditLog_IsDecodedTolerantly()
        {
            AccessedResourcePolicyDetail Bind(JToken auditLog) =>
                new JObject { ["PolicyType"] = "Purview", ["AuditLog"] = auditLog }.ToObject<AccessedResourcePolicyDetail>();

            Assert.IsNull(Bind("").AuditLog);
            Assert.IsNull(Bind("   ").AuditLog);
            Assert.IsNull(Bind("not json").AuditLog);
            Assert.IsNull(Bind("[1, 2]").AuditLog, "Only a JSON object is an AuditLog.");
            Assert.IsNull(Bind(42).AuditLog);
            Assert.IsNull(Bind(JValue.CreateNull()).AuditLog);

            Assert.AreEqual(0, Bind(@"{""PolicyDetails"":[],""AssociatedAdminUnits"":[]}").AuditLog.PolicyDetails.Count);
            Assert.AreEqual(0, Bind(JObject.Parse(@"{""PolicyDetails"":[]}")).AuditLog.PolicyDetails.Count,
                "An AuditLog that arrives as a real object is bound too.");
            Assert.IsNull(Bind(@"{""PolicyDetails"":""not json""}").AuditLog.PolicyDetails);

            // Escaped twice: the decoded text still holds literal \u0022 sequences.
            Assert.AreEqual(0, Bind(@"{\u0022PolicyDetails\u0022:[],\u0022AssociatedAdminUnits\u0022:[]}").AuditLog.PolicyDetails.Count);
        }

        [TestMethod]
        public void PolicyOutcomes_AreKeptVerbatim()
        {
            AccessedResourcePolicyDetail Bind(JToken outcomes) =>
                new JObject { ["PolicyType"] = "Purview", ["PolicyOutcomes"] = outcomes }.ToObject<AccessedResourcePolicyDetail>();

            CollectionAssert.AreEqual(new[] { "None", "SomethingNew" }, Bind(new JArray("None", "SomethingNew")).PolicyOutcomes.ToArray());
            CollectionAssert.AreEqual(new[] { "None" }, Bind("None").PolicyOutcomes.ToArray(), "A single string is one outcome.");
            Assert.IsNull(Bind(new JObject()).PolicyOutcomes);
        }

        #endregion

        #region Classification

        [TestMethod]
        public void NoneOutcomes_AreNotPolicyRelated_EvenWhenTheResourceFailed()
        {
            foreach (var status in new[] { null, "success", "failure", "FAILURE" })
            {
                var parsed = CopilotAuditLogContent.FromJson(Record(Example2PolicyDetailsOnTheWire, status));

                Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated,
                    CopilotDlpRules.Classify(parsed.CopilotEventData.AccessedResources.Single()),
                    $"A policy evaluation with no effect is not DLP (Status {status ?? "(absent)"}).");
                Assert.IsFalse(CopilotDlpRules.HasPolicyDetail(parsed.CopilotEventData.AccessedResources.Single()));
                Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(parsed).Count,
                    $"It must stage nothing (Status {status ?? "(absent)"}).");
            }
        }

        [TestMethod]
        public async Task NoneOutcomes_StageNoDlpRows()
        {
            var writer = new RecordingDlpStagingWriter();
            var manager = new DlpAuditEventManager(writer, new LoggerFactory().CreateLogger("CopilotPolicyDetailsShapeTests"));

            await manager.SaveCopilotDlpMatchesToSqlStaging(
                CopilotAuditLogContent.FromJson(Record(Example2PolicyDetailsOnTheWire, status: "failure")),
                new CommonAuditEvent { Id = Guid.NewGuid() });

            Assert.AreEqual(0, writer.CopilotRows.Count);
            Assert.AreEqual(0, manager.StagedCopilotMatchCount);
            Assert.AreEqual(0, manager.StagedCopilotBlockedCount);
        }

        /// <summary>
        /// The meaning of PolicyOutcomes values is undocumented, so like an unrecognised rule action, no
        /// value is a block on its own - not even on a resource whose access failed.
        /// </summary>
        [TestMethod]
        public void UnknownOutcome_IsKeptVerbatimAndIsNeverABlockOnItsOwn()
        {
            var policyDetails = JsonConvert.SerializeObject(new object[]
            {
                new { PolicyType = "Purview", PolicyOutcomes = new[] { "Blocked", "SomethingNew" }, AuditLog = "" },
            });

            var parsed = CopilotAuditLogContent.FromJson(RecordWithSerialisedPolicyDetails(policyDetails, status: "failure"));
            var resource = parsed.CopilotEventData.AccessedResources.Single();

            CollectionAssert.AreEqual(new[] { "Blocked", "SomethingNew" }, resource.PolicyDetails.Single().PolicyOutcomes.ToArray());
            Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource));
            Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(parsed).Count);
        }

        private static string EvaluationWithLoggedPolicy(string outcome, string ruleMode, params string[] actions)
        {
            var auditLog = JsonConvert.SerializeObject(new
            {
                PolicyDetails = new[]
                {
                    new
                    {
                        PolicyId = "00000000-0000-0000-0000-000000000004",
                        PolicyName = "Αποκλεισμός Copilot σε εμπιστευτικά",
                        Rules = new[]
                        {
                            new
                            {
                                RuleId = "00000000-0000-0000-0000-000000000005",
                                RuleName = "Εμπιστευτικό",
                                Actions = actions,
                                Severity = "High",
                                RuleMode = ruleMode,
                            }
                        }
                    }
                },
                AssociatedAdminUnits = new object[0],
            });

            return JsonConvert.SerializeObject(new object[]
            {
                new { PolicyType = "Purview", PolicyOutcomes = new[] { outcome }, AuditLog = auditLog },
            });
        }

        /// <summary>
        /// Issue #659's acceptance criterion: <c>PolicyOutcomes: ["None"]</c> never produces a DLP match or a
        /// block - not even when the decoded AuditLog names a policy whose rule enforces BlockAccess, and the
        /// resource's access failed. The entry itself says the evaluation had no effect.
        /// </summary>
        [TestMethod]
        public async Task NoneOutcome_WithAnAuditLogNamingAnEnforcedBlockingPolicy_OnAFailedResource_IsNotPolicyRelated()
        {
            var parsed = CopilotAuditLogContent.FromJson(
                RecordWithSerialisedPolicyDetails(EvaluationWithLoggedPolicy("None", "Enforce", "BlockAccess"), status: "failure"));

            foreach (var resource in BothResources(parsed))
            {
                Assert.AreEqual("Αποκλεισμός Copilot σε εμπιστευτικά",
                    resource.PolicyDetails.Single().AuditLog.PolicyDetails.Single().PolicyName,
                    "The AuditLog is still decoded; it is only not reported.");
                Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource));
                Assert.IsFalse(CopilotDlpRules.HasPolicyDetail(resource));
            }

            Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(parsed).Count, "\"None\" must never produce a DLP match.");

            var writer = new RecordingDlpStagingWriter();
            var manager = new DlpAuditEventManager(writer, new LoggerFactory().CreateLogger("CopilotPolicyDetailsShapeTests"));
            await manager.SaveCopilotDlpMatchesToSqlStaging(parsed, new CommonAuditEvent { Id = Guid.NewGuid() });

            Assert.AreEqual(0, writer.CopilotRows.Count, "Nothing may be staged.");
            Assert.AreEqual(0, manager.StagedCopilotMatchCount);
            Assert.AreEqual(0, manager.StagedCopilotBlockedCount);
        }

        /// <summary>
        /// "None" is matched trimmed and in any case, and only when EVERY outcome is "None". A mixed list, an
        /// empty list or no list at all keeps the rule-only classification.
        /// </summary>
        [TestMethod]
        public void NoneOutcomes_AreMatchedTrimmedAndCaseInsensitively_AndOnlyWhenEveryOutcomeIsNone()
        {
            AccessedResource ResourceWithOutcomes(List<string> outcomes) => new AccessedResource
            {
                Name = GreekFileName,
                Status = "failure",
                PolicyDetails = new List<AccessedResourcePolicyDetail>
                {
                    new AccessedResourcePolicyDetail
                    {
                        PolicyType = "Purview",
                        PolicyOutcomes = outcomes,
                        PolicyId = "00000000-0000-0000-0000-000000000006",
                        PolicyName = "Contoso policy",
                        Rules = new List<AccessedResourcePolicyRule>
                        {
                            new AccessedResourcePolicyRule { RuleId = "rule-1", RuleMode = "Enforce", Actions = new List<string> { "BlockAccess" } }
                        },
                    }
                }
            };

            foreach (var noEffect in new[] { new List<string> { "None" }, new List<string> { " none ", "NONE" } })
            {
                var resource = ResourceWithOutcomes(noEffect);
                Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource),
                    $"Outcomes [{string.Join(",", noEffect)}] mean the evaluation had no effect.");
                Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(Wrap(resource)).Count);
            }

            foreach (var other in new[] { new List<string> { "None", "SomethingNew" }, new List<string>(), null })
            {
                var resource = ResourceWithOutcomes(other);
                var label = other == null ? "(none)" : $"[{string.Join(",", other)}]";
                Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked, CopilotDlpRules.Classify(resource),
                    $"Outcomes {label} keep the rule-only classification: the enforcing BlockAccess rule decides.");
                Assert.IsTrue(CopilotDlpRules.ExtractMatches(Wrap(resource)).Single().IsBlocked);
            }
        }

        /// <summary>
        /// "None" is honoured at every level: a policy INSIDE the decoded AuditLog that carries its own
        /// all-"None" outcomes is never a match or a block, even under an outer entry whose outcome is not
        /// "None" and on a resource whose access failed.
        /// </summary>
        [TestMethod]
        public void NoneOutcome_OnAPolicyInsideTheAuditLog_IsNotPolicyRelated_EvenUnderAnUnknownOuterOutcome()
        {
            var auditLog = JsonConvert.SerializeObject(new
            {
                PolicyDetails = new[]
                {
                    new
                    {
                        PolicyId = "00000000-0000-0000-0000-000000000004",
                        PolicyName = "Αποκλεισμός Copilot σε εμπιστευτικά",
                        PolicyOutcomes = new[] { "None" },
                        Rules = new[]
                        {
                            new
                            {
                                RuleId = "00000000-0000-0000-0000-000000000005",
                                RuleName = "Εμπιστευτικό",
                                Actions = new[] { "BlockAccess" },
                                Severity = "High",
                                RuleMode = "Enforce",
                            }
                        }
                    }
                },
                AssociatedAdminUnits = new object[0],
            });
            var policyDetails = JsonConvert.SerializeObject(new object[]
            {
                new { PolicyType = "Purview", PolicyOutcomes = new[] { "SomethingNew" }, AuditLog = auditLog },
            });

            var parsed = CopilotAuditLogContent.FromJson(RecordWithSerialisedPolicyDetails(policyDetails, status: "failure"));

            foreach (var resource in BothResources(parsed))
            {
                var inner = resource.PolicyDetails.Single().AuditLog.PolicyDetails.Single();
                CollectionAssert.AreEqual(new[] { "None" }, inner.PolicyOutcomes.ToArray(), "The inner outcomes are bound.");
                Assert.AreEqual(CopilotDlpOutcome.NotPolicyRelated, CopilotDlpRules.Classify(resource));
            }

            Assert.AreEqual(0, CopilotDlpRules.ExtractMatches(parsed).Count, "An inner \"None\" must never produce a DLP match.");
        }

        /// <summary>
        /// Where an evaluation entry with an outcome other than "None" has a decoded AuditLog naming a policy
        /// in the documented shape, that policy is classified through the existing model.
        /// </summary>
        [TestMethod]
        public void AuditLogPolicy_UnderAnUnknownOutcome_WithAnEnforcedBlockingRule_IsABlock()
        {
            var parsed = CopilotAuditLogContent.FromJson(
                RecordWithSerialisedPolicyDetails(EvaluationWithLoggedPolicy("SomethingNew", "Enforce", "BlockAccess", "NotifyUser"), status: "success"));
            var resource = parsed.CopilotEventData.AccessedResources.Single();

            Assert.IsTrue(CopilotDlpRules.IsPolicyEvaluation(resource.PolicyDetails.Single()));
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked, CopilotDlpRules.Classify(resource));
            Assert.AreEqual(CopilotDlpOutcome.PolicyBlocked, CopilotDlpRules.Classify(parsed.ParsedAuditEvent.AccessedResources.Single()));

            var match = CopilotDlpRules.ExtractMatches(parsed).Single();
            Assert.AreEqual("00000000-0000-0000-0000-000000000004", match.PolicyId);
            Assert.AreEqual("Αποκλεισμός Copilot σε εμπιστευτικά", match.PolicyName);
            Assert.AreEqual("Εμπιστευτικό", match.RuleName);
            Assert.AreEqual("BlockAccess", match.Action);
            Assert.AreEqual(GreekFileName, match.ResourceName);
            Assert.IsTrue(match.IsBlocked);
        }

        /// <summary>
        /// For detail found through a policy-evaluation entry, a failed Status is not taken as the verdict:
        /// the entry carries its own outcome, whose meaning is undocumented, so only the rule decides.
        /// </summary>
        [TestMethod]
        public void AuditLogPolicy_UnderAnUnknownOutcome_WithAnAuditOnlyRule_OnAFailedResource_IsAuditedNotBlocked()
        {
            var parsed = CopilotAuditLogContent.FromJson(
                RecordWithSerialisedPolicyDetails(EvaluationWithLoggedPolicy("SomethingNew", "Audit only", "BlockAccess"), status: "failure"));

            Assert.AreEqual(CopilotDlpOutcome.PolicyAudited, CopilotDlpRules.Classify(parsed.CopilotEventData.AccessedResources.Single()));
            var match = CopilotDlpRules.ExtractMatches(parsed).Single();
            Assert.AreEqual("Αποκλεισμός Copilot σε εμπιστευτικά", match.PolicyName);
            Assert.IsFalse(match.IsBlocked);
        }

        [TestMethod]
        public void EvaluationEntry_UnderAnUnknownOutcome_ThatNamesAPolicyItself_IsReportedButOnlyBlockedByARule()
        {
            var resource = new AccessedResource
            {
                Name = GreekFileName,
                Status = "failure",
                PolicyDetails = new List<AccessedResourcePolicyDetail>
                {
                    new AccessedResourcePolicyDetail
                    {
                        PolicyType = "Purview",
                        PolicyOutcomes = new List<string> { "SomethingNew" },
                        PolicyId = "00000000-0000-0000-0000-000000000006",
                        PolicyName = "Contoso policy",
                    }
                }
            };

            Assert.AreEqual(CopilotDlpOutcome.PolicyAudited, CopilotDlpRules.Classify(resource));
            var match = CopilotDlpRules.ExtractMatches(Wrap(resource)).Single();
            Assert.AreEqual("Contoso policy", match.PolicyName);
            Assert.IsFalse(match.IsBlocked);
        }

        #endregion

        #region Loader and cycle accounting

        [TestMethod]
        public async Task Loader_NewShapeRecordAndOrdinaryRecord_BothImportWithoutWarnings()
        {
            var json = "[" + Record(Example2PolicyDetailsOnTheWire, status: "failure") + "," +
                       Record(null, id: "00000000-0000-0000-0000-000000000003") + "]";
            var logger = new RecordingLogger();

            using (var httpClient = new AutoThrottleHttpClient(new StaticJsonHandler(json), logger))
            {
                var loader = NewLoader(httpClient, logger);
                var logs = await loader.Load(ReportInfo());

                Assert.IsTrue(logs.DownloadComplete);
                Assert.AreEqual(2, logs.Count, "Both interactions must be imported.");
                Assert.IsTrue(logs.All(l => l is CopilotAuditLogContent));
                Assert.AreEqual(0, loader.DeserialisationFailureCount);
                Assert.AreEqual(0, DeserialiseWarnings(logger).Count, string.Join(Environment.NewLine, DeserialiseWarnings(logger)));
            }
        }

        [TestMethod]
        public async Task Loader_UnreadableRecords_AreCountedAndOnlyTheFirstFewLogged()
        {
            const int unreadable = ActivityReportWebLoader.MaxDeserialisationFailuresLoggedInFull + 3;

            // AccessedResources must be a list, so these records genuinely cannot be bound.
            var bad = Enumerable.Range(0, unreadable).Select(i =>
            {
                var record = JObject.Parse(Record(null, id: $"00000000-0000-0000-0000-0000000001{i:00}"));
                record["CopilotEventData"]["AccessedResources"] = "not a list";
                return record.ToString();
            });
            var json = "[" + string.Join(",", bad) + "," + Record(Example1PolicyDetails, id: "00000000-0000-0000-0000-000000000003") + "]";
            var logger = new RecordingLogger();

            using (var httpClient = new AutoThrottleHttpClient(new StaticJsonHandler(json), logger))
            {
                var loader = NewLoader(httpClient, logger);
                var logs = await loader.Load(ReportInfo());

                Assert.AreEqual(1, logs.Count, "The readable record must still import.");
                Assert.AreEqual(unreadable, loader.DeserialisationFailureCount, "Every skipped record must be counted.");

                var warnings = DeserialiseWarnings(logger);
                Assert.AreEqual(ActivityReportWebLoader.MaxDeserialisationFailuresLoggedInFull,
                    warnings.Count(w => w.StartsWith("Failed to deserialize Copilot log:")),
                    "Only the first few failures are logged in full.");
                Assert.AreEqual(1, logger.Entries.Count(e => e.Message.Contains("not logged individually")),
                    "Reaching the cap must be said once.");

                var stats = new ImportStat { RecordsSkippedDeserialisation = loader.DeserialisationFailureCount };
                StringAssert.Contains(stats.ToString(), $"records skipped (could not deserialise): {unreadable}");
            }
        }

        [TestMethod]
        public void ImportStat_SumsAndReportsSkippedRecords()
        {
            var total = new ImportStat();
            total.AddStats(new ImportStat { RecordsSkippedDeserialisation = 2 });
            total.AddStats(new ImportStat { RecordsSkippedDeserialisation = 3 });

            Assert.AreEqual(5, total.RecordsSkippedDeserialisation);
            StringAssert.Contains(total.ToString(), "records skipped (could not deserialise): 5");
            StringAssert.Contains(new ImportStat().ToString(), "records skipped (could not deserialise): 0");
        }

        private static ActivityReportWebLoader NewLoader(AutoThrottleHttpClient httpClient, ILogger logger)
        {
            return new ActivityReportWebLoader(httpClient, logger, Guid.Empty.ToString(),
                importPowerPlatform: false, importCopilot: true, importDlp: false);
        }

        private static ActivityReportInfo ReportInfo()
        {
            return new ActivityReportInfo
            {
                ContentUri = new Uri("https://contoso.example/activity/content/00000000-0000-0000-0000-000000000012"),
                ContentId = "00000000-0000-0000-0000-000000000013",
                ContentType = ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL,
            };
        }

        private static List<string> DeserialiseWarnings(RecordingLogger logger)
        {
            return logger.Entries
                .Where(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Failed to deserialize"))
                .Select(e => e.Message)
                .ToList();
        }

        private sealed class StaticJsonHandler : HttpMessageHandler
        {
            private readonly string _json;

            public StaticJsonHandler(string json)
            {
                _json = json;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
            }
        }

        private sealed class RecordingDlpStagingWriter : IDlpStagingWriter
        {
            public List<CopilotDlpLogTempEntity> CopilotRows { get; } = new List<CopilotDlpLogTempEntity>();

            public void StageCopilotDlp(CopilotDlpLogTempEntity row) => CopilotRows.Add(row);
            public void StageDlpRuleMatch(DlpRuleMatchLogTempEntity row) { }
            public Task CommitAllChanges() => Task.CompletedTask;
        }

        #endregion
    }
}
