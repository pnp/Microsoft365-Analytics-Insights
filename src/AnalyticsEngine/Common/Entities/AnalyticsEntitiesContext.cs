using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Entities.Teams;
using Common.Entities.Entities.UsageReports;
using Common.Entities.Entities.WebTraffic;
using Common.Entities.Teams;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Data.Entity;
using System.Linq;

namespace Common.Entities
{
    /// <summary>
    /// The database model
    /// </summary>
    /// <remarks>
    /// Add-Migration -Name "ExtendedUsageReports" -ProjectName "Common.Entities" -StartUpProjectName "WebJob.Office365ActivityImporter"
    /// Update-Database -TargetMigration "PageCommentsAndLikes" -ProjectName "Common.Entities" -StartUpProjectName "WebJob.Office365ActivityImporter"
    /// </remarks>
    public class AnalyticsEntitiesContext : DbContext
    {
        #region Constructors
#if DEBUG
        public AnalyticsEntitiesContext() : this("name=SPOInsightsEntities", false, true) { }     // Dev build auto-updates schema. THIS IS BAD
#else

        public AnalyticsEntitiesContext() : this("name=SPOInsightsEntities", false, false) { }
#endif


        /// <summary>
        /// Init context for DB
        /// </summary>
        /// <param name="dbName">Name or full connection-string</param>
        /// <param name="isConnectionString">Is connection-string instead of connection-string name</param>
        public AnalyticsEntitiesContext(string dbName, bool isConnectionString, bool autoUpdate) : base(dbName)
        {

            this.Database.Log = (a) => System.Diagnostics.Debug.WriteLine(a);
            if (autoUpdate)
            {
                // Set DB to automatically upgrade
                Database.SetInitializer(new MigrateDatabaseToLatestVersion<AnalyticsEntitiesContext, Migrations.Configuration>(true));
            }
            else
            {
                Database.SetInitializer(new CreateDatabaseIfNotExists<AnalyticsEntitiesContext>());
            }


            // 10 min command timeout. Default is whatever provider is, but we're doing long operations so need more
            ((System.Data.Entity.Infrastructure.IObjectContextAdapter)this).ObjectContext.CommandTimeout = 0;

            if (isConnectionString)
            {
                this.Database.Connection.ConnectionString = dbName;
            }

            this.Configuration.ProxyCreationEnabled = false;

        }

        public AnalyticsEntitiesContext(DbConnection sqlConn) : base(sqlConn, true)
        {
            ((System.Data.Entity.Infrastructure.IObjectContextAdapter)this).ObjectContext.CommandTimeout = 0;
        }

        #endregion

        /// <summary>
        /// Outputs the changes to be made & then calls SaveChangesAsync
        /// </summary>
        public void PrintChangesAndSaveToSQL(ILogger telemetry)
        {
            var adds = this.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList();
            var mods = this.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList();
            if (adds.Count > 0 || mods.Count > 0)
            {
                telemetry.LogInformation($"Saving changes to SQL. Inserts: {adds.Count}, updates: {mods.Count}...");
            }
            else
            {
                telemetry.LogInformation("No changes to commit to SQL.");
            }

            this.SaveChanges();
        }

        /// <summary>
        /// Define model schema
        /// </summary>
        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            // Add unique indexes
            modelBuilder.Entity<Site>().HasIndex(b => b.UrlBase).IsUnique();
            modelBuilder.Entity<Web>().HasIndex(b => b.url_base).IsUnique();


            modelBuilder.Entity<TeamDefinition>().HasIndex(b => b.GraphID).IsUnique();
            modelBuilder.Entity<TeamAddOnDefinition>().HasIndex(b => b.GraphID).IsUnique();
            modelBuilder.Entity<TeamChannel>().HasIndex(b => b.GraphID).IsUnique();

            modelBuilder.Entity<CallModality>().HasIndex(b => b.Name).IsUnique();


            modelBuilder.Entity<CallSessionModalityLookup>()
             .HasIndex(c => new { c.CallModalityID, c.CallSessionID })
             .IsUnique();

            modelBuilder.Entity<TeamTabDefinition>()
             .HasIndex(t => new { t.GraphID })
             .IsUnique();

            modelBuilder.Entity<CallRecord>()
             .HasIndex(t => new { t.GraphID })
             .IsUnique();

            modelBuilder.Entity<YammerMessage>().HasIndex(b => b.YammerID).IsUnique();
            modelBuilder.Entity<StreamVideo>().HasIndex(v => v.StreamID).IsUnique();

            modelBuilder.Entity<O365ClientApplication>().HasIndex(v => v.ClientApplicationId).IsUnique();


            modelBuilder.Entity<UserDepartment>()
             .HasIndex(t => new { t.Name })
             .IsUnique();

