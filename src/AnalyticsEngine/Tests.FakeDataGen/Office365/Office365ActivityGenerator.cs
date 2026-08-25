using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Entities.Teams;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using Tests.FakeDataGen.Generation;
using Tests.FakeDataGen.Seeding;
using WebJob.Office365ActivityImporter.Engine;

namespace Tests.FakeDataGen.Office365
{
    /// <summary>
    /// Generates synthetic Microsoft 365 audit activity in the same relational shape as
    /// the Office 365 Management Activity API importer.
    /// </summary>
    public class Office365ActivityGenerator
    {
        private readonly string _connectionString;
        private readonly Random _random = new Random();

        public Office365ActivityGenerator(string connectionString)
        {
            _connectionString = connectionString;
        }

        public void GenerateOffice365Activity(
            int count,
            int userCount = 250,
            int daysBack = 90,
            DateTime? windowEndUtc = null)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (userCount < 1) throw new ArgumentOutOfRangeException(nameof(userCount));
            if (daysBack < 1) throw new ArgumentOutOfRangeException(nameof(daysBack));

            int totalWeight =
                Office365ActivityGeneratorConfig.SharePointWeight +
                Office365ActivityGeneratorConfig.OneDriveWeight +
                Office365ActivityGeneratorConfig.ExchangeWeight +
                Office365ActivityGeneratorConfig.AzureAdWeight;
            if (totalWeight != 100)
            {
                throw new InvalidOperationException(
                    $"Office 365 workload weights must total 100; configured total is {totalWeight}.");
            }

            DateTime effectiveWindowEndUtc = windowEndUtc ?? DateTime.UtcNow;

            Console.WriteLine($"Generating {count:N0} Microsoft 365 audit events...");
            Console.WriteLine($"- Spread across the last {daysBack:N0} day(s)");
            Console.WriteLine("- Workloads: SharePoint, OneDrive, Exchange, and Microsoft Entra ID");
            Console.WriteLine("- Activity is weighted toward weekdays and business hours");

            EnsureUsersExist(userCount);
            var users = LoadUsers(userCount);
            var documents = BuildDocumentPool(count);
            EnsureGeneratorLookups(documents.All);

            int inserted = 0;
            int profilingRows = 0;
            var counters = new GenerationCounters();

            for (int batchStart = 0; batchStart < count; batchStart += Office365ActivityGeneratorConfig.SaveBatchSize)
            {
                int batchCount = Math.Min(Office365ActivityGeneratorConfig.SaveBatchSize, count - batchStart);

                using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
                {
                    db.Configuration.AutoDetectChangesEnabled = false;
                    var lookups = LoadBatchLookups(db, documents.All);
                    var dailyUsage = new DailyUsageAccumulator();

                    for (int i = 0; i < batchCount; i++)
                    {
                        var user = users[_random.Next(users.Count)];
                        var timestamp = ActivityTimestampGenerator.Next(
                            _random,
                            daysBack,
                            effectiveWindowEndUtc);
                        var workload = PickWorkload();
                        dailyUsage.RecordTeams(user.Id, timestamp.Date, _random);

                        switch (workload)
                        {
                            case SyntheticWorkload.SharePoint:
                                AddSharePointEvent(db, lookups, documents.SharePoint, user, timestamp, false, dailyUsage);
                                counters.SharePoint++;
                                break;
                            case SyntheticWorkload.OneDrive:
                                AddSharePointEvent(db, lookups, documents.OneDrive, user, timestamp, true, dailyUsage);
                                counters.OneDrive++;
                                break;
                            case SyntheticWorkload.Exchange:
                                AddExchangeEvent(db, lookups, user, timestamp, dailyUsage);
                                counters.Exchange++;
                                break;
                            case SyntheticWorkload.AzureActiveDirectory:
                                AddAzureAdEvent(db, lookups, user, timestamp);
                                counters.AzureActiveDirectory++;
                                break;
                            default:
                                throw new InvalidOperationException($"Unsupported synthetic workload: {workload}");
                        }
                    }

                    profilingRows += AddProfilingSourceRows(db, dailyUsage);
                    db.ChangeTracker.DetectChanges();
                    db.SaveChanges();
                }

                inserted += batchCount;
                Console.WriteLine($"Inserted {inserted:N0}/{count:N0} events...");
            }

