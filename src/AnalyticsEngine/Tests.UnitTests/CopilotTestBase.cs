using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Shared base class for Copilot test classes that need DB access and common helpers.
    /// </summary>
    public abstract class CopilotTestBase
    {
        protected ILogger _logger;
        protected TestsAppConfig _config;

        public CopilotTestBase()
        {
            _logger = new LoggerFactory().CreateLogger("CopilotTests");
            _config = new TestsAppConfig();
        }

        protected async Task ClearEvents(AnalyticsEntitiesContext db)
        {
            // Clear events for test
            db.CopilotEventMetadataFiles.RemoveRange(db.CopilotEventMetadataFiles);
            db.CopilotEventMetadataMeetings.RemoveRange(db.CopilotEventMetadataMeetings);
            db.CopilotChats.RemoveRange(db.CopilotChats);

            await db.SaveChangesAsync();
        }

        protected async Task ClearAccessedResources(AnalyticsEntitiesContext db)
        {
            // Clear AccessedResources data for tests
            if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault().GetValueOrDefault() != 0)
            {
                db.CopilotEventAccessedResources.RemoveRange(db.CopilotEventAccessedResources);
                db.CopilotAccessedResourceIds.RemoveRange(db.CopilotAccessedResourceIds);
                db.CopilotAccessedResourceNames.RemoveRange(db.CopilotAccessedResourceNames);

                // Clear SiteUrls if table exists
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resource_site_urls', 'U')").FirstOrDefault().GetValueOrDefault() != 0)
                {
                    db.CopilotAccessedResourceSiteUrls.RemoveRange(db.CopilotAccessedResourceSiteUrls);
                }

                db.CopilotAccessedResourceTypes.RemoveRange(db.CopilotAccessedResourceTypes);
                db.SensitivityLabels.RemoveRange(db.SensitivityLabels);
                await db.SaveChangesAsync();
            }
        }

        // Shared flow for saving Copilot events (normal + no permissions adaptor)
        // Returns list of CommonAuditEvent objects for created chat events (for further assertions if needed).
        protected async Task<List<CommonAuditEvent>> ExecuteCopilotEventManagerSaveFlow(
            ICopilotMetadataLoader adaptor,
            AnalyticsEntitiesContext db,
            Tuple<string, string> chatAgentIdAndName = null)
        {
            var allCreatedChatCommonEvents = new List<CommonAuditEvent>();
            var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, adaptor, _logger);

            // Copilot events are: CommonAuditEvent + child CopilotAuditLogContent + copilot event data
            var commonEventDocEdit = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Document Edit" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test doc user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };
            var commonEventMeeting = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Meeting Op" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test meeting user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };
            var commonOutlook = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Outlook Op" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test outlook user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };
            var commonEventChat = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Chat or something" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test chat user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };

            // Persist common events for FK usage
            allCreatedChatCommonEvents.Add(commonEventMeeting);
            allCreatedChatCommonEvents.Add(commonEventDocEdit);
            allCreatedChatCommonEvents.Add(commonOutlook);
            allCreatedChatCommonEvents.Add(commonEventChat);

            db.AuditEventsCommon.AddRange(allCreatedChatCommonEvents);
            await db.SaveChangesAsync();

            // Save Copilot events - one for each type we know about
            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    // Teams meeting event
                    AppHost = "test",
                    Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = "https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2", // Needs to be real
                        Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                    }
                }
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonEventMeeting);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    // Document event
                    AppHost = "Word",
                    Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = _config.TestCopilotDocContextIdSpSite,
                        Type = _config.TeamSiteFileExtension
                    }
                }
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonEventDocEdit);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    // Outlook event
                    AppHost = "Outlook",
                    AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource{ Type = "http://schema.skype.com/HyperLink" }
                },
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonOutlook);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    // Chat event
                    AppHost = "Teams",
                    Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = "https://microsoft.teams.com/threads/19:somechatthread@thread.v2",
                        Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                    }
                }
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonEventChat);

            await copilotEventManager.CommitAllChanges();

            return allCreatedChatCommonEvents;
        }
    }
}
