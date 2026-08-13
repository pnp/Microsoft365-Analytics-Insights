using App.ControlPanel.Engine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Decorates an <see cref="ILogger"/> so that every install message is also captured into a list,
    /// while still forwarding to the inner logger (e.g. the on-screen install log). The captured list is
    /// what gets registered into <c>sys_configs.messages</c> as the full installer log for that run.
    /// </summary>
    internal class InstallLogCapturingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly List<InstallLogEventArgs> _captured;

        public InstallLogCapturingLogger(ILogger inner, List<InstallLogEventArgs> captured)
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
}
