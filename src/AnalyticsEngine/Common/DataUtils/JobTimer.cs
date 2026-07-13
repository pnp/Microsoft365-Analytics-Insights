using System;
using System.Collections.Generic;
using System.Diagnostics;
using static DataUtils.AnalyticsLogger;

namespace DataUtils
{
    /// <summary>
    /// For tracking the time something takes
    /// </summary>
    public class JobTimer
    {
        private readonly AnalyticsLogger _logger;
        private readonly string _operationName;
        private readonly Stopwatch _sw;

        public JobTimer(AnalyticsLogger logger, string operationName)
        {
            _logger = logger;
            _operationName = operationName;
            _sw = new Stopwatch();
        }
        public void Start()
        {
            _sw.Start();
        }

        public TimeSpan Elapsed => _sw.Elapsed;
        public string OperationName => _operationName;

        public override string ToString()
        {
            var timeTaken = TimeSpan.FromMilliseconds(_sw.ElapsedMilliseconds);
            return FormatElapsed(_operationName, timeTaken);
        }

        /// <summary>
        /// Human-readable elapsed time. Days are only mentioned when the operation took a day or more,
        /// so a multi-day run isn't misreported as just its hours component (e.g. "1 days, 14 hours, ...").
        /// </summary>
        public static string FormatElapsed(string operationName, TimeSpan timeTaken)
        {
            var daysPart = timeTaken.Days > 0 ? $"{timeTaken.Days} days, " : string.Empty;
            return $"{operationName}: {daysPart}{timeTaken.Hours} hours, {timeTaken.Minutes} mins, and {timeTaken.Seconds} seconds.";
        }

        public string PrintElapsed()
        {
            var s = ToString();
            _logger.LogInformation(s);
            return s;
        }
        public string StopAndPrintElapsed()
        {
            _sw.Stop();

            var s = ToString();
            _logger.LogInformation(s);
            _sw.Reset();
            return s;
        }

        public void TrackFinishedEventAndStopTimer(AnalyticsEvent analyticsEvent)
        {
            var context = new Dictionary<string, string>
            {
                { "context", StopAndPrintElapsed() }
            };
            _logger.TrackEvent(analyticsEvent, context);
        }
    }
}
