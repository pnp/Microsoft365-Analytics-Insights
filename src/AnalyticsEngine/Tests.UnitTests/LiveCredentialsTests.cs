using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// What a test that needs a live credential does on a run without one, as on a Dependabot or fork pull request (#860).
    /// </summary>
    [TestClass]
    public class LiveCredentialsTests
    {
        private const string InconclusivePrefix = "Assert.Inconclusive failed. ";

        [TestMethod]
        public void NoClientSecret_IsInconclusive_WithTheMarkerTheJobSummaryLooksFor()
        {
            var config = new AppConfig { ClientSecret = " ", UseClientCertificate = false };

            var skip = Assert.ThrowsException<AssertInconclusiveException>(() => LiveCredentials.RequireGraphClientSecret(config, required: false));

            StringAssert.StartsWith(skip.Message, InconclusivePrefix + LiveCredentials.SkipMarker);
        }

        [TestMethod]
        public void NoClientSecret_FailsOnARunThatRequiresLiveCredentials()
        {
            var config = new AppConfig { ClientSecret = null, UseClientCertificate = false };

            Assert.ThrowsException<AssertFailedException>(() => LiveCredentials.RequireGraphClientSecret(config, required: true));
        }

        [TestMethod]
        public void AClientSecretOrACertificate_LetsTheTestRun()
        {
            LiveCredentials.RequireGraphClientSecret(new AppConfig { ClientSecret = "synthetic", UseClientCertificate = false }, required: true);
            LiveCredentials.RequireGraphClientSecret(new AppConfig { ClientSecret = "", UseClientCertificate = true }, required: true);
        }

        [TestMethod]
        public void TheJobSummary_LooksForTheSameMarker()
        {
            var script = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "scripts", "Write-TestSummary.ps1"));

            StringAssert.Contains(script, "$liveCredentialMarker = '" + LiveCredentials.SkipMarker + "'",
                "Write-TestSummary.ps1 would stop warning that these tests were skipped.");
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, ".github", "pull_request_template.md")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Source repository was not found.");
        }
    }
}
