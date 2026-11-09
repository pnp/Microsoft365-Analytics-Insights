using Common.Entities;
using Common.Entities.UserScope;
using DataUtils;
using DataUtils.Sql;
using DataUtils.Sql.Inserts;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #853: tenant content - a value SQL Server quotes in an error, a page or file URL, a call record - must not
    /// reach the console or Application Insights. Each test plants a synthetic marker and asserts it is never logged.
    /// </summary>
    [TestClass]
    public class SecurityLogHygieneTests
    {
        private const string Marker = "Contoso marker 853";
        private const string UrlMarker = "contoso-marker-853";
        private const string SyntheticConnectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://contoso.invalid/";

        #region SQL error values (critique S5)

        private class MergeRow
        {
            [Column("v", SqlTypeOverride = "nvarchar(100)")]
            public string Value { get; set; }
        }

        /// <summary>
        /// A merge that fails on a duplicate key used to put SQL Server's message, value and all, into the
        /// <see cref="BatchSaveException"/> - which is logged, and its text persisted.
        /// </summary>
        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task MergeFailure_KeepsTheSqlErrorButNotTheValueItQuoted()
        {
            using (var database = ScratchDatabase.Create("MergeLog"))
            {
                var staging = "##ut853_" + Guid.NewGuid().ToString("N");
                var batch = new InsertBatch<MergeRow>(database.ConnectionString, new CapturingLogger());
                batch.Rows.Add(new MergeRow { Value = Marker });
                var mergeSql =
                    "CREATE TABLE #merge_target (v nvarchar(100) NOT NULL PRIMARY KEY); " +
                    $"INSERT INTO #merge_target (v) SELECT v FROM [{staging}]; " +
                    $"INSERT INTO #merge_target (v) SELECT v FROM [{staging}];";

                var ex = await Assert.ThrowsExceptionAsync<BatchSaveException>(() => batch.SaveToStagingTable(10, mergeSql, staging));
                SqlConnection.ClearAllPools();

                Assert.IsFalse(ex.Message.Contains(Marker), ex.Message);
                StringAssert.Contains(ex.Message, "The duplicate key value is (<redacted>)");
                Assert.AreEqual(2627, (ex.InnerException as SqlException)?.Number, "The SqlException stays inner, for TransientSqlRetry.");
                Assert.IsFalse(TransientSqlRetry.IsTransient(ex));

                var logged = Log(logger =>
                {
                    logger.LogError("Merge failed.", ex);
                    logger.LogError($"Import cycle failed: {CommonExceptionHandler.GetErrorText(ex)}");
                });
                Assert.IsFalse(logged.Console.Contains(Marker), logged.Console);
                Assert.IsFalse(logged.Telemetry.Contains(Marker), logged.Telemetry);
                StringAssert.Contains(logged.Console, "SqlException");
            }
        }

        /// <summary>
        /// Whatever wraps it, an exception chain that carries a value SQL Server quoted is logged without it: in the
        /// console line and stack, in the trace, and in the exception telemetry.
        /// </summary>
        [TestMethod]
        public void AnyLoggedText_LosesTheValuesSqlServerQuotes()
        {
            var sqlText = "Violation of UNIQUE KEY constraint 'IX_name'. Cannot insert duplicate key in object 'dbo.lookup'. " +
                $"The duplicate key value is ({Marker}).";
            var ex = new InvalidOperationException("An error occurred while updating the entries.", new Exception(sqlText));

            var logged = Log(logger =>
            {
                logger.LogError("Saving failed.", ex);
                logger.LogWarning("Inner exception = " + ex.InnerException.Message);
                logger.TrackException(new Exception($"String or binary data would be truncated in table 'dbo.urls', column 'full_url'. Truncated value: '{Marker}'."));
            });

            Assert.IsFalse(logged.Console.Contains(Marker), logged.Console);
            Assert.IsFalse(logged.Telemetry.Contains(Marker), logged.Telemetry);
            StringAssert.Contains(logged.Console, "The duplicate key value is (<redacted>)");
            StringAssert.Contains(logged.Telemetry, "Truncated value: <redacted>");
        }

        #endregion

        #region URLs (critique S5)

        /// <summary>
        /// A SharePoint audit event whose cached row is missing used to be logged with the item's URL - a file's
        /// address. The audit-log content id finds the event just as well.
        /// </summary>
        [TestMethod]
        public async Task SharePointAuditEvent_WithoutItsCachedRow_IsLoggedByContentId_NotByUrl()
        {
            var fileUrl = "https://contoso.sharepoint.com/sites/finance/Shared%20Documents/" + UrlMarker + ".docx";
            var content = new SharePointAuditLogContent { Id = Guid.NewGuid(), ObjectId = fileUrl };
            var logger = new CapturingLogger();

            using (var session = new SaveSession(logger, new AnalyticsEntitiesContext(), new TestsAppConfig(), new FakeCopilotMetadataLoader()))
            {
                Assert.IsTrue(await content.ProcessExtendedProperties(session, null, logger));
            }

            var line = logger.Lines.Single().Message;
            Assert.IsFalse(line.Contains(UrlMarker), line);
            StringAssert.Contains(line, content.Id.ToString());
        }

        /// <summary>
        /// A page update whose comments are not valid JSON used to be reported on the console with the page's URL.
        /// </summary>
        [TestMethod]
        public void UnreadablePageUpdate_IsNotPrintedWithItsUrl()
        {
            var props = new PageUpdateEventCustomProps
            {
                Url = "https://contoso.sharepoint.com/sites/news/SitePages/" + UrlMarker + ".aspx",
                CommentsString = "[{ not json",
                LikesString = "{}",
                PropsString = "{\"Comments\":{\"legacy\":true}}",
            };

            var console = CaptureConsole(() =>
            {
                Assert.AreEqual(0, props.PageComments.Count);
                Assert.AreEqual(0, props.Likes.Count);
                Assert.AreEqual(0, props.SimplePropsDic.Count);
            });

            Assert.IsFalse(console.Contains(UrlMarker), console);
        }

        #endregion

        #region Operator single-call mode (X3-N8)

        /// <summary>
        /// <c>--callId</c> logged the whole call record at Information - every participant's name and id. It now logs
        /// what it loaded, by count.
        /// </summary>
        [TestMethod]
        public async Task SingleCallImport_DoesNotLogTheCallRecord()
        {
            var call = new SyntheticMultiPageCall(firstPageAttendees: 2, secondPageAttendees: 1);
            var graph = call.CreateGraph();
            var logger = new CapturingLogger();
            var nobody = UserImportScope.ForMembers(new UserScopeMembers(), "test");

            var saved = await CallRecordDTO.SaveNewCallToDB(call.CallId, new ManualGraphCallClient(graph, logger),
                SyntheticMultiPageCall.CreateLoadContext(graph), logger, SyntheticMultiPageCall.TenantId, nobody);

            Assert.IsNull(saved, "Nobody on the call is in scope, so nothing is saved.");
            var log = string.Join(Environment.NewLine, logger.Lines.Select(l => l.Message));
            Assert.IsFalse(log.Contains(call.Organiser.DisplayName), log);
            Assert.IsFalse(log.Contains(call.Organiser.Id), log);
            Assert.IsFalse(call.Attendees.Any(a => log.Contains(a.DisplayName) || log.Contains(a.Upn)), log);
            StringAssert.Contains(log, call.CallId);
        }

        /// <summary>
        /// The mode passed the OAuth context's credential before <c>InitClientCredential</c> had created it, so it ran
        /// with none. A missing credential now fails at once, saying what to do.
        /// </summary>
        [TestMethod]
        public async Task SingleCallImport_WithoutACredential_SaysItWasNotInitialised()
        {
            var ex = await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => CallRecordDTO.SaveNewCallToDB(
                "00000000-0000-0000-0000-000000000000", new ManualGraphCallClient(new SyntheticCallGraphHandler(), new CapturingLogger()),
                (Azure.Core.TokenCredential)null, new CapturingLogger(), SyntheticMultiPageCall.TenantId));

            Assert.AreEqual("graphServiceClientAuthenticationProvider", ex.ParamName);
            StringAssert.Contains(ex.Message, "InitClientCredential");
        }

        #endregion

        #region Helpers

        private static (string Console, string Telemetry) Log(Action<AnalyticsLogger> log)
        {
            var channel = new RecordingChannel();
            var configuration = new TelemetryConfiguration { ConnectionString = SyntheticConnectionString, TelemetryChannel = channel };
            var logger = new AnalyticsLogger(new TelemetryClient(configuration), nameof(SecurityLogHygieneTests));

            var console = CaptureConsole(() => log(logger));
            var telemetry = Encoding.UTF8.GetString(
                Microsoft.ApplicationInsights.Extensibility.Implementation.JsonSerializer.Serialize(channel.Sent, compress: false));
            return (console, telemetry);
        }

        private static string CaptureConsole(Action action)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var captured = new StringWriter();
            Console.SetOut(captured);
            Console.SetError(captured);
            try
            {
                action();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            return captured.ToString();
        }

        private sealed class RecordingChannel : ITelemetryChannel
        {
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();

            public IList<ITelemetry> Sent
            {
                get
                {
                    lock (_sent) return _sent.ToList();
                }
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(ITelemetry item)
            {
                lock (_sent) _sent.Add(item);
            }

            public void Flush() { }
            public void Dispose() { }
        }

        #endregion
    }
}
