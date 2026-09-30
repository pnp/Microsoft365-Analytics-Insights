using Common.Entities.Config;
using Common.Entities.UserOrgs;
using DataUtils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// Sends the CSV import's lifecycle events to Application Insights as <c>UserOrgCsvImport</c>
    /// custom events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queue-and-drain, like <c>LicenceActivityTelemetry</c>: recording an event is a bounded,
    /// non-blocking enqueue, and one background thread does the sending. A slow or unreachable
    /// telemetry endpoint therefore costs an import nothing, and a full queue drops events - counted in
    /// <c>DroppedEvents</c> - rather than stalling the worker.
    /// </para>
    /// <para>
    /// Every event is ids, codes, counts and timings. There is no user principal name, organisation
    /// name, file name or exception message anywhere in it, by construction: the event type has nowhere
    /// to put one.
    /// </para>
    /// <para>
    /// To follow one import, filter <c>customEvents</c> on <c>name == "UserOrgCsvImport"</c> and the
    /// <c>JobId</c> dimension, ordered by <c>Sequence</c>.
    /// </para>
    /// </remarks>
    internal sealed class UserOrgImportAppInsights : IUserOrgImportTelemetry
    {
        public static readonly UserOrgImportAppInsights Default = new UserOrgImportAppInsights();

        private static readonly string InstanceId = Guid.NewGuid().ToString("N");
        private static readonly ConcurrentDictionary<int, byte> InFlight = new ConcurrentDictionary<int, byte>();
        private static readonly BlockingCollection<Queued> Queue = new BlockingCollection<Queued>(512);
        private static readonly Lazy<Thread> Worker = new Lazy<Thread>(() =>
        {
            var thread = new Thread(Drain) { IsBackground = true, Name = "UserOrgImportTelemetry" };
            thread.Start();
            return thread;
        });

        private static long _sequence;
        private static int _dropped;
        private static int _stopping;

        private UserOrgImportAppInsights()
        {
        }

        public void Record(UserOrgImportTelemetryEvent item)
        {
            if (item == null)
            {
                return;
            }

            TrackInFlight(item);

            try
            {
                if (Volatile.Read(ref _stopping) == 0)
                {
                    _ = Worker.Value;
                    var queued = new Queued
                    {
                        Event = item,
                        Sequence = Interlocked.Increment(ref _sequence),
                        OccurredUtc = DateTimeOffset.UtcNow,
                    };
                    if (Queue.TryAdd(queued))
                    {
                        return;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Shutdown can complete the collection between the stopping check and TryAdd.
            }

            Interlocked.Increment(ref _dropped);
        }

        /// <summary>
        /// Records <c>HostStopping</c> for every import this process is running, then drains the queue.
        /// </summary>
        /// <remarks>
        /// The event is what separates "the web app was recycled under the import" from "the import
        /// hung" - and it tells whoever reads it that the job will be resumed after the restart, rather
        /// than leaving them to guess from a job that simply went quiet.
        /// </remarks>
        internal static void Shutdown()
        {
            foreach (var jobId in InFlight.Keys)
            {
                Default.Record(new UserOrgImportTelemetryEvent { Stage = UserOrgImportStages.HostStopping, JobId = jobId });
            }

            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }

            Queue.CompleteAdding();
            if (Worker.IsValueCreated && Thread.CurrentThread != Worker.Value)
            {
                Worker.Value.Join(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>Builds the event's dimensions and measurements. Separate so it can be tested.</summary>
        internal static void Describe(
            UserOrgImportTelemetryEvent item,
            long sequence,
            out Dictionary<string, string> dimensions,
            out Dictionary<string, double> measurements)
        {
            dimensions = new Dictionary<string, string>
            {
                { "Stage", item.Stage ?? "Unknown" },
                { "InstanceId", InstanceId },
            };
            Add(dimensions, "JobId", item.JobId);
            Add(dimensions, "OrgTypeId", item.OrgTypeId);
            if (item.Mode.HasValue)
            {
                dimensions.Add("Mode", item.Mode.Value == UserOrgImportMode.Replace ? "replace" : "merge");
            }
            if (!string.IsNullOrEmpty(item.Code))
            {
                dimensions.Add("Code", item.Code);
            }
            if (!string.IsNullOrEmpty(item.ExceptionType))
            {
                dimensions.Add("ExceptionType", item.ExceptionType);
            }

            measurements = new Dictionary<string, double>
            {
                { "Sequence", sequence },
                { "DroppedEvents", Volatile.Read(ref _dropped) },
            };
            Add(measurements, "DurationMs", item.DurationMs);
            Add(measurements, "Bytes", item.Bytes);
            Add(measurements, "Rows", item.Rows);
            Add(measurements, "RowsApplied", item.RowsApplied);
            Add(measurements, "RowsCleared", item.RowsCleared);
            Add(measurements, "RowsUnknownUpn", item.RowsUnknownUpn);
            Add(measurements, "RowsInvalid", item.RowsInvalid);
            Add(measurements, "Attempts", item.Attempts);
            Add(measurements, "Count", item.Count);
        }

        private static void TrackInFlight(UserOrgImportTelemetryEvent item)
        {
            if (!item.JobId.HasValue)
            {
                return;
            }

            switch (item.Stage)
            {
                case UserOrgImportStages.Claimed:
                    InFlight[item.JobId.Value] = 0;
                    break;
                case UserOrgImportStages.Succeeded:
                case UserOrgImportStages.Superseded:
                case UserOrgImportStages.Refused:
                case UserOrgImportStages.Failed:
                    InFlight.TryRemove(item.JobId.Value, out _);
                    break;
            }
        }

        private static void Drain()
        {
            AnalyticsLogger logger = null;
            try
            {
                foreach (var queued in Queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (logger == null)
                        {
                            logger = new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "UserOrgCsvImport");
                        }

                        Describe(queued.Event, queued.Sequence, out var dimensions, out var measurements);
                        var operationId = queued.Event.JobId.HasValue
                            ? "userorg-import-" + queued.Event.JobId.Value.ToString(CultureInfo.InvariantCulture)
                            : null;
                        logger.TrackEvent(
                            AnalyticsLogger.AnalyticsEvent.UserOrgCsvImport, dimensions, measurements, operationId, queued.OccurredUtc);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _dropped);
                    }
                }
            }
            finally
            {
                try
                {
                    logger?.Flush();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }

        private static void Add(Dictionary<string, string> target, string key, int? value)
        {
            if (value.HasValue)
            {
                target.Add(key, value.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void Add(Dictionary<string, double> target, string key, long? value)
        {
            if (value.HasValue)
            {
                target.Add(key, value.Value);
            }
        }

        private sealed class Queued
        {
            public UserOrgImportTelemetryEvent Event;
            public long Sequence;
            public DateTimeOffset OccurredUtc;
        }
    }
}
