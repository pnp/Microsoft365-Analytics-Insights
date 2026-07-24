using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// A class for saving a batch of ContentSets. Disables AutoDetectChangesEnabled
    /// </summary>
    public class SaveSession : IDisposable
    {
        private CopilotAuditEventManager _copilotEventResolver = null;
        private PowerPlatformAuditEventManager _powerPlatformEventResolver = null;
        private GraphAppIndentityOAuthContext _authContext;
        private readonly ICopilotMetadataLoader _injectedCopilotLoader;
        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;

        public SaveSession(ILogger logger, AnalyticsEntitiesContext db, AppConfig appConfig)
            : this(logger, db, appConfig, null)
        {
        }

        /// <summary>
        /// <paramref name="copilotLoader"/> (optional) lets the caller inject a run-scoped Copilot metadata
        /// loader so its Graph caches persist across every batch. When null, a per-session loader is created
        /// (original behaviour).
        /// </summary>
        public SaveSession(ILogger logger, AnalyticsEntitiesContext db, AppConfig appConfig, ICopilotMetadataLoader copilotLoader)
        {
            _logger = logger;
            this.Database = db;
            _appConfig = appConfig;
            this.Database.Configuration.AutoDetectChangesEnabled = false;

            this.SharePointLookupManager = new SharePointLookupManager(Database);
            this.StreamLookupManager = new StreamLookupManager(Database);
            _injectedCopilotLoader = copilotLoader;
            if (copilotLoader == null)
            {
                _authContext = new GraphAppIndentityOAuthContext(logger, appConfig.ClientID, appConfig.TenantGUID.ToString(), appConfig.ClientSecret, appConfig.KeyVaultUrl, appConfig.UseClientCertificate);
            }
        }

        internal async Task Init()
        {
            ICopilotMetadataLoader loader = _injectedCopilotLoader;
            if (loader == null)
            {
                await _authContext.InitClientCredential();
                loader = new GraphFileMetadataLoader(new GraphServiceClient(_authContext.Creds), _logger);
            }
            _copilotEventResolver = new CopilotAuditEventManager(_appConfig.ConnectionStrings.DatabaseConnectionString, loader, _logger, _appConfig.ResolveCopilotResourceMetadata);
            _powerPlatformEventResolver = new PowerPlatformAuditEventManager(_appConfig.ConnectionStrings.DatabaseConnectionString, _logger);
        }

        public CopilotAuditEventManager CopilotEventResolver => _copilotEventResolver ?? throw new Exception("Session not initialised");
        public PowerPlatformAuditEventManager PowerPlatformEventResolver => _powerPlatformEventResolver ?? throw new Exception("Session not initialised");
        public SharePointLookupManager SharePointLookupManager { get; set; }
        public StreamLookupManager StreamLookupManager { get; set; }

        public AnalyticsEntitiesContext Database { get; set; }

        public Dictionary<Guid, SharePointEventMetadata> CachedSpEvents { get; set; } = new Dictionary<Guid, SharePointEventMetadata>();

        public void Dispose()
        {
            this.Database.Dispose();
        }

        /// <summary>Time (ms) spent in the Power Platform commit (its staging-table merges) during the last
        /// <see cref="CommitAllChanges"/> call, so the save path can report the Power Platform workload's cost.</summary>
        public double LastPowerPlatformCommitMs { get; private set; }

        /// <summary>Time (ms) spent in the Copilot commit (its staging-table load + the shared
        /// accessed-resource / agents merge SQL) during the last <see cref="CommitAllChanges"/> call. This
        /// merge runs for every batch that carries Copilot events - including chat-only events, and regardless
        /// of <c>ResolveCopilotResourceMetadata</c> - so on Copilot-heavy tenants it is often the dominant save
        /// cost. Surfaced separately so the per-cycle summary can attribute it instead of hiding it in metadata.</summary>
        public double LastCopilotCommitMs { get; private set; }

        /// <summary>Time (ms) spent in the final EF <c>SaveChangesAsync</c> (the metadata write) during the
        /// last <see cref="CommitAllChanges"/> call.</summary>
        public double LastEfSaveChangesMs { get; private set; }

        public async Task CommitAllChanges()
        {
            // Copilot commit = staging-table load + the shared accessed-resource/agents merge SQL. Timed on its
            // own because it is the usual save bottleneck on Copilot-heavy tenants.
            var swCopilot = System.Diagnostics.Stopwatch.StartNew();
            await _copilotEventResolver.CommitAllChanges();
            swCopilot.Stop();
            LastCopilotCommitMs = swCopilot.Elapsed.TotalMilliseconds;

            // Power Platform commit (6 staging-table merges). Skipped when the workload is disabled - no PP
            // events are staged in that case, so this just avoids running the merges against empty tables.
            LastPowerPlatformCommitMs = 0;
            if (_appConfig.ImportJobSettings?.ImportPowerPlatform ?? false)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _powerPlatformEventResolver.CommitAllChanges();
                sw.Stop();
                LastPowerPlatformCommitMs = sw.Elapsed.TotalMilliseconds;
            }

            Database.ChangeTracker.DetectChanges();
            var swEf = System.Diagnostics.Stopwatch.StartNew();
            await this.Database.SaveChangesAsync();
            swEf.Stop();
            LastEfSaveChangesMs = swEf.Elapsed.TotalMilliseconds;
        }
    }
}
