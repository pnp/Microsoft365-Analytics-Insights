using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Copilot;
using Common.Entities.State;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// A class for saving a batch of ContentSets. Disables AutoDetectChangesEnabled
    /// </summary>
    public class SaveSession : IDisposable
    {
        private CopilotAuditEventManager _copilotEventResolver = null;
        private PowerPlatformAuditEventManager _powerPlatformEventResolver = null;
        private DlpAuditEventManager _dlpEventResolver = null;
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
                loader = new GraphFileMetadataLoader(GraphServiceClientFactory.CreateWithTimeout(_authContext.Creds, GraphServiceClientFactory.DefaultGraphSdkTimeout), _logger);
            }
            var enrichmentQueue = StateStore.IsConfigured(_appConfig) ? new CopilotEnrichmentQueue(_appConfig, _logger) : null;
            _copilotEventResolver = new CopilotAuditEventManager(_appConfig.ConnectionStrings.DatabaseConnectionString, loader, _logger,
                _appConfig.ResolveCopilotResourceMetadata, enrichmentQueue == null ? (Func<Guid, Task>)null : enrichmentQueue.EnqueueAsync);
            _powerPlatformEventResolver = new PowerPlatformAuditEventManager(_appConfig.ConnectionStrings.DatabaseConnectionString, _logger);
            _dlpEventResolver = new DlpAuditEventManager(_appConfig.ConnectionStrings.DatabaseConnectionString, _logger);
        }

        public CopilotAuditEventManager CopilotEventResolver => _copilotEventResolver ?? throw new Exception("Session not initialised");
        public PowerPlatformAuditEventManager PowerPlatformEventResolver => _powerPlatformEventResolver ?? throw new Exception("Session not initialised");
        public DlpAuditEventManager DlpEventResolver => _dlpEventResolver ?? throw new Exception("Session not initialised");
        public SharePointLookupManager SharePointLookupManager { get; set; }

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

        /// <summary>Time (ms) spent in the DLP commit (its two staging-table merges) during the last
        /// <see cref="CommitAllChanges"/> call.</summary>
        public double LastDlpCommitMs { get; private set; }

        public async Task CommitAllChanges()
        {
            // Every workload's commit runs even when an earlier one failed, and the failure is rethrown at the end.
            // Rethrowing keeps the batch failed - its content blobs aren't checkpointed and it is retried - but one
            // workload's problem (an enrichment merge, a database missing a newer workload's tables) no longer
            // stops the batch's other workloads from being saved. Every commit here is idempotent, so the retry
            // re-runs the ones that succeeded harmlessly.
            var failures = new List<Exception>();

            // Copilot commit = staging-table load + the shared accessed-resource/agents merge SQL. Timed on its
            // own because it is the usual save bottleneck on Copilot-heavy tenants.
            var swCopilot = System.Diagnostics.Stopwatch.StartNew();
            await RunCommitStep("Copilot", () => _copilotEventResolver.CommitAllChanges(), failures);
            swCopilot.Stop();
            LastCopilotCommitMs = swCopilot.Elapsed.TotalMilliseconds;

            // DLP commit. Deliberately AFTER the Copilot commit: copilot_dlp_events.copilot_chat_id is a
            // foreign key to copilot_chats, and those rows are created by the Copilot merge above. Both
            // DLP merges also INNER JOIN their parent (copilot_chats / audit_events) rather than assuming
            // it exists - which is safe because audit_events is written by the staging merge BEFORE the
            // metadata pass runs: SaveMetadataAsync re-reads the just-saved events out of the database to
            // build eventsJustSavedById, and only events found there reach ProcessExtendedProperties.
            // (If the Copilot commit failed, a match whose interaction wasn't saved is skipped by that join and
            // saved by the batch's retry.)
            // Runs unconditionally because it serves two independently-toggled sources - Copilot-embedded
            // DLP (which needs no DLP permission) and the DLP.All feed - and with nothing staged the
            // merges return before opening a connection.
            var swDlp = System.Diagnostics.Stopwatch.StartNew();
            await RunCommitStep("DLP", () => _dlpEventResolver.CommitAllChanges(), failures);
            swDlp.Stop();
            LastDlpCommitMs = swDlp.Elapsed.TotalMilliseconds;

            // Power Platform commit (6 staging-table merges). Skipped when the workload is disabled - no PP
            // events are staged in that case, so this just avoids running the merges against empty tables.
            LastPowerPlatformCommitMs = 0;
            if (_appConfig.ImportJobSettings?.ImportPowerPlatform ?? false)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await RunCommitStep("Power Platform", () => _powerPlatformEventResolver.CommitAllChanges(), failures);
                sw.Stop();
                LastPowerPlatformCommitMs = sw.Elapsed.TotalMilliseconds;
            }

            System.Diagnostics.Stopwatch swEf = null;
            await RunCommitStep("SharePoint and general", () =>
            {
                Database.ChangeTracker.DetectChanges();
                swEf = System.Diagnostics.Stopwatch.StartNew();
                return this.Database.SaveChangesAsync();
            }, failures);
            swEf?.Stop();
            LastEfSaveChangesMs = swEf?.Elapsed.TotalMilliseconds ?? 0;

            CommitFailures.Rethrow(failures, "metadata commits");
        }

        private async Task RunCommitStep(string workload, Func<Task> commit, List<Exception> failures)
        {
            try
            {
                await commit();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                _logger?.LogError(ex, $"Audit events import: the {workload} metadata commit failed ({ex.Message}). " +
                    "Saving the batch's other workloads anyway; the batch is then reported as failed and retried.");
            }
        }
    }

    /// <summary>
    /// Rethrows what a run of independent commit steps collected, so a caller can let every step run first.
    /// </summary>
    internal static class CommitFailures
    {
        /// <summary>
        /// Nothing for no failures; the failure itself, with its original stack and type, for one (so retry
        /// classification and the operator's log line are as before); an <see cref="AggregateException"/> whose
        /// message lists every failure for more than one.
        /// </summary>
        public static void Rethrow(List<Exception> failures, string what)
        {
            if (failures == null || failures.Count == 0)
            {
                return;
            }
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            var messages = new List<string>();
            foreach (var failure in failures)
            {
                messages.Add(failure.Message);
            }
            throw new AggregateException($"{failures.Count} {what} failed: {string.Join(" | ", messages)}", failures);
        }
    }
}
