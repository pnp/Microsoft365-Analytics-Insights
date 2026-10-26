extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.Models;
using DataUtils;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Tests.UnitTests
{
    /// <summary>
    /// Web requests build <see cref="AnalyticsLogger"/>s freely - the call-records webhook per POST, the exception
    /// reporter per failure, including every 404. Each used to build its own Application Insights configuration,
    /// whose default channel runs a transmitter thread until disposed, and nothing disposed it: one leaked thread per
    /// request, from an anonymous endpoint.
    /// </summary>
    [TestClass]
    public class AnalyticsLoggerSharedTelemetryTests
    {
        private const string SyntheticConnectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://contoso.invalid/";

        [TestMethod]
        public void LoggersBuiltPerRequest_DoNotEachStartATelemetryThread()
        {
            // Warm up: the first logger for this connection string creates the process's one shared channel.
            new AnalyticsLogger(SyntheticConnectionString, "Warm-up");
            var before = Process.GetCurrentProcess().Threads.Count;

            for (var i = 0; i < 100; i++)
            {
                new AnalyticsLogger(SyntheticConnectionString, "Request " + i);
            }

            var started = Process.GetCurrentProcess().Threads.Count - before;
            Assert.IsTrue(started < 50,
                $"100 loggers started {started} threads. Each logger must reuse the process's telemetry channel, not start a transmitter of its own.");
        }

        [TestMethod]
        public void ConcurrentLoggersForOneConnectionString_CreateOneConfiguration()
        {
            var created = 0;
            var channel = new RecordingChannel();
            var clients = new SharedTelemetryClients(cs =>
            {
                Interlocked.Increment(ref created);
                return new TelemetryConfiguration { ConnectionString = cs, TelemetryChannel = channel };
            });

            Parallel.For(0, 100, i => new AnalyticsLogger(SyntheticConnectionString, "Request " + i, clients));

            Assert.AreEqual(1, created);
        }

        [TestMethod]
        public void LoggersSharingAClient_EachStampTheirOwnOperationName()
        {
            // Naively sharing one client and setting its Context.Operation.Name per logger would label one
            // logger's events with whichever logger was built last.
            var channel = new RecordingChannel();
            var clients = new SharedTelemetryClients(cs => new TelemetryConfiguration { ConnectionString = cs, TelemetryChannel = channel });
            var webhook = new AnalyticsLogger(SyntheticConnectionString, "Webhook", clients);
            var reporter = new AnalyticsLogger(SyntheticConnectionString, "Reporter", clients);

            webhook.LogInformation("from the webhook");
            reporter.LogWarning("from the reporter");
            webhook.TrackEvent(AnalyticsLogger.AnalyticsEvent.HealthCheck, "webhook event");
            reporter.TrackException(new InvalidOperationException("synthetic"));

            var sent = channel.Sent;
            Assert.AreEqual(4, sent.Count);
            Assert.AreEqual("Webhook", sent.OfType<TraceTelemetry>().Single(t => t.Message == "from the webhook").Context.Operation.Name);
            Assert.AreEqual("Reporter", sent.OfType<TraceTelemetry>().Single(t => t.Message == "from the reporter").Context.Operation.Name);
            Assert.AreEqual("Webhook", sent.OfType<EventTelemetry>().Single().Context.Operation.Name);
            Assert.AreEqual("Reporter", sent.OfType<ExceptionTelemetry>().Single().Context.Operation.Name);
        }

        [TestMethod]
        public void AnUnusableConnectionString_IsNotRememberedAsAFailure()
        {
            var attempts = 0;
            var clients = new SharedTelemetryClients(cs =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new ArgumentException("synthetic: unusable connection string");
                return new TelemetryConfiguration { ConnectionString = cs, TelemetryChannel = new RecordingChannel() };
            });

            Assert.ThrowsException<ArgumentException>(() => new AnalyticsLogger(SyntheticConnectionString, "First", clients));
            new AnalyticsLogger(SyntheticConnectionString, "Second", clients);

            Assert.AreEqual(2, attempts);
        }

        [TestMethod]
        public void PipelineNotFound_IsNotReportedAsAnException_ButOtherPipelineErrorsAre()
        {
            Assert.IsTrue(WebExceptionTelemetry.IsRoutineNotFound(new HttpException(404, "synthetic: /wp-login.php")));
            Assert.IsFalse(WebExceptionTelemetry.IsRoutineNotFound(new HttpException(500, "synthetic")));
            Assert.IsFalse(WebExceptionTelemetry.IsRoutineNotFound(new InvalidOperationException("synthetic")));
            Assert.IsFalse(WebExceptionTelemetry.IsRoutineNotFound(null));
        }

        [TestMethod]
        public async Task CallRecordWebhook_EmptyJsonBody_IsABadRequest()
        {
            // "{}" binds to a list with no notifications. That used to reach a deliberate null enumeration and come
            // back from an anonymous endpoint as a 500 carrying the exception.
            var response = await new CallRecordWebhookController().Post(new GraphChangeNotificationList(), string.Empty);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        public async Task CallRecordWebhook_NotificationsThatAreNotOurs_NeverReachServiceBus()
        {
            // The test settings hold only a placeholder Service Bus connection string, so building a sender from it
            // would throw: a 200 proves a stranger's notifications (and a null entry) were turned away before then.
            var response = await new CallRecordWebhookController().Post(new GraphChangeNotificationList
            {
                Notifications = new List<GraphChangeNotification>
                {
                    new GraphChangeNotification { ClientState = "synthetic-not-the-secret" },
                    null,
                },
            }, string.Empty);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>
        /// Subscriptions with the derived clientState notify the webhook URL plus "?cs=v1", so Graph's validation POST
        /// arrives with the token after it ("&amp;validationToken=") - and must still be answered with the token, or Graph
        /// refuses to create the subscription. Through Web API's real routing and binding, both URL forms.
        /// </summary>
        [TestMethod]
        public async Task CallRecordWebhook_ValidationHandshakeAndNotifications_WorkOnBothNotificationUrls()
        {
            const string token = "Validation: Testing client application reachability for subscription Request-Id: 00000000-0000-0000-0000-000000000000";
            using (var host = new PortalTestHost(new[] { typeof(CallRecordWebhookController) }, PortalTestHost.Anonymous(), PortalAccessPolicy.NotEnforcing))
            {
                foreach (var url in new[] { "api/CallRecordWebhook", "api/CallRecordWebhook?cs=v1" })
                {
                    var separator = url.Contains("?") ? "&" : "?";
                    var validation = await host.Client.PostAsync(url + separator + "validationToken=" + Uri.EscapeDataString(token),
                        new StringContent(string.Empty, Encoding.UTF8, "text/plain"));
                    Assert.AreEqual(HttpStatusCode.OK, validation.StatusCode, url);
                    Assert.AreEqual("text/plain", validation.Content.Headers.ContentType.MediaType, url);
                    Assert.AreEqual(token, await validation.Content.ReadAsStringAsync(), url);

                    // A notification on the same URL reaches the clientState check (this one fails it, so no Service Bus).
                    var notification = await host.Client.PostAsync(url,
                        new StringContent("{\"value\":[{\"clientState\":\"synthetic-not-the-secret\",\"resourceData\":{\"id\":\"call-1\"}}]}", Encoding.UTF8, "application/json"));
                    Assert.AreEqual(HttpStatusCode.OK, notification.StatusCode, url);
                    Assert.AreEqual("not null and that", await notification.Content.ReadAsStringAsync(), url);
                }
            }
        }

        [TestMethod]
        public void ProcessConfiguration_StampsBuildLabelJobNameAndRunIdOnEveryItem()
        {
            var channel = new RecordingChannel();
            var clients = new SharedTelemetryClients(cs =>
            {
                var configuration = SharedTelemetryClients.CreateProcessConfiguration(cs);
                configuration.TelemetryChannel = channel;
                return configuration;
            });
            var logger = new AnalyticsLogger(SyntheticConnectionString, "Office365ActivityImporter", clients);

            ProcessIdentity.Configure("Office365ActivityImporter", "Build 0000 (synthetic)");
            try
            {
                logger.LogInformation("a trace");
                logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.HealthCheck, "an event");
                logger.TrackException(new InvalidOperationException("synthetic"));
                logger.TrackImporterHeartbeat("AppInsightsImporter", (DateTime?)null, null, DateTime.UtcNow.AddHours(-3));
            }
            finally
            {
                ProcessIdentity.Configure(null, null);
            }

            Assert.AreEqual(4, channel.Sent.Count);
            foreach (var item in channel.Sent.Cast<ISupportProperties>())
            {
                Assert.AreEqual(ProcessIdentity.RunId, item.Properties["RunId"]);
                Assert.AreEqual("Build 0000 (synthetic)", item.Properties["BuildLabel"]);
                Assert.IsTrue(DateTime.TryParse(item.Properties["ProcessStartedUtc"], out _));
            }
            Assert.AreEqual("Office365ActivityImporter", ((ISupportProperties)channel.Sent.OfType<TraceTelemetry>().Single()).Properties["JobName"]);
            Assert.AreEqual("AppInsightsImporter", channel.Sent.OfType<EventTelemetry>().Single(e => e.Name == "ImporterHeartbeat").Properties["JobName"],
                "An item's own JobName must not be overwritten by the process default.");
            Assert.AreEqual("InvalidOperationException", channel.Sent.OfType<ExceptionTelemetry>().Single().Properties["ExceptionType"]);
        }

        [TestMethod]
        public void AnException_IsSentToAppInsightsOnce_HoweverManyTimesItIsLogged()
        {
            var channel = new RecordingChannel();
            var clients = new SharedTelemetryClients(cs => new TelemetryConfiguration { ConnectionString = cs, TelemetryChannel = channel });
            var logger = new AnalyticsLogger(SyntheticConnectionString, "Office365ActivityImporter", clients);
            var other = new AnalyticsLogger(SyntheticConnectionString, "Elsewhere", clients);
            var ex = new InvalidOperationException("synthetic {with braces}");

            logger.TrackException(ex);
            logger.LogError("logged after TrackException, with {braces} that are not a template", ex);
            ((ILogger)other).LogError(ex, "rethrown and logged again further up");

            Assert.AreEqual(1, channel.Sent.OfType<ExceptionTelemetry>().Count(), "One telemetry owner per exception.");
            Assert.AreEqual(2, channel.Sent.OfType<TraceTelemetry>().Count(), "Every log line is still a trace.");
        }

        [TestMethod]
        public void FlushAllBounded_WaitsForAFlushThatCompletes_AndGivesUpOnOneThatNeverDoes()
        {
            var completing = new SharedTelemetryClients(cs => new TelemetryConfiguration { ConnectionString = cs, TelemetryChannel = new AsyncFlushChannel(hang: false) });
            new AnalyticsLogger(SyntheticConnectionString, "Completing", completing);
            Assert.IsTrue(completing.FlushAllBounded(TimeSpan.FromSeconds(5)));

            var hanging = new SharedTelemetryClients(cs => new TelemetryConfiguration { ConnectionString = cs, TelemetryChannel = new AsyncFlushChannel(hang: true) });
            new AnalyticsLogger(SyntheticConnectionString, "Hanging", hanging);
            var clock = Stopwatch.StartNew();
            Assert.IsFalse(hanging.FlushAllBounded(TimeSpan.FromMilliseconds(300)));
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), "An exit path must never wait longer than its bound for the ingestion endpoint.");
        }

        private sealed class AsyncFlushChannel : ITelemetryChannel, IAsyncFlushable
        {
            private readonly bool _hang;

            public AsyncFlushChannel(bool hang)
            {
                _hang = hang;
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }
            public void Send(ITelemetry item) { }
            public void Flush() { }
            public void Dispose() { }

            public async Task<bool> FlushAsync(CancellationToken cancellationToken)
            {
                if (_hang) await Task.Delay(Timeout.Infinite, cancellationToken);
                return true;
            }
        }

        private sealed class RecordingChannel : ITelemetryChannel
        {
            private readonly object _gate = new object();
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();

            public IList<ITelemetry> Sent
            {
                get
                {
                    lock (_gate)
                    {
                        return _sent.ToList();
                    }
                }
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(ITelemetry item)
            {
                lock (_gate)
                {
                    _sent.Add(item);
                }
            }

            public void Flush()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