            modelBuilder.Entity<UserJobTitle>()
             .HasIndex(t => new { t.Name })
             .IsUnique();
            modelBuilder.Entity<UserJobTitle>()
             .HasIndex(t => new { t.Name })
             .IsUnique();
            modelBuilder.Entity<UserOfficeLocation>()
             .HasIndex(t => new { t.Name })
             .IsUnique();

            modelBuilder.Entity<LicenseType>()
             .HasIndex(t => new { t.Name })
             .IsUnique();

            modelBuilder.Entity<UserLicenseTypeLookup>()
             .HasIndex(t => new { t.LicenseTypeId, t.UserId })
             .IsUnique();

            modelBuilder.Entity<StateOrProvince>()
             .HasIndex(t => new { t.Name })
             .IsUnique();

            modelBuilder.Entity<CountryOrRegion>()
             .HasIndex(t => new { t.Name })
             .IsUnique();

            modelBuilder.Entity<CompanyName>()
             .HasIndex(t => new { t.Name })
             .IsUnique();


            modelBuilder.Entity<Clicks>()
             .HasIndex(t => new { t.HitID, t.UrlId, t.TimeStamp })
             .IsUnique();
            modelBuilder.Entity<ClickedElementTitle>()
             .HasIndex(t => new { t.Name })
             .IsUnique();

            modelBuilder.Entity<ClickedElementsClassNames>()
             .HasIndex(t => new { t.AllClassNames })
             .IsUnique();

            modelBuilder.Entity<FileMetadataFieldName>()
             .HasIndex(t => new { t.Name })
             .IsUnique();
            modelBuilder.Entity<FileMetadataPropertyValue>()
             .HasIndex(t => new { t.UrlId, t.FieldId })
             .IsUnique();

            base.OnModelCreating(modelBuilder);
        }

        #region Properties/Tables

        public virtual DbSet<SharePointEventMetadata> sharepoint_events { get; set; }
        public virtual DbSet<ExchangeEventMetadata> exchange_events { get; set; }
        public virtual DbSet<AzureADEventMetadata> azure_ad_events { get; set; }
        public virtual DbSet<GeneralEventMetada> general_audit_events { get; set; }

        public virtual DbSet<ExchangeExtendedProperties> audit_event_props { get; set; }
        public virtual DbSet<AuditPropertyName> audit_event_prop_names { get; set; }
        public virtual DbSet<AuditPropertyValue> audit_event_prop_vals { get; set; }

        public virtual DbSet<Site> sites { get; set; }
        public virtual DbSet<Web> webs { get; set; }
        public virtual DbSet<Office365Event> AuditEventsCommon { get; set; }
        public virtual DbSet<Browser> browsers { get; set; }
        public virtual DbSet<City> cities { get; set; }
        public virtual DbSet<Country> countries { get; set; }
        public virtual DbSet<Device> devices { get; set; }
        public virtual DbSet<SPEventFileExtension> event_file_ext { get; set; }
        public virtual DbSet<SPEventFileName> event_file_names { get; set; }
        public virtual DbSet<EventOperation> event_operations { get; set; }
        public virtual DbSet<SPEventType> event_types { get; set; }

        public virtual DbSet<Hit> hits { get; set; }
        public virtual DbSet<ImportLog> import_log { get; set; }
        public virtual DbSet<OperatingSystem> operating_systems { get; set; }
        public virtual DbSet<OrgUrl> org_urls { get; set; }
        public virtual DbSet<Org> orgs { get; set; }
        public virtual DbSet<PageTitle> page_titles { get; set; }
        public virtual DbSet<IgnoredEvent> ignored_audit_events { get; set; }
        public virtual DbSet<Province> provinces { get; set; }
        public virtual DbSet<SearchTerm> search_terms { get; set; }
        public virtual DbSet<Search> searches { get; set; }
        public virtual DbSet<UserSession> sessions { get; set; }


        public virtual DbSet<Url> urls { get; set; }
        public virtual DbSet<User> users { get; set; }


        public virtual DbSet<Language> Languages { get; set; }
        public virtual DbSet<KeyWord> KeyWords { get; set; }

        public virtual DbSet<TeamDefinition> Teams { get; set; }
        public virtual DbSet<TeamOwners> TeamOwners { get; set; }
        public virtual DbSet<TeamAddOnDefinition> TeamAddOns { get; set; }
        public virtual DbSet<TeamChannel> TeamChannels { get; set; }
        public virtual DbSet<TeamMembershipLog> TeamMembershipLogs { get; set; }
        public virtual DbSet<TeamAddOnLog> TeamAddOnLogs { get; set; }
        public virtual DbSet<UserAppsLog> UserAppsLog { get; set; }
        public virtual DbSet<GlobalTeamsUserUsageLog> TeamUserActivityLogs { get; set; }
        public virtual DbSet<GlobalTeamsUserDeviceUsageLog> TeamsUserDeviceUsageLog { get; set; }
        public virtual DbSet<AppPlatformUserActivityLog> AppPlatformUserUsageLog { get; set; }
        public virtual DbSet<TeamTabDefinition> TeamTabDefinitions { get; set; }

