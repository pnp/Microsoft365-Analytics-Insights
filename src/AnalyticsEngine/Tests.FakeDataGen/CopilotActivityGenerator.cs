using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Collections.Generic;
using System.Data.Entity;
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

        private static readonly string[] AppHosts = { "Teams", "Word", "Excel", "PowerPoint", "Outlook", "M365Chat" };
        private static readonly string[] AgentNames = { "Researcher", "Sales Assistant", "HR Helper", "IT Support Bot", "Marketing Agent" };
        private static readonly string[] StandardAgentIds = { "Microsoft.Copilot.Researcher", "Microsoft.Copilot.Teams", "Microsoft.Copilot.Office" };
        private static readonly string[] DepartmentNames = { "Engineering", "Sales", "Marketing", "Human Resources", "Finance", "IT", "Customer Support", "Operations", "Product Management", "Legal" };

        // License SKU IDs - https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference
        private const string COPILOT_LICENSE_SKU = "Microsoft_365_Copilot";
        private const string E5_LICENSE_SKU = "ENTERPRISEPREMIUM";
        private const string E3_LICENSE_SKU = "ENTERPRISEPACK";
        private const string BUSINESS_PREMIUM_SKU = "SPB";
        private const string EXCHANGE_ONLINE_SKU = "EXCHANGESTANDARD";

        public CopilotActivityGenerator(string connectionString)
        {
            _connectionString = connectionString;
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
                EnsureLicensesExist(db);

                // Ensure we have users
                var users = db.users.Take(10).ToList();
                if (users.Count == 0)
                {
                    Console.WriteLine("No users found in database. Creating test users...");
                    users = CreateTestUsers(db, 10, copilotLicensePercentage);
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
            }
        }

        private CopilotChat GenerateSingleCopilotEvent(AnalyticsEntitiesContext db, User user, EventOperation operation, bool withAgent, bool isCustomAgent)
        {
            var eventId = Guid.NewGuid();
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
                AppHost = AppHosts[_random.Next(AppHosts.Length)],
                CopilotCreditEstimateTotal = _random.Next(1, 50)
            };

            // Add agent if requested
            if (withAgent)
            {
                var agent = GetOrCreateAgent(db, isCustomAgent);
                copilotChat.Agent = agent;
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
            // Check if we have any existing meetings, otherwise create one
            var meeting = db.Set<OnlineMeeting>().FirstOrDefault();
            if (meeting == null)
            {
                meeting = new OnlineMeeting
                {
                    Name = "Test Meeting " + _random.Next(1000),
                    CreatedUTC = DateTime.UtcNow.AddDays(-_random.Next(1, 30)),
                    MeetingId = Guid.NewGuid().ToString()
                };
                db.Set<OnlineMeeting>().Add(meeting);
                db.SaveChanges();
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
                agentName = AgentNames[_random.Next(1, AgentNames.Length)]; // Skip "Copilot" which is standard
                agentId = $"Copilot.Studio.Default-{Guid.NewGuid()}-{agentName.Replace(" ", "")}";
            }
            else
            {
                // Standard Microsoft agent
                agentName = AgentNames[0]; // "Copilot"
                agentId = StandardAgentIds[_random.Next(StandardAgentIds.Length)];
            }

            var agent = db.CopilotAgents.FirstOrDefault(a => a.AgentID == agentId);
            if (agent == null)
            {
                agent = new CopilotAgent
                {
                    Name = agentName,
                    AgentID = agentId,
                    IsCustomAgent = isCustomAgent
                };
                db.CopilotAgents.Add(agent);
                db.SaveChanges();
            }

            return agent;
        }

        private SPEventFileName GetOrCreateFileName(AnalyticsEntitiesContext db, string name)
        {
            var fileName = db.event_file_names.FirstOrDefault(f => f.Name == name);
            if (fileName == null)
            {
                fileName = new SPEventFileName { Name = name };
                db.event_file_names.Add(fileName);
                db.SaveChanges();
            }
            return fileName;
        }

        private SPEventFileExtension GetOrCreateFileExtension(AnalyticsEntitiesContext db, string ext)
        {
            var fileExt = db.event_file_ext.FirstOrDefault(f => f.extension_name == ext);
            if (fileExt == null)
            {
                fileExt = new SPEventFileExtension { extension_name = ext };
                db.event_file_ext.Add(fileExt);
                db.SaveChanges();
            }
            return fileExt;
        }

        private Url GetOrCreateUrl(AnalyticsEntitiesContext db, string fullUrl)
        {
            var url = db.urls.FirstOrDefault(u => u.FullUrl == fullUrl);
            if (url == null)
            {
                url = new Url { FullUrl = fullUrl };
                db.urls.Add(url);
                db.SaveChanges();
            }
            return url;
        }

        private Site GetOrCreateSite(AnalyticsEntitiesContext db, string siteUrl)
        {
            var site = db.sites.FirstOrDefault(s => s.UrlBase == siteUrl);
            if (site == null)
            {
                site = new Site { UrlBase = siteUrl };
                db.sites.Add(site);
                db.SaveChanges();
            }
            return site;
        }

        private List<User> CreateTestUsers(AnalyticsEntitiesContext db, int count, int copilotLicensePercentage)
        {
            var users = new List<User>();
            var copilotLicense = db.LicenseTypes.FirstOrDefault(l => l.SKUID == COPILOT_LICENSE_SKU);
            var e5License = db.LicenseTypes.FirstOrDefault(l => l.SKUID == E5_LICENSE_SKU);
            var e3License = db.LicenseTypes.FirstOrDefault(l => l.SKUID == E3_LICENSE_SKU);

            Console.WriteLine($"Creating {count} test users with {copilotLicensePercentage}% having Copilot licenses...");

            for (int i = 0; i < count; i++)
            {
                // Get or create a random department
                var departmentName = DepartmentNames[_random.Next(DepartmentNames.Length)];
                var department = GetOrCreateDepartment(db, departmentName);

                var upn = $"testuser{i}@contoso.com";
                var user = new User
                {
                    UserPrincipalName = upn,
                    Mail = upn,
                    Department = department,
                    AccountEnabled = true,
                    AzureAdId = Guid.NewGuid().ToString()
                };
                db.users.Add(user);
                users.Add(user);
            }
            db.SaveChanges();

            // Assign licenses to users
            int usersWithCopilot = 0;
            for (int i = 0; i < users.Count; i++)
            {
                var user = users[i];
                bool shouldHaveCopilot = _random.Next(100) < copilotLicensePercentage;

                if (shouldHaveCopilot && copilotLicense != null)
                {
                    // User gets Copilot + E5
                    AssignLicenseToUser(db, user, copilotLicense);
                    if (e5License != null)
                    {
                        AssignLicenseToUser(db, user, e5License);
                    }
                    usersWithCopilot++;
                }
                else if (e3License != null)
                {
                    // User gets E3 only
                    AssignLicenseToUser(db, user, e3License);
                }
            }
            db.SaveChanges();

            Console.WriteLine($"Assigned Copilot licenses to {usersWithCopilot}/{count} users ({(usersWithCopilot * 100.0 / count):F1}%)");

            return users;
        }

        private UserDepartment GetOrCreateDepartment(AnalyticsEntitiesContext db, string departmentName)
        {
            var department = db.UserDepartments.FirstOrDefault(d => d.Name == departmentName);
            if (department == null)
            {
                department = new UserDepartment { Name = departmentName };
                db.UserDepartments.Add(department);
                db.SaveChanges();
            }
            return department;
        }

        private void EnsureLicensesExist(AnalyticsEntitiesContext db)
        {
            // Check if licenses already exist
            var licenseCount = db.LicenseTypes.Count();
            if (licenseCount > 0)
            {
                Console.WriteLine($"Found {licenseCount} existing license types in database.");
                return;
            }

            Console.WriteLine("No licenses found. Creating test license types...");

            // Create license types
            var licenses = new[]
            {
                new LicenseType { Name = "Microsoft 365 Copilot", SKUID = COPILOT_LICENSE_SKU },
                new LicenseType { Name = "Office 365 E5", SKUID = E5_LICENSE_SKU },
                new LicenseType { Name = "Office 365 E3", SKUID = E3_LICENSE_SKU },
                new LicenseType { Name = "Microsoft 365 Business Premium", SKUID = BUSINESS_PREMIUM_SKU },
                new LicenseType { Name = "Exchange Online Plan 1", SKUID = EXCHANGE_ONLINE_SKU }
            };

            foreach (var license in licenses)
            {
                db.LicenseTypes.Add(license);
            }
            db.SaveChanges();

            Console.WriteLine($"Created {licenses.Length} license types.");
        }

        private void AssignLicenseToUser(AnalyticsEntitiesContext db, User user, LicenseType license)
        {
            // Check if user already has this license
            var existingLookup = db.UserLicenseTypeLookups
                .FirstOrDefault(l => l.UserId == user.ID && l.LicenseTypeId == license.ID);

            if (existingLookup == null)
            {
                var lookup = new UserLicenseTypeLookup
                {
                    User = user,
                    License = license
                };
                db.UserLicenseTypeLookups.Add(lookup);
            }
        }

        private string GenerateEventData()
        {
            return $"{{\"TestData\": \"Generated at {DateTime.UtcNow}\"}}";
        }

        private string GetRandomExtension()
        {
            string[] extensions = { "docx", "xlsx", "pptx", "pdf", "txt" };
            return extensions[_random.Next(extensions.Length)];
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
