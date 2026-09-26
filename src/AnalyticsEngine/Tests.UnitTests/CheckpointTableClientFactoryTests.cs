using Azure;
using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System;
using System.Runtime.Serialization;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers how the audit blob checkpoint decides between shared-key and RBAC/Entra ID authentication for its
    /// Azure Table store. Storage accounts with allowSharedKeyAccess = false return
    /// "403 KeyBasedAuthenticationNotPermitted", which must route the importer to the runtime service principal
    /// rather than degrading to the non-durable in-memory checkpoint.
    /// </summary>
    [TestClass]
    public class CheckpointTableClientFactoryTests
    {
        // Obviously-fake, low-entropy account/key; never a real connection string.
        private const string SharedKeyConnStr =
            "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=FAKEFAKEFAKEFAKEFAKEFAKE==;EndpointSuffix=core.windows.net";

        [TestMethod]
        public void GetTableEndpoint_ComposesFromAccountNameAndSuffix()
        {
            Assert.AreEqual(new Uri("https://contosoanalytics.table.core.windows.net"),
                CheckpointTableClientFactory.GetTableEndpoint(SharedKeyConnStr));
        }

        /// <summary>Sovereign clouds use a different endpoint suffix, so it must never be hard-coded.</summary>
        [TestMethod]
        public void GetTableEndpoint_HonoursSovereignCloudSuffix()
        {
            var connStr = "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=abc==;EndpointSuffix=core.usgovcloudapi.net";

            Assert.AreEqual(new Uri("https://contosoanalytics.table.core.usgovcloudapi.net"),
                CheckpointTableClientFactory.GetTableEndpoint(connStr));
        }

        /// <summary>An explicit TableEndpoint wins over the composed one (custom/private DNS).</summary>
        [TestMethod]
        public void GetTableEndpoint_PrefersExplicitTableEndpoint()
        {
            var connStr = "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=abc==;" +
                          "TableEndpoint=https://contosoanalytics.privatelink.table.core.windows.net/;EndpointSuffix=core.windows.net";

            Assert.AreEqual(new Uri("https://contosoanalytics.privatelink.table.core.windows.net/"),
                CheckpointTableClientFactory.GetTableEndpoint(connStr));
        }

        [TestMethod]
        public void GetTableEndpoint_NoAccountName_ReturnsNull()
        {
            Assert.IsNull(CheckpointTableClientFactory.GetTableEndpoint("DefaultEndpointsProtocol=https;EndpointSuffix=core.windows.net"));
            Assert.IsNull(CheckpointTableClientFactory.GetTableEndpoint(null));
            Assert.IsNull(CheckpointTableClientFactory.GetTableEndpoint(string.Empty));
        }

        /// <summary>
        /// An account key is base64 and routinely ends in '=' padding, so parsing must split on the FIRST '='
        /// only - otherwise the key is silently truncated and shared-key auth breaks.
        /// </summary>
        [TestMethod]
        public void HasAccountKey_DetectsBase64PaddedKey()
        {
            Assert.IsTrue(CheckpointTableClientFactory.HasAccountKey(SharedKeyConnStr));
            Assert.IsTrue(CheckpointTableClientFactory.HasAccountKey("AccountName=contosoanalytics;AccountKey=YWJj=="));
        }

        /// <summary>An RBAC-only connection string names the account but carries no key.</summary>
        [TestMethod]
        public void HasAccountKey_FalseWhenAbsentOrBlank()
        {
            Assert.IsFalse(CheckpointTableClientFactory.HasAccountKey("DefaultEndpointsProtocol=https;AccountName=contosoanalytics;EndpointSuffix=core.windows.net"));
            Assert.IsFalse(CheckpointTableClientFactory.HasAccountKey("AccountName=contosoanalytics;AccountKey="));
            Assert.IsFalse(CheckpointTableClientFactory.HasAccountKey(null));
        }

        [TestMethod]
        public void IsDevelopmentStorage_DetectsEmulator()
        {
            Assert.IsTrue(CheckpointTableClientFactory.IsDevelopmentStorage("UseDevelopmentStorage=true"));
            Assert.IsFalse(CheckpointTableClientFactory.IsDevelopmentStorage(SharedKeyConnStr));
        }

        /// <summary>This is the exact error a storage account with allowSharedKeyAccess = false returns.</summary>
        [TestMethod]
        public void IsKeyAuthDisabled_TrueForKeyBasedAuthenticationNotPermitted()
        {
            Assert.IsTrue(CheckpointTableClientFactory.IsKeyAuthDisabled(
                new RequestFailedException(403, "Key based authentication is not permitted on this storage account.", "KeyBasedAuthenticationNotPermitted", null)));
            Assert.IsTrue(CheckpointTableClientFactory.IsKeyAuthDisabled(
                new RequestFailedException(403, "Auth type disabled", "AuthenticationTypeDisabled", null)));
        }

        /// <summary>
        /// A firewall / private-endpoint block also surfaces as 403, but retrying with a token would not help,
        /// so it must NOT be treated as "key auth disabled".
        /// </summary>
        [TestMethod]
        public void IsKeyAuthDisabled_FalseForOtherFailures()
        {
            Assert.IsFalse(CheckpointTableClientFactory.IsKeyAuthDisabled(
                new RequestFailedException(403, "This request is not authorized to perform this operation.", "AuthorizationFailure", null)));
            Assert.IsFalse(CheckpointTableClientFactory.IsKeyAuthDisabled(
                new RequestFailedException(403, "Permission mismatch", "AuthorizationPermissionMismatch", null)));
            Assert.IsFalse(CheckpointTableClientFactory.IsKeyAuthDisabled(
                new RequestFailedException(404, "Not found", "ResourceNotFound", null)));
            Assert.IsFalse(CheckpointTableClientFactory.IsKeyAuthDisabled(null));
        }

        /// <summary>
        /// With no key AND no service principal there is nothing to authenticate with, so the store must throw
        /// (letting ProcessedBlobStoreFactory fall back to in-memory) rather than hang or return a dead client.
        /// </summary>
        [TestMethod]
        public void CreateAndEnsureTable_NoKeyAndNoServicePrincipal_Throws()
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                CheckpointTableClientFactory.CreateAndEnsureTable(
                    "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;EndpointSuffix=core.windows.net",
                    "AuditImporterProcessedBlobs", null, null, null, null));
        }

        [TestMethod]
        public void CreateAndEnsureTable_EmptyConnectionString_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                CheckpointTableClientFactory.CreateAndEnsureTable(string.Empty, "AuditImporterProcessedBlobs",
                    "00000000-0000-0000-0000-000000000000", "client", "secret", null));
        }

        [TestMethod]
        public void BlobCheckpointFailureClassifier_ExplainsStorageFirewallAuthorizationFailure()
        {
            var result = BlobCheckpointFailureClassifier.Classify(
                new RequestFailedException(403, "This request is not authorized to perform this operation.", "AuthorizationFailure", null));

            Assert.AreEqual("AuthorizationFailure", result.ErrorCode);
            Assert.AreEqual(403, result.Status);
            StringAssert.Contains(result.OperatorMessage, "Storage firewall/network rules");
            StringAssert.Contains(result.OperatorMessage, "HTTP 403 AuthorizationFailure");
            StringAssert.Contains(result.OperatorMessage, "IP allow-list rules do not apply");
            StringAssert.Contains(result.OperatorMessage, "same Azure region");
            StringAssert.Contains(result.OperatorMessage, "Microsoft.Storage service endpoint");
        }

        [TestMethod]
        public void BlobCheckpointFailureClassifier_ExplainsPermissionMismatch()
        {
            var result = BlobCheckpointFailureClassifier.Classify(
                new RequestFailedException(403, "Permission mismatch", "AuthorizationPermissionMismatch", null));

            StringAssert.Contains(result.OperatorMessage, "Storage Table Data Contributor");
            StringAssert.Contains(result.OperatorMessage, "HTTP 403 AuthorizationPermissionMismatch");
        }

        [TestMethod]
        public void BlobCheckpointFailureClassifier_ExplainsAuthenticationFailure()
        {
            var result = BlobCheckpointFailureClassifier.Classify(
                new RequestFailedException(403, "Authentication failed.", "AuthenticationFailed", null));

            StringAssert.Contains(result.OperatorMessage, "checkpoint credential");
            StringAssert.Contains(result.OperatorMessage, "HTTP 403 AuthenticationFailed");
        }

        [TestMethod]
        public void BlobCheckpointFailureClassifier_ExplainsKeyAuthDisabled()
        {
            var result = BlobCheckpointFailureClassifier.Classify(
                new RequestFailedException(403, "Key based authentication is not permitted.", "KeyBasedAuthenticationNotPermitted", null));

            StringAssert.Contains(result.OperatorMessage, "shared-key authentication disabled");
            StringAssert.Contains(result.OperatorMessage, "Storage Table Data Contributor");
            StringAssert.Contains(result.OperatorMessage, "HTTP 403 KeyBasedAuthenticationNotPermitted");
        }

        [TestMethod]
        public void BlobCheckpointFailureClassifier_UnknownCodesStillCarryStatusAndCode()
        {
            var result = BlobCheckpointFailureClassifier.Classify(
                new RequestFailedException(409, "Conflict.", "TableBeingDeleted", null));

            StringAssert.Contains(result.OperatorMessage, "HTTP 409 TableBeingDeleted");
            StringAssert.Contains(result.OperatorMessage, "Table checkpoint request");
        }

        [TestMethod]
        public void ProcessedBlobStoreFactory_AuthorizationFailureFallsBackAndLogsClassifiedCause()
        {
            var logger = new RecordingLogger();
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.DaysBeforeNowToDownload = 7;
            config.ConnectionStrings = (AppConnectionStrings)FormatterServices.GetUninitializedObject(typeof(AppConnectionStrings));
            config.ConnectionStrings.StorageConnectionString = SharedKeyConnStr;

            var store = ProcessedBlobStoreFactory.Create(
                config,
                logger,
                (conn, retention, log, tenantId, clientId, clientSecret) =>
                    throw new RequestFailedException(403, "This request is not authorized to perform this operation.", "AuthorizationFailure", null));

            Assert.IsInstanceOfType(store, typeof(InMemoryProcessedBlobStore));
            var error = logger.Entries.Single(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
            StringAssert.Contains(error.Message, "Storage firewall/network rules");
            StringAssert.Contains(error.Message, "HTTP 403 AuthorizationFailure");
            StringAssert.Contains(error.Message, "same Azure region");
        }
    }
}
