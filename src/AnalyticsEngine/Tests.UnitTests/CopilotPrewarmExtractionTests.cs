using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests the Copilot file-context extraction used to pre-warm Graph metadata resolution outside the SQL
    /// lock. Must mirror CopilotAuditEventManager's context handling (first file context only; meeting ends
    /// processing; chat is additive, not a file).
    /// </summary>
    [TestClass]
    public class CopilotPrewarmExtractionTests
    {
        private static CopilotAuditLogContent CopilotEvent(string upn, params Context[] contexts)
            => new CopilotAuditLogContent
            {
                UserId = upn,
                CopilotEventData = new CopilotEventData { Contexts = contexts.ToList() }
            };

        private static Context File(string id) => new Context { Id = id, Type = "file" };
        private static Context Chat(string id) => new Context { Id = id, Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT };
        private static Context Meeting(string id) => new Context { Id = id, Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING };

        [TestMethod]
        public void ExtractsFirstFileContextWithUpn()
        {
            var acts = new List<AbstractAuditLogContent>
            {
                CopilotEvent("a@contoso.com", File("https://contoso.sharepoint.com/sites/x/a.docx"))
            };
            var result = ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("a@contoso.com", result["https://contoso.sharepoint.com/sites/x/a.docx"]);
        }

        [TestMethod]
        public void ChatOnlyEventsYieldNoFileContext()
        {
            var acts = new List<AbstractAuditLogContent> { CopilotEvent("a@contoso.com", Chat("19:chat@thread.v2")) };
            Assert.AreEqual(0, ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts).Count);
        }

        [TestMethod]
        public void MeetingContextEndsProcessingWithNoFile()
        {
            // Meeting first -> break -> no file resolved (mirrors the manager's meeting-takes-priority behaviour).
            var acts = new List<AbstractAuditLogContent>
            {
                CopilotEvent("a@contoso.com", Meeting("19:meeting@thread.v2"), File("https://contoso.sharepoint.com/sites/x/a.docx"))
            };
            Assert.AreEqual(0, ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts).Count);
        }

        [TestMethod]
        public void ChatBeforeFileStillResolvesFile()
        {
            var acts = new List<AbstractAuditLogContent>
            {
                CopilotEvent("a@contoso.com", Chat("19:chat@thread.v2"), File("https://contoso.sharepoint.com/sites/x/a.docx"))
            };
            var result = ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts);
            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey("https://contoso.sharepoint.com/sites/x/a.docx"));
        }

        [TestMethod]
        public void DuplicateContextIdsAreDeduped()
        {
            var url = "https://contoso.sharepoint.com/sites/x/a.docx";
            var acts = new List<AbstractAuditLogContent>
            {
                CopilotEvent("a@contoso.com", File(url)),
                CopilotEvent("b@contoso.com", File(url))
            };
            var result = ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts);
            Assert.AreEqual(1, result.Count, "Same context id across events resolves once");
        }

        [TestMethod]
        public void NonCopilotActivitiesIgnored()
        {
            var acts = new List<AbstractAuditLogContent> { new SharePointAuditLogContent() };
            Assert.AreEqual(0, ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts).Count);
        }

        /// <summary>
        /// The extraction moved to CopilotPrewarmPolicy in issue #373; the manager keeps a one-line
        /// delegating wrapper. This does NOT try to prove delegation - with a one-line wrapper that
        /// comparison is tautological - it pins the thing that could actually regress: a non-Latin file
        /// URL surviving the wrapper unchanged as the dictionary key, which is what the lookup below
        /// depends on.
        ///
        /// The URL is the Unicode-bearing field here and the map's value is deliberately ASCII: it is
        /// the Copilot event's UserId, which this path consumes as an Entra UPN (it is handed to
        /// GraphFileMetadataLoader.GetSpoFileInfo as eventUpn and on to GetUserDriveAsync), and Entra
        /// UPNs are ASCII - see #402/#414.
        /// </summary>
        [TestMethod]
        public void ManagerWrapper_KeepsNonLatinFileUrlsIntact()
        {
            var url = "https://contoso.sharepoint.com/sites/x/Καλημέρα κόσμε.docx";
            var acts = new List<AbstractAuditLogContent>
            {
                CopilotEvent("a@contoso.onmicrosoft.com", Chat("19:chat@thread.v2"), File(url)),
                CopilotEvent("b@contoso.com", Meeting("19:meeting@thread.v2"), File("https://contoso.sharepoint.com/sites/x/b.docx"))
            };

            var viaWrapper = ActivityReportSqlPersistenceManager.ExtractCopilotFileContexts(acts);

            Assert.AreEqual(1, viaWrapper.Count);
            Assert.AreEqual("a@contoso.onmicrosoft.com", viaWrapper[url]);
        }

        [TestMethod]
        public void PrewarmIsSkippedWhenCopilotResourceResolutionIsDisabled()
        {
            // With resolution off the save path makes no Graph resource calls at all, so warming would be
            // pure outbound Graph traffic for a cache nothing reads.
            Assert.IsFalse(CopilotPrewarmPolicy.ShouldPrewarm(hasSharedLoader: true, resolveCopilotResourceMetadata: false));
        }

        [TestMethod]
        public void PrewarmIsSkippedWhenTheSharedLoaderCouldNotBeBuilt()
        {
            // Building the run-scoped loader is best-effort (no Graph credentials in a test, for instance).
            Assert.IsFalse(CopilotPrewarmPolicy.ShouldPrewarm(hasSharedLoader: false, resolveCopilotResourceMetadata: true));
            Assert.IsFalse(CopilotPrewarmPolicy.ShouldPrewarm(hasSharedLoader: false, resolveCopilotResourceMetadata: false));
        }

        [TestMethod]
        public void PrewarmRunsWhenThereIsALoaderAndResolutionIsEnabled()
        {
            Assert.IsTrue(CopilotPrewarmPolicy.ShouldPrewarm(hasSharedLoader: true, resolveCopilotResourceMetadata: true));
        }
    }
}
