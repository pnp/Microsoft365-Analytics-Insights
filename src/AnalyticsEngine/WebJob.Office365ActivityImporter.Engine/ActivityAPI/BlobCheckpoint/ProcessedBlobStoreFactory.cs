using Azure;
using Common.Entities.Config;
using DataUtils;
using DataUtils.Health;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint
{
    /// <summary>
    /// Builds the <see cref="IProcessedBlobStore"/> for a run: an Azure Table-backed store when a storage
    /// connection string is configured (durable across process restarts), otherwise an in-memory store that
    /// persists only for the life of this (long-running) process. The checkpoint is deliberately kept OUT of
    /// the analytics SQL database.
    /// </summary>
    public static class ProcessedBlobStoreFactory
    {
        public static IProcessedBlobStore Create(AppConfig config, ILogger logger,
            Func<string, TimeSpan, ILogger, string, string, string, IProcessedBlobStore> createAzureStore = null)
        {
            // Retain checkpoint entries a little longer than the API lookback window - a blob older than
            // that can never be re-listed, so it is safe to forget (keeps the store bounded).
            int lookbackDays = config != null && config.DaysBeforeNowToDownload > 0 ? config.DaysBeforeNowToDownload : 7;
            var retention = TimeSpan.FromDays(lookbackDays + 1);

            var storageConn = config?.ConnectionStrings?.StorageConnectionString;
            if (!string.IsNullOrWhiteSpace(storageConn))
            {
                try
                {
                    var tenantId = config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString();
                    var storeFactory = createAzureStore ?? ((conn, ret, log, tid, cid, secret) =>
                        new AzureTableProcessedBlobStore(conn, ret, log, tid, cid, secret));
                    var store = storeFactory(storageConn, retention, logger, tenantId, config.ClientID, config.ClientSecret);
                    logger?.LogInformation("Blob checkpoint: durable Azure Table store initialised (processed blobs persist across restarts).");
                    (logger as AnalyticsLogger)?.TrackHealthCheck(HealthComponent.BlobCheckpoint, HealthStatus.Healthy,
                        "Durable Azure Table checkpoint active.");
                    return store;
                }
                catch (Exception ex)
                {
                    // Inline the exception detail (type + flattened inner-exception chain) into the message
                    // itself. The ILogger/App Insights provider routes the exception object to a separate
                    // 'exception' telemetry item, so a trace-only log export shows this line with no reason
                    // otherwise. Matches the rest of the codebase (e.g. ActivityReportLoader). The likely
                    // causes are called out so operators can act without pulling the exceptions table.
                    var detail = ex.Message;
                    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        detail += " -> " + inner.Message;
                    var classification = BlobCheckpointFailureClassifier.Classify(ex);
                    logger?.LogError(ex, $"Blob checkpoint: could not initialise the Azure Table store ({ex.GetType().Name}: {detail}). " +
                        $"{classification.OperatorMessage} " +
                        "falling back to in-memory (dedupes across cycles but only for the life of this process - lost on restart/redeploy; " +
                        "durable cross-cycle metadata recovery is unavailable in this degraded mode).");
                    // Surface the degraded (non-durable) checkpoint on the Health page instead of only in the log.
                    TrackDegradedHealth(logger, classification);
                }
            }
            else
            {
                logger?.LogInformation("Blob checkpoint: no storage connection string configured; using an in-memory store (persists across cycles only while this process runs).");
                (logger as AnalyticsLogger)?.TrackHealthCheck(HealthComponent.BlobCheckpoint, HealthStatus.Degraded,
                    "No Storage connection string configured; using non-durable in-memory checkpoint " +
                    "(lost on restart; durable cross-cycle metadata recovery unavailable).");
            }

            return new InMemoryProcessedBlobStore(retention);
        }

        private static void TrackDegradedHealth(ILogger logger, BlobCheckpointFailureClassification classification)
        {
            var healthDetail = $"Azure Table checkpoint unavailable: {classification.OperatorMessage} Using non-durable in-memory checkpoint " +
                "(lost on restart; durable cross-cycle metadata recovery unavailable). See importer error log.";

            var analyticsLogger = logger as AnalyticsLogger;
            if (analyticsLogger == null) return;

            var context = new Dictionary<string, string>
            {
                { "Component", HealthComponent.BlobCheckpoint.ToString() },
                { "Status", HealthStatus.Degraded.ToString() },
                { "Detail", healthDetail },
            };
            if (!string.IsNullOrEmpty(classification.ReasonKey)) context.Add("ReasonKey", classification.ReasonKey);
            if (!string.IsNullOrEmpty(classification.ErrorCode)) context.Add("ErrorCode", classification.ErrorCode);
            if (classification.Status.HasValue) context.Add("HttpStatus", classification.Status.Value.ToString());

            analyticsLogger.TrackEvent(AnalyticsLogger.AnalyticsEvent.HealthCheck, context);
        }
    }

    public class BlobCheckpointFailureClassification
    {
        public BlobCheckpointFailureClassification(string reasonKey, string errorCode, int? status, string operatorMessage)
        {
            ReasonKey = reasonKey;
            ErrorCode = errorCode;
            Status = status;
            OperatorMessage = operatorMessage;
        }

        public string ReasonKey { get; }
        public string ErrorCode { get; }
        public int? Status { get; }
        public string OperatorMessage { get; }
    }

    public static class BlobCheckpointFailureClassifier
    {
        public static BlobCheckpointFailureClassification Classify(Exception exception)
        {
            var requestFailed = FindRequestFailedException(exception);
            if (requestFailed == null)
            {
                return new BlobCheckpointFailureClassification("blobCheckpoint.transport", null, null,
                    "The Table checkpoint request failed before Azure Storage returned a service error. Check the Storage connection string, DNS and network reachability.");
            }

            var statusAndCode = $"HTTP {requestFailed.Status} {requestFailed.ErrorCode ?? "UnknownError"}";
            switch (requestFailed.ErrorCode)
            {
                case "AuthorizationFailure":
                    return new BlobCheckpointFailureClassification("blobCheckpoint.storageFirewall", requestFailed.ErrorCode, requestFailed.Status,
                        $"Storage firewall/network rules rejected the Table checkpoint request ({statusAndCode}). On a public install, set the storage account to 'Enabled from all networks'; IP allow-list rules do not apply to requests from an App Service in the same Azure region as the storage account. Anything stricter needs App Service VNet integration plus a Microsoft.Storage service endpoint, or the private-endpoint deployment.");
                case "AuthorizationPermissionMismatch":
                    return new BlobCheckpointFailureClassification("blobCheckpoint.permissionMismatch", requestFailed.ErrorCode, requestFailed.Status,
                        $"The runtime identity reached Table storage but does not have the required data-plane role ({statusAndCode}). Grant the runtime service principal 'Storage Table Data Contributor' on the storage account; 'Storage Blob Data Contributor' does not cover the Table service.");
                case "AuthenticationFailed":
                    return new BlobCheckpointFailureClassification("blobCheckpoint.authenticationFailed", requestFailed.ErrorCode, requestFailed.Status,
                        $"Azure Storage rejected the checkpoint credential ({statusAndCode}). Check the Storage connection string/account key, or the runtime service-principal credential used for RBAC fallback.");
                case "KeyBasedAuthenticationNotPermitted":
                case "AuthenticationTypeDisabled":
                    return new BlobCheckpointFailureClassification("blobCheckpoint.keyAuthDisabled", requestFailed.ErrorCode, requestFailed.Status,
                        $"The storage account has shared-key authentication disabled ({statusAndCode}). The importer falls back to RBAC/Entra ID when the runtime service principal is configured; that identity needs 'Storage Table Data Contributor' on the storage account.");
                default:
                    return new BlobCheckpointFailureClassification("blobCheckpoint.storageRejected", requestFailed.ErrorCode, requestFailed.Status,
                        $"Azure Storage rejected the Table checkpoint request ({statusAndCode}). Check the Storage connection string, Table service reachability, storage firewall/private endpoint settings and Table data-plane permissions.");
            }
        }

        private static RequestFailedException FindRequestFailedException(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is RequestFailedException requestFailed) return requestFailed;
            }

            return null;
        }
    }
}
