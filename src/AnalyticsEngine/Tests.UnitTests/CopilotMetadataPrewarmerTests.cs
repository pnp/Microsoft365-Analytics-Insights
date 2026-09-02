using ActivityImporter.Engine.ActivityAPI.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot Graph pre-warm that runs before the audit-log save takes the SQL lock, split out of
    /// ActivityReportSqlPersistenceManager by issue #373 part 2. Zero Graph, zero SQL Server: the Graph
    /// client is now built by <see cref="ICopilotMetadataLoaderFactory"/>.
    ///
    /// CopilotPrewarmExtractionTests already covers which contexts are extracted and the
    /// <see cref="WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules.CopilotPrewarmPolicy.ShouldPrewarm"/>
    /// predicate in isolation. What could not be observed before, and is covered here, is the behaviour
    /// around them: that a disabled tenant really makes no outbound Copilot file lookup (it still builds the
    /// loader, which authenticates - the pre-warm is what is skipped), that a Graph auth failure is
    /// survivable and not retried per batch, and that the fan-out stays throttled.
    /// </summary>
    [TestClass]
    public class CopilotMetadataPrewarmerTests
    {
        private const string FileContextA = "https://contoso.sharepoint.com/sites/x/Καλημέρα κόσμε.docx";
        private const string FileContextB = "https://contoso.sharepoint.com/sites/x/b.docx";

        private static CopilotAuditLogContent CopilotEvent(string upn, params string[] fileContextIds)
            => new CopilotAuditLogContent
            {
                Id = Guid.NewGuid(),
                UserId = upn,
                CreationTime = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc),
                CopilotEventData = new CopilotEventData
                {
                    Contexts = fileContextIds.Select(id => new Context { Id = id, Type = "file" }).ToList()
                }
            };

        private static List<AbstractAuditLogContent> Activities(params AbstractAuditLogContent[] events)
            => events.ToList();

        [TestMethod]
        public async Task Prewarm_CopilotResourceResolutionDisabled_MakesNoGraphFileLookups()
        {
            // A tenant that has turned Copilot resource resolution off stages every Copilot event
            // agent-metadata-only, so the serial pass never asks Graph for a file. Warming would be pure
            // outbound Graph traffic for a cache nothing reads - and on a Copilot-heavy tenant that is
            // thousands of calls per cycle against a tenant that deliberately opted out.
            var graph = new RecordingCopilotMetadataLoader();
            var prewarmer = new CopilotMetadataPrewarmer(new FakeCopilotMetadataLoaderFactory(graph), new RecordingLogger());

            var loader = await prewarmer.GetLoaderAndPrewarmAsync(
                Activities(CopilotEvent("a@contoso.com", FileContextA), CopilotEvent("b@contoso.com", FileContextB)),
                resolveCopilotResourceMetadata: false);

            Assert.AreEqual(0, graph.FileLookups.Count, "Resolution is disabled: nothing may be pre-resolved from Graph.");
            Assert.AreSame(graph, loader, "The loader is still returned - the SaveSession is given it either way.");
        }

        [TestMethod]
        public async Task Prewarm_CopilotResourceResolutionEnabled_ResolvesEachDistinctContextOnceWithItsOwnUpn()
        {
            var graph = new RecordingCopilotMetadataLoader();
            var prewarmer = new CopilotMetadataPrewarmer(new FakeCopilotMetadataLoaderFactory(graph), new RecordingLogger());

            var loader = await prewarmer.GetLoaderAndPrewarmAsync(
                Activities(
                    CopilotEvent("a@contoso.com", FileContextA),
                    CopilotEvent("b@contoso.com", FileContextB),
                    CopilotEvent("c@contoso.com", FileContextA),      // same file as the first event
                    new SharePointAuditLogContent { Id = Guid.NewGuid(), UserId = "d@contoso.com" }),   // not a Copilot event at all
                resolveCopilotResourceMetadata: true);

            Assert.AreSame(graph, loader);
            Assert.AreEqual(2, graph.FileLookups.Count,
                "A context seen twice in a batch costs one Graph round-trip, not two - and a non-Copilot event costs none.");

            var byContext = graph.FileLookups.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Assert.AreEqual("a@contoso.com", byContext[FileContextA], "The first event to claim a context supplies the UPN Graph is asked with.");
            Assert.AreEqual("b@contoso.com", byContext[FileContextB]);
        }

        [TestMethod]
        public async Task Prewarm_LoaderFactoryFails_ReturnsNullAndWarnsAndSkipsThePrewarm()
        {
            // Building the run-scoped loader is best-effort: with no Graph credentials the import must
            // continue, with each SaveSession falling back to building its own loader.
            var factory = FakeCopilotMetadataLoaderFactory.FailingWith(new InvalidOperationException("no Graph creds"));
            var logger = new RecordingLogger();
            var prewarmer = new CopilotMetadataPrewarmer(factory, logger);

            var loader = await prewarmer.GetLoaderAndPrewarmAsync(
                Activities(CopilotEvent("a@contoso.com", FileContextA)),
                resolveCopilotResourceMetadata: true);

            Assert.IsNull(loader, "A failed build must degrade to null, not throw out of the save path.");

            var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual("Could not build a run-scoped Copilot metadata loader; falling back to per-batch loaders.", warnings[0].Message);
            Assert.IsInstanceOfType(warnings[0].Exception, typeof(InvalidOperationException),
                "The cause must reach the log, not just the fact that something failed.");
        }

        [TestMethod]
        public async Task Prewarm_LoaderIsBuiltAtMostOncePerCycle_EvenWhenTheFactoryFailed()
        {
            // Every save batch calls this. Retrying a broken Graph auth per batch would cost an
            // authentication round-trip - and a warning line - for each of the cycle's batches.
            var okFactory = new FakeCopilotMetadataLoaderFactory(new RecordingCopilotMetadataLoader());
            var okPrewarmer = new CopilotMetadataPrewarmer(okFactory, new RecordingLogger());
            var batch = Activities(CopilotEvent("a@contoso.com", FileContextA));

            await okPrewarmer.GetLoaderAndPrewarmAsync(batch, resolveCopilotResourceMetadata: true);
            await okPrewarmer.GetLoaderAndPrewarmAsync(batch, resolveCopilotResourceMetadata: true);
            Assert.AreEqual(1, okFactory.CreateCallCount, "The successful loader is built once and reused for the whole cycle.");

            var badFactory = FakeCopilotMetadataLoaderFactory.FailingWith(new InvalidOperationException("no Graph creds"));
            var badLogger = new RecordingLogger();
            var badPrewarmer = new CopilotMetadataPrewarmer(badFactory, badLogger);

            await badPrewarmer.GetLoaderAndPrewarmAsync(batch, resolveCopilotResourceMetadata: true);
            await badPrewarmer.GetLoaderAndPrewarmAsync(batch, resolveCopilotResourceMetadata: true);
            Assert.AreEqual(1, badFactory.CreateCallCount, "A FAILED build must not be retried on the next batch either.");
            Assert.AreEqual(1, badLogger.Entries.Count(e => e.Level == LogLevel.Warning), "...and must therefore warn only once.");
        }

        [TestMethod]
        public async Task Prewarm_OneContextFailingInGraph_DoesNotStopTheOthers()
        {
            // The prewarm is an optimisation; the authoritative resolution and its error reporting happen in
            // the serial ProcessExtendedProperties pass. One unresolvable file must not abort the batch.
            var graph = new RecordingCopilotMetadataLoader();
            graph.FailForContextIds.Add(FileContextA);
            var logger = new RecordingLogger();
            var prewarmer = new CopilotMetadataPrewarmer(new FakeCopilotMetadataLoaderFactory(graph), logger);

            await prewarmer.GetLoaderAndPrewarmAsync(
                Activities(CopilotEvent("a@contoso.com", FileContextA), CopilotEvent("b@contoso.com", FileContextB)),
                resolveCopilotResourceMetadata: true);

            Assert.AreEqual(2, graph.FileLookups.Count, "Both contexts are still attempted.");
            Assert.AreEqual(1, logger.Entries.Count(e => e.Level == LogLevel.Debug),
                "The failure is recorded at Debug - it is not an operator-actionable error here.");
            Assert.AreEqual(0, logger.Entries.Count(e => e.Level == LogLevel.Error || e.Level == LogLevel.Warning));
        }

        [TestMethod]
        public async Task Prewarm_FanOutIsThrottled_ButStillConcurrent()
        {
            // The pre-warm runs OUTSIDE the SQL lock precisely so it can overlap, but an unbounded fan-out
            // over a large batch would burst Graph and get the tenant throttled.
            var graph = new RecordingCopilotMetadataLoader { FileLookupDuration = TimeSpan.FromMilliseconds(150) };
            var prewarmer = new CopilotMetadataPrewarmer(new FakeCopilotMetadataLoaderFactory(graph), new RecordingLogger());

            var events = Enumerable.Range(0, 24)
                .Select(i => (AbstractAuditLogContent)CopilotEvent($"u{i}@contoso.com", $"https://contoso.sharepoint.com/sites/x/f{i}.docx"))
                .ToList();

            await prewarmer.GetLoaderAndPrewarmAsync(events, resolveCopilotResourceMetadata: true);

            Assert.AreEqual(24, graph.FileLookups.Count);
            Assert.IsTrue(graph.PeakConcurrentFileLookups > 1,
                $"The pre-warm must actually overlap its Graph calls; peak concurrency was {graph.PeakConcurrentFileLookups}.");
            // Asserted as the literal cap, not against CopilotMetadataPrewarmer.PrewarmConcurrency: comparing
            // the observed peak to the very constant that sizes the semaphore cannot fail when that constant
            // changes, which is exactly the value an operator would care about.
            Assert.IsTrue(graph.PeakConcurrentFileLookups <= 8,
                $"The fan-out must stay within 8 concurrent Graph lookups; peak concurrency was {graph.PeakConcurrentFileLookups}.");
        }
    }
}
