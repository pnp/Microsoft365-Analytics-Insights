using App.ControlPanel.Engine.Models;
using App.ControlPanel.Frames;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using static App.ControlPanel.Frames.InstallWizard.InstallSolutionControl;

namespace App.ControlPanel
{
    /// <summary>
    /// Base ILogger implementation for installer.
    /// Each line is prefixed with "HH:mm:ss  LEVEL  " for consistency and easier scanning.
    /// Lines starting with "===" (phase headers / summary block) are emitted verbatim so they
    /// stand out visually in the log pane.
    /// </summary>
    abstract class SolutionInstallerLogger : BaseAnalyticsLogger
    {
        public override void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = string.Empty;
            if (formatter != null)
            {
                message += formatter(state, exception);
            }

            string formatted;
            if (string.IsNullOrEmpty(message))
            {
                // Allow callers to emit a blank separator line.
                formatted = string.Empty;
            }
            else if (message.StartsWith("=== ") && message.EndsWith(" ==="))
            {
                // Visual section header / summary block: pass through unprefixed.
                // Tight match on the leading + trailing sentinel so an Azure SDK error that
                // coincidentally contains "=== " doesn't get its timestamp/level stripped.
                formatted = message;
            }
            else
            {
                formatted = $"{DateTime.Now:HH:mm:ss}  {LevelLabel(logLevel)}  {message}";
            }

            var isErr = logLevel > LogLevel.Warning;
            AddToSolutionLog(formatted, isErr);
        }

        private static string LevelLabel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Information: return "INFO ";
                case LogLevel.Warning: return "WARN ";
                case LogLevel.Error: return "ERROR";
                case LogLevel.Critical: return "FATAL";
                default: return level.ToString().PadRight(5).Substring(0, 5);
            }
        }

        protected abstract void AddToSolutionLog(string msg, bool isErr);
    }

    /// <summary>
    /// Logs to InstallSPOSitesControl
    /// </summary>
    internal class InstallSPOSitesControlLogger : SolutionInstallerLogger
    {
        private readonly InstallSPOSitesControl _installSPOSitesControl;

        public InstallSPOSitesControlLogger(InstallSPOSitesControl installSPOSitesControl)
        {
            _installSPOSitesControl = installSPOSitesControl;
        }

        protected override void AddToSolutionLog(string msg, bool isErr)
        {
            _installSPOSitesControl.LogItemOnUIThread(new InstallLogLVI(new InstallLogEventArgs() { Text = msg, IsError = isErr }));
        }
    }

    internal class InMemoryLogger : SolutionInstallerLogger
    {
        private List<(string, bool)> _items = new List<(string, bool)>();
        protected override void AddToSolutionLog(string msg, bool isErr)
        {
            _items.Add((msg, isErr));
        }

        internal string GetMessages()
        {
            var all = string.Empty;
            foreach (var m in _items)
            {
                if (m.Item2)
                {
                    all += $"Error: {m.Item1}";
                }
                else
                {
                    all += $"{m.Item1}";
                }
                all += Environment.NewLine;
            }

            return all.TrimEnd(Environment.NewLine.ToCharArray());
        }
    }
}

