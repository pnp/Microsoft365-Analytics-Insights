using App.ControlPanel.Engine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Decorates an <see cref="ILogger"/> so that every install message is also captured into an <see cref="InstallLogCapture"/>,
    /// while still forwarding to the inner logger (e.g. the on-screen install log). The capture is what gets registered into
    /// <c>sys_configs.messages</c> as the full installer log for that run.
    /// </summary>
    internal class InstallLogCapturingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly InstallLogCapture _captured;

        public InstallLogCapturingLogger(ILogger inner, InstallLogCapture captured)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _captured = captured ?? throw new ArgumentNullException(nameof(captured));
        }

        public IDisposable BeginScope<TState>(TState state) => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            // Forward to the real logger first (UI / event log) so behaviour is unchanged.
            _inner.Log(logLevel, eventId, state, exception, formatter);

            if (formatter == null) return;
            var text = formatter(state, exception);
            if (string.IsNullOrEmpty(text)) return;

            _captured.Add(new InstallLogEventArgs { Text = text, IsError = logLevel >= LogLevel.Error });
        }
    }

    /// <summary>
    /// The messages logged during one install run, in order. Safe to add to from several threads at once: the downloaded
    /// control-panel app's standard output and standard error are each read on a thread of their own, and both log.
    /// </summary>
    internal sealed class InstallLogCapture
    {
        private readonly List<InstallLogEventArgs> _events = new List<InstallLogEventArgs>();
        private readonly object _lock = new object();

        public void Add(InstallLogEventArgs logEvent)
        {
            lock (_lock) _events.Add(logEvent);
        }

        /// <summary>A copy of what has been captured so far, to read or serialise while messages may still be arriving.</summary>
        public List<InstallLogEventArgs> Snapshot()
        {
            lock (_lock) return new List<InstallLogEventArgs>(_events);
        }
    }
}
