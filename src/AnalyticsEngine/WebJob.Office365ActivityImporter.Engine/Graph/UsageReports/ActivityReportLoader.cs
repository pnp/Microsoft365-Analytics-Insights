using Microsoft.Extensions.Logging;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    public abstract class ActivityReportLoader
    {
        internal ActivityReportLoader(ILogger logger)
        {
            this.Telemetry = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public abstract string ReportGraphURL { get; }
        public ILogger Telemetry { get; set; }
    }
}
