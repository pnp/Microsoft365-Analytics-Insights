using App.ControlPanel.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// The installer's "Test Configuration" check for the Teams import. It looked for each group's workloads where
    /// Microsoft.Graph 4 put them, so under Microsoft.Graph 6 it never found a Team, never read a channel - the access the
    /// import actually needs - and still reported "Successfully verified Graph API for Teams". It must classify groups the
    /// way the Teams crawl does, and only report success when it really read a Team's channels.
    /// </summary>
    /// <remarks>
    /// Graph's own JSON goes through the real SDK. The check is driven through the verifier's own entry point.
    /// </remarks>
    [TestClass]
    public class TeamsImportVerificationTests
    {
        private const string TeamGroupId = "00000000-0000-0000-0000-0000000000f1";

        private static string Group(string id, string displayName, string provisioningOptionsJson)
            => "{\"id\":\"" + id + "\",\"displayName\":\"" + displayName + "\"" +
               (provisioningOptionsJson == null ? "" : ",\"resourceProvisioningOptions\":" + provisioningOptionsJson) + "}";

        private static async Task<RecordingLogger> VerifyTeamsImport(TeamsCrawlTests.StubGraphHandler graph)
        {
            var logger = new RecordingLogger();
            var verifier = new SolutionInstallVerifier(new SolutionInstallConfig(), logger, null);
            var verifyTeamsImport = typeof(SolutionInstallVerifier).GetMethod("VerifyTeamsImport", BindingFlags.Instance | BindingFlags.NonPublic);
            await (Task)verifyTeamsImport.Invoke(verifier, new object[] { TeamsCrawlTests.StubGraphHandler.Client(graph) });
            return logger;
        }

        private static bool ReportedSuccess(RecordingLogger logger)
            => logger.Entries.Any(e => e.Message != null && e.Message.StartsWith("Successfully verified Graph API for Teams"));

        [TestMethod]
        public async Task TeamsCheck_FindsATeamTheWayTheCrawlDoes_AndReadsItsChannels()
        {
            // The Team is on the second page: the check used to look at the first page only.
            var graph = new TeamsCrawlTests.StubGraphHandler()
                .Route("/groups", request => TeamsCrawlTests.StubGraphHandler.Response(HttpStatusCode.OK,
                    request.RequestUri.Query.Contains("skiptoken")
                        ? "{\"value\":[" + Group(TeamGroupId, "Contoso Καλημέρα", "[\"Team\"]") + "]}"
                        : "{\"value\":[" + Group("00000000-0000-0000-0000-0000000000f2", "Contoso Security Group", "[]") + "]," +
                          "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/groups?$skiptoken=page-2\"}"))
                .Json("/teams/" + TeamGroupId + "/channels", "{\"value\":[{\"id\":\"19:general\",\"displayName\":\"General\",\"membershipType\":\"standard\"}]}");

            var logger = await VerifyTeamsImport(graph);

            Assert.IsTrue(graph.Requests.Any(u => u.AbsolutePath.EndsWith("/teams/" + TeamGroupId + "/channels")),
                "The check must actually read a Team's channels: that is the access the Teams import needs.");
            Assert.IsTrue(ReportedSuccess(logger));
            Assert.IsFalse(logger.Entries.Any(e => e.Level >= LogLevel.Warning), string.Join(" | ", logger.Entries.Select(e => e.Message)));
        }

        [TestMethod]
        public async Task TeamsCheck_ThatFindsNoTeam_SaysItVerifiedNothing()
        {
            var graph = new TeamsCrawlTests.StubGraphHandler().Json("/groups", "{\"value\":[" +
                Group("00000000-0000-0000-0000-0000000000f3", "Contoso Security Group", "[]") + "," +
                Group("00000000-0000-0000-0000-0000000000f4", "Contoso Mailbox Group", "[\"Exchange\"]") + "]}");

            var logger = await VerifyTeamsImport(graph);

            Assert.IsFalse(ReportedSuccess(logger), "Nothing was read, so nothing was verified.");
            Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("NOT verified")));
        }

        [TestMethod]
        public async Task TeamsCheck_WhenNoGroupReportsItsWorkloads_IsAnError()
        {
            // How every group looked to the importer after the Graph SDK upgrade: the crawl would find no Team either.
            var graph = new TeamsCrawlTests.StubGraphHandler().Json("/groups", "{\"value\":[" +
                Group("00000000-0000-0000-0000-0000000000f5", "Contoso Team", null) + "," +
                Group("00000000-0000-0000-0000-0000000000f6", "Contoso Group", null) + "]}");

            var logger = await VerifyTeamsImport(graph);

            Assert.IsFalse(ReportedSuccess(logger));
            Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("resourceProvisioningOptions")));
        }
    }
}
