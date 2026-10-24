using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace DataUtils
{
    /// <summary>
    /// Who this process is, for a log export that mixes several hosts, builds and restarts: the build label, the
    /// job name, and a per-process run id with the time the process started. A shared telemetry configuration stamps
    /// them on every item (<see cref="ProcessIdentityTelemetryInitializer"/>), so a query never has to guess which
    /// run a trace belongs to.
    /// </summary>
    public static class ProcessIdentity
    {
        private static string _jobName;
        private static string _buildLabel;

        /// <summary>Different for every process start, and the same for everything that process sends.</summary>
        public static string RunId { get; } = Guid.NewGuid().ToString("N");

        public static DateTime ProcessStartedUtc { get; } = ResolveProcessStartedUtc();

        public static string JobName => Volatile.Read(ref _jobName);
        public static string BuildLabel => Volatile.Read(ref _buildLabel);

        /// <summary>Called by a host once its configuration is read. Items sent before that carry only the run id.</summary>
        public static void Configure(string jobName, string buildLabel)
        {
            Volatile.Write(ref _jobName, jobName);
            Volatile.Write(ref _buildLabel, buildLabel);
        }

        /// <summary>Adds the identity properties that are known, without replacing any the item already has.</summary>
        public static void Stamp(IDictionary<string, string> properties)
        {
            if (properties == null) return;
            Add(properties, "RunId", RunId);
            Add(properties, "ProcessStartedUtc", ProcessStartedUtc.ToString("o", CultureInfo.InvariantCulture));
            Add(properties, "JobName", JobName);
            Add(properties, "BuildLabel", BuildLabel);
        }

        private static void Add(IDictionary<string, string> properties, string key, string value)
        {
            if (!string.IsNullOrEmpty(value) && !properties.ContainsKey(key)) properties[key] = value;
        }

        private static DateTime ResolveProcessStartedUtc()
        {
            try
            {
                return Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return DateTime.UtcNow;
            }
        }
    }

    /// <summary>Stamps <see cref="ProcessIdentity"/> on every telemetry item the configuration sends.</summary>
    public sealed class ProcessIdentityTelemetryInitializer : ITelemetryInitializer
    {
        public void Initialize(ITelemetry telemetry)
        {
            if (telemetry is ISupportProperties withProperties) ProcessIdentity.Stamp(withProperties.Properties);
        }
    }
}
