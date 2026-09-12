using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Azure-free tests for two installer decisions that used to be wrong in ways the install log did not
    /// reveal: which Azure AI Language endpoint the runtime is given, and which Key Vault RBAC roles are
    /// granted when the vault uses the RBAC permission model instead of access policies.
    /// </summary>
    [TestClass]
    public class InstallerAuthAndEndpointTests
    {
        // ---------------------------------------------------------------------
        // Azure AI Language endpoint selection
        // ---------------------------------------------------------------------

        /// <summary>
        /// The regression this guards: the installer used to hard-code the REGIONAL endpoint, which only
        /// supports API-key auth. When the resource has account keys disabled the installer falls back to
        /// Entra ID, and Azure then rejects every call with 400 "Please provide a custom subdomain for token
        /// authentication, otherwise API key is required" - an install that reports success but whose
        /// sentiment/key-phrase enrichment never works.
        /// </summary>
        [TestMethod]
        public void ResolveEndpoint_PrefersTheResourcesOwnEndpoint()
        {
            var endpoint = TextAnalyticsInstallTask.ResolveEndpoint(
                reportedEndpoint: "https://contoso-language.cognitiveservices.azure.com/",
                customSubDomain: "contoso-language",
                location: "westeurope",
                usedRegionalFallback: out var fellBack);

            Assert.AreEqual("https://contoso-language.cognitiveservices.azure.com/", endpoint);
            Assert.IsFalse(fellBack);
            StringAssert.Contains(endpoint, "cognitiveservices.azure.com",
                "Entra ID token auth only works against the custom-subdomain endpoint.");
        }

        [TestMethod]
        public void ResolveEndpoint_BuildsCustomSubdomainWhenArmHasNotPopulatedTheEndpointYet()
        {
            var endpoint = TextAnalyticsInstallTask.ResolveEndpoint(
                reportedEndpoint: null,
                customSubDomain: "contoso-language",
                location: "westeurope",
                usedRegionalFallback: out var fellBack);

            Assert.AreEqual("https://contoso-language.cognitiveservices.azure.com/", endpoint);
            Assert.IsFalse(fellBack, "A custom subdomain is available, so this is not the regional fallback.");
        }

        [TestMethod]
        public void ResolveEndpoint_FallsBackToRegionalOnlyAsALastResortAndFlagsIt()
        {
            var endpoint = TextAnalyticsInstallTask.ResolveEndpoint(
                reportedEndpoint: "   ",
                customSubDomain: null,
                location: "westeurope",
                usedRegionalFallback: out var fellBack);

            Assert.AreEqual("https://westeurope.api.cognitive.microsoft.com/", endpoint);
            Assert.IsTrue(fellBack, "The caller must warn: RBAC auth cannot work against a regional endpoint.");
        }

        // ---------------------------------------------------------------------
        // Key Vault access-policy permissions -> RBAC roles
        // ---------------------------------------------------------------------

        /// <summary>
        /// Read-only secret access must NOT be granted the Officer role - that would hand the web app's
        /// managed identity the ability to overwrite and delete every secret in the vault.
        /// </summary>
        [TestMethod]
        public void MapPermissionsToRoles_ReadOnlySecretsGetsSecretsUserNotOfficer()
        {
            var roles = BaseKeyVaultAddPolicyTask.MapPermissionsToRoles(
                new[] { "Get", "List" }, new string[0]);

            CollectionAssert.AreEqual(new[] { BaseKeyVaultAddPolicyTask.ROLE_SECRETS_USER }, roles.ToList());
        }

        [TestMethod]
        public void MapPermissionsToRoles_WritePermissionsEscalateToSecretsOfficer()
        {
            // The permission set used by the task that writes the runtime app-registration secret.
            var roles = BaseKeyVaultAddPolicyTask.MapPermissionsToRoles(
                new[] { "Get", "List", "Set", "Delete", "Recover", "Backup", "Restore" }, new[] { "Get" });

            CollectionAssert.AreEquivalent(
                new[] { BaseKeyVaultAddPolicyTask.ROLE_SECRETS_OFFICER, BaseKeyVaultAddPolicyTask.ROLE_CERTIFICATE_USER },
                roles.ToList());
            CollectionAssert.DoesNotContain(roles.ToList(), BaseKeyVaultAddPolicyTask.ROLE_SECRETS_USER);
        }

        [TestMethod]
        public void MapPermissionsToRoles_WriteDetectionIsCaseInsensitive()
        {
            var roles = BaseKeyVaultAddPolicyTask.MapPermissionsToRoles(
                new[] { "get", "set" }, new string[0]);

            CollectionAssert.AreEqual(new[] { BaseKeyVaultAddPolicyTask.ROLE_SECRETS_OFFICER }, roles.ToList());
        }

        /// <summary>
        /// Certificate access needs "Key Vault Certificate User" specifically, because downloading a
        /// certificate's private key also reads the backing secret.
        /// </summary>
        [TestMethod]
        public void MapPermissionsToRoles_CertificateOnlyGrantsCertificateUser()
        {
            var roles = BaseKeyVaultAddPolicyTask.MapPermissionsToRoles(
                new string[0], new[] { "Get" });

            CollectionAssert.AreEqual(new[] { BaseKeyVaultAddPolicyTask.ROLE_CERTIFICATE_USER }, roles.ToList());
        }

        [TestMethod]
        public void MapPermissionsToRoles_NoPermissionsGrantsNoRoles()
        {
            Assert.AreEqual(0, BaseKeyVaultAddPolicyTask.MapPermissionsToRoles(null, null).Count);
            Assert.AreEqual(0, BaseKeyVaultAddPolicyTask.MapPermissionsToRoles(new string[0], new string[0]).Count);
        }

        // ---------------------------------------------------------------------
        // 403 classification
        // ---------------------------------------------------------------------

        /// <summary>
        /// An RBAC-model vault reports a missing role assignment with the inner error code
        /// <c>ForbiddenByRbac</c>. That wording carries none of the phrases the other permission markers
        /// match, so without an explicit marker it classified as Unknown and the operator got the generic
        /// "could be networking, could be permissions" message instead of the RBAC remedy.
        /// </summary>
        [TestMethod]
        public void Classify_ForbiddenByRbacInnerCode_IsPermissionDenied()
        {
            Assert.AreEqual(KeyVaultForbiddenReason.PermissionDenied,
                KeyVaultForbiddenClassifier.Classify("Access denied. Inner error: {\"code\":\"ForbiddenByRbac\"}"));
        }

        /// <summary>
        /// Ordering guard: the networking markers are tested first, so a firewall rejection must not be
        /// reclassified as a permission problem by the new RBAC marker.
        /// </summary>
        [TestMethod]
        public void Classify_FirewallStillWinsOverTheRbacMarker()
        {
            Assert.AreEqual(KeyVaultForbiddenReason.NetworkBlocked,
                KeyVaultForbiddenClassifier.Classify("Client address is not authorized. Inner error: {\"code\":\"ForbiddenByFirewall\"}"));
        }
    }
}
