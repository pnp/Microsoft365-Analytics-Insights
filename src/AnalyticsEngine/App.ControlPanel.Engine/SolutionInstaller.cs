using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.InstallerTasks.Adoptify;
using App.ControlPanel.Engine.Models;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Top-level installer class. Executes a full solution install based on a SolutionInstallConfig values
    /// </summary>
    public class SolutionInstaller : BaseInstallProcessWithFtp
    {
        public SolutionInstaller(SolutionInstallConfig config, ILogger logger, SoftwareReleaseConfig softwareConfig, InstallerFtpConfig ftpConfig,
            string installingUsername, string configPassword) : base(config, logger, ftpConfig)
        {
            _softwareConfig = softwareConfig;
            this.InstalledByUsername = installingUsername;
            _configPassword = configPassword;
        }

        /// <summary>
        /// Installed by who?
        /// </summary>
        public string InstalledByUsername { get; set; }

        private readonly SoftwareReleaseConfig _softwareConfig;
        private readonly string _configPassword;

        /// <summary>
        /// Main execution entrypoint. Accepts an optional <see cref="CancellationToken"/> so the UI
        /// can request cancellation via a Cancel button. Cancellation is co-operative and takes
        /// effect at phase / task boundaries — in-flight Azure SDK calls complete first.
        /// </summary>
        public async Task InstallOrUpdate(CancellationToken ct = default(CancellationToken))
        {
            // Wrap the inbound logger so every WARN/ERROR raised during the run is captured
            // into an end-of-run summary block. Use the underlying _logger when emitting the
            // summary itself so its lines aren't re-captured recursively.
            var summary = new InstallSummary();
            var log = (ILogger)new SummaryCapturingLogger(_logger, summary);

            log.LogInformation($"Starting install. Authenticating & selecting subscription '{this.Config.Subscription.DisplayName}'...");

            // Setup the things. Catch as specific exceptions as possible; Azure & our own exceptions
            var azureSub = BaseAnalyticsSolutionInstallJob.FromConfig(this.Config);
            try
            {
                ct.ThrowIfCancellationRequested();
                log.LogInformation("=== Phase: Azure backend resources ===");
                // Get/create AppService + SQL + Redis. Binaries installed post-create.
                var azureBackeEndCreationJob = new AzurePaaSInstallJob(log, Config, azureSub);
                azureBackeEndCreationJob.CancellationToken = ct;
                await azureBackeEndCreationJob.Install();

                ct.ThrowIfCancellationRequested();
                // Secure resources with RBAC roles
                log.LogInformation("=== Phase: RBAC role assignments ===");
                try
                {
                    var resourceSecurityJob = new ResourceSecurityInstallJob(log, Config, azureSub);
                    resourceSecurityJob.CancellationToken = ct;
                    await resourceSecurityJob.Install();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogError($"Failed to assign RBAC roles: {ex.Message}. Continuing installation...");
                }

                ct.ThrowIfCancellationRequested();
                // Run stuff now everything in Azure is created
                log.LogInformation("=== Phase: App Service configuration & content deploy ===");
                var tasks = new ConfigureAzureComponentsTasks(Config, log, _ftpConfig, InstalledByUsername, _softwareConfig, _configPassword);
                await tasks.RunPostCreatePaaSTasks(
                    azureBackeEndCreationJob.CreatedWebSiteResource,
                    azureBackeEndCreationJob.DatabasePaaSInfo,
                    azureBackeEndCreationJob.Storage,
                    azureBackeEndCreationJob.CreatedAutomationAccount,
                    azureBackeEndCreationJob.AppInsights,
                    azureBackeEndCreationJob.Redis,
                    azureBackeEndCreationJob.CognitiveServicesInfo,
                    azureBackeEndCreationJob.KeyVault,
                    azureBackeEndCreationJob.SBQueueWithConnectionString?.ConnectionString, azureBackeEndCreationJob.Subscription
                );

                ct.ThrowIfCancellationRequested();
                // Warm-up app-service
                log.LogInformation("=== Phase: App Service warm-up ===");
                var adminSiteUrl = $"https://{azureBackeEndCreationJob.CreatedWebSiteResource.Data.HostNames.First()}/";
                await WarmupAppServiceSite(log, adminSiteUrl, ct);

                log.LogInformation($"Reminder: Ensure Azure AD app registration for the runtime account has correct authentication configuration (see 'Configure Reply URLs' of deployment guide).");

                // Install Adoptify components
                if (Config.SolutionConfig.SolutionTargeted == SolutionImportType.Adoptify)
                {
                    ct.ThrowIfCancellationRequested();
                    log.LogInformation("=== Phase: Adoptify components ===");
                    await InstallAdoptifyComponents(azureSub, log);
                }

                // Open admin site?
                if (Config.TasksConfig.OpenAdminSitePostInstall)
                {
                    System.Diagnostics.Process.Start(adminSiteUrl);
                }

                // Warn if no sites configured for import (and needed)
                var needSiteFilter = Config.SolutionConfig.ImportTaskSettings.WebTraffic || Config.SolutionConfig.ImportTaskSettings.ActivityLog;
                if (needSiteFilter && Config.SharePointConfig.TargetSites.Count == 0)
                {
                    log.LogInformation($"IMPORTANT! There are no configured SharePoint urls specified. Please add manually at least one URL to allow site data import. " +
                        $"See 'Configure Filtered URLs' in deployment guide for more info.");
                }
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("Install cancelled by user — stopped at the next safe checkpoint.");
                PrintFinalStatus(summary, cancelled: true);
                return;
            }
            catch (InstallException ex)       // General API error
            {
                log.LogError(ex.Message);
                PrintFinalStatus(summary);
                return;
            }
            catch (Exception ex)            // Anything else
            {
                // Anything else. Log error as fatal
                log.LogError($"FATAL: Unexpected error of type '{ex.GetType().Name}': " + ex.Message);
                Console.WriteLine(ex);
                InstallerLogs.AddToWindowsEventLog($"FATAL: Unexpected error of type '{ex.GetType().Name}': " + ex.Message, true);
                InstallerLogs.AddToWindowsEventLog(ex.ToString(), true);
                PrintFinalStatus(summary);
                return;
            }

            PrintFinalStatus(summary);
        }

        /// <summary>
        /// Emit final completion line + structured summary block. Uses the underlying logger
        /// (not the summary-capturing wrapper) so summary lines aren't recursively captured.
        /// </summary>
        private void PrintFinalStatus(InstallSummary summary, bool cancelled = false)
        {
            if (cancelled)
            {
                _logger.LogInformation($"Install cancelled — {summary.ErrorCount} error(s), {summary.WarningCount} warning(s) before cancellation.");
            }
            else if (summary.ErrorCount == 0 && summary.WarningCount == 0)
            {
                _logger.LogInformation("All tasks completed.");
            }
            else
            {
                _logger.LogInformation($"Completed with {summary.ErrorCount} error(s), {summary.WarningCount} warning(s). See summary below.");
            }
            summary.Print(_logger);
        }

        private async Task InstallAdoptifyComponents(Azure.ResourceManager.Resources.SubscriptionResource azureSub, ILogger log)
        {
            log.LogInformation($"Launching web login pop-up for existing Adoptify site '{Config.SolutionConfig.Adoptify.ExistingSiteUrl}'...");
            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(Config.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var adoptifyInstallJob = new AdoptifyInstallJob(log, Config, azureSub, ctx);

                // Install SPSite content and Azure components
                await adoptifyInstallJob.Install();

                log.LogInformation("Adoptify back-end setup complete. Remember to authorize the API connections in the portal.");
            }
        }

        /// <summary>
        /// Hit the admin site and retry on 5xx for ~2 minutes — the App Service has just been
        /// (re)started and cold-start 503s for 30-90s are normal. Only declare failure once the
        /// retry window is exhausted, or on a non-5xx unexpected response. Honors the
        /// <paramref name="ct"/> so the user's Cancel button short-circuits the wait.
        /// </summary>
        private async Task WarmupAppServiceSite(ILogger log, string adminSiteUrl, CancellationToken ct = default(CancellationToken))
        {
            const int totalWarmupSeconds = 120;
            log.LogInformation($"Warming up web-application '{adminSiteUrl}' (retrying on 5xx for up to {totalWarmupSeconds}s)...");
            await Task.Delay(5000, ct);     // initial 5s grace

            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                // Per-request timeout deliberately shorter than the overall budget so a single hung
                // request can't consume the whole 2-minute warmup window (cold-start App Service
                // sometimes hangs the first request rather than 503-ing).
                var deadline = DateTime.UtcNow.AddSeconds(totalWarmupSeconds);
                int attempt = 0;
                HttpStatusCode? lastStatus = null;
                string lastError = null;

                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    attempt++;
                    try
                    {
                        var response = await httpClient.GetAsync(adminSiteUrl, ct);
                        lastStatus = response.StatusCode;
                        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Moved || response.StatusCode == HttpStatusCode.MovedPermanently)
                        {
                            log.LogInformation($"Web-app warmup OK ({(int)response.StatusCode} {response.StatusCode}) on attempt {attempt}.");
                            return;
                        }
                        if ((int)response.StatusCode < 500)
                        {
                            log.LogError($"Web-app warmup got unexpected non-success response {(int)response.StatusCode} {response.StatusCode} — check manually that the App Service is started.");
                            return;
                        }
                        // 5xx — keep retrying
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (HttpRequestException ex)
                    {
                        lastError = ex.Message;
                    }
                    catch (TaskCanceledException)
                    {
                        lastError = "request timed out";
                    }

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;

                    var delaySeconds = Math.Min(30, 5 * attempt);
                    var actualDelay = TimeSpan.FromSeconds(Math.Min(delaySeconds, Math.Max(1, (int)remaining.TotalSeconds)));
                    log.LogInformation($"Web-app warmup attempt {attempt}: {(lastStatus.HasValue ? $"{(int)lastStatus.Value} {lastStatus.Value}" : lastError ?? "no response")}. Retrying in {(int)actualDelay.TotalSeconds}s...");
                    await Task.Delay(actualDelay, ct);
                }

                var lastDetail = lastStatus.HasValue ? $"{(int)lastStatus.Value} {lastStatus.Value}" : lastError ?? "no response";
                log.LogError($"Web-app warmup did not succeed within {totalWarmupSeconds}s — last response was {lastDetail}. Check manually that the App Service is started.");
            }
        }
    }
}
