using Microsoft.ApplicationInsights.DataContracts;
using System;
using System.Globalization;

namespace DataUtils
{
    /// <summary>
    /// The console half of <see cref="AnalyticsLogger"/>. App Service keeps console output as application logs and
    /// marks stdout as information and stderr as error, so a line must say when it happened and how serious it is,
    /// and errors must go to stderr or an error-level filter drops the very lines it is looking for.
    /// </summary>
    internal static class ConsoleLog
    {
        internal const string EventToken = "EVT";

        internal static string LevelToken(SeverityLevel level)
        {
            switch (level)
            {
                case SeverityLevel.Verbose: return "DBG";
                case SeverityLevel.Information: return "INF";
                case SeverityLevel.Warning: return "WRN";
                case SeverityLevel.Error: return "ERR";
                case SeverityLevel.Critical: return "CRT";
                default: return "INF";
            }
        }

        /// <summary>Error and Critical lines belong on stderr.</summary>
        internal static bool UsesStandardError(SeverityLevel level) => level == SeverityLevel.Error || level == SeverityLevel.Critical;

        /// <summary><c>2026-01-02T03:04:05.678Z [ERR] [op 1a2b3c4d] message</c>; the operation is left out when there is none.</summary>
        internal static string Format(DateTime utcNow, string levelToken, string operationId, string message)
        {
            var op = string.IsNullOrEmpty(operationId)
                ? string.Empty
                : " [op " + (operationId.Length > 8 ? operationId.Substring(0, 8) : operationId) + "]";
            return utcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                + " [" + levelToken + "]" + op + " " + message;
        }

        internal static void Write(SeverityLevel level, string operationId, string message, Exception exception)
        {
            var includeStack = UsesStandardError(level);
            var line = Format(DateTime.UtcNow, LevelToken(level), operationId,
                message + ExceptionDescriber.ConsoleDetail(exception, includeStack));
            (includeStack ? Console.Error : Console.Out).WriteLine(line);
        }

        internal static void WriteEvent(string operationId, string message)
        {
            Console.Out.WriteLine(Format(DateTime.UtcNow, EventToken, operationId, message));
        }
    }
}
