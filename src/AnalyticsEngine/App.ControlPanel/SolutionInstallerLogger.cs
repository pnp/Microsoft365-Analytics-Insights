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
    /// Base ILogger implementation for installer
    /// </summary>
    abstract class SolutionInstallerLogger : BaseAnalyticsLogger
    {
        public override void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = String.Empty;
            if (formatter != null)
            {
                message += formatter(state, exception);
            }
            var prefix = logLevel == LogLevel.Information ? string.Empty : $"{logLevel.ToString()} - ";
            var msg = $"{prefix}{message}";
            var isErr = logLevel > LogLevel.Warning;
            AddToSolutionLog(msg, isErr);
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
