using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Severity classification for a captured summary entry.
    /// </summary>
    public enum SummaryEntryKind
    {
        Warning,
        Error
    }

    /// <summary>
    /// A single warning/error captured during installation, with optional component tag.
    /// </summary>
    public class SummaryEntry
    {
        public SummaryEntryKind Kind { get; set; }
        public string Component { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Aggregates warnings and errors raised during an install run and prints a final structured summary block.
    /// Thread-safe: callbacks like Process.OutputDataReceived / ErrorDataReceived fire on ThreadPool
    /// threads and capture WARN/ERROR via SummaryCapturingLogger, so the underlying entries list must be
    /// protected. We use a single lock around all reads/writes — entry rate is in the tens, not millions.
    /// </summary>
    public class InstallSummary
    {
        private readonly List<SummaryEntry> _entries = new List<SummaryEntry>();
        private readonly object _entriesLock = new object();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public IReadOnlyList<SummaryEntry> Entries
        {
            get { lock (_entriesLock) { return _entries.ToList(); } }
        }

        public int ErrorCount
        {
            get { lock (_entriesLock) { return _entries.Count(e => e.Kind == SummaryEntryKind.Error); } }
        }

        public int WarningCount
        {
            get { lock (_entriesLock) { return _entries.Count(e => e.Kind == SummaryEntryKind.Warning); } }
        }

        public TimeSpan Duration => _stopwatch.Elapsed;

        /// <summary>
        /// Add a warning with an explicit component tag (used by call sites that want a tidy entry).
        /// </summary>
        public void AddWarning(string component, string message)
        {
            lock (_entriesLock)
            {
                _entries.Add(new SummaryEntry { Kind = SummaryEntryKind.Warning, Component = component, Message = message });
            }
        }

        /// <summary>
        /// Add an error with an explicit component tag.
        /// </summary>
        public void AddError(string component, string message)
        {
            lock (_entriesLock)
            {
                _entries.Add(new SummaryEntry { Kind = SummaryEntryKind.Error, Component = component, Message = message });
            }
        }

        /// <summary>
        /// Auto-capture a log event. Component is inferred from message text via keyword heuristics.
        /// Suppresses only consecutive duplicates (a single failure that gets re-logged immediately) to
        /// keep the summary readable; same line appearing later in the run is still recorded so the
        /// repeat-count signal isn't lost.
        /// </summary>
        public void Capture(LogLevel level, string message)
        {
            if (level != LogLevel.Warning && level != LogLevel.Error && level != LogLevel.Critical) return;
            if (string.IsNullOrWhiteSpace(message)) return;

            var kind = level == LogLevel.Warning ? SummaryEntryKind.Warning : SummaryEntryKind.Error;
            // Allow longer messages for errors/critical so the actionable tail of a FATAL line
            // (e.g. exception class + message) isn't lopped off.
            var maxLen = kind == SummaryEntryKind.Error ? 500 : 240;
            var truncated = Truncate(message, maxLen);

            lock (_entriesLock)
            {
                var last = _entries.Count > 0 ? _entries[_entries.Count - 1] : null;
                if (last != null && last.Kind == kind && string.Equals(last.Message, truncated, StringComparison.Ordinal))
                {
                    return;
                }

                _entries.Add(new SummaryEntry
                {
                    Kind = kind,
                    Component = ComponentInference.Infer(message),
                    Message = truncated
                });
            }
        }

        /// <summary>
        /// Emit the structured summary block via the supplied logger.
        /// Pass the underlying (un-wrapped) logger so summary lines don't get re-captured.
        /// </summary>
        public void Print(ILogger logger, IEnumerable<string> nextSteps = null)
        {
            _stopwatch.Stop();
            var d = _stopwatch.Elapsed;
            var durationStr = d.TotalMinutes >= 1
                ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
                : $"{(int)d.TotalSeconds}s";

            // Snapshot entries under the lock to avoid mutation while we iterate.
            List<SummaryEntry> snapshot;
            lock (_entriesLock)
            {
                snapshot = _entries.ToList();
            }

            var errors = snapshot.Where(e => e.Kind == SummaryEntryKind.Error).ToList();
            var warnings = snapshot.Where(e => e.Kind == SummaryEntryKind.Warning).ToList();

            logger.LogInformation(string.Empty);
            logger.LogInformation("=== Install summary ===");
            logger.LogInformation($"Duration: {durationStr}");

            if (errors.Count == 0 && warnings.Count == 0)
            {
                logger.LogInformation("Errors: 0");
                logger.LogInformation("Warnings: 0");
            }
            else
            {
                if (errors.Count > 0)
                {
                    logger.LogInformation($"Errors ({errors.Count}):");
                    foreach (var e in errors)
                    {
                        logger.LogInformation("  - " + FormatEntry(e));
                    }
                }
                else
                {
                    logger.LogInformation("Errors: 0");
                }

                if (warnings.Count > 0)
                {
                    logger.LogInformation($"Warnings ({warnings.Count}):");
                    foreach (var e in warnings)
                    {
                        logger.LogInformation("  - " + FormatEntry(e));
                    }
                }
                else
                {
                    logger.LogInformation("Warnings: 0");
                }
            }

            var steps = (nextSteps ?? Enumerable.Empty<string>()).Concat(DeriveNextSteps(snapshot)).Distinct().ToList();
            if (steps.Count > 0)
            {
                logger.LogInformation("Next steps:");
                foreach (var s in steps)
                {
                    logger.LogInformation("  - " + s);
                }
            }

            logger.LogInformation("=== End of summary ===");
        }

        private static string FormatEntry(SummaryEntry e)
        {
            return string.IsNullOrEmpty(e.Component) ? e.Message : $"[{e.Component}] {e.Message}";
        }

        private static IEnumerable<string> DeriveNextSteps(IEnumerable<SummaryEntry> entries)
        {
            // Very small set of pattern → suggestion. Keep conservative so this stays useful, not noisy.
            foreach (var e in entries)
            {
                var msg = (e.Message ?? string.Empty).ToLowerInvariant();
                if (msg.Contains("vm is in") && (msg.Contains("deallocated") || msg.Contains("stopped")))
                {
                    yield return "Start the Hybrid Worker VM and re-run the installer.";
                }
                if (msg.Contains("appsecret") && msg.Contains("public network access"))
                {
                    yield return "If the runtime app-registration secret was rotated, temporarily allow public access on Key Vault and re-run; otherwise the existing secret in Key Vault is still valid.";
                }
                if (msg.Contains("warmup") || msg.Contains("warm-up"))
                {
                    yield return "If warmup failed, browse to the admin site URL manually to confirm the App Service is responding.";
                }
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    internal static class ComponentInference
    {
        // Order matters: more-specific keywords first.
        private static readonly (string keyword, string component)[] _map = new[]
        {
            ("hybrid runbook worker", "HybridWorker"),
            ("hybrid worker", "HybridWorker"),
            ("automation account", "Automation"),
            ("automation variable", "Automation"),
            ("automation schedule", "Automation"),
            ("runbook", "Automation"),
            ("key vault", "KeyVault"),
            ("keyvault", "KeyVault"),
            ("service bus", "ServiceBus"),
            ("service-bus", "ServiceBus"),
            ("servicebus", "ServiceBus"),
            ("private endpoint", "PrivateEndpoint"),
            ("private dns", "PrivateDns"),
            ("dns zone", "PrivateDns"),
            ("vnet", "Network"),
            ("subnet", "Network"),
            ("sql server", "Sql"),
            ("sql database", "Sql"),
            ("sql connection", "Sql"),
            ("storage account", "Storage"),
            ("storage-account", "Storage"),
            ("redis", "Redis"),
            ("cognitive service", "Cognitive"),
            ("text analytics", "Cognitive"),
            ("language analytics", "Cognitive"),
            ("app service", "AppService"),
            ("app-service", "AppService"),
            ("web-app", "AppService"),
            ("web app", "AppService"),
            ("warm-up", "AppService"),
            ("warmup", "AppService"),
            ("application insights", "AppInsights"),
            ("app insights", "AppInsights"),
            ("appinsights", "AppInsights"),
            ("log analytics", "AppInsights"),
            ("sharepoint", "SharePoint"),
            ("aitracker", "SharePoint"),
            ("sub-web", "SharePoint"),
            ("custom action", "SharePoint"),
            ("spfx", "SharePoint"),
            ("rbac", "RBAC"),
            ("role assignment", "RBAC"),
            ("role '", "RBAC"),
            ("database initialisation", "Database"),
            ("database initialization", "Database"),
            ("db init", "Database"),
        };

        public static string Infer(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var lower = message.ToLowerInvariant();
            foreach (var kv in _map)
            {
                if (lower.Contains(kv.keyword)) return kv.component;
            }
            return null;
        }
    }

    /// <summary>
    /// ILogger wrapper that forwards to an inner logger and feeds warnings/errors into an <see cref="InstallSummary"/>.
    /// Use this around the existing UI/console logger so every WARN/ERROR raised during an install run is recorded.
    /// </summary>
    public class SummaryCapturingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly InstallSummary _summary;

        public SummaryCapturingLogger(ILogger inner, InstallSummary summary)
        {
            _inner = inner;
            _summary = summary;
        }

        public IDisposable BeginScope<TState>(TState state) => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);

            if (logLevel == LogLevel.Warning || logLevel == LogLevel.Error || logLevel == LogLevel.Critical)
            {
                var msg = formatter != null ? formatter(state, exception) : (state != null ? state.ToString() : string.Empty);
                _summary.Capture(logLevel, msg);
            }
        }
    }
}
