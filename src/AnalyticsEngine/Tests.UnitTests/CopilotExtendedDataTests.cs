using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Unit tests for Copilot extended data storage (Messages and AccessedResources only)
    /// Note: AgentActions, AIToolUsages, and FlowActions were removed as redundant.
    /// </summary>
    [TestClass]
    public class CopilotExtendedDataTests
    {
        protected ILogger _logger;
        protected TestsAppConfig _config;

        public CopilotExtendedDataTests()
        {
            _logger = new LoggerFactory().CreateLogger("CopilotExtendedDataTests");
            _config = new TestsAppConfig();
        }

        #region Cost Estimation Tests

        [TestMethod]
        public void Copilot_CostEstimation_GenerativeAnswersOnly_CalculatesCorrectly()
        {
            // Arrange - 3 response messages, no tenant resources, no deep reasoning
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': true },
                    { 'Id': '2', 'isPrompt': false },
                    { 'Id': '3', 'isPrompt': false },
                    { 'Id': '4', 'isPrompt': false }
                ],
                'AccessedResources': [],
                'AISystemPlugin': [{ 'Id': 'BingWebSearch', 'Name': 'BuiltIn' }]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            // 3 messages × 2 credits = 6 credits
            Assert.AreEqual(6, cost.TotalCredits);
            Assert.AreEqual(3, cost.GenerativeAnswers);
            Assert.AreEqual(0, cost.TenantGraphGroundedAnswers);
            Assert.AreEqual(0, cost.DeepReasoningActions);
            Assert.AreEqual(6, cost.CreditBreakdown["Generative Answers"]);
        }

        [TestMethod]
        public void Copilot_CostEstimation_TenantGraphGrounding_CalculatesCorrectly()
        {
            // Arrange - 3 response messages with SharePoint resources (tenant graph grounding)
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': true },
                    { 'Id': '2', 'isPrompt': false },
                    { 'Id': '3', 'isPrompt': false },
                    { 'Id': '4', 'isPrompt': false }
                ],
                'AccessedResources': [
                    { 'SiteUrl': 'https://contoso.sharepoint.com/sites/sales/doc.docx', 'Type': 'docx' },
                    { 'SiteUrl': 'https://contoso.sharepoint.com/sites/sales/sheet.xlsx', 'Type': 'xlsx' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            // 3 messages × (2 credits generative + 10 credits tenant graph) = 36 credits
            Assert.AreEqual(36, cost.TotalCredits);
            Assert.AreEqual(3, cost.GenerativeAnswers);
            Assert.AreEqual(3, cost.TenantGraphGroundedAnswers);
            Assert.AreEqual(0, cost.DeepReasoningActions);
            Assert.AreEqual(6, cost.CreditBreakdown["Generative Answers"]); // 3 × 2
            Assert.AreEqual(30, cost.CreditBreakdown["Tenant Graph Grounding"]); // 3 × 10
        }

        [TestMethod]
        public void Copilot_CostEstimation_DeepReasoningWithTenantGraph_CalculatesCorrectly()
        {
            // Arrange - Example payload with Teams files and DEEP_LEO model
            var json = @"{
                'AISystemPlugin': [
                    { 'Id': 'BingWebSearch', 'Name': 'BuiltIn' }
                ],
                'AccessedResources': [
                    {
                        'Action': 'Read',
                        'PolicyDetails': '',
                        'SiteUrl': 'https://fr-prod.asyncgw.teams.microsoft.com/v1/objects/0-frca-d2-d7a936559e3d5f01eef884309ca6b0e1/views/original/comparativa_peajes_2025_2026_actualizado.xlsx',
                        'XPIADetected': false
                    },
                    {
                        'Action': 'Read',
                        'PolicyDetails': '',
                        'SiteUrl': 'https://fr-prod.asyncgw.teams.microsoft.com/v1/objects/0-frca-d15-8fc0b503d934582e98f8b1274353d7cf/views/original/comparativa_peajes_2025_2026.xlsx',
                        'XPIADetected': false
                    }
                ],
                'AppHost': 'Teams',
                'Contexts': [],
                'MessageIds': [],
                'Messages': [
                    { 'Id': '1763549047513', 'JailbreakDetected': false, 'isPrompt': true },
                    { 'Id': '1763549047748', 'JailbreakDetected': false, 'isPrompt': false },
                    { 'Id': '1763549047879', 'JailbreakDetected': false, 'isPrompt': false },
                    { 'Id': '1763549048002', 'JailbreakDetected': false, 'isPrompt': false }
                ],
                'ModelTransparencyDetails': [
                    { 'ModelName': 'DEEP_LEO' }
                ],
                'ThreadId': '19:UaHJMloQ_AuVUtFfv5B2tblIEniRRgkUI4mz9JDtZ3A1@thread.v2'
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            // 3 messages × 2 credits (generative) = 6 credits
            // 3 messages × 10 credits (tenant graph) = 30 credits
            // 1 deep reasoning agent action = 5 credits
            // Total = 41 credits
            Assert.AreEqual(41, cost.TotalCredits);
            Assert.AreEqual(3, cost.GenerativeAnswers);
            Assert.AreEqual(3, cost.TenantGraphGroundedAnswers);
            Assert.AreEqual(1, cost.DeepReasoningActions);
            Assert.AreEqual(6, cost.CreditBreakdown["Generative Answers"]);
            Assert.AreEqual(30, cost.CreditBreakdown["Tenant Graph Grounding"]);
            Assert.AreEqual(5, cost.CreditBreakdown["Agent Actions (Deep Reasoning)"]);
            Assert.IsTrue(cost.ModelsUsed.Contains("DEEP_LEO"));
        }

        [TestMethod]
        public void Copilot_CostEstimation_DeepReasoningWithoutTenantGraph_CalculatesCorrectly()
        {
            // Arrange - Deep reasoning with web search only
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': false },
                    { 'Id': '2', 'isPrompt': false }
                ],
                'AccessedResources': [],
                'ModelTransparencyDetails': [
                    { 'ModelName': 'DEEP_LEO' }
                ],
                'AISystemPlugin': [{ 'Id': 'BingWebSearch', 'Name': 'BuiltIn' }]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            // 2 messages × 2 credits (generative) = 4 credits
            // 1 deep reasoning agent action = 5 credits
            // Total = 9 credits
            Assert.AreEqual(9, cost.TotalCredits);
            Assert.AreEqual(2, cost.GenerativeAnswers);
            Assert.AreEqual(0, cost.TenantGraphGroundedAnswers);
            Assert.AreEqual(1, cost.DeepReasoningActions);
            Assert.AreEqual(4, cost.CreditBreakdown["Generative Answers"]);
            Assert.AreEqual(5, cost.CreditBreakdown["Agent Actions (Deep Reasoning)"]);
        }

        [TestMethod]
        public void Copilot_CostEstimation_OneDriveResources_DetectedAsTenantGraph()
        {
            // Arrange
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': false }
                ],
                'AccessedResources': [
                    { 'SiteUrl': 'https://contoso-my.sharepoint.com/personal/user/Documents/file.docx', 'Type': 'docx' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            // 1 message × (2 + 10) = 12 credits
            Assert.AreEqual(12, cost.TotalCredits);
            Assert.AreEqual(1, cost.TenantGraphGroundedAnswers);
        }

        [TestMethod]
        public void Copilot_CostEstimation_TeamsAsyncGatewayResources_DetectedAsTenantGraph()
        {
            // Arrange - Teams async gateway URL pattern
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': false }
                ],
                'AccessedResources': [
                    { 'SiteUrl': 'https://fr-prod.asyncgw.teams.microsoft.com/v1/objects/file.xlsx' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            Assert.AreEqual(12, cost.TotalCredits);
            Assert.AreEqual(1, cost.TenantGraphGroundedAnswers);
        }

        [TestMethod]
        public void Copilot_CostEstimation_MultipleResourceTypes_CountedOnce()
        {
            // Arrange - Multiple resources but billing is per message, not per resource
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': false }
                ],
                'AccessedResources': [
                    { 'SiteUrl': 'https://contoso.sharepoint.com/doc1.docx', 'Type': 'docx' },
                    { 'SiteUrl': 'https://contoso.sharepoint.com/doc2.docx', 'Type': 'docx' },
                    { 'SiteUrl': 'https://contoso.sharepoint.com/doc3.xlsx', 'Type': 'xlsx' },
                    { 'SiteUrl': 'https://contoso.sharepoint.com/doc4.pptx', 'Type': 'pptx' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            // Still just 1 message × (2 + 10) = 12 credits (not multiplied by resource count)
            Assert.AreEqual(12, cost.TotalCredits);
            Assert.AreEqual(4, cost.ResourceTypeBreakdown.Values.Sum()); // 4 resources for reference
        }

        [TestMethod]
        public void Copilot_CostEstimation_NullOrEmptyInput_ReturnsZero()
        {
            // Arrange & Act
            var costNull = CopilotCreditEstimation.Analyze((string)null);
            var costEmpty = CopilotCreditEstimation.Analyze("");
            var costWhitespace = CopilotCreditEstimation.Analyze("   ");

            // Assert
            Assert.AreEqual(0, costNull.TotalCredits);
            Assert.AreEqual(0, costEmpty.TotalCredits);
            Assert.AreEqual(0, costWhitespace.TotalCredits);
        }

        [TestMethod]
        public void Copilot_CostEstimation_NoMessages_ReturnsZero()
        {
            // Arrange
            var json = @"{
                'Messages': [],
                'AccessedResources': [
                    { 'SiteUrl': 'https://contoso.sharepoint.com/doc.docx' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            Assert.AreEqual(0, cost.TotalCredits);
            Assert.AreEqual(0, cost.GenerativeAnswers);
        }

        [TestMethod]
        public void Copilot_CostEstimation_OnlyPromptMessages_ReturnsZero()
        {
            // Arrange
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': true },
                    { 'Id': '2', 'isPrompt': true }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            Assert.AreEqual(0, cost.TotalCredits);
            Assert.AreEqual(0, cost.GenerativeAnswers);
        }

        [TestMethod]
        public void Copilot_CostEstimation_ResourceTypeBreakdown_PopulatedCorrectly()
        {
            // Arrange
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': false }
                ],
                'AccessedResources': [
                    { 'Type': 'docx' },
                    { 'Type': 'docx' },
                    { 'Type': 'xlsx' },
                    { 'Type': 'pptx' },
                    { 'Type': '' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            Assert.AreEqual(2, cost.ResourceTypeBreakdown["docx"]);
            Assert.AreEqual(1, cost.ResourceTypeBreakdown["xlsx"]);
            Assert.AreEqual(1, cost.ResourceTypeBreakdown["pptx"]);
            Assert.AreEqual(1, cost.ResourceTypeBreakdown["WebPage"]); // Empty type defaults to WebPage
        }

        [TestMethod]
        public void Copilot_CostEstimation_CaseInsensitiveModelDetection_WorksCorrectly()
        {
            // Arrange - Test case insensitivity for DEEP_LEO
            var json = @"{
                'Messages': [
                    { 'Id': '1', 'isPrompt': false }
                ],
                'ModelTransparencyDetails': [
                    { 'ModelName': 'deep_leo' }
                ]
            }";

            // Act
            var cost = CopilotCreditEstimation.Analyze(json);

            // Assert
            Assert.AreEqual(7, cost.TotalCredits); // 2 + 5
            Assert.AreEqual(1, cost.DeepReasoningActions);
        }

        #endregion

        #region Serialization Tests

        [TestMethod]
        public void Copilot_SerializeMessages_WithResponses_ReturnsCorrectJson()
        {
            // Arrange
            var auditRecord = new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    Messages = new List<Message>
                    {
                        new Message { Id = "1", IsPrompt = false },
                        new Message { Id = "2", IsPrompt = false }
                    }
                }
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeMessages(auditRecord);

            // Assert
            Assert.IsNotNull(json);
            var messages = JsonConvert.DeserializeObject<List<Message>>(json);
            Assert.AreEqual(2, messages.Count);
            Assert.IsFalse(messages[0].IsPrompt);
        }

        [TestMethod]
        public void Copilot_SerializeMessages_WithNullAuditRecord_ReturnsNull()
        {
            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeMessages(null);

            // Assert
            Assert.IsNull(json);
        }

        #endregion

        #region Accessed Resources Tests

        [TestMethod]
        public void Copilot_SerializeAccessedResources_WithValidResources_ReturnsCorrectJson()
        {
            // Arrange
            var resources = new List<AccessedResource>
            {
                new AccessedResource 
                { 
                    Id = "resource-id-1", 
                    Name = "Document1.docx", 
                    Type = "docx",
                    SiteUrl = "https://contoso.sharepoint.com/sites/sales",
                    SensitivityLabelId = "label-123"
                },
                new AccessedResource 
                { 
                    Id = "resource-id-2", 
                    Name = "Presentation.pptx", 
                    Type = "pptx",
                    SiteUrl = "https://contoso.sharepoint.com/sites/marketing"
                }
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAccessedResources(resources);

            // Assert
            Assert.IsNotNull(json);
            var deserializedResources = JsonConvert.DeserializeObject<List<AccessedResource>>(json);
            Assert.AreEqual(2, deserializedResources.Count);
            Assert.AreEqual("Document1.docx", deserializedResources[0].Name);
            Assert.AreEqual("resource-id-1", deserializedResources[0].Id);
            Assert.AreEqual("label-123", deserializedResources[0].SensitivityLabelId);
        }

        [TestMethod]
        public void Copilot_SerializeAccessedResources_WithEmptyList_ReturnsNull()
        {
            // Arrange
            var resources = new List<AccessedResource>();

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAccessedResources(resources);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public void Copilot_SerializeAccessedResources_WithNullList_ReturnsNull()
        {
            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAccessedResources(null);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithAccessedResources_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("Accessed Resources tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Resources" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@resources.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:testchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        },
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource 
                            { 
                                Id = "resource-id-1", 
                                Name = "Document1.docx", 
                                Type = "docx",
                                SiteUrl = "https://contoso.sharepoint.com/sites/sales",
                                SensitivityLabelId = "label-123"
                            },
                            new AccessedResource 
                            { 
                                Id = "resource-id-2", 
                                Name = "Presentation.pptx", 
                                Type = "pptx",
                                SiteUrl = "https://contoso.sharepoint.com/sites/marketing"
                            }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert
                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(r => r.ResourceId)
                    .Include(r => r.ResourceName)
                    .Include(r => r.ResourceType)
                    .Include(r => r.SensitivityLabel)
                    .Where(r => r.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(2, accessedResources.Count);

                var resource1 = accessedResources.FirstOrDefault(r => r.ResourceName?.Name == "Document1.docx");
                Assert.IsNotNull(resource1);
                Assert.AreEqual("resource-id-1", resource1.ResourceId?.ResourceId);
                Assert.AreEqual("docx", resource1.ResourceType?.Name);
                Assert.AreEqual("label-123", resource1.SensitivityLabel?.LabelId);

                var resource2 = accessedResources.FirstOrDefault(r => r.ResourceName?.Name == "Presentation.pptx");
                Assert.IsNotNull(resource2);
                Assert.AreEqual("resource-id-2", resource2.ResourceId?.ResourceId);
                Assert.AreEqual("pptx", resource2.ResourceType?.Name);
            }
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithMultipleResourceTypes_SavesAllCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("Accessed Resources tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Multiple Resources" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@multiresources.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:testchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        },
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { Id = "sp-1", Name = "Doc.docx", Type = "docx", SiteUrl = "https://contoso.sharepoint.com" },
                            new AccessedResource { Id = "sp-2", Name = "Sheet.xlsx", Type = "xlsx", SiteUrl = "https://contoso.sharepoint.com" },
                            new AccessedResource { Id = "od-1", Name = "Personal.pdf", Type = "pdf", SiteUrl = "https://contoso-my.sharepoint.com" },
                            new AccessedResource { Id = "teams-1", Name = "Message", Type = "TeamsMessage", SiteUrl = "https://teams.microsoft.com" },
                            new AccessedResource { Id = "email-1", Name = "Email", Type = "EmailMessage" }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert
                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(r => r.ResourceType)
                    .Where(r => r.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(5, accessedResources.Count);

                var resourceTypes = accessedResources.Select(r => r.ResourceType?.Name).Distinct().ToList();
                Assert.IsTrue(resourceTypes.Contains("docx"));
                Assert.IsTrue(resourceTypes.Contains("xlsx"));
                Assert.IsTrue(resourceTypes.Contains("pdf"));
                Assert.IsTrue(resourceTypes.Contains("TeamsMessage"));
                Assert.IsTrue(resourceTypes.Contains("EmailMessage"));
            }
        }

        #endregion

        #region Model Transparency Tests

        [TestMethod]
        public void Copilot_SerializeModelTransparencyDetails_WithValidDetails_ReturnsCorrectJson()
        {
            // Arrange
            var auditRecord = new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    ModelTransparencyDetails = new List<ModelTransparencyDetail>
                    {
                        new ModelTransparencyDetail { ModelName = "DEEP_LEO" },
                        new ModelTransparencyDetail { ModelName = "GPT-4" }
                    }
                }
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeModelTransparencyDetails(auditRecord);

            // Assert
            Assert.IsNotNull(json);
            var deserializedModels = JsonConvert.DeserializeObject<List<ModelTransparencyDetail>>(json);
            Assert.AreEqual(2, deserializedModels.Count);
            Assert.AreEqual("DEEP_LEO", deserializedModels[0].ModelName);
            Assert.AreEqual("GPT-4", deserializedModels[1].ModelName);
        }

        [TestMethod]
        public void Copilot_SerializeModelTransparencyDetails_WithNullDetails_ReturnsNull()
        {
            // Arrange
            var auditRecord = new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    ModelTransparencyDetails = null
                }
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeModelTransparencyDetails(auditRecord);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public void Copilot_SerializeModelTransparencyDetails_WithEmptyList_ReturnsNull()
        {
            // Arrange
            var auditRecord = new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    ModelTransparencyDetails = new List<ModelTransparencyDetail>()
                }
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeModelTransparencyDetails(auditRecord);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithModelTransparency_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_ai_models', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("AI Models tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Model Transparency" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@models.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "1", IsPrompt = false }
                        },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO" }
                        }
                    },
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:testchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert
                var aiModels = await db.CopilotEventAIModels
                    .Include(m => m.AIModel)
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(1, aiModels.Count);
                Assert.AreEqual("DEEP_LEO", aiModels[0].AIModel.Name);
            }
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithMultipleModels_SavesAllCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_ai_models', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("AI Models tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Multiple Models" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@multimodels.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "1", IsPrompt = false }
                        },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO" },
                            new ModelTransparencyDetail { ModelName = "GPT-4" },
                            new ModelTransparencyDetail { ModelName = "GPT-3.5-Turbo" }
                        }
                    },
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:testchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert
                var aiModels = await db.CopilotEventAIModels
                    .Include(m => m.AIModel)
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(3, aiModels.Count);
                
                var modelNames = aiModels.Select(m => m.AIModel.Name).OrderBy(n => n).ToList();
                Assert.IsTrue(modelNames.Contains("DEEP_LEO"));
                Assert.IsTrue(modelNames.Contains("GPT-4"));
                Assert.IsTrue(modelNames.Contains("GPT-3.5-Turbo"));
            }
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithDuplicateModels_DeduplicatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_ai_models', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("AI Models tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // Create first event with DEEP_LEO
                var commonEvent1 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Dedup 1" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@dedup1.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent1);
                await db.SaveChangesAsync();

                var auditLogContent1 = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message> { new Message { Id = "1", IsPrompt = false } },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO" }
                        }
                    },
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context { Id = "https://microsoft.teams.com/threads/19:testchat1@thread.v2", Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT }
                        }
                    }
                };

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent1, commonEvent1);
                await copilotEventManager.CommitAllChanges();

                // Create second event with the same DEEP_LEO model
                var commonEvent2 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Dedup 2" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@dedup2.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent2);
                await db.SaveChangesAsync();

                var auditLogContent2 = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message> { new Message { Id = "2", IsPrompt = false } },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO" }
                        }
                    },
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context { Id = "https://microsoft.teams.com/threads/19:testchat2@thread.v2", Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent2, commonEvent2);
                await copilotEventManager.CommitAllChanges();

                // Assert - Should only have one DEEP_LEO entry in lookup table
                var allModels = await db.CopilotAIModels.ToListAsync();
                var deepLeoModels = allModels.Where(m => m.Name == "DEEP_LEO").ToList();
                Assert.AreEqual(1, deepLeoModels.Count, "DEEP_LEO should only appear once in lookup table");

                // Assert - Both events should link to the same model
                var event1Models = await db.CopilotEventAIModels.Include(m => m.AIModel).Where(m => m.ChatId == commonEvent1.Id).ToListAsync();
                var event2Models = await db.CopilotEventAIModels.Include(m => m.AIModel).Where(m => m.ChatId == commonEvent2.Id).ToListAsync();

                Assert.AreEqual(1, event1Models.Count);
                Assert.AreEqual(1, event2Models.Count);
                Assert.AreEqual(event1Models[0].ModelId, event2Models[0].ModelId, "Both events should reference the same model ID");
            }
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithNoModels_DoesNotCreateModelRecords()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_ai_models', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("AI Models tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test No Models" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@nomodels.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "1", IsPrompt = false }
                        },
                        ModelTransparencyDetails = null // No model information
                    },
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:testchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert
                var aiModels = await db.CopilotEventAIModels
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(0, aiModels.Count, "Should not create model records when ModelTransparencyDetails is null");
            }
        }

        [TestMethod]
        public async Task Copilot_SaveCopilotEvent_WithDeepReasoningModel_CalculatesCostCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_ai_models', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("AI Models tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Deep Reasoning Cost" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@deepcost.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "1", IsPrompt = false },
                            new Message { Id = "2", IsPrompt = false }
                        },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO" }
                        },
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { SiteUrl = "https://contoso.sharepoint.com/doc.docx", Type = "docx" }
                        }
                    },
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:testchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                };

                // Act
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert - Verify model was saved
                var aiModels = await db.CopilotEventAIModels
                    .Include(m => m.AIModel)
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(1, aiModels.Count);
                Assert.AreEqual("DEEP_LEO", aiModels[0].AIModel.Name);

                // Assert - Verify cost calculation
                var cost = CopilotCreditEstimation.Analyze(auditLogContent.ParsedAuditEvent);
                // 2 messages × (2 generative + 10 tenant graph) + 5 deep reasoning = 29 credits
                Assert.AreEqual(29, cost.TotalCredits);
                Assert.AreEqual(1, cost.DeepReasoningActions);
                Assert.IsTrue(cost.ModelsUsed.Contains("DEEP_LEO"));
            }
        }

        #endregion

        #region Helper Methods

        private async Task ClearExtendedDataTables(AnalyticsEntitiesContext db)
        {
            // Clear AI Models
            if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_ai_models', 'U')").FirstOrDefault() != null)
            {
                db.CopilotEventAIModels.RemoveRange(db.CopilotEventAIModels);
                db.CopilotAIModels.RemoveRange(db.CopilotAIModels);
            }

            // Clear Accessed Resources
            if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault() != null)
            {
                db.CopilotEventAccessedResources.RemoveRange(db.CopilotEventAccessedResources);
                db.CopilotAccessedResourceIds.RemoveRange(db.CopilotAccessedResourceIds);
                db.CopilotAccessedResourceNames.RemoveRange(db.CopilotAccessedResourceNames);
                db.CopilotAccessedResourceTypes.RemoveRange(db.CopilotAccessedResourceTypes);
                db.CopilotSensitivityLabels.RemoveRange(db.CopilotSensitivityLabels);
            }

            // Clear Messages
            if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_messages', 'U')").FirstOrDefault() != null)
            {
                db.CopilotMessages.RemoveRange(db.CopilotMessages);
            }

            await db.SaveChangesAsync();
        }

        #endregion
    }
}

