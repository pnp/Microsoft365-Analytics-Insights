using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// A real <see cref="AnalyticsLogger"/> whose App Insights traces are kept in memory, for asserting on exactly what an
    /// importer logs - and at which severity - without a telemetry endpoint.
    /// </summary>
    internal sealed class CapturedAnalyticsLogger : IDisposable
    {
        private readonly Channel _channel = new Channel();
        private readonly TelemetryConfiguration _configuration;

        public CapturedAnalyticsLogger()
        {
            _configuration = new TelemetryConfiguration
            {
                TelemetryChannel = _channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };
            Logger = new AnalyticsLogger(new TelemetryClient(_configuration), "CapturedAnalyticsLogger");
        }

        public AnalyticsLogger Logger { get; }

        public List<TraceTelemetry> Traces => _channel.Sent.OfType<TraceTelemetry>().ToList();

        public List<string> Messages(SeverityLevel? severity = null)
            => Traces.Where(t => severity == null || t.SeverityLevel == severity).Select(t => t.Message).ToList();

        public void Dispose() => _configuration.Dispose();

        private sealed class Channel : ITelemetryChannel
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
