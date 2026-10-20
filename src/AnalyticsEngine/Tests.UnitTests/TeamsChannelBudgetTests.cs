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
            var previous = ConfigurationManager.AppSettings["TeamsMaxChannelsPerTeamPerCycle"];
            try
            {
                foreach (var raw in new[] { null, "", " \t " })
                {
                    ConfigurationManager.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", raw);
                    Assert.AreEqual(0, new AppConfig().TeamsMaxChannelsPerTeamPerCycle);
                }
                foreach (var value in new[] { 0, 8, int.MaxValue })
                {
                    ConfigurationManager.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle",
                        value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Assert.AreEqual(value, new AppConfig().TeamsMaxChannelsPerTeamPerCycle);
                }
                Assert.AreEqual(0, JsonConvert.DeserializeObject<BaseSolutionInstallConfig>("{}").TeamsMaxChannelsPerTeamPerCycle);
                var config = new BaseSolutionInstallConfig { TeamsMaxChannelsPerTeamPerCycle = 8 };
                Assert.AreEqual(8, JsonConvert.DeserializeObject<BaseSolutionInstallConfig>(JsonConvert.SerializeObject(config)).TeamsMaxChannelsPerTeamPerCycle);
                config.TeamsMaxChannelsPerTeamPerCycle = -1;
                Assert.IsTrue(config.ValidatInputAndGetErrors().Any(e => e.Contains("TeamsMaxChannelsPerTeamPerCycle")));
            }

            finally { ConfigurationManager.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", previous); }
        }

        [DataTestMethod]
        [DataRow("-1")]
        [DataRow("not-a-number")]
        [DataRow("2147483648")]
        [DataRow("1.5")]
        [DoNotParallelize]
        public void Config_PresentMalformedBudgetFailsWithNamedConfigurationError(string raw)
        {
            var previous = ConfigurationManager.AppSettings["TeamsMaxChannelsPerTeamPerCycle"];
            try
            {
                ConfigurationManager.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", raw);
                var error = Assert.ThrowsException<ConfigurationErrorsException>(() => new AppConfig());
                StringAssert.Contains(error.Message, "TeamsMaxChannelsPerTeamPerCycle");
            }
            finally { ConfigurationManager.AppSettings.Set("TeamsMaxChannelsPerTeamPerCycle", previous); }
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
