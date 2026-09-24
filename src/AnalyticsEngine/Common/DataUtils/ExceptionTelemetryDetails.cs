using System;
using System.Collections.Generic;

namespace DataUtils
{
    /// <summary>
    /// Opt-in contract for exceptions whose App Insights exception row needs curated,
    /// telemetry-safe dimensions rather than the raw exception message and log-template
    /// arguments.
    /// </summary>
    public interface IExceptionTelemetryDetails
    {
        Exception ToTelemetryException();
        string TelemetryProblemId { get; }
        IDictionary<string, string> TelemetryProperties { get; }
    }
}
