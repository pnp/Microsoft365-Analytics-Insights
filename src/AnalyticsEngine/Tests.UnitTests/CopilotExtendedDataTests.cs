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
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Unit tests for Copilot extended data storage (Messages, AgentActions, AIToolUsages, FlowActions)
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

        #region Serialization Tests

        [TestMethod]
        public void SerializeMessages_WithClassicAnswers_ReturnsCorrectJson()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                ClassicAnswers = 2,
                GenerativeAnswers = 0,
                TenantGraphGroundedAnswers = 0
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeMessages(cost);

            // Assert
            Assert.IsNotNull(json);
            var messages = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Assert.AreEqual(2, messages.Count);
            Assert.AreEqual("Classic", (string)messages[0].Type);
            Assert.IsFalse((bool)messages[0].IsPrompt);
        }

        [TestMethod]
        public void SerializeMessages_WithMixedAnswers_ReturnsCorrectJson()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                ClassicAnswers = 1,
                GenerativeAnswers = 2,
                TenantGraphGroundedAnswers = 1
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeMessages(cost);

            // Assert
            Assert.IsNotNull(json);
            var messages = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Assert.AreEqual(4, messages.Count);
            
            // Should have 1 Classic, 2 Generative, 1 TenantGraph
            var classicCount = messages.Count(m => (string)m.Type == "Classic");
            var generativeCount = messages.Count(m => (string)m.Type == "Generative");
            var tenantGraphCount = messages.Count(m => (string)m.Type == "TenantGraph");
            
            Assert.AreEqual(1, classicCount);
            Assert.AreEqual(2, generativeCount);
            Assert.AreEqual(1, tenantGraphCount);
        }

        [TestMethod]
        public void SerializeMessages_WithNullCost_ReturnsNull()
        {
            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeMessages(null);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public void SerializeAgentActions_WithActions_ReturnsCorrectJson()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                AgentActionCount = 3
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAgentActions(cost);

            // Assert
            Assert.IsNotNull(json);
            var actions = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Assert.AreEqual(3, actions.Count);
            Assert.AreEqual("Action", (string)actions[0].Type);
        }

        [TestMethod]
        public void SerializeAgentActions_WithZeroActions_ReturnsNull()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                AgentActionCount = 0
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAgentActions(cost);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public void SerializeAIToolUsages_WithMultipleTiers_ReturnsCorrectJson()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                BasicAIToolResponses = 5,
                StandardAIToolResponses = 10,
                PremiumAIToolResponses = 2
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAIToolUsages(cost);

            // Assert
            Assert.IsNotNull(json);
            var toolUsages = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Assert.AreEqual(3, toolUsages.Count);
            
            var basicTool = toolUsages.FirstOrDefault(t => (string)t.Tier == "Basic");
            var standardTool = toolUsages.FirstOrDefault(t => (string)t.Tier == "Standard");
            var premiumTool = toolUsages.FirstOrDefault(t => (string)t.Tier == "Premium");
            
            Assert.IsNotNull(basicTool);
            Assert.AreEqual(5, (int)basicTool.ResponseCount);
            Assert.IsNotNull(standardTool);
            Assert.AreEqual(10, (int)standardTool.ResponseCount);
            Assert.IsNotNull(premiumTool);
            Assert.AreEqual(2, (int)premiumTool.ResponseCount);
        }

        [TestMethod]
        public void SerializeAIToolUsages_WithOnlyBasicTier_ReturnsCorrectJson()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                BasicAIToolResponses = 15,
                StandardAIToolResponses = 0,
                PremiumAIToolResponses = 0
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAIToolUsages(cost);

            // Assert
            Assert.IsNotNull(json);
            var toolUsages = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Assert.AreEqual(1, toolUsages.Count);
            Assert.AreEqual("Basic", (string)toolUsages[0].Tier);
            Assert.AreEqual(15, (int)toolUsages[0].ResponseCount);
        }

        [TestMethod]
        public void SerializeFlowActions_WithActions_ReturnsCorrectJson()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                FlowActions = 150
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeFlowActions(cost);

            // Assert
            Assert.IsNotNull(json);
            var flowAction = JsonConvert.DeserializeObject<dynamic>(json);
            Assert.AreEqual(150, (int)flowAction.ActionCount);
        }

        [TestMethod]
        public void SerializeFlowActions_WithZeroActions_ReturnsNull()
        {
            // Arrange
            var cost = new CopilotCreditEstimation
            {
                FlowActions = 0
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeFlowActions(cost);

            // Assert
            Assert.IsNull(json);
        }

        #endregion

        #region Database Integration Tests

        [TestMethod]
        public async Task SaveCopilotEvent_WithMessages_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_messages', 'U')").FirstOrDefault() == 0)
                {
                    Assert.Inconclusive("Messages tables do not exist. Run migration first.");
                    return;
                }

                // Arrange - Clear test data
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Messages Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@messages.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Create cost with messages
                var cost = new CopilotCreditEstimation
                {
                    ClassicAnswers = 1,
                    GenerativeAnswers = 2,
                    TenantGraphGroundedAnswers = 1
                };

                var auditLogContent = new CopilotAuditLogContent
                {
                    Cost = cost,
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
                var messages = await db.CopilotMessages
                    .Include(m => m.MessageType)
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(4, messages.Count);
                
                var classicMessages = messages.Where(m => m.MessageType?.Name == "Classic").ToList();
                var generativeMessages = messages.Where(m => m.MessageType?.Name == "Generative").ToList();
                var tenantGraphMessages = messages.Where(m => m.MessageType?.Name == "TenantGraph").ToList();

                Assert.AreEqual(1, classicMessages.Count);
                Assert.AreEqual(2, generativeMessages.Count);
                Assert.AreEqual(1, tenantGraphMessages.Count);

                // Verify all are marked as not prompts
                Assert.IsTrue(messages.All(m => !m.IsPrompt));
            }
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithAgentActions_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_agent_actions', 'U')").FirstOrDefault() == 0)
                {
                    Assert.Inconclusive("Agent Actions tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Agent Actions" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@actions.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var cost = new CopilotCreditEstimation
                {
                    AgentActionCount = 5
                };

                var auditLogContent = new CopilotAuditLogContent
                {
                    Cost = cost,
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
                var agentActions = await db.CopilotAgentActions
                    .Include(a => a.ActionType)
                    .Where(a => a.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(5, agentActions.Count);
                Assert.IsTrue(agentActions.All(a => a.ActionType?.Name == "Action"));
            }
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithAIToolUsages_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_ai_tool_usages', 'U')").FirstOrDefault() == 0)
                {
                    Assert.Inconclusive("AI Tool Usages tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test AI Tools" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@aitools.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var cost = new CopilotCreditEstimation
                {
                    BasicAIToolResponses = 10,
                    StandardAIToolResponses = 20,
                    PremiumAIToolResponses = 5
                };

                var auditLogContent = new CopilotAuditLogContent
                {
                    Cost = cost,
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
                var toolUsages = await db.CopilotAIToolUsages
                    .Include(t => t.Tier)
                    .Where(t => t.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(3, toolUsages.Count);

                var basicUsage = toolUsages.FirstOrDefault(t => t.Tier?.Name == "Basic");
                var standardUsage = toolUsages.FirstOrDefault(t => t.Tier?.Name == "Standard");
                var premiumUsage = toolUsages.FirstOrDefault(t => t.Tier?.Name == "Premium");

                Assert.IsNotNull(basicUsage);
                Assert.AreEqual(10, basicUsage.ResponseCount);
                Assert.IsNotNull(standardUsage);
                Assert.AreEqual(20, standardUsage.ResponseCount);
                Assert.IsNotNull(premiumUsage);
                Assert.AreEqual(5, premiumUsage.ResponseCount);
            }
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithFlowActions_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_flow_actions', 'U')").FirstOrDefault() == 0)
                {
                    Assert.Inconclusive("Flow Actions table does not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Flow Actions" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@flow.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var cost = new CopilotCreditEstimation
                {
                    FlowActions = 250
                };

                var auditLogContent = new CopilotAuditLogContent
                {
                    Cost = cost,
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
                var flowActions = await db.CopilotFlowActions
                    .Where(f => f.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(1, flowActions.Count);
                Assert.AreEqual(250, flowActions[0].ActionCount);
            }
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithAllDataTypes_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_messages', 'U')").FirstOrDefault() == 0)
                {
                    Assert.Inconclusive("Extended data tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test All Data" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@all.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Create comprehensive cost data
                var cost = new CopilotCreditEstimation
                {
                    ClassicAnswers = 2,
                    GenerativeAnswers = 3,
                    TenantGraphGroundedAnswers = 1,
                    AgentActionCount = 4,
                    BasicAIToolResponses = 15,
                    StandardAIToolResponses = 25,
                    PremiumAIToolResponses = 8,
                    FlowActions = 175
                };

                var auditLogContent = new CopilotAuditLogContent
                {
                    Cost = cost,
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

                // Assert - Verify all data types were saved
                var messages = await db.CopilotMessages.Where(m => m.ChatId == commonEvent.Id).ToListAsync();
                var agentActions = await db.CopilotAgentActions.Where(a => a.ChatId == commonEvent.Id).ToListAsync();
                var toolUsages = await db.CopilotAIToolUsages.Where(t => t.ChatId == commonEvent.Id).ToListAsync();
                var flowActions = await db.CopilotFlowActions.Where(f => f.ChatId == commonEvent.Id).ToListAsync();

                Assert.AreEqual(6, messages.Count); // 2 + 3 + 1
                Assert.AreEqual(4, agentActions.Count);
                Assert.AreEqual(3, toolUsages.Count); // One per tier
                Assert.AreEqual(1, flowActions.Count);
                Assert.AreEqual(175, flowActions[0].ActionCount);

                // Verify we can reconstruct credit estimate from SQL
                var totalMessages = messages.Count;
                var totalAgentActions = agentActions.Count;
                var totalToolResponses = toolUsages.Sum(t => t.ResponseCount);
                var totalFlowActions = flowActions.Sum(f => f.ActionCount);

                Assert.AreEqual(6, totalMessages);
                Assert.AreEqual(4, totalAgentActions);
                Assert.AreEqual(48, totalToolResponses); // 15 + 25 + 8
                Assert.AreEqual(175, totalFlowActions);
            }
        }

        #endregion

        #region Helper Methods

        private async Task ClearExtendedDataTables(AnalyticsEntitiesContext db)
        {
            // Clear Messages
            if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_messages', 'U')").FirstOrDefault() != 0)
            {
                db.CopilotMessages.RemoveRange(db.CopilotMessages);
                db.CopilotMessageTypes.RemoveRange(db.CopilotMessageTypes);
            }

            // Clear Agent Actions
            if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_agent_actions', 'U')").FirstOrDefault() != 0)
            {
                db.CopilotAgentActions.RemoveRange(db.CopilotAgentActions);
                db.CopilotAgentActionTypes.RemoveRange(db.CopilotAgentActionTypes);
            }

            // Clear AI Tool Usages
            if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_ai_tool_usages', 'U')").FirstOrDefault() != 0)
            {
                db.CopilotAIToolUsages.RemoveRange(db.CopilotAIToolUsages);
                db.CopilotAIToolTiers.RemoveRange(db.CopilotAIToolTiers);
            }

            // Clear Flow Actions
            if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_flow_actions', 'U')").FirstOrDefault() != 0)
            {
                db.CopilotFlowActions.RemoveRange(db.CopilotFlowActions);
            }

            await db.SaveChangesAsync();
        }

        #endregion
    }
}