        public virtual DbSet<TeamsReactionType> TeamsReactionTypes { get; set; }
        public virtual DbSet<TeamsUserReaction> TeamsUserReactions { get; set; }

        public virtual DbSet<OutlookUsageActivityLog> OutlookUsageActivityLogs { get; set; }
        public virtual DbSet<OneDriveUserActivityLog> OneDriveUserActivityLogs { get; set; }
        public virtual DbSet<OneDriveUsageLog> OneDriveUsageLogs { get; set; }
        public virtual DbSet<SharePointUserActivityLog> SharePointUserActivityLogs { get; set; }
        public virtual DbSet<YammerUserActivityLog> YammerUserActivityLogs { get; set; }
        public virtual DbSet<YammerGroupActivityLog> YammerGroupActivityLogs { get; set; }
        public virtual DbSet<YammerDeviceActivityLog> YammerDeviceActivityLogs { get; set; }
        public virtual DbSet<SharePointSitesFileWeeklyStats> SharePointSiteStats { get; set; }

        public virtual DbSet<ChannelStatsLog> TeamChannelStats { get; set; }
        public virtual DbSet<ChannelLogKeyword> TeamChannelStatKeywords { get; set; }
        public virtual DbSet<ChannelLogLanguage> TeamChannelStatLanguages { get; set; }

        public virtual DbSet<ChannelTabLog> ChannelTabLogs { get; set; }

        public virtual DbSet<CallRecord> CallRecords { get; set; }
        public virtual DbSet<CallSession> CallSessions { get; set; }
        public virtual DbSet<CallSessionModalityLookup> CallModalityLookups { get; set; }
        public virtual DbSet<CallModality> CallModalities { get; set; }
        public virtual DbSet<CallFailureReasonLookup> CallFailures { get; set; }
        public virtual DbSet<CallFeedback> CallFeedback { get; set; }
        public virtual DbSet<CallType> CallTypes { get; set; }


        public virtual DbSet<ConfigState> ConfigStates { get; set; }
        public virtual DbSet<TelemetryReport> TelemetryReports { get; set; }

        public virtual DbSet<StreamVideo> Streams { get; set; }
        public virtual DbSet<YammerMessage> YammerMessages { get; set; }
        public virtual DbSet<YammerStreamLink> YammerStreamLinks { get; set; }
        public virtual DbSet<YammerGroup> YammerGroups { get; set; }

        public virtual DbSet<StreamEventMetada> StreamEvents { get; set; }

        public virtual DbSet<O365ClientApplication> O365ClientApplications { get; set; }
        public virtual DbSet<UserDepartment> UserDepartments { get; set; }
        public virtual DbSet<UserJobTitle> UserJobTitles { get; set; }
        public virtual DbSet<UserOfficeLocation> UserOfficeLocations { get; set; }
        public virtual DbSet<UserUsageLocation> UserUsageLocations { get; set; }
        public virtual DbSet<LicenseType> LicenseTypes { get; set; }
        public virtual DbSet<UserLicenseTypeLookup> UserLicenseTypeLookups { get; set; }

        public virtual DbSet<StateOrProvince> StateOrProvinces { get; set; }
        public virtual DbSet<CountryOrRegion> CountryOrRegions { get; set; }
        public virtual DbSet<CompanyName> CompanyNames { get; set; }

        public virtual DbSet<GroupsCrawlConfig> GroupsCrawlConfig { get; set; }

        public virtual DbSet<Clicks> Clicks { get; set; }
        public virtual DbSet<ClickedElementsClassNames> ClickedElementsClassNames { get; set; }
        public virtual DbSet<ClickedElementTitle> ClickedElementTitles { get; set; }

        public virtual DbSet<FileMetadataFieldName> FileMetadataFields { get; set; }
        public virtual DbSet<FileMetadataPropertyValue> FileMetadataPropertyValues { get; set; }

        public virtual DbSet<PageLike> UrlLikes { get; set; }
        public virtual DbSet<PageComment> UrlComments { get; set; }


        public DbSet<CopilotChat> CopilotChats { get; set; }
        public DbSet<CopilotEventMetadataFile> CopilotEventMetadataFiles { get; set; }
        public DbSet<CopilotEventMetadataMeeting> CopilotEventMetadataMeetings { get; set; }


        #endregion
    }

    /// <summary>
    /// config automatically read by EF
    /// </summary>
    public class SPOInsightsDBConfiguration : DbConfiguration
    {
        // https://docs.microsoft.com/en-us/ef/ef6/fundamentals/connection-resiliency/retry-logic
        public SPOInsightsDBConfiguration()
        {
            SetExecutionStrategy("System.Data.SqlClient", () => new System.Data.Entity.SqlServer.SqlAzureExecutionStrategy());
        }
    }
}
