using Common.Entities;
using DataUtils;
using System;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// Deterministic <see cref="IClock"/> for tests. See issue #368.
    /// </summary>
    public class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow)
        {
            if (utcNow.Kind == DateTimeKind.Local)
            {
                throw new ArgumentException("Use a UTC or unspecified instant - a local time makes the test machine's timezone part of the assertion.", nameof(utcNow));
            }
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        /// <summary>Moves the clock forward (or back, with a negative interval).</summary>
        public void Advance(TimeSpan by)
        {
            UtcNow = UtcNow.Add(by);
        }
    }

    /// <summary>
    /// <see cref="IAnalyticsDbContextFactory"/> that always throws, for exercising the failure paths of
    /// callers that must degrade gracefully rather than crash - without needing a database.
    /// </summary>
    public class ThrowingAnalyticsDbContextFactory : IAnalyticsDbContextFactory
    {
        private readonly Func<Exception> _exceptionFactory;

        public ThrowingAnalyticsDbContextFactory(string message = "simulated database failure")
            : this(() => new InvalidOperationException(message))
        {
        }

        public ThrowingAnalyticsDbContextFactory(Func<Exception> exceptionFactory)
        {
            _exceptionFactory = exceptionFactory ?? throw new ArgumentNullException(nameof(exceptionFactory));
        }

        /// <summary>How many times a caller tried to open a context.</summary>
        public int CreateAttempts { get; private set; }

        public AnalyticsEntitiesContext Create()
        {
            CreateAttempts++;
            throw _exceptionFactory();
        }
    }
}
