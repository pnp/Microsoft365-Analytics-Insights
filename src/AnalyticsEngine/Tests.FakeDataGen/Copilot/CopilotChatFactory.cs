using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Builds one Copilot interaction - the audit record, the chat row and every detail table that
    /// hangs off it - at a caller-supplied timestamp, app host and agent.
    ///
    /// Extracted so the two generators that need interactions build them the same way. The random
    /// scatter in <see cref="CopilotActivityGenerator"/> lets this class choose the timestamp and app,
    /// whereas <see cref="CopilotAdoptionScenarioGenerator"/> dictates both, because the adoption
    /// bands are computed from exactly those two columns (distinct active dates and distinct
    /// <c>app_host</c> values). Two copies of this code would drift, and the scenario would silently
    /// stop landing users in the bands it claims to.
    /// </summary>
    public class CopilotChatFactory
    {
        private readonly Random _random;
        private readonly CopilotResourceGenerator _resourceGenerator;
        private readonly CopilotEventDetailGenerator _detailGenerator;

        /// <summary>
        /// The last few conversation ids seen per user, so consecutive interactions can share a thread rather
        /// than every interaction being its own conversation. <c>thread_id</c> exists to group interactions,
        /// so generating a unique one per row would make it useless for exactly the reports it is for.
        /// </summary>
        private readonly Dictionary<int, List<string>> _recentThreadsByUser = new Dictionary<int, List<string>>();

        public CopilotChatFactory(Random random, CopilotResourceGenerator resourceGenerator, CopilotEventDetailGenerator detailGenerator)
        {
            _random = random;
            _resourceGenerator = resourceGenerator;
            _detailGenerator = detailGenerator;
        }

        /// <summary>
        /// Creates one interaction. <paramref name="appHost"/> and <paramref name="timestampUtc"/> are
        /// required rather than optional: they are the two fields the adoption analysis actually
        /// measures, so a caller should always have decided them deliberately.
        /// </summary>
        /// <param name="agent">The agent that answered, or null for a plain Copilot interaction.</param>
        /// <param name="withMetadata">
        /// Whether to attach the meeting/file metadata rows. Off for bulk scenario generation, which
        /// cares about volume and shape rather than about exercising every child table.
        /// </param>
        public CopilotChat Create(
            AnalyticsEntitiesContext db,
            User user,
            EventOperation operation,
            DateTime timestampUtc,
            string appHost,
            CopilotAgent agent = null,
            bool withMetadata = true)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (string.IsNullOrWhiteSpace(appHost)) throw new ArgumentException("An app host is required.", nameof(appHost));

            // A random GUID is unique for practical purposes; the previous round-trip to check meant
            // one SELECT per generated event, which dominated the runtime of a large generation run.
            var eventId = Guid.NewGuid();

            var auditEvent = new CommonAuditEvent
            {
                Id = eventId,
                User = user,
                Operation = operation,
                TimeStamp = timestampUtc,
                EventData = GenerateEventData()
            };

            db.AuditEventsCommon.Add(auditEvent);

            var copilotChat = new CopilotChat
            {
                EventID = eventId,
                AuditEvent = auditEvent,
                AppHost = appHost,
                CopilotCreditEstimateTotal = _random.Next(1, 50),

                // Denormalised copies of the audit event's user and timestamp. The real importer merge
                // (common_upsert_copilot_agents.sql) writes these from the audit event it has just
                // inserted; generated data must carry them too or every Copilot report reads as empty,
                // because they are what the reports filter on now instead of joining dbo.audit_events.
                UserId = user.ID,
                TimeStampUtc = timestampUtc,

                // Fields the importer parses but used to discard. Generating them keeps any report built
                // over them honest - an empty column looks the same as a working report on no data.
                ThreadId = NextThreadId(user.ID),
                ClientRegion = CopilotActivityGeneratorConfig.ClientRegions[
                    _random.Next(CopilotActivityGeneratorConfig.ClientRegions.Length)],
                CopilotLogVersion = CopilotActivityGeneratorConfig.CopilotLogVersions[
                    _random.Next(CopilotActivityGeneratorConfig.CopilotLogVersions.Length)]
            };

            if (agent != null)
            {
                copilotChat.Agent = agent;
            }

            db.CopilotChats.Add(copilotChat);

            if (agent != null && agent.IsCustomAgent == true)
            {
                _resourceGenerator.AddAccessedResources(db, copilotChat);
            }

            // Detail tables that hang off the same audit record: both messages, every context, the models
            // that answered and the plugins that grounded it.
            _detailGenerator.AddMessages(db, copilotChat);
            _detailGenerator.AddContexts(db, copilotChat);
            _detailGenerator.AddAIModels(db, copilotChat);
            _detailGenerator.AddSystemPlugins(db, copilotChat);

            if (withMetadata)
            {
                // Randomly decide if this is a file, meeting, or chat-only event
                int eventType = _random.Next(3);
                if (eventType == 0 && appHost == "Teams")
                {
                    CreateMeetingEvent(db, copilotChat);
                }
                else if (eventType == 1)
                {
                    CreateFileEvent(db, copilotChat);
                }
                // Otherwise it's chat-only (no additional metadata)
            }

            return copilotChat;
        }

        /// <summary>Picks a random app host, for callers that do not care which surface was used.</summary>
        public string RandomAppHost()
        {
            return CopilotActivityGeneratorConfig.AppHosts[_random.Next(CopilotActivityGeneratorConfig.AppHosts.Length)];
        }

        /// <summary>
        /// Picks a conversation id for this user: usually continues a recent conversation, sometimes starts a
        /// new one. A fresh id per interaction would make <c>thread_id</c> useless for the grouping it exists
        /// to support.
        /// </summary>
        private string NextThreadId(int userId)
        {
            if (!_recentThreadsByUser.TryGetValue(userId, out var recent))
            {
                recent = new List<string>();
                _recentThreadsByUser[userId] = recent;
            }

            // 60% continue an existing conversation, so threads have several turns.
            if (recent.Count > 0 && _random.Next(100) < 60)
            {
                return recent[_random.Next(recent.Count)];
            }

            var threadId = $"19:copilot_{Guid.NewGuid():N}@thread.v2";
            recent.Add(threadId);

            // Only the last few conversations stay "open" - otherwise every user accumulates every thread
            // they have ever had and the distribution flattens out.
            if (recent.Count > 5)
                recent.RemoveAt(0);

            return threadId;
        }

        /// <summary>Resolves (or creates) one of the stock agents, standard or custom.</summary>
        public CopilotAgent GetOrCreateAgent(AnalyticsEntitiesContext db, bool isCustomAgent)
        {
            string agentName;
            string agentId;

            if (isCustomAgent)
            {
                // Custom agent with organization-specific naming
                agentName = CopilotActivityGeneratorConfig.AgentNames[_random.Next(1, CopilotActivityGeneratorConfig.AgentNames.Length)]; // Skip "Copilot" which is standard
                agentId = $"Copilot.Studio.Default-{Guid.NewGuid()}-{agentName.Replace(" ", "")}";
            }
            else
            {
                // Standard Microsoft agent
                agentName = CopilotActivityGeneratorConfig.AgentNames[0]; // "Copilot"
                agentId = CopilotActivityGeneratorConfig.StandardAgentIds[_random.Next(CopilotActivityGeneratorConfig.StandardAgentIds.Length)];
            }

            return GetOrCreateNamedAgent(db, agentName, agentId, isCustomAgent);
        }

        /// <summary>
        /// Resolves (or creates) an agent with a specific id, so a caller can drive a named agent's
        /// usage history deliberately - which is how the inventory's Keep/Review/Retire/New verdicts
        /// are produced on demand.
        /// </summary>
        public CopilotAgent GetOrCreateNamedAgent(AnalyticsEntitiesContext db, string name, string agentId, bool isCustomAgent)
        {
            var agent = db.CopilotAgents.Local.FirstOrDefault(a => a.AgentID == agentId)
                        ?? db.CopilotAgents.FirstOrDefault(a => a.AgentID == agentId);

            if (agent == null)
            {
                agent = new CopilotAgent
                {
                    Name = name,
                    AgentID = agentId,
                    IsCustomAgent = isCustomAgent
                };
                db.CopilotAgents.Add(agent);
            }

            return agent;
        }

        private void CreateMeetingEvent(AnalyticsEntitiesContext db, CopilotChat copilotChat)
        {
            // Check if we have any existing meetings in local context first, then database
            var meeting = db.Set<OnlineMeeting>().Local.FirstOrDefault()
                          ?? db.Set<OnlineMeeting>().FirstOrDefault();

            if (meeting == null)
            {
                meeting = new OnlineMeeting
                {
                    Name = "Test Meeting " + _random.Next(1000),
                    CreatedUTC = DateTime.UtcNow.AddDays(-_random.Next(1, 30)),
                    MeetingId = Guid.NewGuid().ToString()
                };
                db.Set<OnlineMeeting>().Add(meeting);
            }

            var meetingEvent = new CopilotEventMetadataMeeting
            {
                ChatId = copilotChat.EventID,
                RelatedChat = copilotChat,
                OnlineMeeting = meeting
            };

            db.CopilotEventMetadataMeetings.Add(meetingEvent);
        }

        private void CreateFileEvent(AnalyticsEntitiesContext db, CopilotChat copilotChat)
        {
            // Get or create file-related lookups
            var fileName = GetOrCreateFileName(db, "Document_" + _random.Next(1000));
            var fileExt = GetOrCreateFileExtension(db, GetRandomExtension());
            var site = GetOrCreateSite(db, "https://contoso.sharepoint.com/sites/test");
            var url = GetOrCreateUrl(db, $"https://contoso.sharepoint.com/sites/test/document_{_random.Next(1000)}.{fileExt.extension_name}");

            var fileEvent = new CopilotEventMetadataFile
            {
                ChatId = copilotChat.EventID,
                RelatedChat = copilotChat,
                FileName = fileName,
                FileExtension = fileExt,
                Url = url,
                Site = site
            };

            db.CopilotEventMetadataFiles.Add(fileEvent);
        }

        private SPEventFileName GetOrCreateFileName(AnalyticsEntitiesContext db, string name)
        {
            var fileName = db.event_file_names.Local.FirstOrDefault(f => f.Name == name)
                           ?? db.event_file_names.FirstOrDefault(f => f.Name == name);

            if (fileName == null)
            {
                fileName = new SPEventFileName { Name = name };
                db.event_file_names.Add(fileName);
            }
            return fileName;
        }

        private SPEventFileExtension GetOrCreateFileExtension(AnalyticsEntitiesContext db, string ext)
        {
            var fileExt = db.event_file_ext.Local.FirstOrDefault(f => f.extension_name == ext)
                          ?? db.event_file_ext.FirstOrDefault(f => f.extension_name == ext);

            if (fileExt == null)
            {
                fileExt = new SPEventFileExtension { extension_name = ext };
                db.event_file_ext.Add(fileExt);
            }
            return fileExt;
        }

        private Url GetOrCreateUrl(AnalyticsEntitiesContext db, string fullUrl)
        {
            var url = db.urls.Local.FirstOrDefault(u => u.FullUrl == fullUrl)
                      ?? db.urls.FirstOrDefault(u => u.FullUrl == fullUrl);

            if (url == null)
            {
                url = new Url { FullUrl = fullUrl };
                db.urls.Add(url);
            }
            return url;
        }

        private Site GetOrCreateSite(AnalyticsEntitiesContext db, string siteUrl)
        {
            var site = db.sites.Local.FirstOrDefault(s => s.UrlBase == siteUrl)
                       ?? db.sites.FirstOrDefault(s => s.UrlBase == siteUrl);

            if (site == null)
            {
                site = new Site { UrlBase = siteUrl };
                db.sites.Add(site);
            }
            return site;
        }

        private string GenerateEventData()
        {
            return $"{{\"TestData\": \"Generated at {DateTime.UtcNow}\"}}";
        }

        private string GetRandomExtension()
        {
            return CopilotActivityGeneratorConfig.FileExtensions[_random.Next(CopilotActivityGeneratorConfig.FileExtensions.Length)];
        }
    }
}
