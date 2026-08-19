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
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the Copilot audit fields that used to be parsed and then silently dropped
    /// (milestone "Copilot: persist parsed-but-dropped audit fields"):
    ///
    ///   * CopilotEventData.ThreadId, ClientRegion, CopilotLogVersion   -> copilot_chats columns
    ///   * ALL Contexts (Id / Type / ContainerId)                       -> copilot_event_contexts
    ///   * AISystemPlugin (Id / Name / Version)                         -> lookup + junction
    ///   * AccessedResources[].Action / .listItemUniqueId               -> copilot_event_accessed_resources
    ///   * Messages[].Size / .isPrompt                                  -> copilot_event_messages
    ///   * ModelTransparencyDetails ModelProviderName / ModelVersion    -> copilot_ai_models
    ///
    /// These exercise the real merge SQL (common_upsert_copilot_agents.sql) against the test database,
    /// not just the serialisation, because most of the mapping lives in that merge.
    ///
    /// Test data is synthetic and deliberately includes non-ASCII (Greek) content, since SharePoint
    /// URLs / file names routinely contain it and every column here must be Unicode-safe.
    /// </summary>
    [TestClass]
    public class CopilotDroppedAuditFieldsTests : CopilotTestBase
    {
        // "Καλημέρα κόσμε" - the classic Greek charset sample (synthetic; no customer data).
        private const string Greek = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5";

        private static string GreekUrl =>
            $"https://contoso.sharepoint.com/sites/example/Shared Documents/{Greek}.pdf";

        #region Serialization

        [TestMethod]
        public void Copilot_SerializeContexts_RoundTripsAllContextsIncludingContainerId()
        {
            var manager = NewManager();

            var json = manager.SerializeContexts(new List<Context>
            {
                new Context { Id = "ctx-1", Type = "docx", ContainerId = "container-1" },
                new Context { Id = GreekUrl, Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT },
            });

            Assert.IsNotNull(json);
            var back = JsonConvert.DeserializeObject<List<Context>>(json);
            Assert.AreEqual(2, back.Count, "Every context must be serialized, not just the first.");
            Assert.AreEqual("container-1", back[0].ContainerId);
            Assert.AreEqual(GreekUrl, back[1].Id, "Non-ASCII context ids must round-trip unchanged.");
        }

        [TestMethod]
        public void Copilot_SerializeContexts_WithNullOrEmpty_ReturnsNull()
        {
            var manager = NewManager();
            Assert.IsNull(manager.SerializeContexts(null));
            Assert.IsNull(manager.SerializeContexts(new List<Context>()));
        }

        [TestMethod]
        public void Copilot_SerializeAISystemPlugins_PrefersCopilotEventDataThenParsedEvent()
        {
            var manager = NewManager();

            var fromEventData = manager.SerializeAISystemPlugins(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    AISystemPlugin = new List<AISystemPlugin> { new AISystemPlugin { Id = "FromEventData", Name = "BuiltIn", Version = "1.0" } }
                }
            });
            Assert.IsTrue(fromEventData.Contains("FromEventData"));
            Assert.IsTrue(fromEventData.Contains("1.0"), "Version is part of the schema and must be serialized.");

            // Call sites that only populate ParsedAuditEvent must still get their plugins staged.
            var fromParsed = manager.SerializeAISystemPlugins(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData(),
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    AISystemPlugin = new List<AISystemPlugin> { new AISystemPlugin { Id = "FromParsed", Name = "BuiltIn" } }
                }
            });
            Assert.IsTrue(fromParsed.Contains("FromParsed"));
        }

        [TestMethod]
        public void Copilot_SerializeAISystemPlugins_WithNothingToSerialize_ReturnsNull()
        {
            var manager = NewManager();
            Assert.IsNull(manager.SerializeAISystemPlugins(null));
            Assert.IsNull(manager.SerializeAISystemPlugins(new CopilotAuditLogContent()));
            Assert.IsNull(manager.SerializeAISystemPlugins(new CopilotAuditLogContent { CopilotEventData = new CopilotEventData() }));
        }

        /// <summary>
        /// Deliberate behaviour change: prompts used to be filtered out because only the message id was
        /// stored. Size is only available on the prompt row and is_prompt would otherwise be a constant,
        /// so both are staged now. See the CopilotMessage entity docs.
        /// </summary>
        [TestMethod]
        public void Copilot_SerializeMessages_KeepsPromptsAsWellAsResponses()
        {
            var manager = NewManager();

            var json = manager.SerializeMessages(new CopilotAuditLogContent
            {
                ParsedAuditEvent = new CopilotAuditEvent
                {
                    Messages = new List<Message>
                    {
                        new Message { Id = "prompt-1", IsPrompt = true, Size = 1234 },
                        new Message { Id = "response-1", IsPrompt = false, Size = 5678 },
                    }
                }
            });

            var back = JsonConvert.DeserializeObject<List<Message>>(json);
            Assert.AreEqual(2, back.Count, "Prompts must no longer be filtered out.");
            Assert.IsTrue(back.Any(m => m.IsPrompt && m.Size == 1234));
            Assert.IsTrue(back.Any(m => !m.IsPrompt && m.Size == 5678));
        }

        #endregion

        #region End-to-end persistence

        /// <summary>
        /// The headline case: an interaction with MULTIPLE contexts, MULTIPLE plugins, resource
        /// action/listItemUniqueId, prompt+response messages with sizes, model provider/version and the
        /// chat-level thread/region/log-version - all persisted in one merge.
        /// </summary>
        [TestMethod]
        public async Task Copilot_AllPreviouslyDroppedFields_ArePersisted()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                var manager = NewManager();
                var commonEvent = await AddCommonEvent(db, "All Dropped Fields");

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    ClientRegion = "WEU",
                    CopilotLogVersion = "2024-11-30",
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        ThreadId = "19:" + Greek + "@thread.v2",
                        Contexts = new List<Context>
                        {
                            // Only the first (chat) context drives copilot_event_files/meetings; the rest
                            // used to be discarded entirely.
                            new Context { Id = "https://contoso.teams.com/threads/19:first@thread.v2", Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT, ContainerId = "team-1" },
                            new Context { Id = GreekUrl, Type = "pdf", ContainerId = "container-" + Greek },
                            new Context { Id = "https://contoso.sharepoint.com/sites/example/doc.docx", Type = "docx" },
                        },
                        AISystemPlugin = new List<AISystemPlugin>
                        {
                            new AISystemPlugin { Id = "BingWebSearch", Name = "BuiltIn", Version = "1.0" },
                            new AISystemPlugin { Id = "ContosoConnector", Name = Greek, Version = "2.5" },
                        },
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-alpha",
                                Name = Greek + ".pdf",
                                Type = "pdf",
                                SiteUrl = "https://contoso.sharepoint.com/sites/example",
                                Action = "Read",
                                ListItemUniqueId = "00000000-0000-0000-0000-000000000123",
                            },
                        },
                    },
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message>
                        {
                            new Message { Id = "prompt-1", IsPrompt = true, Size = 4096 },
                            new Message { Id = "response-1", IsPrompt = false, Size = 65536 },
                        },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO", ModelProviderName = "Contoso AI", ModelVersion = "2026-01-01" },
                        },
                    },
                }, commonEvent);

                await manager.CommitAllChanges();

                // --- chat-level columns ---
                var chat = await db.CopilotChats.SingleAsync(c => c.EventID == commonEvent.Id);
                Assert.AreEqual("19:" + Greek + "@thread.v2", chat.ThreadId, "ThreadId must persist, Unicode intact.");
                Assert.AreEqual("WEU", chat.ClientRegion);
                Assert.AreEqual("2024-11-30", chat.CopilotLogVersion);

                // --- contexts: ALL of them, not just the first ---
                var contexts = await db.CopilotEventContexts.Include(c => c.ContextType)
                    .Where(c => c.ChatId == commonEvent.Id).ToListAsync();
                Assert.AreEqual(3, contexts.Count, "Every context must be persisted, including the ones the file/meeting resolution skips.");
                var greekContext = contexts.SingleOrDefault(c => c.ContextRef == GreekUrl);
                Assert.IsNotNull(greekContext, "Non-ASCII context ref must survive the nvarchar round-trip.");
                Assert.AreEqual("pdf", greekContext.ContextType?.Name);
                Assert.AreEqual("container-" + Greek, greekContext.ContainerId);
                Assert.AreEqual("team-1", contexts.Single(c => c.ContextRef.EndsWith("19:first@thread.v2")).ContainerId);

                // --- plugins: lookup + junction ---
                var plugins = await db.CopilotEventAISystemPlugins.Include(p => p.AISystemPlugin)
                    .Where(p => p.ChatId == commonEvent.Id).ToListAsync();
                Assert.AreEqual(2, plugins.Count, "Both plugins must be linked to the interaction.");
                var bing = plugins.Single(p => p.AISystemPlugin.PluginId == "BingWebSearch").AISystemPlugin;
                Assert.AreEqual("BuiltIn", bing.Name);
                Assert.AreEqual("1.0", bing.Version);
                var contoso = plugins.Single(p => p.AISystemPlugin.PluginId == "ContosoConnector").AISystemPlugin;
                Assert.AreEqual(Greek, contoso.Name);
                Assert.AreEqual("2.5", contoso.Version);

                // --- accessed resource action + listItemUniqueId ---
                var resource = await db.CopilotEventAccessedResources
                    .Include(r => r.Action).Include(r => r.ListItemUniqueId).Include(r => r.ResourceName)
                    .SingleAsync(r => r.ChatId == commonEvent.Id);
                Assert.AreEqual("Read", resource.Action?.Name);
                Assert.AreEqual("00000000-0000-0000-0000-000000000123", resource.ListItemUniqueId?.ResourceId);
                Assert.AreEqual(Greek + ".pdf", resource.ResourceName?.Name);

                // --- messages: prompt AND response, with sizes ---
                var messages = await db.CopilotMessages.Where(m => m.ChatId == commonEvent.Id).ToListAsync();
                Assert.AreEqual(2, messages.Count, "Prompt and response are both stored now.");
                var prompt = messages.Single(m => m.MessageId == "prompt-1");
                Assert.AreEqual(true, prompt.IsPrompt);
                Assert.AreEqual(4096L, prompt.Size);
                var response = messages.Single(m => m.MessageId == "response-1");
                Assert.AreEqual(false, response.IsPrompt);
                Assert.AreEqual(65536L, response.Size);

                // --- model provider / version ---
                var model = (await db.CopilotEventAIModels.Include(m => m.AIModel)
                    .Where(m => m.ChatId == commonEvent.Id).ToListAsync()).Single().AIModel;
                Assert.AreEqual("DEEP_LEO", model.Name);
                Assert.AreEqual("Contoso AI", model.ProviderName);
                Assert.AreEqual("2026-01-01", model.Version);
            }
        }

        /// <summary>
        /// A payload with none of the new fields (an older-shaped record) must import exactly as before:
        /// no crash, no child rows invented, NULL columns.
        /// </summary>
        [TestMethod]
        public async Task Copilot_MissingAndNullNewFields_ImportCleanlyAsNulls()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                var manager = NewManager();
                var commonEvent = await AddCommonEvent(db, "Null New Fields");

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    // ClientRegion / CopilotLogVersion deliberately absent
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        // ThreadId absent, Contexts empty, AISystemPlugin empty
                        AccessedResources = new List<AccessedResource>
                        {
                            // No Action, no listItemUniqueId - the fields we are adding
                            new AccessedResource { Id = "resource-no-extras", Name = "Doc.docx", Type = "docx" },
                        },
                    },
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        // No Size, no explicit provider/version
                        Messages = new List<Message> { new Message { Id = "response-only", IsPrompt = false } },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO" },
                        },
                    },
                }, commonEvent);

                await manager.CommitAllChanges();

                var chat = await db.CopilotChats.SingleAsync(c => c.EventID == commonEvent.Id);
                Assert.IsNull(chat.ThreadId);
                Assert.IsNull(chat.ClientRegion);
                Assert.IsNull(chat.CopilotLogVersion);

                Assert.AreEqual(0, await db.CopilotEventContexts.CountAsync(c => c.ChatId == commonEvent.Id));
                Assert.AreEqual(0, await db.CopilotEventAISystemPlugins.CountAsync(p => p.ChatId == commonEvent.Id));

                var resource = await db.CopilotEventAccessedResources.SingleAsync(r => r.ChatId == commonEvent.Id);
                Assert.IsNull(resource.ActionId, "No Action in the payload must leave action_id NULL, not invent a lookup row.");
                Assert.IsNull(resource.ListItemUniqueIdId);

                var message = await db.CopilotMessages.SingleAsync(m => m.ChatId == commonEvent.Id);
                Assert.IsNull(message.Size, "Microsoft does not populate Size for every host; it must stay NULL.");
                Assert.AreEqual(false, message.IsPrompt);

                var model = (await db.CopilotEventAIModels.Include(m => m.AIModel)
                    .Where(m => m.ChatId == commonEvent.Id).ToListAsync()).Single().AIModel;
                Assert.AreEqual("DEEP_LEO", model.Name);
                Assert.IsNull(model.ProviderName);
                Assert.IsNull(model.Version);
            }
        }

        /// <summary>
        /// The dimensions must de-duplicate across interactions (that is the point of a lookup table),
        /// and the child tables must not double up when the same interaction is staged twice - the
        /// importer legitimately re-processes a batch after a transient failure.
        /// </summary>
        [TestMethod]
        public async Task Copilot_ContextsAndPlugins_DeduplicateAcrossEventsAndReruns()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                var eventA = await AddCommonEvent(db, "Dedup A");
                var eventB = await AddCommonEvent(db, "Dedup B");

                Func<CopilotAuditLogContent> payload = () => new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context { Id = "https://contoso.teams.com/threads/19:shared@thread.v2", Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT, ContainerId = "team-shared" },
                        },
                        AISystemPlugin = new List<AISystemPlugin>
                        {
                            new AISystemPlugin { Id = "BingWebSearch", Name = "BuiltIn", Version = "1.0" },
                        },
                    },
                };

                // Two different interactions sharing the same context type + plugin...
                var manager = NewManager();
                await manager.SaveSingleCopilotEventToSqlStaging(payload(), eventA);
                await manager.SaveSingleCopilotEventToSqlStaging(payload(), eventB);
                await manager.CommitAllChanges();

                // ...and then event A staged a second time (a re-processed batch).
                var manager2 = NewManager();
                await manager2.SaveSingleCopilotEventToSqlStaging(payload(), eventA);
                await manager2.CommitAllChanges();

                Assert.AreEqual(1, await db.CopilotAISystemPlugins.CountAsync(),
                    "The plugin lookup must hold one row per distinct (id, name, version) tuple.");
                Assert.AreEqual(1, await db.CopilotContextTypes.CountAsync(),
                    "The context type lookup must hold one row per distinct type.");
                Assert.AreEqual(1, await db.CopilotEventContexts.CountAsync(c => c.ChatId == eventA.Id),
                    "Re-staging the same interaction must not duplicate its contexts.");
                Assert.AreEqual(1, await db.CopilotEventAISystemPlugins.CountAsync(p => p.ChatId == eventA.Id),
                    "Re-staging the same interaction must not duplicate its plugin links.");
                Assert.AreEqual(1, await db.CopilotEventContexts.CountAsync(c => c.ChatId == eventB.Id));
            }
        }

        /// <summary>
        /// The same model name at two versions is two dimension rows (version is part of a model's
        /// identity for AI-transparency reporting), while a repeat of the same tuple is not.
        /// </summary>
        [TestMethod]
        public async Task Copilot_AiModels_AreKeyedOnNameProviderAndVersion()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                var commonEvent = await AddCommonEvent(db, "Model Versions");
                var manager = NewManager();

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData { AppHost = "Teams" },
                    ParsedAuditEvent = new CopilotAuditEvent
                    {
                        Messages = new List<Message> { new Message { Id = "1", IsPrompt = false } },
                        ModelTransparencyDetails = new List<ModelTransparencyDetail>
                        {
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO", ModelProviderName = "Contoso AI", ModelVersion = "2026-01-01" },
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO", ModelProviderName = "Contoso AI", ModelVersion = "2026-06-01" },
                            new ModelTransparencyDetail { ModelName = "DEEP_LEO", ModelProviderName = "Contoso AI", ModelVersion = "2026-06-01" },
                        },
                    },
                }, commonEvent);
                await manager.CommitAllChanges();

                var models = await db.CopilotAIModels.Where(m => m.Name == "DEEP_LEO").ToListAsync();
                Assert.AreEqual(2, models.Count, "Two versions of the same model are two dimension rows; the repeat is not.");
                CollectionAssert.AreEquivalent(new[] { "2026-01-01", "2026-06-01" }, models.Select(m => m.Version).ToArray());
                Assert.AreEqual(2, await db.CopilotEventAIModels.CountAsync(m => m.ChatId == commonEvent.Id));
            }
        }

        /// <summary>
        /// listItemUniqueId shares the accessed-resource id dimension deliberately - the audit payload
        /// frequently repeats the resource Id verbatim as listItemUniqueId, so the two must collapse onto
        /// ONE lookup row rather than storing the same (up to 850 char) string twice.
        /// </summary>
        [TestMethod]
        public async Task Copilot_ListItemUniqueId_SharesTheResourceIdDimension()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                const string sharedId = "AAAAA-synthetic-resource-identifier-00000";
                var commonEvent = await AddCommonEvent(db, "Shared Resource Dimension");
                var manager = NewManager();

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Bing",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { Id = sharedId, Name = "Doc.docx", Type = "docx", Action = "Read", ListItemUniqueId = sharedId },
                        },
                    },
                }, commonEvent);
                await manager.CommitAllChanges();

                Assert.AreEqual(1, await db.CopilotAccessedResourceIds.CountAsync(r => r.ResourceId == sharedId),
                    "Id and listItemUniqueId with the same value must share one dimension row.");

                var resource = await db.CopilotEventAccessedResources
                    .SingleAsync(r => r.ChatId == commonEvent.Id);
                Assert.IsNotNull(resource.ResourceIdId);
                Assert.AreEqual(resource.ResourceIdId, resource.ListItemUniqueIdId);
            }
        }

        /// <summary>
        /// Regression guard for the hot path: adding action_id / list_item_unique_id_id must NOT widen the
        /// accessed-resource de-duplication tuple (that tuple is what IX_copilot_event_accessed_resources_dedup
        /// covers). The same resource logged twice in one interaction with different actions must therefore
        /// still collapse to a single junction row.
        /// </summary>
        [TestMethod]
        public async Task Copilot_AccessedResourceDedup_IsNotWidenedByTheNewColumns()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                var commonEvent = await AddCommonEvent(db, "Dedup Tuple Unchanged");
                var manager = NewManager();

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Bing",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { Id = "same-resource", Name = "Doc.docx", Type = "docx", Action = "Read" },
                            new AccessedResource { Id = "same-resource", Name = "Doc.docx", Type = "docx", Action = "Write" },
                        },
                    },
                }, commonEvent);
                await manager.CommitAllChanges();

                var junction = await db.CopilotEventAccessedResources.Include(r => r.Action)
                    .Where(r => r.ChatId == commonEvent.Id).ToListAsync();
                Assert.AreEqual(1, junction.Count,
                    "The de-dup tuple must stay exactly the 5 columns covered by IX_copilot_event_accessed_resources_dedup.");
                Assert.IsNotNull(junction[0].Action, "A deterministic non-NULL action should be kept for the collapsed row.");
            }
        }

        /// <summary>
        /// Persisting all contexts must not change which single context is RESOLVED into
        /// copilot_event_files / copilot_event_meetings - that behaviour is asserted elsewhere and is
        /// deliberately first-context-wins.
        /// </summary>
        [TestMethod]
        public async Task Copilot_AllContextsPersisted_DoesNotChangeFileResolution()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (!await NewTablesExist(db)) { Assert.Inconclusive("CopilotDroppedAuditFields migration not applied."); return; }

                await ClearEvents(db);
                await ClearAccessedResources(db);
                await ClearDroppedFieldTables(db);

                var commonEvent = await AddCommonEvent(db, "File Resolution Unchanged");
                var manager = NewManager();

                await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        Contexts = new List<Context>
                        {
                            new Context { Id = "https://contoso.sharepoint.com/sites/example/first.docx", Type = "docx" },
                            new Context { Id = "https://contoso.sharepoint.com/sites/example/second.docx", Type = "docx" },
                            new Context { Id = GreekUrl, Type = "pdf" },
                        },
                    },
                }, commonEvent);
                await manager.CommitAllChanges();

                Assert.AreEqual(1, await db.CopilotEventMetadataFiles.CountAsync(f => f.ChatId == commonEvent.Id),
                    "Still exactly one resolved file per interaction (first file context wins).");
                Assert.AreEqual(3, await db.CopilotEventContexts.CountAsync(c => c.ChatId == commonEvent.Id),
                    "...but all three contexts are now recorded.");
            }
        }

        #endregion

        #region Helpers

        private CopilotAuditEventManager NewManager()
            => new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

        private static async Task<CommonAuditEvent> AddCommonEvent(AnalyticsEntitiesContext db, string label)
        {
            var commonEvent = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = label + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = $"{label}@contoso.com{DateTime.Now.Ticks}" },
                Id = Guid.NewGuid(),
            };
            db.AuditEventsCommon.Add(commonEvent);
            await db.SaveChangesAsync();
            return commonEvent;
        }

        private static async Task<bool> NewTablesExist(AnalyticsEntitiesContext db)
        {
            var found = await db.Database
                .SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_contexts', 'U')")
                .FirstOrDefaultAsync();
            return found.GetValueOrDefault() != 0;
        }

        private static async Task ClearDroppedFieldTables(AnalyticsEntitiesContext db)
        {
            db.CopilotEventContexts.RemoveRange(db.CopilotEventContexts);
            db.CopilotEventAISystemPlugins.RemoveRange(db.CopilotEventAISystemPlugins);
            db.CopilotEventAIModels.RemoveRange(db.CopilotEventAIModels);
            db.CopilotMessages.RemoveRange(db.CopilotMessages);
            await db.SaveChangesAsync();

            db.CopilotContextTypes.RemoveRange(db.CopilotContextTypes);
            db.CopilotAISystemPlugins.RemoveRange(db.CopilotAISystemPlugins);
            db.CopilotAIModels.RemoveRange(db.CopilotAIModels);
            db.CopilotAccessedResourceActions.RemoveRange(db.CopilotAccessedResourceActions);
            await db.SaveChangesAsync();
        }

        #endregion
    }
}
