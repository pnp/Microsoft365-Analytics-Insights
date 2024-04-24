using Microsoft.Extensions.Logging;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    public abstract class ActivityReportLoader
    {
        internal ActivityReportLoader(ILogger telemetry)
        {
            this.Telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        public abstract string ReportGraphURL { get; }
        public ILogger Telemetry { get; set; }
    }
}
