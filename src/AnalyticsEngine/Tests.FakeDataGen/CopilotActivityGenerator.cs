using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Linq;

namespace Tests.FakeDataGen
{
    /// <summary>
    /// Generates fake Copilot activity data for testing purposes
    /// </summary>
    public class CopilotActivityGenerator
    {
        private readonly string _connectionString;
        private readonly Random _random = new Random();
        private readonly CopilotLicenseManager _licenseManager;
        private readonly CopilotUserManager _userManager;
        private readonly CopilotResourceGenerator _resourceGenerator;

        public CopilotActivityGenerator(string connectionString)
        {
            _connectionString = connectionString;
            _licenseManager = new CopilotLicenseManager();
            _userManager = new CopilotUserManager(_random, _licenseManager);
            _resourceGenerator = new CopilotResourceGenerator(_random);
        }

        /// <summary>
        /// Generates fake copilot activity events
        /// </summary>
        /// <param name="count">Number of events to generate</param>
        /// <param name="customAgentPercentage">Percentage of events that should use custom agents (0-100)</param>
        /// <param name="agentPercentage">Percentage of events that should have agents (0-100)</param>
        /// <param name="copilotLicensePercentage">Percentage of users that should have Copilot licenses (0-100)</param>
        public void GenerateCopilotActivity(int count, int customAgentPercentage = 10, int agentPercentage = 30, int copilotLicensePercentage = 30)
        {
            Console.WriteLine($"Generating {count} copilot activity events...");
            Console.WriteLine($"- {agentPercentage}% will have agents");
            Console.WriteLine($"- {customAgentPercentage}% of those will be custom agents");
            Console.WriteLine($"- {copilotLicensePercentage}% of users will have Copilot licenses");

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                // Ensure we have licenses
                _licenseManager.EnsureLicensesExist(db);

                // Ensure we have users
                var users = db.users.Take(10).ToList();
                if (users.Count == 0)
                {
                    Console.WriteLine("No users found in database. Creating test users...");
                    users = _userManager.CreateTestUsers(db, 10, copilotLicensePercentage);
                }
                else
                {
                    Console.WriteLine($"Found {users.Count} existing users in database.");
                }

                // Ensure we have operations
                var copilotOperation = db.event_operations.FirstOrDefault(o => o.Name == "CopilotInteraction");
                if (copilotOperation == null)
                {
                    copilotOperation = new EventOperation { Name = "CopilotInteraction" };
                    db.event_operations.Add(copilotOperation);
                    db.SaveChanges();
                }

                int inserted = 0;
                int withAgents = 0;
                int withCustomAgents = 0;
                int withAccessedResources = 0;

                for (int i = 0; i < count; i++)
                {
                    var user = users[_random.Next(users.Count)];
                    bool shouldHaveAgent = _random.Next(100) < agentPercentage;
                    bool isCustomAgent = shouldHaveAgent && _random.Next(100) < customAgentPercentage;

                    var copilotEvent = GenerateSingleCopilotEvent(db, user, copilotOperation, shouldHaveAgent, isCustomAgent);

                    if (shouldHaveAgent)
                    {
                        withAgents++;
                        if (isCustomAgent)
                        {
                            withCustomAgents++;
                            withAccessedResources++;
                        }
                    }

                    inserted++;

                    if (inserted % 100 == 0)
                    {
                        Console.WriteLine($"Inserted {inserted}/{count} events...");
                        db.SaveChanges();
                    }
                }

                // Final save
                db.SaveChanges();

                Console.WriteLine($"\nGeneration complete!");
                Console.WriteLine($"Total events: {inserted}");
                Console.WriteLine($"Events with agents: {withAgents} ({(withAgents * 100.0 / inserted):F1}%)");
                Console.WriteLine($"Events with custom agents: {withCustomAgents} ({(withCustomAgents * 100.0 / inserted):F1}%)");
                Console.WriteLine($"Events with accessed resources: {withAccessedResources} ({(withAccessedResources * 100.0 / inserted):F1}%)");
            }
        }

