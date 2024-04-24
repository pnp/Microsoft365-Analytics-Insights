using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// A class for saving a batch of ContentSets. Disables AutoDetectChangesEnabled
    /// </summary>
    public class SaveSession : IDisposable
    {
        private CopilotAuditEventManager _copilotEventResolver = null;
        private readonly GraphAppIndentityOAuthContext _authContext;
        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;

        public SaveSession(ILogger logger, AnalyticsEntitiesContext db, AppConfig appConfig)
        {
            _logger = logger;
            this.Database = db;
            _appConfig = appConfig;
            this.Database.Configuration.AutoDetectChangesEnabled = false;

            this.SharePointLookupManager = new SharePointLookupManager(Database);
            this.StreamLookupManager = new StreamLookupManager(Database);
            _authContext = new GraphAppIndentityOAuthContext(logger, appConfig.ClientID, appConfig.TenantGUID.ToString(), appConfig.ClientSecret, appConfig.KeyVaultUrl, appConfig.UseClientCertificate);
        }

        internal async Task Init()
        {
            await _authContext.InitClientCredential();
            var loader = new GraphFileMetadataLoader(new GraphServiceClient(_authContext.Creds), _logger);
            _copilotEventResolver = new CopilotAuditEventManager(_appConfig.ConnectionStrings.DatabaseConnectionString, loader, _logger);
        }

        public CopilotAuditEventManager CopilotEventResolver => _copilotEventResolver ?? throw new Exception("Session not initialised");
        public SharePointLookupManager SharePointLookupManager { get; set; }
        public StreamLookupManager StreamLookupManager { get; set; }

        public AnalyticsEntitiesContext Database { get; set; }

        public Dictionary<Guid, SharePointEventMetadata> CachedSpEvents { get; set; } = new Dictionary<Guid, SharePointEventMetadata>();

        public void Dispose()
        {
            this.Database.Dispose();
        }

        public async Task CommitAllChanges()
        {
            await _copilotEventResolver.CommitAllChanges();
            Database.ChangeTracker.DetectChanges();
            await this.Database.SaveChangesAsync();
        }
    }
}
