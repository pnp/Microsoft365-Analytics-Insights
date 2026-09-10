using Common.Entities;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
using WebJob.Office365ActivityImporter.Engine.Graph;
using Common.Entities.Config;
using WebJob.AppInsightsImporter.Engine;
using System.Reflection;
using System.Linq;
using UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the two ports introduced by issue #368 - <see cref="IClock"/> and
    /// <see cref="IAnalyticsDbContextFactory"/> - and their adapters.
    ///
    /// All of these run with zero SQL Server, Graph, Redis or Service Bus. Note that
    /// <c>DefaultAnalyticsDbContextFactory_Create_ReturnsDistinctContextPerCall</c> constructs EF6
    /// contexts but never queries: EF6 construction is lazy, so no connection is opened. It needs only
    /// the <c>SPOInsightsEntities</c> entry to exist in config.
    /// </summary>
    [TestClass]
    public class ClockAndContextFactoryTests
    {
        [TestMethod]
        public void FixedClock_Advance_MovesUtcNowByExactInterval()
        {
            var start = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var clock = new FixedClock(start);

            Assert.AreEqual(start, clock.UtcNow, "A fixed clock must not drift.");

            clock.Advance(TimeSpan.FromMinutes(90));
            Assert.AreEqual(start.AddMinutes(90), clock.UtcNow);

            clock.Advance(TimeSpan.FromMinutes(-30));
            Assert.AreEqual(start.AddMinutes(60), clock.UtcNow, "Advance must also accept a negative interval.");
        }

        [TestMethod]
        public void FixedClock_ReadTwice_ReturnsTheSameInstant()
        {
            // The whole point of the port: two reads inside one rule must not straddle a tick, which is
            // exactly the flakiness DateTime.UtcNow introduces into window arithmetic.
            var clock = new FixedClock(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

            Assert.AreEqual(clock.UtcNow, clock.UtcNow);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void FixedClock_LocalTime_IsRejected()
        {
            // A local instant would make the test machine's timezone part of every assertion.
            new FixedClock(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local));
        }

        [TestMethod]
        public void SystemClock_ReturnsUtcKind()
        {
            var now = SystemClock.Instance.UtcNow;

            Assert.AreEqual(DateTimeKind.Utc, now.Kind, "Import windows compare against UTC timestamps.");
            Assert.IsTrue(Math.Abs((DateTime.UtcNow - now).TotalMinutes) < 1, "Sanity: the system clock tracks wall time.");
        }

        [TestMethod]
        public void ConnectionStringAnalyticsDbContextFactory_BlankConnectionString_IsRejected()
        {
            foreach (var bad in new[] { null, string.Empty, "   " })
            {
                try
                {
                    new ConnectionStringAnalyticsDbContextFactory(bad);
                    Assert.Fail($"A blank connection string ('{bad}') must be rejected up front, not at first use.");
                }
                catch (ArgumentException)
                {
                    // expected
                }
            }
        }

        [TestMethod]
        public void DefaultAnalyticsDbContextFactory_Create_ReturnsDistinctContextPerCall()
        {
            // Callers dispose what they create, so returning a shared instance would mean the second
            // caller gets an already-disposed context.
            var factory = DefaultAnalyticsDbContextFactory.Instance;

            using (var first = factory.Create())
            using (var second = factory.Create())
            {
                Assert.IsNotNull(first);
                Assert.IsNotNull(second);
                Assert.AreNotSame(first, second);
            }
        }

        [TestMethod]
        public void ThrowingContextFactory_RecordsAttempts_AndThrows()
        {
            var factory = new ThrowingAnalyticsDbContextFactory("boom");

            try
            {
                factory.Create();
                Assert.Fail("Expected the fake to throw.");
            }
            catch (InvalidOperationException ex)
            {
                Assert.AreEqual("boom", ex.Message);
            }

            Assert.AreEqual(1, factory.CreateAttempts);
        }

        [TestMethod]
        public void EveryClockInjectedImporter_UsesTheInjectedClock_AndNeverLeavesItNull()
        {
            // Constructs all three importers for real and reads the field, rather than checking their
            // shape. An earlier version of this test only asserted "one ctor, takes an IClock, has an
            // IClock field" - which a constructor that accepts the clock and then forgets to assign it
            // would still have passed, i.e. it did not cover the bug it was written for.
            var clock = new FixedClock(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var settings = new AppConfig();

            var importers = new Func<IClock, object>[]
            {
                c => new AppInsightsImporter(null, AnalyticsLogger.ConsoleOnlyTracer(), c),

                // GraphServiceClient and friends are only stored, never dereferenced, by the constructor.
                c => new GraphImporter(logger, null, null, null, settings, clock: c),

                c => new CopilotInteractionHistoryImporter(
                        logger, settings,
                        new StubAiInteractionSourceLoader(),
                        null,   // cognitive enricher defaults internally
                        null,   // pilot group resolver is optional
                        new UserGroupsFilterModel(),
                        clock: c),
            };

            foreach (var build in importers)
            {
                var withClock = build(clock);
                Assert.AreSame(clock, ClockFieldOf(withClock),
                    $"{withClock.GetType().Name} must store the injected clock.");

                var withoutClock = build(null);
                Assert.AreSame(SystemClock.Instance, ClockFieldOf(withoutClock),
                    $"{withoutClock.GetType().Name} must fall back to SystemClock, never leave the field null.");
            }
        }

        private static FieldInfo ClockFieldInfoOf(Type t)
        {
            return t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(f => f.FieldType == typeof(IClock));
        }

        /// <summary>
        /// Minimal source loader - the importer's constructor only requires it to be non-null, and these
        /// tests never run an import.
        /// </summary>
        private class StubAiInteractionSourceLoader : IAiInteractionSourceLoader
        {
            public Task<bool> HasInteractionReadAccessAsync() => Task.FromResult(false);

            public Task<AiInteractionLoadResult> LoadInteractionsForUserAsync(Common.Entities.User user, DateTime fromUtc, DateTime toUtc)
                => Task.FromResult(new AiInteractionLoadResult());
        }

        private static IClock ClockFieldOf(object instance)
        {
            var field = ClockFieldInfoOf(instance.GetType());
            Assert.IsNotNull(field, $"{instance.GetType().Name} has no IClock field.");
            return (IClock)field.GetValue(instance);
        }
    }
}