        private CopilotChat GenerateSingleCopilotEvent(AnalyticsEntitiesContext db, User user, EventOperation operation, bool withAgent, bool isCustomAgent)
        {
            // Generate unique event ID - ensure it doesn't already exist
            Guid eventId;
            do
            {
                eventId = Guid.NewGuid();
            } while (db.AuditEventsCommon.Any(e => e.Id == eventId));

            var timestamp = DateTime.UtcNow.AddDays(-_random.Next(0, 30)).AddHours(-_random.Next(0, 24));

            // Create common audit event
            var auditEvent = new CommonAuditEvent
            {
                Id = eventId,
                User = user,
                Operation = operation,
                TimeStamp = timestamp,
                EventData = GenerateEventData()
            };

            db.AuditEventsCommon.Add(auditEvent);

            // Create copilot chat event
            var copilotChat = new CopilotChat
            {
                EventID = eventId,
                AuditEvent = auditEvent,
                AppHost = CopilotActivityGeneratorConfig.AppHosts[_random.Next(CopilotActivityGeneratorConfig.AppHosts.Length)],
                CopilotCreditEstimateTotal = _random.Next(1, 50)
            };

            // Add agent if requested
            if (withAgent)
            {
                var agent = GetOrCreateAgent(db, isCustomAgent);
                copilotChat.Agent = agent;

                // Add accessed resources for custom agents
                if (isCustomAgent)
                {
                    _resourceGenerator.AddAccessedResources(db, copilotChat);
                }
            }

            db.CopilotChats.Add(copilotChat);

            // Randomly decide if this is a file, meeting, or chat-only event
            int eventType = _random.Next(3);

            if (eventType == 0 && copilotChat.AppHost == "Teams")
            {
                // Create meeting event
                CreateMeetingEvent(db, copilotChat, user);
            }
            else if (eventType == 1)
            {
                // Create file event
                CreateFileEvent(db, copilotChat);
            }
            // Otherwise it's chat-only (no additional metadata)

            return copilotChat;
        }

        private void CreateMeetingEvent(AnalyticsEntitiesContext db, CopilotChat copilotChat, User user)
        {
            // Check if we have any existing meetings in local context first, then database
            var meeting = db.Set<OnlineMeeting>().Local.FirstOrDefault();
            if (meeting == null)
            {
                meeting = db.Set<OnlineMeeting>().FirstOrDefault();
            }

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

        private CopilotAgent GetOrCreateAgent(AnalyticsEntitiesContext db, bool isCustomAgent)
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

            // Check both database and local context for existing agent
            var agent = db.CopilotAgents.Local.FirstOrDefault(a => a.AgentID == agentId);
            if (agent == null)
            {
                agent = db.CopilotAgents.FirstOrDefault(a => a.AgentID == agentId);
            }

            if (agent == null)
            {
                agent = new CopilotAgent
                {
                    Name = agentName,
                    AgentID = agentId,
                    IsCustomAgent = isCustomAgent
                };
                db.CopilotAgents.Add(agent);
            }

            return agent;
        }

        private SPEventFileName GetOrCreateFileName(AnalyticsEntitiesContext db, string name)
        {
            // Check both database and local context for existing file name
            var fileName = db.event_file_names.Local.FirstOrDefault(f => f.Name == name);
            if (fileName == null)
            {
                fileName = db.event_file_names.FirstOrDefault(f => f.Name == name);
            }

            if (fileName == null)
            {
                fileName = new SPEventFileName { Name = name };
                db.event_file_names.Add(fileName);
            }
            return fileName;
        }

        private SPEventFileExtension GetOrCreateFileExtension(AnalyticsEntitiesContext db, string ext)
        {
            // Check both database and local context for existing file extension
            var fileExt = db.event_file_ext.Local.FirstOrDefault(f => f.extension_name == ext);
            if (fileExt == null)
            {
                fileExt = db.event_file_ext.FirstOrDefault(f => f.extension_name == ext);
            }

            if (fileExt == null)
            {
                fileExt = new SPEventFileExtension { extension_name = ext };
                db.event_file_ext.Add(fileExt);
            }
            return fileExt;
        }

        private Url GetOrCreateUrl(AnalyticsEntitiesContext db, string fullUrl)
        {
            // Check both database and local context for existing url
            var url = db.urls.Local.FirstOrDefault(u => u.FullUrl == fullUrl);
            if (url == null)
            {
                url = db.urls.FirstOrDefault(u => u.FullUrl == fullUrl);
            }

            if (url == null)
            {
                url = new Url { FullUrl = fullUrl };
                db.urls.Add(url);
            }
            return url;
        }

        private Site GetOrCreateSite(AnalyticsEntitiesContext db, string siteUrl)
        {
            // Check both database and local context for existing site
            var site = db.sites.Local.FirstOrDefault(s => s.UrlBase == siteUrl);
            if (site == null)
            {
                site = db.sites.FirstOrDefault(s => s.UrlBase == siteUrl);
            }

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

        /// <summary>
        /// Creates a new database context for external use (e.g., checking database state)
        /// </summary>
        public AnalyticsEntitiesContext CreateContext()
        {
            return new AnalyticsEntitiesContext(_connectionString, true, false);
        }
    }
}
