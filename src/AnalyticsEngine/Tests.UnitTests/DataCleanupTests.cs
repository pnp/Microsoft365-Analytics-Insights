using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Entities.Teams;
using Common.Entities.Entities.UsageReports;
using Common.Entities.Entities.WebTraffic;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class DataCleanupTests
    {
        [TestMethod]
        public async Task CleanupHistoricalDataTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await InsertTestDataAll(db);

                await db.Database.ExecuteSqlCommandAsync(Properties.Resources.Clean_Old_Data_Data);
            }
        }

        [TestMethod]
        public async Task CleanupDataForUserDataTests()
        {
            var userId = 0;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await InsertTestDataAll(db);
                userId = user.ID;

                // Ensure stored proc exists
                var statements = StringUtils.SplitSqlStatements(ResourceProxy.CreateOrUpdateClean_Data_By_User_StoredProc);
                foreach (var statement in statements)
                    await db.Database.ExecuteSqlCommandAsync(statement);

                // Run cleanup for user
                await db.Database.ExecuteSqlCommandAsync("EXEC CleanDataByUser @p0", userId);
            }
            using (var db = new AnalyticsEntitiesContext())
            {
                // Find user again in new context for caching. Should be gone
                var checkUser = await db.users.FindAsync(userId);
                Assert.IsNull(checkUser);
            }
        }


        private async Task<User> InsertTestDataAll(AnalyticsEntitiesContext db)
        {
            var user = new User { UserPrincipalName = "user" + DateTime.Now.Ticks + "@example.com", Mail = "user" + DateTime.Now.Ticks + "@example.com" };
            db.users.Add(user);
            await InsertTestDataAll(user, db);  
            return user;
        }
        private async Task InsertTestDataAll(User user, AnalyticsEntitiesContext db)
        {

            var oneYearAgo = DateTime.Now.AddYears(-1);
            long ticks = DateTime.Now.Ticks;

            // Base lookups first (reused later)
            var site = new Site { UrlBase = "http://site-" + ticks, SiteId = "SiteId-" + ticks };
            db.sites.Add(site);

            var web = new Web { url_base = site.UrlBase + "/web-" + ticks, title = "Web Title " + ticks, site = site };
            db.webs.Add(web);

            var url = new Url { FullUrl = web.url_base + "/page-" + ticks };
            db.urls.Add(url);

            var op = new EventOperation { Name = "Operation-" + ticks };
            db.event_operations.Add(op);


            var browser = new Browser { browser_name = "Browser-" + ticks };
            var city = new City { city_name = "City-" + ticks };
            var country = new Country { country_name = "Country-" + ticks };
            var device = new Device { device_name = "Device-" + ticks };
            var os = new Common.Entities.OperatingSystem { os_name = "OS-" + ticks };
            db.browsers.Add(browser);
            db.cities.Add(city);
            db.countries.Add(country);
            db.devices.Add(device);
            db.operating_systems.Add(os);

            var pageTitle = new PageTitle { title = "Title-" + ticks };
            db.page_titles.Add(pageTitle);

            var lang = new Language { Name = "Lang-" + (ticks % 900000) };
            db.Languages.Add(lang);

            var keyword = new KeyWord { Name = "Keyword-" + ticks };
            db.KeyWords.Add(keyword);

            var fileField = new FileMetadataFieldName { Name = "Field-" + ticks };
            db.FileMetadataFields.Add(fileField);

            var fileProp = new FileMetadataPropertyValue
            {
                Url = url,
                Field = fileField,
                FieldValue = "Value-" + ticks,
                Updated = oneYearAgo
            };
            db.FileMetadataPropertyValues.Add(fileProp);

            // URL user records
            var pageLike = new PageLike { Url = url, User = user, Created = oneYearAgo, SpID = (int)(ticks % int.MaxValue) };
            var pageComment = new PageComment { Url = url, User = user, Created = oneYearAgo, Comment = "Comment-" + ticks, SpID = (int)(ticks % int.MaxValue) };
            db.UrlLikes.Add(pageLike);
            db.UrlComments.Add(pageComment);

            // Audit base event
            var auditEvent = new Office365Event
            {
                Id = Guid.NewGuid(),
                TimeStamp = oneYearAgo,
                Operation = op,
                User = user,
                EventData = "{}"
            };
            db.AuditEventsCommon.Add(auditEvent);

            // SP Event type + extension + filename
            var spExt = new SPEventFileExtension { extension_name = "Ext-" + ticks };
            var spFileName = new SPEventFileName { Name = "File-" + ticks };
            var spType = new SPEventType { type_name = "Type-" + ticks };
            db.event_file_ext.Add(spExt);
            db.event_file_names.Add(spFileName);
            db.event_types.Add(spType);

            var spEvent = new SharePointEventMetadata
            {
                url = url,
                Event = new Office365Event
                {
                    Id = Guid.NewGuid(),
                    Operation = op,
                    User = user,
                    TimeStamp = oneYearAgo,
                    EventData = "{}"
                }
            };
            db.sharepoint_events.Add(spEvent);

            // Hit (minimal)
            var hit = new Hit
            {
                url = url,
                web = web,
                agent = browser,
                city = city,
                country = country,
                device = device,
                os = os,
                hit_timestamp = oneYearAgo,
                page_title = pageTitle,
                page_request_id = Guid.NewGuid()
            };
            db.hits.Add(hit);

            // Search term + session + search
            var session = new UserSession { ai_session_id = "sess-" + ticks, user = user };
            db.sessions.Add(session);

            var term = new SearchTerm { search_term = "term-" + ticks };
            db.search_terms.Add(term);

            var search = new Search
            {
                search_term = term,
                session = session,
                DateTime = oneYearAgo
            };
            db.searches.Add(search);

            // Org + OrgUrl
            var org = new Org { org_name = "Org-" + ticks };
            db.orgs.Add(org);
            var orgUrl = new OrgUrl { UrlBase = "https://org-" + ticks + ".example.com" };
            db.org_urls.Add(orgUrl);

            // Import log
            var import = new ImportLog { time_stamp = oneYearAgo, import_message = "Details-" + ticks, contents = "Test", machine_name = "UnitTestVM" };
            db.import_log.Add(import);

            // Ignored event
            var ignored = new IgnoredEvent { processed_timestamp = oneYearAgo, event_id = Guid.NewGuid() };
            db.ignored_audit_events.Add(ignored);

            // Geography
            var province = new Province { province_name = "Prov-" + ticks };
            db.provinces.Add(province);

            // Simple taxonomy style lookups for user
            var dept = new UserDepartment { Name = "Dept-" + ticks };
            var job = new UserJobTitle { Name = "Job-" + ticks };
            var officeLoc = new UserOfficeLocation { Name = "Office-" + ticks };
            var usageLoc = new UserUsageLocation { Name = "UL-" + ticks };
            var licType = new LicenseType { Name = "Lic-" + ticks };
            var stateOrProv = new StateOrProvince { Name = "StateProv-" + ticks };
            var countryOrRegion = new CountryOrRegion { Name = "CountryRegion-" + ticks };
            var company = new CompanyName { Name = "Company-" + ticks };

            db.UserDepartments.Add(dept);
            db.UserJobTitles.Add(job);
            db.UserOfficeLocations.Add(officeLoc);
            db.UserUsageLocations.Add(usageLoc);
            db.LicenseTypes.Add(licType);
            db.StateOrProvinces.Add(stateOrProv);
            db.CountryOrRegions.Add(countryOrRegion);
            db.CompanyNames.Add(company);

            var userLicense = new UserLicenseTypeLookup { License = licType, User = user };
            db.UserLicenseTypeLookups.Add(userLicense);

            // Teams definitions
            var team = new TeamDefinition
            {
                Name = "Team-" + ticks,
                GraphID = "TeamGraph-" + ticks,
                FirstDiscovered = oneYearAgo,
                HasRefreshToken = false
            };
            db.Teams.Add(team);

            var teamChannel = new Common.Entities.Teams.TeamChannel
            {
                GraphID = "ChanGraph-" + ticks,
                Name = "Channel-" + ticks,
                Team = team
            };
            db.TeamChannels.Add(teamChannel);

            var owner = new TeamOwners
            {
                Team = team,
                Owner = user,
                Discovered = oneYearAgo
            };
            db.TeamOwners.Add(owner);

            var membership = new TeamMembershipLog
            {
                Team = team,
                User = user,
                Date = oneYearAgo
            };
            db.TeamMembershipLogs.Add(membership);

            var addonDef = new Common.Entities.Teams.TeamAddOnDefinition
            {
                GraphID = "AddOnGraph-" + ticks,
                Name = "AddOn-" + ticks,
                AddOnType = 1,
                PublishedState = "published"
            };
            db.TeamAddOns.Add(addonDef);

            var addonLog = new Common.Entities.Teams.TeamAddOnLog
            {
                Team = team,
                AddOn = addonDef,
                Date = oneYearAgo
            };
            db.TeamAddOnLogs.Add(addonLog);

            var userAppLog = new Common.Entities.Teams.UserAppsLog
            {
                AddOn = addonDef,
                User = user,
                Date = oneYearAgo
            };
            db.UserAppsLog.Add(userAppLog);

            var tabDef = new TeamTabDefinition
            {
                GraphID = "TabGraph-" + ticks,
                Name = "Tab-" + ticks,
                WebUrl = "https://tab/" + ticks,
                TeamAddOnDefinition = addonDef
            };
            db.TeamTabDefinitions.Add(tabDef);

            var channelTabLog = new ChannelTabLog
            {
                Channel = teamChannel,
                Date = oneYearAgo,
                TabDefinition = tabDef
            };
            db.ChannelTabLogs.Add(channelTabLog);

            // Channel stats & associations
            var chanStats = new ChannelStatsLog
            {
                Channel = teamChannel,
                Date = oneYearAgo,
                ChatsCount = 1,
                SentimentScore = 0.5
            };
            db.TeamChannelStats.Add(chanStats);

            var chanKeyword = new ChannelLogKeyword
            {
                ChannelStatsLog = chanStats,
                KeyWord = keyword,
                KeyWordCount = 2
            };
            db.TeamChannelStatKeywords.Add(chanKeyword);

            var chanLang = new ChannelLogLanguage
            {
                ChannelStatsLog = chanStats,
                Language = lang
            };
            db.TeamChannelStatLanguages.Add(chanLang);

            // Reactions
            var reactType = new TeamsReactionType { Name = "Reaction-" + ticks };
            db.TeamsReactionTypes.Add(reactType);

            var reaction = new TeamsUserReaction
            {
                Reaction = reactType,
                User = user,
                Channel = teamChannel,
                Date = oneYearAgo
            };
            db.TeamsUserReactions.Add(reaction);

            // Usage activity logs
            db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
            {
                User = user,
                Date = oneYearAgo,
                PrivateChatMessageCount = 1,
                TeamChatMessageCount = 1,
                CallCount = 0,
                MeetingCount = 0
            });

            db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog
            {
                User = user,
                Date = oneYearAgo,
                UsedWindows = true
            });

            db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
            {
                User = user,
                Date = oneYearAgo,
                Web = true
            });

            db.OutlookUsageActivityLogs.Add(new OutlookUsageActivityLog { User = user, Date = oneYearAgo });
            db.OneDriveUserActivityLogs.Add(new OneDriveUserActivityLog { User = user, Date = oneYearAgo });
            db.OneDriveUsageLogs.Add(new OneDriveUsageLog { Date = oneYearAgo, StorageUsedInBytes = 1 });
            db.SharePointUserActivityLogs.Add(new SharePointUserActivityLog { User = user, Date = oneYearAgo });
            db.YammerUserActivityLogs.Add(new YammerUserActivityLog { User = user, Date = oneYearAgo });
            db.YammerGroupActivityLogs.Add(new YammerGroupActivityLog { Date = oneYearAgo, PostedCount = 1 });
            db.YammerDeviceActivityLogs.Add(new YammerDeviceActivityLog { Date = oneYearAgo, UsedWeb = true });
            db.SharePointSiteStats.Add(new SharePointSitesFileWeeklyStats { Site = site, ForWeekEnding = oneYearAgo });

            // Call related
            var callType = new CallType { Name = "CallType-" + ticks };
            db.CallTypes.Add(callType);

            var callModality = new CallModality { Name = "Modality-" + ticks };
            db.CallModalities.Add(callModality);

            var callRecord = new CallRecord
            {
                Organizer = user,
                CallType = callType,
                GraphID = "CallGraph-" + ticks,
                StartDateTime = oneYearAgo,
                EndDateTime = oneYearAgo.AddMinutes(5)
            };
            db.CallRecords.Add(callRecord);

            var callSession = new CallSession
            {
                Attendee = user,
                Start = oneYearAgo,
                End = oneYearAgo.AddMinutes(5),
                ParentRecord = callRecord
            };
            db.CallSessions.Add(callSession);

            var callSessionModality = new CallSessionModalityLookup
            {
                CallSession = callSession,
                CallModality = callModality
            };
            db.CallModalityLookups.Add(callSessionModality);

            var callFeedback = new CallFeedback
            {
                Call = callRecord,
                Rating = "5"
            };
            db.CallFeedback.Add(callFeedback);

            var callFailureReason = new CallFailureReasonLookup
            {
                Call = callRecord,
                Reason = "None-" + ticks
            };
            db.CallFailures.Add(callFailureReason);

            // Stream / Yammer
            var streamVideo = new StreamVideo { StreamID = Guid.NewGuid(), Name = "Video-" + ticks };
            db.Streams.Add(streamVideo);

            var yammerGroup = new YammerGroup { Name = "Group-" + ticks };
            db.YammerGroups.Add(yammerGroup);

            var yammerMessage = new YammerMessage { Created = oneYearAgo, YammerID = DateTime.Now.Ticks };
            db.YammerMessages.Add(yammerMessage);

            var yammerLink = new YammerStreamLink { Message = yammerMessage, Video = streamVideo };
            db.YammerStreamLinks.Add(yammerLink);

            var streamEvent = new StreamEventMetada
            {
                Event = new Office365Event
                {
                    Id = Guid.NewGuid(),
                    TimeStamp = oneYearAgo,
                    Operation = op,
                    User = user
                },
                Video = streamVideo
            };
            db.StreamEvents.Add(streamEvent);

            // O365 client app
            var clientApp = new O365ClientApplication { ClientApplicationId = Guid.NewGuid(), Name = "ClientAppName-" + ticks };
            db.O365ClientApplications.Add(clientApp);

            // Associate client app with the previously created stream event (if navigation exists)
            streamEvent.ClientApplication = clientApp;

            // Click tracking
            var clickedTitle = new ClickedElementTitle { Name = "CTitle-" + ticks };
            var clickedClasses = new ClickedElementsClassNames { AllClassNames = "cls-" + ticks };
            db.ClickedElementTitles.Add(clickedTitle);
            db.ClickedElementsClassNames.Add(clickedClasses);

            var click = new Clicks
            {
                Url = url,
                TimeStamp = oneYearAgo,
                Title = clickedTitle,
                ClassNames = clickedClasses,
                PageView = hit
            };
            db.Clicks.Add(click);

            var copilotFileEvent = new CopilotEventMetadataFile
            {
                // Reuse existing SP file extension & file name & site
                FileExtension = spExt,
                FileName = spFileName,
                Site = site,
                RelatedChat = new CopilotChat
                {
                    Event = new Office365Event
                    {
                        Id = Guid.NewGuid(),
                        TimeStamp = oneYearAgo,
                        Operation = op,
                        User = user,
                        EventData = "{}"
                    },
                    AppHost = "AppHost-" + ticks
                }
            };

            // Use generic set to avoid needing explicit DbSet property
            db.CopilotEventMetadataFiles.Add(copilotFileEvent);

            await db.SaveChangesAsync();
        }
    }
}
