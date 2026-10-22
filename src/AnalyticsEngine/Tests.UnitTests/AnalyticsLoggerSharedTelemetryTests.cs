extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Common.Entities.Models;
using DataUtils;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
