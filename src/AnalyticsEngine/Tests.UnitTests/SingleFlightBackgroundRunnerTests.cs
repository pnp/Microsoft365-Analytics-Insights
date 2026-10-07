using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// The runner that takes the deferred Graph pass - the once-a-day usage reports - off the import cycle's
    /// critical path (issue #706). The cycle must not wait for it, a second run must never start while one is in
    /// flight, and a failure must be logged rather than lost in a task nobody awaits.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class SingleFlightBackgroundRunnerTests
    {
        private const string RunnerName = "Deferred test import";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private static async Task<string> CaptureConsole(Func<Task> action)
        {
            var captured = new StringWriter();
            var original = Console.Out;
            Console.SetOut(TextWriter.Synchronized(captured));
            try
            {
                await action();
            }
            finally
            {
                Console.SetOut(original);
            }

            return captured.ToString();
        }

        private static async Task AwaitWithTimeout(Task task, string because)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Timeout));
            Assert.AreSame(task, winner, because);
            await task;
        }

        [TestMethod]
        public async Task TryStart_WhileARunIsInFlight_StartsNoSecondRun()
        {
            var runner = new SingleFlightBackgroundRunner(AnalyticsLogger.ConsoleOnlyTracer(), RunnerName);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRuns = 0;

            var output = await CaptureConsole(async () =>
            {
                Assert.IsTrue(runner.TryStart(async () =>
                {
                    started.SetResult(true);
                    await release.Task;
                }));
                await AwaitWithTimeout(started.Task, "The first run must start.");

                Assert.IsFalse(runner.TryStart(() =>
                {
                    Interlocked.Increment(ref secondRuns);
                    return Task.CompletedTask;
                }), "A run is in flight, so a later cycle must not start another.");
                Assert.IsTrue(runner.IsRunning);

                release.SetResult(true);
                await AwaitWithTimeout(runner.InFlight, "The first run must finish once released.");
            });

            Assert.AreEqual(0, secondRuns, "The second start must not have run its work at all.");
            StringAssert.Contains(output, RunnerName + " is still running from an earlier cycle",
                "Operators need to see why the usage reports did not start this cycle.");
        }

        [TestMethod]
        public async Task TryStart_AfterTheRunCompletes_StartsANewRunUnderANewOperation()
        {
            var runner = new SingleFlightBackgroundRunner(AnalyticsLogger.ConsoleOnlyTracer(), RunnerName);
            var runs = 0;
            Func<Task> work = () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            };

            var output = await CaptureConsole(async () =>
            {
                Assert.IsTrue(runner.TryStart(work));
                await AwaitWithTimeout(runner.InFlight, "The first run must finish.");

                Assert.IsFalse(runner.IsRunning);
                Assert.IsTrue(runner.TryStart(work), "Once the previous run has finished, the next cycle starts a new one.");
                await AwaitWithTimeout(runner.InFlight, "The second run must finish.");
            });

            Assert.AreEqual(2, runs);
            var operations = Regex.Matches(output, "started in the background as operation '([0-9a-f]{32})'")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.AreEqual(2, operations.Length, "Each run must log the operation its telemetry is recorded under.");
            Assert.AreNotEqual(operations[0], operations[1], "Each run must get its own telemetry operation.");
        }

        [TestMethod]
        public async Task TryStart_WorkThatThrows_IsLoggedAndTheTaskDoesNotFault()
        {
            var runner = new SingleFlightBackgroundRunner(AnalyticsLogger.ConsoleOnlyTracer(), RunnerName);

            var output = await CaptureConsole(async () =>
            {
                Assert.IsTrue(runner.TryStart(() => Task.FromException(new InvalidOperationException("faulted task"))));
                await AwaitWithTimeout(runner.InFlight, "A failing run must still complete.");
                Assert.IsFalse(runner.InFlight.IsFaulted, "Nothing awaits the task in Release, so it must never fault.");

                // A delegate that throws before returning a task is caught too.
                Assert.IsTrue(runner.TryStart(() => throw new InvalidOperationException("thrown synchronously")),
                    "A failed run must not stop the next cycle starting a new one.");
                await AwaitWithTimeout(runner.InFlight, "A run that throws synchronously must still complete.");
                Assert.IsFalse(runner.InFlight.IsFaulted);
            });

            StringAssert.Contains(output, "Got exception on " + RunnerName + ": faulted task");
            StringAssert.Contains(output, "Got exception on " + RunnerName + ": thrown synchronously");
        }

        [TestMethod]
        public async Task TryStart_ReturnsWithoutWaiting_AndTheInFlightTaskCanBeAwaited()
        {
            var runner = new SingleFlightBackgroundRunner(AnalyticsLogger.ConsoleOnlyTracer(), RunnerName);
            Assert.IsTrue(runner.InFlight.IsCompleted, "With nothing started, the in-flight task is already complete.");
            Assert.IsFalse(runner.IsRunning);

            var finished = false;
            using (var release = new ManualResetEventSlim(false))
            {
                await CaptureConsole(async () =>
                {
                    // The work blocks its thread before it ever awaits, as the real pass does while it builds its
                    // clients. TryStart must still return straight away: the import cycle must not wait for it.
                    Assert.IsTrue(runner.TryStart(() =>
                    {
                        release.Wait(Timeout);
                        finished = true;
                        return Task.CompletedTask;
                    }));

                    Assert.IsFalse(finished, "TryStart must return before the work has finished.");
                    Assert.IsTrue(runner.IsRunning);

                    release.Set();
                    await AwaitWithTimeout(runner.InFlight, "Awaiting the in-flight task waits for the run, as a Debug build does before it exits.");
                });
            }

            Assert.IsTrue(finished);
            Assert.IsFalse(runner.IsRunning);
        }
    }
}
