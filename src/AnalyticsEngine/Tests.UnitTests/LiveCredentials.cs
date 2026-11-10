using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Tests.UnitTests
{
    /// <summary>
    /// The gate for a test that signs in to the test tenant with the app registration's client secret.
    /// </summary>
    /// <remarks>
    /// Dependabot and fork pull requests get no repository secrets, so their CI runs have no ClientSecret (#860). There such a
    /// test is Inconclusive, as the Azurite tests are without Azurite, and the job summary warns that it did not run
    /// (.github/scripts/Write-TestSummary.ps1 finds it by <see cref="SkipMarker"/>). Every other CI run sets
    /// REQUIRE_LIVE_CREDENTIALS=true, which makes a missing secret a failure. A test that never calls the tenant mustn't need
    /// a secret at all: build its dependencies without one instead (see <c>OfflineSqlActivityPersistence</c>).
    /// </remarks>
    internal static class LiveCredentials
    {
        /// <summary>Starts the skip message. Write-TestSummary.ps1 looks for it, so change both together.</summary>
        internal const string SkipMarker = "Needs a live credential:";

        internal static void RequireGraphClientSecret(AppConfig config)
        {
            RequireGraphClientSecret(config, string.Equals(Environment.GetEnvironmentVariable("REQUIRE_LIVE_CREDENTIALS"), "true",
                StringComparison.OrdinalIgnoreCase));
        }

        internal static void RequireGraphClientSecret(AppConfig config, bool required)
        {
            if (config.UseClientCertificate || !string.IsNullOrWhiteSpace(config.ClientSecret))
            {
                return;
            }

            var reason = SkipMarker + " no ClientSecret is configured, so this test can't sign in to Microsoft Graph.";
            if (required)
            {
                Assert.Fail(reason + " REQUIRE_LIVE_CREDENTIALS is set, so this is a failure rather than a skip.");
            }
            Assert.Inconclusive(reason);
        }
    }
}
