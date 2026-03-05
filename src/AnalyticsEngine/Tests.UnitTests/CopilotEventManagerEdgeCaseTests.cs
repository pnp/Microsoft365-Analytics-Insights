using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    /// Edge-case and regression tests for CopilotAuditEventManager: deduplication, null handling, mixed contexts.
    /// </summary>
    [TestClass]
    public class CopilotEventManagerEdgeCaseTests : CopilotTestBase
    {
        /// <summary>
        /// Regression test: an event with multiple TEAMS_CHAT contexts must produce only one copilot_chats row.
        /// Previously caused BatchSaveException "Violation of PRIMARY KEY constraint 'PK_dbo.copilot_chats'".
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerMultipleChatContextsDoesNotDuplicate()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "MultiChatCtx Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@multichat.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chat1@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            },
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chat2@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            },
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chat3@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, commonEvent);

                // Previously threw BatchSaveException with PK violation on copilot_chats
                await copilotEventManager.CommitAllChanges();

                var chatCount = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, chatCount, "Multiple TEAMS_CHAT contexts for the same event should produce exactly one copilot_chats row.");
            }
        }

        /// <summary>
        /// Null audit record or null base event should be handled gracefully without throwing.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerNullInputsHandledGracefully()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "NullInput Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@null.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Null audit record
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(null, commonEvent);

                // Null base event
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData { AppHost = "Teams" }
                }, null);

                // Both null
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(null, null);

                // CommitAllChanges should succeed with 0 rows staged
                await copilotEventManager.CommitAllChanges();

                var chatCount = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(0, chatCount, "Null inputs should not produce any copilot_chats rows.");
            }
        }

        /// <summary>
        /// An event with no contexts (null or empty) should be treated as chat-only.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerNoContextsCreatesChatOnly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var eventNullContexts = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "NullCtx Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@nullctx.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var eventEmptyContexts = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "EmptyCtx Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@emptyctx.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.AddRange(new[] { eventNullContexts, eventEmptyContexts });
                await db.SaveChangesAsync();

                // Null Contexts
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = null
                    }
                }, eventNullContexts);

                // Empty Contexts list
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>()
                    }
                }, eventEmptyContexts);

                await copilotEventManager.CommitAllChanges();

                // Both should create chat-only entries
                var chatCount1 = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == eventNullContexts.Id);
                var chatCount2 = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == eventEmptyContexts.Id);
                Assert.AreEqual(1, chatCount1, "Null contexts should create a chat-only row.");
                Assert.AreEqual(1, chatCount2, "Empty contexts should create a chat-only row.");

                // Neither should create file or meeting entries
                var fileCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingCount = await db.CopilotEventMetadataMeetings.CountAsync();
                Assert.AreEqual(0, fileCount, "No file events should be created for null/empty contexts.");
                Assert.AreEqual(0, meetingCount, "No meeting events should be created for null/empty contexts.");
            }
        }

        /// <summary>
        /// An event with [CHAT, FILE] contexts should create entries in both the chat-only and file staging tables.
        /// The common SQL's NOT EXISTS guard prevents a duplicate copilot_chats row when both batches are committed.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerChatAndFileContextsSaveBoth()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "ChatAndFile Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@chatandfile.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Event with a CHAT context followed by a FILE context
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chatthread@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            },
                            new Context
                            {
                                Id = _config.TestCopilotDocContextIdSpSite,
                                Type = _config.TeamSiteFileExtension
                            }
                        }
                    }
                }, commonEvent);

                // Should not throw — the same event_id goes into both staging tables,
                // but the common SQL's NOT EXISTS prevents a duplicate copilot_chats insert.
                await copilotEventManager.CommitAllChanges();

                // Exactly one copilot_chats row
                var chatCount = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, chatCount, "Mixed CHAT+FILE contexts should produce exactly one copilot_chats row.");

                // File metadata should also be saved
                var fileCount = await db.CopilotEventMetadataFiles.CountAsync(f => f.RelatedChat.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, fileCount, "File context should produce a file metadata row.");
            }
        }

        /// <summary>
        /// Committing the same event_id in two separate batches should not throw.
        /// The second batch's NOT EXISTS check should skip the duplicate.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerDuplicateEventIdAcrossBatchesIsHandled()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "CrossBatch Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@crossbatch.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Batch 1: save and commit the event
                var manager1 = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
                await manager1.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:batch1@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, commonEvent);
                await manager1.CommitAllChanges();

                // Batch 2: same event_id committed again (e.g. overlapping Activity API content blobs)
                var manager2 = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
                await manager2.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:batch2@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, commonEvent);

                // Should not throw — NOT EXISTS skips the duplicate
                await manager2.CommitAllChanges();

                // Still exactly one copilot_chats row
                var chatCount = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, chatCount, "Duplicate event_id across batches should produce exactly one copilot_chats row.");
            }
        }

        /// <summary>
        /// When an event has a meeting context followed by a chat context the meeting
        /// context takes priority and the loop breaks — no chat-only row should be created.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerMeetingContextTakesPriorityOverChat()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "MeetingPriority Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@meetingpriority.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:meeting_test123@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                            },
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chat_shouldskip@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                var meetingCount = await db.CopilotEventMetadataMeetings.CountAsync(m => m.RelatedChat.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, meetingCount, "Meeting context should produce a meeting metadata row.");

                var chatCount = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, chatCount, "Meeting context should produce exactly one copilot_chats row (via the meeting path).");
            }
        }

        /// <summary>
        /// A file context causes the loop to break — a trailing chat context should not create
        /// a separate chat-only staging entry.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerFileContextBreaksBeforeChat()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "FileBreak Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@filebreak.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = _config.TestCopilotDocContextIdSpSite,
                                Type = _config.TeamSiteFileExtension
                            },
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chat_afterfile@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                var fileCount = await db.CopilotEventMetadataFiles.CountAsync(f => f.RelatedChat.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, fileCount, "File context should produce a file metadata row.");

                var chatCount = await db.CopilotChats.CountAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, chatCount, "File context should produce exactly one copilot_chats row (via the file path).");
            }
        }

        /// <summary>
        /// Multiple meeting contexts for the same event should stage only the first meeting.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerMultipleMeetingContextsOnlyFirstStaged()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "MultiMeeting Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@multimeeting.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:meeting_first@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                            },
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:meeting_second@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                var meetingCount = await db.CopilotEventMetadataMeetings.CountAsync(m => m.RelatedChat.AuditEvent.Id == commonEvent.Id);
                Assert.AreEqual(1, meetingCount, "Multiple meeting contexts should produce exactly one meeting metadata row.");
            }
        }

        /// <summary>
        /// A non-null auditRecord with null CopilotEventData should fall into the chat-only path
        /// with an "Unknown" AppHost.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerNullCopilotEventDataCreatesChatOnlyWithUnknownHost()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "NullEventData Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@nulleventdata.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = null
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                var chat = await db.CopilotChats.FirstOrDefaultAsync(c => c.AuditEvent.Id == commonEvent.Id);
                Assert.IsNotNull(chat, "Null CopilotEventData should still create a chat-only row.");
                Assert.AreEqual("Unknown", chat.AppHost, "AppHost should default to 'Unknown' when CopilotEventData is null.");
            }
        }

        /// <summary>
        /// When the metadata loader throws for meeting/file contexts the manager should
        /// catch the exception and not stage any meeting/file rows, while still allowing
        /// CommitAllChanges to succeed.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerMetadataLoaderExceptionHandledGracefully()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(
                    _config.ConnectionStrings.DatabaseConnectionString,
                    new ThrowingCopilotMetadataLoader(),
                    _logger);

                var meetingEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "ThrowMeeting Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@throwmeeting.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var fileEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "ThrowFile Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@throwfile.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.AddRange(new[] { meetingEvent, fileEvent });
                await db.SaveChangesAsync();

                // Meeting context — loader will throw on GetUserIdFromUpn
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:meeting_throw@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                            }
                        }
                    }
                }, meetingEvent);

                // File context — loader will throw on GetSpoFileInfo
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://contoso.sharepoint.com/sites/test/doc.docx",
                                Type = "docx"
                            }
                        }
                    }
                }, fileEvent);

                // CommitAllChanges should succeed even though no rows were staged
                await copilotEventManager.CommitAllChanges();

                var meetingCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var fileCount = await db.CopilotEventMetadataFiles.CountAsync();
                Assert.AreEqual(0, meetingCount, "No meeting rows should be staged when the loader throws.");
                Assert.AreEqual(0, fileCount, "No file rows should be staged when the loader throws.");
            }
        }

        /// <summary>
        /// After CommitAllChanges the internal state should be reset so that a second
        /// batch only contains events staged after the first commit.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerCommitResetsStateForNextBatch()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // --- Batch 1: one chat event ---
                var event1 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Batch1 Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@batch1.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                db.AuditEventsCommon.Add(event1);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:batch1chat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, event1);

                await copilotEventManager.CommitAllChanges();

                var countAfterBatch1 = await db.CopilotChats.CountAsync();

                // --- Batch 2: empty commit (nothing staged) ---
                await copilotEventManager.CommitAllChanges();

                var countAfterBatch2 = await db.CopilotChats.CountAsync();
                Assert.AreEqual(countAfterBatch1, countAfterBatch2,
                    "An empty commit after a prior batch should not create additional rows.");

                // --- Batch 3: one more chat event on the same manager ---
                var event3 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Batch3 Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@batch3.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                db.AuditEventsCommon.Add(event3);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:batch3chat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    }
                }, event3);

                await copilotEventManager.CommitAllChanges();

                var countAfterBatch3 = await db.CopilotChats.CountAsync();
                Assert.AreEqual(countAfterBatch1 + 1, countAfterBatch3,
                    "Third batch should produce exactly one additional copilot_chats row.");
            }
        }
    }
}
