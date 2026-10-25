using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>An <see cref="ILogger"/> that keeps what it was asked to write, for asserting on log lines.</summary>
    internal sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _lines = new List<(LogLevel, string)>();

        internal IReadOnlyList<(LogLevel Level, string Message)> Lines => _lines;

        internal string Single(LogLevel level) => _lines.Where(l => l.Level == level).Select(l => l.Message).Single();

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            _lines.Add((logLevel, formatter(state, exception)));
    }
}