            Console.WriteLine();
            Console.WriteLine("Generation complete!");
            Console.WriteLine($"Total events:       {inserted:N0}");
            Console.WriteLine($"SharePoint:         {counters.SharePoint:N0} ({Percent(counters.SharePoint, inserted):F1}%)");
            Console.WriteLine($"OneDrive:           {counters.OneDrive:N0} ({Percent(counters.OneDrive, inserted):F1}%)");
            Console.WriteLine($"Exchange:           {counters.Exchange:N0} ({Percent(counters.Exchange, inserted):F1}%)");
            Console.WriteLine($"Microsoft Entra ID: {counters.AzureActiveDirectory:N0} ({Percent(counters.AzureActiveDirectory, inserted):F1}%)");
            Console.WriteLine($"Profiling rows:     {profilingRows:N0} daily SharePoint/OneDrive/Outlook/Teams source row(s)");
            Console.WriteLine();
            Console.WriteLine("Profiling note: Microsoft Entra ID has no ActivitiesWeekly metric.");
            Console.WriteLine("Yammer and Copilot metrics depend on their separate source generators.");
        }

        private void EnsureUsersExist(int userCount)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM users WHERE user_name IS NOT NULL AND user_name <> N''
) THEN 1 ELSE 0 END;";

                    if (Convert.ToInt32(command.ExecuteScalar()) == 1)
                    {
                        return;
                    }
                }

                Console.WriteLine($"No users found in database. Creating {userCount:N0} test users...");
                UserMetadataSeeder.EnsureMetadataLookups(connection);
                UserMetadataSeeder.EnsureLicenseTypes(connection);

                var seededUsers = UserMetadataSeeder.SeedUsers(
                    connection,
                    userCount,
                    _random,
                    upnPrefix: "o365user");

                var licenseIds = UserMetadataSeeder.LoadLicenseTypeIds(connection);
                int assignments = UserMetadataSeeder.AssignRandomLicenses(
                    connection,
                    seededUsers.Select(u => u.Id),
                    licenseIds,
                    _random,
                    maxLicensesPerUser: 2);

                Console.WriteLine($"Created {seededUsers.Count:N0} user(s) and {assignments:N0} license assignment(s).");
            }
        }

        private List<UserReference> LoadUsers(int userCount)
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var rows = db.users
                    .AsNoTracking()
                    .Where(u => u.UserPrincipalName != null && u.UserPrincipalName != string.Empty)
                    .OrderBy(u => u.ID)
                    .Take(userCount)
                    .Select(u => u.ID)
                    .ToList();

                var users = rows
                    .Select(id => new UserReference(id))
                    .ToList();

                if (users.Count == 0)
                {
                    throw new InvalidOperationException("No users with a user principal name are available for generated activity.");
                }

                Console.WriteLine($"Spreading activity across {users.Count:N0} user(s).");
                return users;
            }
        }

        private DocumentPool BuildDocumentPool(int eventCount)
        {
            int desiredPoolSize = Math.Min(
                Office365ActivityGeneratorConfig.MaxDocumentPoolSize,
                Math.Max(2, Math.Min(eventCount, Math.Max(20, eventCount / 5))));

            int sharePointCount = Math.Max(1, (int)Math.Round(desiredPoolSize * 0.65));
            int oneDriveCount = Math.Max(1, desiredPoolSize - sharePointCount);

            var sharePointDocuments = BuildDocuments(
                Office365ActivityGeneratorConfig.SharePointSites,
                sharePointCount,
                0);

            var oneDriveDocuments = BuildDocuments(
                Office365ActivityGeneratorConfig.OneDriveSites,
                oneDriveCount,
                sharePointCount);

            return new DocumentPool(sharePointDocuments, oneDriveDocuments);
        }

        private static List<SyntheticDocument> BuildDocuments(
            IReadOnlyList<Office365SiteDefinition> sites,
            int count,
            int indexOffset)
        {
            var documents = new List<SyntheticDocument>(count);
            for (int i = 0; i < count; i++)
            {
                int index = indexOffset + i;
                var site = sites[i % sites.Count];
                string extension = Office365ActivityGeneratorConfig.FileExtensions[
                    index % Office365ActivityGeneratorConfig.FileExtensions.Length];
                string stem = Office365ActivityGeneratorConfig.FileNameStems[
                    index % Office365ActivityGeneratorConfig.FileNameStems.Length];
                string folder = Office365ActivityGeneratorConfig.FolderNames[
                    index % Office365ActivityGeneratorConfig.FolderNames.Length];
                string fileName = $"{stem}-{index:D4}.{extension}";
                string objectUrl = StringUtils.EnsureUrlWithinLength(
                    $"{site.Url.TrimEnd('/')}/{folder}/{fileName}",
                    Url.FullUrlMaxLength);

                documents.Add(new SyntheticDocument(site, fileName, extension, objectUrl));
            }
            return documents;
        }

        private void EnsureGeneratorLookups(IReadOnlyList<SyntheticDocument> documents)
        {
            string[] operationNames = Office365ActivityGeneratorConfig.GetAllOperationNames();
            string[] extensionNames = documents.Select(d => d.Extension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] fileNames = documents.Select(d => d.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] objectUrls = documents.Select(d => d.ObjectUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] propertyNames = Office365ActivityGeneratorConfig.AuditPropertyNames;
            string[] propertyValues = Office365ActivityGeneratorConfig.GetAllAuditPropertyValues();
            var siteDefinitions = Office365ActivityGeneratorConfig.GetAllSites();
            string[] siteUrls = siteDefinitions.Select(s => s.Url).ToArray();

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                var existingOperations = new HashSet<string>(
                    db.event_operations
                        .Where(o => operationNames.Contains(o.Name))
                        .Select(o => o.Name)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string name in operationNames)
                {
                    if (existingOperations.Add(name))
                    {
                        db.event_operations.Add(new EventOperation { Name = name });
                    }
                }

                var existingExtensions = new HashSet<string>(
                    db.event_file_ext
                        .Where(e => extensionNames.Contains(e.extension_name))
                        .Select(e => e.extension_name)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string extension in extensionNames)
                {
                    if (existingExtensions.Add(extension))
                    {
                        db.event_file_ext.Add(new SPEventFileExtension { extension_name = extension });
                    }
                }

                var existingFileNames = new HashSet<string>(
                    db.event_file_names
                        .Where(f => fileNames.Contains(f.Name))
                        .Select(f => f.Name)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string fileName in fileNames)
                {
                    if (existingFileNames.Add(fileName))
                    {
                        db.event_file_names.Add(new SPEventFileName { Name = fileName });
                    }
                }

                const string itemType = "File";
                bool itemTypeExists = db.event_types.Any(t => t.type_name == itemType);
                if (!itemTypeExists)
                {
                    db.event_types.Add(new SPEventType { type_name = itemType });
                }

                var existingUrls = new HashSet<string>(
                    db.urls
                        .Where(u => objectUrls.Contains(u.FullUrl))
                        .Select(u => u.FullUrl)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string objectUrl in objectUrls)
                {
                    if (existingUrls.Add(objectUrl))
                    {
                        db.urls.Add(new Url { FullUrl = objectUrl });
                    }
                }

                var existingPropertyNames = new HashSet<string>(
                    db.audit_event_prop_names
                        .Where(p => propertyNames.Contains(p.name))
                        .Select(p => p.name)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string propertyName in propertyNames)
                {
                    if (existingPropertyNames.Add(propertyName))
                    {
                        db.audit_event_prop_names.Add(new AuditPropertyName { name = propertyName });
                    }
                }

                var existingPropertyValues = new HashSet<string>(
                    db.audit_event_prop_vals
                        .Where(p => propertyValues.Contains(p.value))
                        .Select(p => p.value)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string propertyValue in propertyValues)
                {
                    if (existingPropertyValues.Add(propertyValue))
                    {
                        db.audit_event_prop_vals.Add(new AuditPropertyValue { value = propertyValue });
                    }
                }

                var sitesByUrl = ToDictionaryFirst(
                    db.sites.Where(s => siteUrls.Contains(s.UrlBase)).ToList(),
                    s => s.UrlBase,
                    s => s);
                for (int i = 0; i < siteDefinitions.Count; i++)
                {
                    var definition = siteDefinitions[i];
                    if (!sitesByUrl.ContainsKey(definition.Url))
                    {
                        var site = new Site
                        {
                            UrlBase = definition.Url,
                            SiteId = $"synthetic-site-{i:D3}"
                        };
                        db.sites.Add(site);
                        sitesByUrl.Add(definition.Url, site);
                    }
                }

                var existingWebUrls = new HashSet<string>(
                    db.webs
                        .Where(w => siteUrls.Contains(w.url_base))
                        .Select(w => w.url_base)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var definition in siteDefinitions)
                {
                    if (existingWebUrls.Add(definition.Url))
                    {
                        db.webs.Add(new Web
                        {
                            url_base = definition.Url,
                            title = definition.Title,
                            site = sitesByUrl[definition.Url]
                        });
                    }
                }

                db.ChangeTracker.DetectChanges();
                db.SaveChanges();
            }
        }

        private BatchLookups LoadBatchLookups(
            AnalyticsEntitiesContext db,
            IReadOnlyList<SyntheticDocument> documents)
        {
            string[] operationNames = Office365ActivityGeneratorConfig.GetAllOperationNames();
            string[] extensionNames = documents.Select(d => d.Extension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] fileNames = documents.Select(d => d.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] objectUrls = documents.Select(d => d.ObjectUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] siteUrls = Office365ActivityGeneratorConfig.GetAllSites().Select(s => s.Url).ToArray();
            string[] propertyNames = Office365ActivityGeneratorConfig.AuditPropertyNames;
            string[] propertyValues = Office365ActivityGeneratorConfig.GetAllAuditPropertyValues();

            var lookups = new BatchLookups
            {
                OperationIds = ToDictionaryFirst(
                    db.event_operations.Where(o => operationNames.Contains(o.Name)).ToList(),
                    o => o.Name,
                    o => o.ID),
                FileExtensions = ToDictionaryFirst(
                    db.event_file_ext.Where(e => extensionNames.Contains(e.extension_name)).ToList(),
                    e => e.extension_name,
                    e => e),
                FileNames = ToDictionaryFirst(
                    db.event_file_names.Where(f => fileNames.Contains(f.Name)).ToList(),
                    f => f.Name,
                    f => f),
                Urls = ToDictionaryFirst(
                    db.urls.Where(u => objectUrls.Contains(u.FullUrl)).ToList(),
                    u => u.FullUrl,
                    u => u),
                Webs = ToDictionaryFirst(
                    db.webs.Where(w => siteUrls.Contains(w.url_base)).ToList(),
                    w => w.url_base,
                    w => w),
                EventTypes = ToDictionaryFirst(
                    db.event_types.Where(t => t.type_name == "File").ToList(),
                    t => t.type_name,
                    t => t),
                PropertyNameIds = ToDictionaryFirst(
                    db.audit_event_prop_names.Where(p => propertyNames.Contains(p.name)).ToList(),
                    p => p.name,
                    p => p.id),
                PropertyValueIds = ToDictionaryFirst(
                    db.audit_event_prop_vals.Where(p => propertyValues.Contains(p.value)).ToList(),
                    p => p.value,
                    p => p.id)
            };

            EnsureAllPresent("event operation", operationNames, lookups.OperationIds.Keys);
            EnsureAllPresent("file extension", extensionNames, lookups.FileExtensions.Keys);
            EnsureAllPresent("file name", fileNames, lookups.FileNames.Keys);
            EnsureAllPresent("URL", objectUrls, lookups.Urls.Keys);
            EnsureAllPresent("site web", siteUrls, lookups.Webs.Keys);
            EnsureAllPresent("event type", new[] { "File" }, lookups.EventTypes.Keys);
            EnsureAllPresent("audit property name", propertyNames, lookups.PropertyNameIds.Keys);
            EnsureAllPresent("audit property value", propertyValues, lookups.PropertyValueIds.Keys);

            return lookups;
        }

        private void AddSharePointEvent(
            AnalyticsEntitiesContext db,
            BatchLookups lookups,
            IReadOnlyList<SyntheticDocument> documents,
            UserReference user,
            DateTime timestamp,
            bool isOneDrive,
            DailyUsageAccumulator dailyUsage)
        {
            var document = documents[_random.Next(documents.Count)];
            string workload = isOneDrive
                ? ActivityImportConstants.WORKLOAD_OD
                : ActivityImportConstants.WORKLOAD_SP;
            string[] operations = isOneDrive
                ? Office365ActivityGeneratorConfig.OneDriveOperations
                : Office365ActivityGeneratorConfig.SharePointOperations;
            string operation = Pick(operations);

            var commonEvent = new CommonAuditEvent
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                OperationId = lookups.OperationIds[operation],
                TimeStamp = timestamp,
                EventData = JsonConvert.SerializeObject(new
                {
                    Synthetic = true,
                    Workload = workload,
                    Operation = operation,
                    SiteUrl = document.Site.Url,
                    ObjectId = document.ObjectUrl,
                    SourceFileName = document.FileName,
                    SourceFileExtension = document.Extension,
                    ItemType = "File"
                })
            };

            db.AuditEventsCommon.Add(commonEvent);
            db.sharepoint_events.Add(new SharePointEventMetadata
            {
                AuditEvent = commonEvent,
                file_extension = lookups.FileExtensions[document.Extension],
                file_name = lookups.FileNames[document.FileName],
                url = lookups.Urls[document.ObjectUrl],
                related_web = lookups.Webs[document.Site.Url],
                item_type = lookups.EventTypes["File"]
            });
            dailyUsage.RecordSharePoint(user.Id, timestamp.Date, isOneDrive, operation, _random);
        }

        private void AddExchangeEvent(
            AnalyticsEntitiesContext db,
            BatchLookups lookups,
            UserReference user,
            DateTime timestamp,
            DailyUsageAccumulator dailyUsage)
        {
            string operation = Pick(Office365ActivityGeneratorConfig.ExchangeOperations);
            var commonEvent = new CommonAuditEvent
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                OperationId = lookups.OperationIds[operation],
                TimeStamp = timestamp
            };
            var metadata = new ExchangeEventMetadata
            {
                AuditEvent = commonEvent,
                object_id = $"mailbox/{user.Id}/items/{Guid.NewGuid():N}"
            };

            db.AuditEventsCommon.Add(commonEvent);
            db.exchange_events.Add(metadata);
            AddExchangeProperty(db, lookups, metadata, "ClientIP",
                Pick(Office365ActivityGeneratorConfig.ExchangeClientIpValues));
            AddExchangeProperty(db, lookups, metadata, "ClientInfoString",
                Pick(Office365ActivityGeneratorConfig.ExchangeClientInfoValues));
            AddExchangeProperty(db, lookups, metadata, "LogonType",
                Pick(Office365ActivityGeneratorConfig.LogonTypeValues));
            dailyUsage.RecordExchange(user.Id, timestamp.Date, operation, _random);
        }

        private void AddAzureAdEvent(
            AnalyticsEntitiesContext db,
            BatchLookups lookups,
            UserReference user,
            DateTime timestamp)
        {
            string operation = Pick(Office365ActivityGeneratorConfig.AzureAdOperations);
            var commonEvent = new CommonAuditEvent
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                OperationId = lookups.OperationIds[operation],
                TimeStamp = timestamp
            };
            var metadata = new AzureADEventMetadata
            {
                AuditEvent = commonEvent
            };

            db.AuditEventsCommon.Add(commonEvent);
            db.azure_ad_events.Add(metadata);
            AddAzureAdProperty(db, lookups, metadata, "ResultStatus",
                Pick(Office365ActivityGeneratorConfig.ResultStatusValues));
            AddAzureAdProperty(db, lookups, metadata, "AuthenticationMethod",
                Pick(Office365ActivityGeneratorConfig.AuthenticationMethodValues));
            AddAzureAdProperty(db, lookups, metadata, "ClientIP",
                Pick(Office365ActivityGeneratorConfig.AzureAdClientIpValues));
            AddAzureAdProperty(db, lookups, metadata, "UserAgent",
                Pick(Office365ActivityGeneratorConfig.UserAgentValues));
        }

        private static void AddExchangeProperty(
            AnalyticsEntitiesContext db,
            BatchLookups lookups,
            ExchangeEventMetadata parent,
            string name,
            string value)
        {
            db.audit_event_props.Add(new ExchangeExtendedProperties
            {
                ParentEvent = parent,
                PropNameID = lookups.PropertyNameIds[name],
                PropValID = lookups.PropertyValueIds[value]
            });
        }

        private static void AddAzureAdProperty(
            AnalyticsEntitiesContext db,
            BatchLookups lookups,
            AzureADEventMetadata parent,
            string name,
            string value)
        {
            db.Set<AzureADExtendedProperties>().Add(new AzureADExtendedProperties
            {
                ParentEvent = parent,
                PropNameID = lookups.PropertyNameIds[name],
                PropValID = lookups.PropertyValueIds[value]
            });
        }

        private static int AddProfilingSourceRows(
            AnalyticsEntitiesContext db,
            DailyUsageAccumulator dailyUsage)
        {
            int added = 0;
            foreach (var summary in dailyUsage.Summaries)
            {
                switch (summary.Workload)
                {
                    case SyntheticWorkload.SharePoint:
                        db.SharePointUserActivityLogs.Add(new SharePointUserActivityLog
                        {
                            UserID = summary.UserId,
                            Date = summary.Date,
                            LastActivityDate = summary.Date,
                            ViewedOrEdited = summary.ViewedOrEdited,
                            Synced = summary.Synced,
                            SharedInternally = summary.SharedInternally,
                            SharedExternally = summary.SharedExternally
                        });
                        break;
                    case SyntheticWorkload.OneDrive:
                        db.OneDriveUserActivityLogs.Add(new OneDriveUserActivityLog
                        {
                            UserID = summary.UserId,
                            Date = summary.Date,
                            LastActivityDate = summary.Date,
                            ViewedOrEdited = summary.ViewedOrEdited,
                            Synced = summary.Synced,
                            SharedInternally = summary.SharedInternally,
                            SharedExternally = summary.SharedExternally
                        });
                        break;
                    case SyntheticWorkload.Exchange:
                        db.OutlookUsageActivityLogs.Add(new OutlookUsageActivityLog
                        {
                            UserID = summary.UserId,
                            Date = summary.Date,
                            LastActivityDate = summary.Date,
                            SendCount = summary.EmailSent,
                            ReceiveCount = summary.EmailReceived,
                            ReadCount = summary.EmailRead,
                            MeetingCreated = summary.MeetingsCreated,
                            MeetingInteracted = summary.MeetingsInteracted
                        });
                        break;
                    case SyntheticWorkload.Teams:
                        db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                        {
                            UserID = summary.UserId,
                            Date = summary.Date,
                            LastActivityDate = summary.Date,
                            PrivateChatMessageCount = summary.TeamsPrivateChats,
                            TeamChatMessageCount = summary.TeamsTeamChats,
                            CallCount = summary.TeamsCalls,
                            MeetingCount = summary.TeamsMeetings,
                            MeetingsAttendedCount = summary.TeamsMeetingsAttended,
                            MeetingsOrganizedCount = summary.TeamsMeetingsOrganized,
                            AdHocMeetingsAttendedCount = summary.TeamsAdHocMeetingsAttended,
                            AdHocMeetingsOrganizedCount = summary.TeamsAdHocMeetingsOrganized,
                            ScheduledOneTimeMeetingsAttendedCount = summary.TeamsScheduledOneTimeMeetingsAttended,
                            ScheduledOneTimeMeetingsOrganizedCount = summary.TeamsScheduledOneTimeMeetingsOrganized,
                            ScheduledRecurringMeetingsAttendedCount = summary.TeamsScheduledRecurringMeetingsAttended,
                            ScheduledRecurringMeetingsOrganizedCount = summary.TeamsScheduledRecurringMeetingsOrganized,
                            AudioDurationSeconds = summary.TeamsAudioDurationSeconds,
                            VideoDurationSeconds = summary.TeamsVideoDurationSeconds,
                            ScreenShareDurationSeconds = summary.TeamsScreenShareDurationSeconds,
                            PostMessages = summary.TeamsPostMessages,
                            ReplyMessages = summary.TeamsReplyMessages,
                            UrgentMessages = summary.TeamsUrgentMessages
                        });
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported profiling source workload: {summary.Workload}");
                }
                added++;
            }
            return added;
        }

        private SyntheticWorkload PickWorkload()
        {
            int roll = _random.Next(100);
            int upper = Office365ActivityGeneratorConfig.SharePointWeight;
            if (roll < upper) return SyntheticWorkload.SharePoint;

            upper += Office365ActivityGeneratorConfig.OneDriveWeight;
            if (roll < upper) return SyntheticWorkload.OneDrive;

            upper += Office365ActivityGeneratorConfig.ExchangeWeight;
            if (roll < upper) return SyntheticWorkload.Exchange;

            upper += Office365ActivityGeneratorConfig.AzureAdWeight;
            if (roll < upper) return SyntheticWorkload.AzureActiveDirectory;

            throw new InvalidOperationException($"No workload configured for weight roll {roll}.");
        }

        private string Pick(string[] values)
        {
            return values[_random.Next(values.Length)];
        }

        private static double Percent(int value, int total)
        {
            return total == 0 ? 0 : value * 100.0 / total;
        }

        private static Dictionary<string, TValue> ToDictionaryFirst<TSource, TValue>(
            IEnumerable<TSource> values,
            Func<TSource, string> keySelector,
            Func<TSource, TValue> valueSelector)
        {
            var dictionary = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                string key = keySelector(value);
                if (!string.IsNullOrEmpty(key) && !dictionary.ContainsKey(key))
                {
                    dictionary.Add(key, valueSelector(value));
                }
            }
            return dictionary;
        }

        private static void EnsureAllPresent(
            string lookupType,
            IEnumerable<string> expected,
            IEnumerable<string> actual)
        {
            var actualSet = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
            var missing = expected
                .Where(value => !actualSet.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Missing {lookupType} lookup(s) after seeding: {string.Join(", ", missing)}");
            }
        }

        private enum SyntheticWorkload
        {
            SharePoint,
            OneDrive,
            Exchange,
            AzureActiveDirectory,
            Teams
        }

        private sealed class UserReference
        {
            public int Id { get; }

            public UserReference(int id)
            {
                Id = id;
            }
        }

        private sealed class SyntheticDocument
        {
            public Office365SiteDefinition Site { get; }
            public string FileName { get; }
            public string Extension { get; }
            public string ObjectUrl { get; }

            public SyntheticDocument(
                Office365SiteDefinition site,
                string fileName,
                string extension,
                string objectUrl)
            {
                Site = site;
                FileName = fileName;
                Extension = extension;
                ObjectUrl = objectUrl;
            }
        }

        private sealed class DocumentPool
        {
            public IReadOnlyList<SyntheticDocument> SharePoint { get; }
            public IReadOnlyList<SyntheticDocument> OneDrive { get; }
            public IReadOnlyList<SyntheticDocument> All { get; }

            public DocumentPool(
                IReadOnlyList<SyntheticDocument> sharePoint,
                IReadOnlyList<SyntheticDocument> oneDrive)
            {
                SharePoint = sharePoint;
                OneDrive = oneDrive;
                All = sharePoint.Concat(oneDrive).ToList();
            }
        }

        private sealed class BatchLookups
        {
            public Dictionary<string, int> OperationIds { get; set; }
            public Dictionary<string, SPEventFileExtension> FileExtensions { get; set; }
            public Dictionary<string, SPEventFileName> FileNames { get; set; }
            public Dictionary<string, Url> Urls { get; set; }
            public Dictionary<string, Web> Webs { get; set; }
            public Dictionary<string, SPEventType> EventTypes { get; set; }
            public Dictionary<string, int> PropertyNameIds { get; set; }
            public Dictionary<string, int> PropertyValueIds { get; set; }
        }

        private sealed class GenerationCounters
        {
            public int SharePoint { get; set; }
            public int OneDrive { get; set; }
            public int Exchange { get; set; }
            public int AzureActiveDirectory { get; set; }
        }

        private sealed class DailyUsageAccumulator
        {
            private readonly Dictionary<(int UserId, DateTime Date, SyntheticWorkload Workload), DailyUsageSummary>
                _summaries =
                    new Dictionary<(int UserId, DateTime Date, SyntheticWorkload Workload), DailyUsageSummary>();

            public IEnumerable<DailyUsageSummary> Summaries => _summaries.Values;

            public void RecordSharePoint(
                int userId,
                DateTime date,
                bool isOneDrive,
                string operation,
                Random random)
            {
                var workload = isOneDrive
                    ? SyntheticWorkload.OneDrive
                    : SyntheticWorkload.SharePoint;
                var summary = GetOrCreate(userId, date, workload);

                if (operation.IndexOf("Sync", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    summary.Synced++;
                }
                else if (operation.StartsWith("Sharing", StringComparison.OrdinalIgnoreCase))
                {
                    if (random.Next(100) < 85)
                    {
                        summary.SharedInternally++;
                    }
                    else
                    {
                        summary.SharedExternally++;
                    }
                }
                else
                {
                    summary.ViewedOrEdited++;
                }
            }

            public void RecordExchange(
                int userId,
                DateTime date,
                string operation,
                Random random)
            {
                var summary = GetOrCreate(userId, date, SyntheticWorkload.Exchange);

                if (operation == "Send")
                {
                    summary.EmailSent++;
                }
                else if (operation == "Create")
                {
                    if (random.Next(100) < 25)
                    {
                        summary.MeetingsCreated++;
                    }
                    else
                    {
                        summary.EmailReceived++;
                    }
                }
                else if (operation == "Update")
                {
                    if (random.Next(100) < 25)
                    {
                        summary.MeetingsInteracted++;
                    }
                    else
                    {
                        summary.EmailRead++;
                    }
                }
                else
                {
                    summary.EmailRead++;
                }
            }

            public void RecordTeams(int userId, DateTime date, Random random)
            {
                if (random.Next(100) >= 60)
                {
                    return;
                }

                var summary = GetOrCreate(userId, date, SyntheticWorkload.Teams);

                long adHocAttended = random.Next(2);
                long adHocOrganized = random.Next(2);
                long oneTimeAttended = random.Next(2);
                long oneTimeOrganized = random.Next(2);
                long recurringAttended = random.Next(2);
                long recurringOrganized = random.Next(2);
                long meetingsAttended = adHocAttended + oneTimeAttended + recurringAttended;
                long meetingsOrganized = adHocOrganized + oneTimeOrganized + recurringOrganized;
                long calls = random.Next(3);

                summary.TeamsPrivateChats += random.Next(1, 6);
                summary.TeamsTeamChats += random.Next(7);
                summary.TeamsCalls += calls;
                summary.TeamsMeetings += meetingsAttended;
                summary.TeamsMeetingsAttended += meetingsAttended;
                summary.TeamsMeetingsOrganized += meetingsOrganized;
                summary.TeamsAdHocMeetingsAttended += adHocAttended;
                summary.TeamsAdHocMeetingsOrganized += adHocOrganized;
                summary.TeamsScheduledOneTimeMeetingsAttended += oneTimeAttended;
                summary.TeamsScheduledOneTimeMeetingsOrganized += oneTimeOrganized;
                summary.TeamsScheduledRecurringMeetingsAttended += recurringAttended;
                summary.TeamsScheduledRecurringMeetingsOrganized += recurringOrganized;
                summary.TeamsPostMessages += random.Next(4);
                summary.TeamsReplyMessages += random.Next(7);
                summary.TeamsUrgentMessages += random.Next(100) < 8 ? 1 : 0;

                if (calls + meetingsAttended + meetingsOrganized > 0)
                {
                    summary.TeamsAudioDurationSeconds += random.Next(60, 1801);
                    summary.TeamsVideoDurationSeconds += random.Next(30, 901);
                    summary.TeamsScreenShareDurationSeconds += random.Next(20, 601);
                }
            }

            private DailyUsageSummary GetOrCreate(
                int userId,
                DateTime date,
                SyntheticWorkload workload)
            {
                var key = (userId, date.Date, workload);
                if (!_summaries.TryGetValue(key, out var summary))
                {
                    summary = new DailyUsageSummary(userId, date.Date, workload);
                    _summaries.Add(key, summary);
                }
                return summary;
            }
        }

        private sealed class DailyUsageSummary
        {
            public int UserId { get; }
            public DateTime Date { get; }
            public SyntheticWorkload Workload { get; }

            public long ViewedOrEdited { get; set; }
            public long Synced { get; set; }
            public long SharedInternally { get; set; }
            public long SharedExternally { get; set; }
            public long EmailSent { get; set; }
            public long EmailReceived { get; set; }
            public long EmailRead { get; set; }
            public long MeetingsCreated { get; set; }
            public long MeetingsInteracted { get; set; }
            public long TeamsPrivateChats { get; set; }
            public long TeamsTeamChats { get; set; }
            public long TeamsCalls { get; set; }
            public long TeamsMeetings { get; set; }
            public long TeamsMeetingsAttended { get; set; }
            public long TeamsMeetingsOrganized { get; set; }
            public long TeamsAdHocMeetingsAttended { get; set; }
            public long TeamsAdHocMeetingsOrganized { get; set; }
            public long TeamsScheduledOneTimeMeetingsAttended { get; set; }
            public long TeamsScheduledOneTimeMeetingsOrganized { get; set; }
            public long TeamsScheduledRecurringMeetingsAttended { get; set; }
            public long TeamsScheduledRecurringMeetingsOrganized { get; set; }
            public int TeamsAudioDurationSeconds { get; set; }
            public int TeamsVideoDurationSeconds { get; set; }
            public int TeamsScreenShareDurationSeconds { get; set; }
            public long TeamsPostMessages { get; set; }
            public long TeamsReplyMessages { get; set; }
            public long TeamsUrgentMessages { get; set; }

            public DailyUsageSummary(
                int userId,
                DateTime date,
                SyntheticWorkload workload)
            {
                UserId = userId;
                Date = date;
                Workload = workload;
            }
        }
    }
}
