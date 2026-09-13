using App.ControlPanel.Engine;
using App.ControlPanel.Engine.InstallerTasks;
using CloudInstallEngine.Azure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Covers the two diagnosis helpers added after an install where both a Key Vault 403 and a database
    /// failure were reported with messages that pointed away from their real causes.
    /// </summary>
    [TestClass]
    public class InstallFailureDiagnosisTests
    {
        #region Key Vault 403 classification

        // Azure returns ErrorCode "Forbidden" for every one of these, so the message is the only signal.
        // Wording per https://learn.microsoft.com/en-us/azure/key-vault/general/common-error-codes

        [TestMethod]
        public void Classify_ClientAddressNotAuthorised_IsNetworkBlocked()
        {
            var message = "Client address is not authorized and caller is not a trusted service.\r\n"
                + "Client address: 203.0.113.10\r\nCaller: appid=00000000-0000-0000-0000-000000000000";

            Assert.AreEqual(KeyVaultForbiddenReason.NetworkBlocked, KeyVaultForbiddenClassifier.Classify(message));
        }

        [TestMethod]
        public void Classify_PublicNetworkAccessDisabled_IsNetworkBlocked()
        {
            var message = "Public network access is disabled and request is not from a trusted service "
                + "nor via an approved private link.";

            Assert.AreEqual(KeyVaultForbiddenReason.NetworkBlocked, KeyVaultForbiddenClassifier.Classify(message));
        }

        [TestMethod]
        public void Classify_InnerErrorCodes_AreNetworkBlocked()
        {
            Assert.AreEqual(KeyVaultForbiddenReason.NetworkBlocked,
                KeyVaultForbiddenClassifier.Classify("ForbiddenByFirewall: something"));
            Assert.AreEqual(KeyVaultForbiddenReason.NetworkBlocked,
                KeyVaultForbiddenClassifier.Classify("ForbiddenByConnection: something"));
        }

        [TestMethod]
        public void Classify_MissingSecretsPermission_IsPermissionDenied()
        {
            var message = "The user, group or application 'appid=00000000-0000-0000-0000-000000000000' "
                + "does not have secrets set permission on key vault 'contosovault'.";

            Assert.AreEqual(KeyVaultForbiddenReason.PermissionDenied, KeyVaultForbiddenClassifier.Classify(message));
        }

        [TestMethod]
        public void Classify_RbacDenial_IsPermissionDenied()
        {
            Assert.AreEqual(KeyVaultForbiddenReason.PermissionDenied,
                KeyVaultForbiddenClassifier.Classify("Caller is not authorized to perform action on resource."));
        }

        /// <summary>
        /// The discriminating case. A firewall rejection also contains "not authorized", so a naive
        /// permission test matches it and sends the operator to fix the wrong thing.
        /// </summary>
        [TestMethod]
        public void Classify_FirewallWordingIsNotMistakenForAPermissionProblem()
        {
            var firewall = "Client address is not authorized and caller is not a trusted service.";
            var rbac = "Caller is not authorized to perform action on resource.";

            Assert.AreEqual(KeyVaultForbiddenReason.NetworkBlocked, KeyVaultForbiddenClassifier.Classify(firewall));
            Assert.AreEqual(KeyVaultForbiddenReason.PermissionDenied, KeyVaultForbiddenClassifier.Classify(rbac));
        }

        [TestMethod]
        public void Classify_UnknownOrEmpty_IsUnknown()
        {
            Assert.AreEqual(KeyVaultForbiddenReason.Unknown, KeyVaultForbiddenClassifier.Classify((string)null));
            Assert.AreEqual(KeyVaultForbiddenReason.Unknown, KeyVaultForbiddenClassifier.Classify("   "));
            Assert.AreEqual(KeyVaultForbiddenReason.Unknown, KeyVaultForbiddenClassifier.Classify("Something else went wrong."));
        }

        [TestMethod]
        public void FirstLine_TakesOnlyTheExplanation()
        {
            var message = "Client address is not authorized and caller is not a trusted service.\r\n"
                + "Client address: 203.0.113.10\r\nCaller: appid=00000000-0000-0000-0000-000000000000";

            Assert.AreEqual("Client address is not authorized and caller is not a trusted service.",
                KeyVaultForbiddenClassifier.FirstLine(message));
        }

        [TestMethod]
        public void FirstLine_SingleLineAndEmpty()
        {
            Assert.AreEqual("Only one line.", KeyVaultForbiddenClassifier.FirstLine("  Only one line.  "));
            Assert.IsNull(KeyVaultForbiddenClassifier.FirstLine(null));
            Assert.IsNull(KeyVaultForbiddenClassifier.FirstLine("  "));
        }

        #endregion

        #region Downloaded-build Entra hint

        /// <summary>
        /// The observed failure: the child never announced Entra authentication, and Entity Framework's
        /// error blamed the 'master' database instead of the missing login.
        /// </summary>
        [TestMethod]
        public void EntraHint_GivenWhenChildNeverAnnouncedEntraAuth()
        {
            var childOutput = "Build 'Build 1825' - begin database upgrade.\r\n"
                + "Connecting to database @ '...' with Entity Framework context initializer set to 'MigrateDatabaseToLatestVersion'...\r\n"
                + "Initialise database failed with EF. Exception: 'System.InvalidOperationException: This operation requires a "
                + "connection to the 'master' database. ---> Microsoft.Data.SqlClient.SqlException: Login failed for user ''.";

            var hint = SqlInstallerTasks.DownloadedBuildEntraHint(true, childOutput, null);

            StringAssert.Contains(hint, "did not report");
            StringAssert.Contains(hint, "local release override");
        }

        [TestMethod]
        public void EntraHint_SuppressedWhenChildDidAuthenticateWithEntra()
        {
            var childOutput = "Build 'Build 9999' - begin database upgrade.\r\n"
                + DatabaseUpgrader.EntraAuthAnnouncement + " (no SQL login in the connection string).\r\n"
                + "Initialise database failed with EF. Exception: 'something unrelated'.";

            Assert.AreEqual(string.Empty, SqlInstallerTasks.DownloadedBuildEntraHint(true, childOutput, null));
        }

        [TestMethod]
        public void EntraHint_SuppressedForSqlAuthenticationDatabases()
        {
            // A SQL-login database never needed a token, so the hint would be pure noise.
            Assert.AreEqual(string.Empty, SqlInstallerTasks.DownloadedBuildEntraHint(false, "anything at all", null));
        }

        [TestMethod]
        public void EntraHint_ReadsStandardErrorToo()
        {
            // The child writes its unhandled exception to stderr; the announcement may only be on stdout.
            var stdErr = DatabaseUpgrader.EntraAuthAnnouncement + " (no SQL login in the connection string).";

            Assert.AreEqual(string.Empty, SqlInstallerTasks.DownloadedBuildEntraHint(true, null, stdErr));
        }

        [TestMethod]
        public void EntraHint_HandlesNoCapturedOutput()
        {
            var hint = SqlInstallerTasks.DownloadedBuildEntraHint(true, null, null);

            StringAssert.Contains(hint, "did not report");
        }

        #endregion

        #region Downloaded-build capability probe

        /// <summary>
        /// The probe must not block an install just because it could not look. Its three states exist so
        /// that "no answer" and "definitely absent" are never confused.
        /// </summary>
        [TestMethod]
        public void Probe_MissingOrNullFolder_IsUnknown()
        {
            Assert.AreEqual(DownloadedBuildCapability.Unknown,
                DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(null));
            Assert.AreEqual(DownloadedBuildCapability.Unknown,
                DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(
                    new DirectoryInfo(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()))));
        }

        [TestMethod]
        public void Probe_BuildCarryingTheMarker_IsSupported()
        {
            using (var folder = new TempFolder())
            {
                folder.WriteBinary("Other.dll", "nothing interesting here");
                // Mimics the type name sitting in assembly metadata.
                folder.WriteBinary("DataUtils.dll", "...\0DataUtils.Sql\0AzureSqlTokenAuth\0GetAccessToken\0...");

                Assert.AreEqual(DownloadedBuildCapability.Supported,
                    DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(folder.Info));
            }
        }

        [TestMethod]
        public void Probe_BuildWithoutTheMarker_IsNotSupported()
        {
            using (var folder = new TempFolder())
            {
                folder.WriteBinary("DataUtils.dll", "...\0DataUtils.Sql\0SomeOtherType\0...");
                folder.WriteBinary("AnalyticsInstaller.exe", "...\0Program\0Main\0...");

                Assert.AreEqual(DownloadedBuildCapability.NotSupported,
                    DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(folder.Info));
            }
        }

        /// <summary>
        /// Which assembly carries the type is a packaging detail, so every managed file is scanned - and
        /// sub-folders count, because the release layout is not guaranteed flat.
        /// </summary>
        [TestMethod]
        public void Probe_FindsMarkerInAnyAssemblyIncludingSubfolders()
        {
            using (var folder = new TempFolder())
            {
                folder.WriteBinary("AnalyticsInstaller.exe", "no marker here");
                folder.WriteBinary(Path.Combine("sub", "Repackaged.dll"), "xxx AzureSqlTokenAuth xxx");

                Assert.AreEqual(DownloadedBuildCapability.Supported,
                    DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(folder.Info));
            }
        }

        [TestMethod]
        public void Probe_EmptyFolder_IsUnknownNotAbsent()
        {
            using (var folder = new TempFolder())
            {
                // Nothing was readable, so absence cannot be claimed.
                Assert.AreEqual(DownloadedBuildCapability.Unknown,
                    DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(folder.Info));
            }
        }

        /// <summary>
        /// The scan reads in blocks, so a marker straddling a block boundary is the case most likely to be
        /// missed. Padded to push the marker well past the 64KB block size.
        /// </summary>
        [TestMethod]
        public void Probe_FindsMarkerSpanningABlockBoundary()
        {
            using (var folder = new TempFolder())
            {
                var padding = new string('x', (64 * 1024) - 8);
                folder.WriteBinary("Big.dll", padding + DownloadedBuildCapabilityProbe.EntraSqlAuthMarker + "tail");

                Assert.AreEqual(DownloadedBuildCapability.Supported,
                    DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(folder.Info));
            }
        }

        /// <summary>
        /// Only managed files count. A release note or config that merely mentions the type must not be
        /// read as the capability being present - so here the marker exists ONLY in a non-managed file,
        /// and every managed file is free of it.
        /// </summary>
        [TestMethod]
        public void Probe_IgnoresNonManagedFiles()
        {
            using (var folder = new TempFolder())
            {
                folder.WriteBinary("readme.txt", "This release adds " + DownloadedBuildCapabilityProbe.EntraSqlAuthMarker + " support.");
                folder.WriteBinary("release-notes.md", DownloadedBuildCapabilityProbe.EntraSqlAuthMarker);
                folder.WriteBinary("Other.dll", "no marker in any assembly");
                folder.WriteBinary("AnalyticsInstaller.exe", "no marker in any assembly");

                Assert.AreEqual(DownloadedBuildCapability.NotSupported,
                    DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(folder.Info));
            }
        }

        private sealed class TempFolder : IDisposable
        {
            public DirectoryInfo Info { get; }

            public TempFolder()
            {
                Info = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "probe-" + Guid.NewGuid()));
            }

            public void WriteBinary(string relativePath, string content)
            {
                var full = Path.Combine(Info.FullName, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllBytes(full, Encoding.ASCII.GetBytes(content));
            }

            public void Dispose()
            {
                try { Info.Delete(true); } catch (IOException) { }
            }
        }

        #endregion
    }
}
