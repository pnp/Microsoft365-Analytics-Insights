using Common.Entities.Config;
using Common.Entities.Installer;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    [TestClass]
    public class TeamsChannelBudgetTests
    {
        private static List<ChannelWithReactions> Channels(int count) => Enumerable.Range(0, count)
            .Select(i => new ChannelWithReactions { Id = $"channel-{i:D6}", DisplayName = "Contoso Καλημέρα" }).ToList();

        [TestMethod]
        public async Task ManyChannels_RoundRobinSurvivesRestartReorderAndDeletion()
        {
            var state = new InMemoryKeyValueStore();
            var logger = new BudgetLogger();
            var channels = Channels(5);
            var reads = new FakeChannelMessagesSourceLoader();
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var budget = new TeamsChannelBudgetProgress(state, 2, logger);
                await new TeamsChannelCrawler(reads, budget).PopulateNewMessagesAndReactions(channels, "contoso-team");
                await budget.CommitAfterPersistence();
                channels.Reverse();
            }
            CollectionAssert.AreEqual(new[] { "channel-000000", "channel-000001", "channel-000002",
                "channel-000003", "channel-000004", "channel-000000" }, reads.ChannelsRead);
            channels.RemoveAll(c => c.Id == "channel-000000");
            var resumed = new TeamsChannelBudgetProgress(state, 1, logger);
            await new TeamsChannelCrawler(reads, resumed).PopulateNewMessagesAndReactions(channels, "contoso-team");
            Assert.AreEqual("channel-000001", reads.ChannelsRead.Last(), "A deleted cursor channel resumes at its lexical successor.");
            Assert.AreEqual(4, logger.Messages.Count(m => m.Contains("TeamsChannelBudgetExhausted")));
            Assert.IsFalse(logger.Messages.Any(m => m.Contains("contoso-team") || m.Contains("Καλημέρα")));
        }

        [TestMethod]
        public async Task BudgetDoesNotPersistCursorDuringReads_AndIncompleteChannelDoesNotStarveOthers()
        {
            var state = new InMemoryKeyValueStore();
            var logger = new BudgetLogger();
            var source = new FakeChannelMessagesSourceLoader().ReturningNoToken("channel-000000")
                .ReturningToken("channel-000001", "complete");
            var first = new TeamsChannelBudgetProgress(state, 1, logger);
            var pending = await new TeamsChannelCrawler(source, first).PopulateNewMessagesAndReactions(Channels(2), "contoso-team");
            Assert.AreEqual(0, pending.Count);
            Assert.IsNull(await state.GetStringAsync("budget|contoso-team"));
            await first.CommitAfterPersistence();
            var second = new TeamsChannelBudgetProgress(state, 1, logger);
            pending = await new TeamsChannelCrawler(source, second).PopulateNewMessagesAndReactions(Channels(2), "contoso-team");
            Assert.AreEqual("channel-000001", pending.Single().ChannelId);
            Assert.AreEqual("complete", pending.Single().DeltaTokenInfo.Token);
            Assert.IsTrue(logger.Messages.Any(m => m.Contains("TeamsChannelReadIncomplete")));
        }

        [TestMethod]
        public async Task FailedReadDoesNotAdvanceCursorOrClaimBudgetExhaustion()
        {
            var state = new InMemoryKeyValueStore();
            var logger = new BudgetLogger();
            var budget = new TeamsChannelBudgetProgress(state, 1, logger);
            await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                new TeamsChannelCrawler(new FakeChannelMessagesSourceLoader().Failing("channel-000000"), budget)
                    .PopulateNewMessagesAndReactions(Channels(3), "contoso-team"));
            await budget.CommitAfterPersistence();
            Assert.IsNull(await state.GetStringAsync("budget|contoso-team"));
            Assert.IsTrue(logger.Messages.Any(m => m.Contains("TeamsChannelGraphFailure")));
            Assert.IsFalse(logger.Messages.Any(m => m.Contains("TeamsChannelBudgetExhausted")));
        }

        /// <summary>
        /// A channel this authorisation can't read (a private channel the person isn't in) is skipped, and scheduling
        /// moves past it - otherwise a budget of one would pick the same unreadable channel every cycle and starve the
        /// rest. Nothing was read from it, so it must not claim an incomplete read that could double-count statistics.
        /// </summary>
        [TestMethod]
        public async Task SkippedChannel_AdvancesTheCursor_WithoutClaimingAnIncompleteRead()
        {
            var state = new InMemoryKeyValueStore();
            var logger = new BudgetLogger();
            var source = new FakeChannelMessagesSourceLoader()
                .Failing("channel-000000", ChannelReadFailureKind.SkipChannel)
                .ReturningToken("channel-000001", "complete");

            var first = new TeamsChannelBudgetProgress(state, 1, logger);
            var pending = await new TeamsChannelCrawler(source, first).PopulateNewMessagesAndReactions(Channels(2), "contoso-team");
            await first.CommitAfterPersistence();

            Assert.AreEqual(0, pending.Count, "The skipped channel has no Graph checkpoint.");
            Assert.AreEqual("channel-000000", await state.GetStringAsync("budget|contoso-team"));
            Assert.IsFalse(logger.Messages.Any(m => m.Contains("TeamsChannelReadIncomplete") || m.Contains("TeamsChannelGraphFailure")));
            Assert.IsTrue(logger.Messages.Any(m => m.Contains("TeamsChannelReadSkipped")));

            var second = new TeamsChannelBudgetProgress(state, 1, logger);
            pending = await new TeamsChannelCrawler(source, second).PopulateNewMessagesAndReactions(Channels(2), "contoso-team");
            Assert.AreEqual("channel-000001", pending.Single().ChannelId, "The next cycle moves on to the next channel.");
        }

        [TestMethod]
        public async Task CanceledReadNeverPublishesProgress()
        {
            var state = new InMemoryKeyValueStore();
            var budget = new TeamsChannelBudgetProgress(state, 1, new BudgetLogger());
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                new TeamsChannelCrawler(new NoisyChannelSource { Cancel = true }, budget)
                    .PopulateNewMessagesAndReactions(Channels(2), "contoso-team"));
            await budget.CommitAfterPersistence();
            Assert.IsNull(await state.GetStringAsync("budget|contoso-team"));
        }

        [TestMethod]
        public async Task DisabledBudget_PreservesSourceOrderAndReadsEveryChannel()
        {
            var source = new FakeChannelMessagesSourceLoader();
            var channels = Channels(20);
            channels.Reverse();
            await new TeamsChannelCrawler(source).PopulateNewMessagesAndReactions(channels, "contoso-team");
            CollectionAssert.AreEqual(channels.Select(c => c.Id).ToArray(), source.ChannelsRead);
        }

        [TestMethod]
        public async Task NoisyChannel_IsNotSplitByBudget_AndNextCycleStartsAfterIt()
        {
            var state = new InMemoryKeyValueStore();
            var channels = Channels(1000);
            var source = new NoisyChannelSource();
            var first = new TeamsChannelBudgetProgress(state, 1, new BudgetLogger());
            var commits = await new TeamsChannelCrawler(source, first).PopulateNewMessagesAndReactions(channels, "contoso-team");
            Assert.AreEqual(10000, channels[0].Messages.Count, "Scheduling must never truncate a completed Graph read.");
            Assert.AreEqual(1, commits.Count);
            Assert.AreEqual("channel-000000", commits.Single().ChannelId);
            await first.CommitAfterPersistence();
            var second = new TeamsChannelBudgetProgress(state, 1, new BudgetLogger());
            commits = await new TeamsChannelCrawler(source, second).PopulateNewMessagesAndReactions(channels, "contoso-team");
            Assert.AreEqual("channel-000001", commits.Single().ChannelId);
            Assert.AreEqual(2, source.Reads);
        }

        [TestMethod]
        [DoNotParallelize]
        public void Config_OptInRoundTripsAndMissingRuntimeValuesRetainLegacyBehavior()
        {
            var previous = AnalyticsConfig.AppSettings["TeamsMaxChannelsPerTeamPerCycle"];
            try
            {
                foreach (var raw in new[] { null, "", " \t " })
                {
                    AnalyticsConfig.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", raw);
                    Assert.AreEqual(0, new AppConfig().TeamsMaxChannelsPerTeamPerCycle);
                }
                foreach (var value in new[] { 0, 8, int.MaxValue })
                {
                    AnalyticsConfig.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle",
                        value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Assert.AreEqual(value, new AppConfig().TeamsMaxChannelsPerTeamPerCycle);
                }
                Assert.AreEqual(0, JsonConvert.DeserializeObject<BaseSolutionInstallConfig>("{}").TeamsMaxChannelsPerTeamPerCycle);
                var config = new BaseSolutionInstallConfig { TeamsMaxChannelsPerTeamPerCycle = 8 };
                Assert.AreEqual(8, JsonConvert.DeserializeObject<BaseSolutionInstallConfig>(JsonConvert.SerializeObject(config)).TeamsMaxChannelsPerTeamPerCycle);
                config.TeamsMaxChannelsPerTeamPerCycle = -1;
                Assert.IsTrue(config.ValidatInputAndGetErrors().Any(e => e.Contains("TeamsMaxChannelsPerTeamPerCycle")));
            }

            finally { AnalyticsConfig.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", previous); }
        }

        [DataTestMethod]
        [DataRow("-1")]
        [DataRow("not-a-number")]
        [DataRow("2147483648")]
        [DataRow("1.5")]
        [DoNotParallelize]
        public void Config_PresentMalformedBudgetFailsWithNamedConfigurationError(string raw)
        {
            var previous = AnalyticsConfig.AppSettings["TeamsMaxChannelsPerTeamPerCycle"];
            try
            {
                AnalyticsConfig.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", raw);
                var error = Assert.ThrowsException<ConfigurationErrorsException>(() => new AppConfig());
                StringAssert.Contains(error.Message, "TeamsMaxChannelsPerTeamPerCycle");
            }
            finally { AnalyticsConfig.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", previous); }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(8)]
        [DataRow(32)]
        public async Task SyntheticScale_BoundsDeepReadsAcrossTwoHundredThousandChannels(int limit)
        {
            var channels = Channels(200000);
            var state = new InMemoryKeyValueStore();
            var source = new FakeChannelMessagesSourceLoader();
            foreach (var channel in channels) source.ReturningToken(channel.Id, "synthetic-complete");
            var timer = Stopwatch.StartNew();
            var startMemory = GC.GetTotalMemory(true);
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var budget = new TeamsChannelBudgetProgress(state, limit, new BudgetLogger());
                await new TeamsChannelCrawler(source, budget).PopulateNewMessagesAndReactions(channels, "contoso-team");
                await budget.CommitAfterPersistence();
            }
            timer.Stop();
            Assert.AreEqual(limit * 10, source.ChannelsRead.Count);
            Assert.AreEqual(limit * 10, source.ChannelsRead.Distinct().Count());
            Console.WriteLine($"Synthetic scheduling only: 200000 channels, 10 cycles, limit {limit}; {limit * 10} distinct deep reads; elapsed={timer.ElapsedMilliseconds}ms; managed retained delta={GC.GetTotalMemory(true) - startMemory} bytes. No Graph/SQL latency or payload-size claim.");
        }

        [TestMethod]
        public async Task SyntheticManyChannelComparison_MeasuresUnbudgetedAndCandidatePolicies()
        {
            foreach (var limit in new[] { 0, 1, 8, 32 })
            {
                var channels = Channels(1000);
                var state = new InMemoryKeyValueStore();
                var source = new FakeChannelMessagesSourceLoader();
                var timer = Stopwatch.StartNew();
                for (int cycle = 0; cycle < 10; cycle++)
                {
                    var budget = limit == 0 ? null : new TeamsChannelBudgetProgress(state, limit, new BudgetLogger());
                    await new TeamsChannelCrawler(source, budget).PopulateNewMessagesAndReactions(channels, "contoso-team");
                    if (budget != null) await budget.CommitAfterPersistence();
                }
                timer.Stop();
                var expected = (limit == 0 ? 1000 : limit) * 10;
                Assert.AreEqual(expected, source.ChannelsRead.Count);
                Console.WriteLine($"Synthetic 1000-channel comparison: limit={limit}; cycles=10; deep reads={source.ChannelsRead.Count}; elapsed={timer.ElapsedMilliseconds}ms. Fake completed in-memory work only, no network/storage time.");
            }
        }

        #region One failing channel must not stall its Team (F-1)

        private static FakeChannelMessagesSourceLoader FailingChannel(params string[] failing)
        {
            var source = new FakeChannelMessagesSourceLoader();
            foreach (var id in failing) source.Failing(id, ChannelReadFailureKind.ChannelFailed);
            return source;
        }

        [TestMethod]
        public async Task OneFailingChannel_IsSkipped_AndTheTeamsOtherChannelsAreStillRead()
        {
            var source = FailingChannel("channel-000001").ReturningToken("channel-000000", "t0").ReturningToken("channel-000002", "t2");
            var stats = new TeamsChannelCrawlStats();
            var logger = new BudgetLogger();

            var pending = await new TeamsChannelCrawler(source, null, null, stats, logger)
                .PopulateNewMessagesAndReactions(Channels(3), "contoso-team");

            CollectionAssert.AreEqual(new[] { "channel-000000", "channel-000001", "channel-000002" }, source.ChannelsRead.ToArray(),
                "A failing channel doesn't stop the channels after it.");
            CollectionAssert.AreEqual(new[] { "channel-000000", "channel-000002" }, pending.Select(p => p.ChannelId).ToArray(),
                "The failing channel has no checkpoint to commit.");
            Assert.AreEqual(2, stats.ChannelsRead);
            Assert.AreEqual(1, stats.ChannelsFailed);
            Assert.AreEqual("InvalidOperationException", stats.TopFailure);
            Assert.IsFalse(stats.Stopped);
            var line = logger.Messages.Single(m => m.StartsWith("TeamsChannelReadFailed:"));
            StringAssert.Contains(line, "teamId=contoso-team; channelId=channel-000001; status=none; code=none;");
            StringAssert.Contains(line, "consecutive=1");
        }

        [TestMethod]
        public async Task ThreeChannelsFailingInARow_IsGraphBeingDown_AndStopsTheTeamsCrawl()
        {
            var source = FailingChannel("channel-000001", "channel-000002", "channel-000003").ReturningToken("channel-000000", "t0");
            var stats = new TeamsChannelCrawlStats();
            var pending = new List<TeamChannelDeltaTokenCommit>();

            var ex = await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                new TeamsChannelCrawler(source, null, null, stats).PopulateNewMessagesAndReactions(Channels(5), "contoso-team", pending));

            Assert.AreEqual(ChannelReadFailureKind.ChannelFailed, ex.Kind);
            CollectionAssert.AreEqual(new[] { "channel-000000", "channel-000001", "channel-000002", "channel-000003" }, source.ChannelsRead.ToArray(),
                "Waiting out a timeout on every remaining channel of every Team is what an outage would cost.");
            CollectionAssert.AreEqual(new[] { "channel-000000" }, pending.Select(p => p.ChannelId).ToArray(), "Earlier checkpoints stay pending.");
            Assert.IsTrue(stats.Stopped);
        }

        [TestMethod]
        public async Task ASuccessBetweenFailures_ResetsTheRunOfFailures()
        {
            var source = FailingChannel("channel-000000", "channel-000001", "channel-000003", "channel-000004");
            await new TeamsChannelCrawler(source).PopulateNewMessagesAndReactions(Channels(5), "contoso-team");
            CollectionAssert.AreEqual(Channels(5).Select(c => c.Id).ToArray(), source.ChannelsRead.ToArray());
        }

        [TestMethod]
        public async Task ThrottlingStillStopsTheTeam_ItIsNotTheChannelsFault()
        {
            var source = new FakeChannelMessagesSourceLoader().Failing("channel-000000", ChannelReadFailureKind.Transient);
            await Assert.ThrowsExceptionAsync<ChannelMessagesReadException>(() =>
                new TeamsChannelCrawler(source).PopulateNewMessagesAndReactions(Channels(3), "contoso-team"));
            Assert.AreEqual(1, source.ChannelsRead.Count);
        }

        [TestMethod]
        public async Task AChannelThatKeepsFailing_HoldsTheBudgetCursorForTwoCycles_ThenTheCursorMovesPastIt_ItsTokenUntouched()
        {
            var state = new InMemoryKeyValueStore();
            var logger = new BudgetLogger();
            var source = FailingChannel("channel-000000").ReturningToken("channel-000001", "t1");
            var tokens = new InMemoryTeamChannelDeltaTokenStore();
            await tokens.SetDeltaToken("contoso-team", "channel-000000", new TeamChannelDeltaTokenInfo { Token = "resume-here" });

            async Task<List<TeamChannelDeltaTokenCommit>> Cycle()
            {
                var budget = new TeamsChannelBudgetProgress(state, 1, logger);
                var pending = await new TeamsChannelCrawler(source, budget, new TeamsChannelFailureLedger(state, logger), null, logger)
                    .PopulateNewMessagesAndReactions(Channels(2), "contoso-team");
                await budget.CommitAfterPersistence();
                return pending;
            }

            await Cycle();
            Assert.IsNull(await state.GetStringAsync("budget|contoso-team"), "One failure: a budget of one retries the channel, so a blip costs a cycle.");
            await Cycle();
            Assert.IsNull(await state.GetStringAsync("budget|contoso-team"));
            await Cycle();
            Assert.AreEqual("channel-000000", await state.GetStringAsync("budget|contoso-team"),
                "Failed " + TeamsChannelFailureLedger.MovePastAfterFailures + " times in a row: the cursor moves past it.");
            Assert.AreEqual("resume-here", (await tokens.GetDeltaToken("contoso-team", "channel-000000")).Token, "Its delta token is never advanced or dropped.");

            var next = await Cycle();
            Assert.AreEqual("channel-000001", next.Single().ChannelId, "The channel behind it is read again.");
            Assert.AreEqual(3, source.ChannelsRead.Count(id => id == "channel-000000"));
            Assert.IsTrue(logger.Messages.Any(m => m.Contains("TeamsChannelReadWithheld") && m.Contains("consecutiveFailures=3")));
            Assert.AreEqual(3, logger.Messages.Count(m => m.StartsWith("TeamsChannelReadFailed:")));
            StringAssert.Contains(logger.Messages.Last(m => m.StartsWith("TeamsChannelReadFailed:")), "consecutive=3");
        }

        [TestMethod]
        public async Task TheFailureCountIsKeptInTheChannelsPartition_ResetsOnSuccess_AndForgetsDeletedChannels()
        {
            var state = new InMemoryKeyValueStore();
            var logger = new BudgetLogger();

            var failing = FailingChannel("channel-000000", "channel-000001");
            await new TeamsChannelCrawler(failing, null, new TeamsChannelFailureLedger(state, logger)).PopulateNewMessagesAndReactions(Channels(2), "contoso-team");
            var row = await state.GetStringAsync("failures|contoso-team");
            Assert.IsNotNull(row);
            StringAssert.Contains(row, "\"channel-000000\":1");
            StringAssert.Contains(row, "\"channel-000001\":1");

            // channel-000001 was deleted; channel-000000 recovers: nothing left to remember, so the row goes.
            await new TeamsChannelCrawler(new FakeChannelMessagesSourceLoader(), null, new TeamsChannelFailureLedger(state, logger))
                .PopulateNewMessagesAndReactions(Channels(1), "contoso-team");
            Assert.IsNull(await state.GetStringAsync("failures|contoso-team"));
        }

        [TestMethod]
        public async Task AnUnreachableStateStore_CostsOnlyTheCounts_NeverTheCrawl()
        {
            var logger = new BudgetLogger();
            var source = FailingChannel("channel-000001").ReturningToken("channel-000000", "t0").ReturningToken("channel-000002", "t2");

            var pending = await new TeamsChannelCrawler(source, null, new TeamsChannelFailureLedger(new UnreachableStore(), logger))
                .PopulateNewMessagesAndReactions(Channels(3), "contoso-team");

            Assert.AreEqual(2, pending.Count);
            Assert.IsTrue(logger.Messages.Any(m => m.Contains("TeamsChannelFailureCountsUnavailable")));
        }

        private sealed class UnreachableStore : IKeyValueStore
        {
            public string Description => "unreachable";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
        }

        [DataTestMethod]
        [DataRow(0, 0, true)]
        [DataRow(2, 0, true)]
        [DataRow(2, 1, true)]
        [DataRow(0, 3, false)]
        public void ATeamWhoseEveryChannelFailedWasNotRefreshed(int read, int failed, bool refreshed)
        {
            var stats = new TeamsChannelCrawlStats { ChannelsRead = read, ChannelsFailed = failed };
            Assert.AreEqual(refreshed, O365Team.CrawlRefreshedTheTeam(stats));
        }

        [TestMethod]
        public async Task RecordChannelReadFailure_StampsTheTeamRefreshedOnlyIfAChannelWasRead()
        {
            var logger = new BudgetLogger();
            foreach (var read in new[] { 0, 2 })
            {
                var team = new O365Team { Id = "contoso-team", DisplayName = "Contoso", ChannelCrawlStats = new TeamsChannelCrawlStats { ChannelsRead = read } };
                await O365Team.RecordChannelReadFailure(team, new ChannelMessagesReadException(ChannelReadFailureKind.ChannelFailed, "synthetic", null), null, logger);

                Assert.IsTrue(team.HasRefreshToken, "The authorisation is kept.");
                Assert.AreEqual(read > 0, team.LastRefreshed.HasValue, "Nothing read, nothing refreshed: read=" + read);
            }
        }

        #endregion

        private sealed class BudgetLogger : ILogger
        {
            public List<string> Messages { get; } = new List<string>();
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter) => Messages.Add(formatter(state, exception));
        }

        private sealed class NoisyChannelSource : IChannelMessagesSourceLoader
        {
            public int Reads { get; private set; }
            public bool Cancel { get; set; }
            public Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
            {
                Reads++;
                if (Cancel) throw new OperationCanceledException();
                for (int i = 0; i < 10000; i++) channel.Messages.Add(new ChatMessage { Id = "synthetic-message-" + i });
                return Task.FromResult(new TeamChannelDeltaTokenInfo { Token = "synthetic-completed" });
            }
        }
    }
}
