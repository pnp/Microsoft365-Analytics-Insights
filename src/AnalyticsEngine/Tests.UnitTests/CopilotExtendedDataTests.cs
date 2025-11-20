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

        #region Serialization Tests

        [TestMethod]
        public void SerializeMessages_WithResponses_ReturnsCorrectJson()
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
        public void SerializeMessages_WithNullAuditRecord_ReturnsNull()
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
        public void SerializeAccessedResources_WithValidResources_ReturnsCorrectJson()
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
        public void SerializeAccessedResources_WithEmptyList_ReturnsNull()
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
        public void SerializeAccessedResources_WithNullList_ReturnsNull()
        {
            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeAccessedResources(null);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithAccessedResources_SavesCorrectly()
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
        public async Task SaveCopilotEvent_WithMultipleResourceTypes_SavesAllCorrectly()
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

        #region Edge Case Tests

        [TestMethod]
        public void SerializeMessages_WithEmptyMessageList_ReturnsNull()
        {
            // Arrange
            var auditRecord = new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    Messages = new List<Message>()
                }
            };

            // Act
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var json = manager.SerializeMessages(auditRecord);

            // Assert
            Assert.IsNull(json);
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithMixedPromptAndResponseMessages_SavesOnlyResponses()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_messages', 'U')").FirstOrDefault() == null)
                {
                    Assert.Inconclusive("Messages tables do not exist. Run migration first.");
                    return;
                }

                // Arrange
                await ClearExtendedDataTables(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Mixed Messages" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@mixed.com" + DateTime.Now.Ticks },
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
                            new Message { Id = "1", IsPrompt = true, Type = "Classic" },  // Should be filtered
                            new Message { Id = "2", IsPrompt = false, Type = "Classic" },
                            new Message { Id = "3", IsPrompt = true, Type = "Generative" }, // Should be filtered
                            new Message { Id = "4", IsPrompt = false, Type = "Generative" }
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

                // Assert - Should only save non-prompt messages (prompts are filtered during import)
                var messages = await db.CopilotMessages
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(2, messages.Count); // Only 2 response messages, not 4 (prompts filtered out)
            }
        }

        [TestMethod]
        public async Task SaveCopilotEvent_WithNullParsedAuditEvent_HandlesGracefully()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Arrange
                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Null Event" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@null.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var auditLogContent = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = null,
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

                // Act - Should not throw exception
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Assert - Should complete without error (no data saved)
                Assert.IsTrue(true);
            }
        }

        #endregion

        #region Database Integration Tests

        [TestMethod]
        public async Task SaveCopilotEvent_WithMessages_SavesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip if migration hasn't been run
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_messages', 'U')").FirstOrDefault() == null)
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

                // Create audit log content with messages
                var auditLogContent = new CopilotAuditLogContent
                {
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "1", IsPrompt = false, Type = "Classic" },
                            new Message { Id = "2", IsPrompt = false, Type = "Generative" },
                            new Message { Id = "3", IsPrompt = false, Type = "Generative" },
                            new Message { Id = "4", IsPrompt = false, Type = "TenantGraph" }
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
                var messages = await db.CopilotMessages
                    .Where(m => m.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(4, messages.Count);
                
                // Note: MessageType was removed as it was never populated
                // Messages are all responses (prompts filtered during import)
            }
        }

        #endregion

        #region Helper Methods

        private async Task ClearExtendedDataTables(AnalyticsEntitiesContext db)
        {
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
