using Azure.Identity;
using Common.Entities;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UsageReporting;
using WebJob.Office365ActivityImporter.Engine.StatsUploader;

namespace Tests.UnitTests
{
    [TestClass]
    public class UsageStatsTests
    {
        /// <summary>
        /// Make sure we can crash a lot and still not affect the caller
        /// </summary>
        [TestMethod]
        public async Task UsageStatsReporterFakeAdaptorTest()
        {
            var tenantId = Guid.NewGuid();
            var tracer = AnalyticsLogger.ConsoleOnlyTracer();
            var r = new UsageStatsManager(new ShittyUsageStatsReporterAdaptor(tracer, tenantId),
                new ShittyDatesLoader(tracer), new FakeStatsUploader(tracer, true), tracer);


            var result = await r.ProcessAndFailSilently(); // Crash GetLastSettings
            Assert.IsFalse(result);
            await r.ProcessAndFailSilently();            // Null GetLastUploadDt
            await r.ProcessAndFailSilently();            // Crash LoadUsageStatsModel
            await r.ProcessAndFailSilently();            // Crash RegisterLastUploadDt
            await r.ProcessAndFailSilently();            // Crash SaveUsageStatsModelToDatabase
            result = await r.ProcessAndFailSilently();   // Work
            Assert.IsTrue(result);
        }

        /// <summary>
        /// Test the service adaptor here, just to make sure it works in the API as this project is part of DevOps pipeline.
        /// </summary>
        public async Task UsageStatsCosmosTelemetrySaveAdaptorTests()
        {
            var cosmosTestConfig = new TestConfig();
            if (!cosmosTestConfig.IsValid)
            {
                Assert.Fail("Invalid config for Cosmos DB");
            }

            var config = new Common.Entities.Config.AppConfig();
            var cosmosClient = new CosmosClient(cosmosTestConfig.CosmosConnectionString, new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret));
            var a = new CosmosTelemetrySaveAdaptor(cosmosClient, cosmosTestConfig);

            var tenantId = Guid.NewGuid();

            var model = AnonUsageStatsModelLoader.Load(tenantId, new BaseSolutionInstallConfig());

            // Not saved yet, so should be null
            var result = await a.LoadCurrentRecordByClientId(model);
            Assert.IsNull(result);

            await a.Init();
            await a.Init();     // Should be idempotent
            await a.SaveOrUpdate(model);

            try
            {
                await a.SaveOrUpdate(model);
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Expected. We're uploading the same stats twice
            }

            // We've saved now so should be something
            result = await a.LoadCurrentRecordByClientId(model);
            Assert.IsNotNull(result);
            Assert.AreEqual(result.AnonClientId, model.AnonClientId);

            // Clean up
            var db = cosmosClient.GetDatabase(cosmosTestConfig.DatabaseName);
            await db.DeleteAsync();
        }

        class TestConfig : IStatsServiceCosmosConfig
        {
            public TestConfig()
            {
                this.CosmosConnectionString = ConfigurationManager.AppSettings.Get("CosmosDb");
                this.ContainerNameCurrent = ConfigurationManager.AppSettings.Get("CosmosDbTestContainerCurrent");
                this.ContainerNameHistory = ConfigurationManager.AppSettings.Get("CosmosDbTestContainerHistory");
                this.DatabaseName = ConfigurationManager.AppSettings.Get("CosmosDbTestDatabaseName");
            }
            public bool IsValid => !string.IsNullOrEmpty(CosmosConnectionString) && !string.IsNullOrEmpty(DatabaseName) &&
                !string.IsNullOrEmpty(ContainerNameHistory) && !string.IsNullOrEmpty(ContainerNameCurrent);
            public string CosmosConnectionString { get; set; }
            public string DatabaseName { get; set; }
            public string ContainerNameHistory { get; set; }
            public string ContainerNameCurrent { get; set; }
        }

        /// <summary>
        /// Use real adaptor. Fake data in redis & SQL to get new stats set.
        /// </summary>
        [TestMethod]
        public async Task UsageStatsReporterRealTests()
        {
            var tenantId = Guid.NewGuid();
            var tracer = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = new AnalyticsEntitiesContext())
            {
                // Fake "last uploaded". Also test
                var randoDate = DateTime.UtcNow.AddYears(-12);
                var sqlStatsAdaptor = new SqlUsageStatsBuilder(db, tracer, tenantId);
                var redisDatesAdaptor = new RedisStatsDatesLoader(new Common.Entities.Config.AppConfig());

                await redisDatesAdaptor.RegisterLastUploadDt(randoDate);
                var randoDateResult = await redisDatesAdaptor.GetLastUploadDt();
                Assert.IsTrue(randoDateResult.HasValue && randoDateResult.Value == randoDate);

                // Clear out config. Stats should fail
                db.ConfigStates.RemoveRange(db.ConfigStates.ToList());
                await db.SaveChangesAsync();

                // Do everything for real except actually upload stats
                var r = new UsageStatsManager(sqlStatsAdaptor, redisDatesAdaptor, new FakeStatsUploader(tracer, false), tracer);

                var result = await r.ProcessAndUploadStats();   // Won't work because no config saved in DB
                Assert.IsFalse(result);

                // Add a config
                var cfg = new BaseSolutionInstallConfig()
                {
                    AllowTelemetry = true,
                    SolutionConfig = new TargetSolutionConfig()
                    {
                        SolutionTargeted = SolutionImportType.Adoptify,
                    }
                };
                db.ConfigStates.Add(new Common.Entities.Config.ConfigState
                {
                    ConfigJson = JsonConvert.SerializeObject(cfg),
                    DateApplied = DateTime.Now
                });
                await db.SaveChangesAsync();

                // Should now work
                result = await r.ProcessAndUploadStats();
                Assert.IsTrue(result);

                // Verify result saved in DB
                var latestReport = await sqlStatsAdaptor.GetLatestSavedDbStats();
                Assert.IsNotNull(latestReport);
                Assert.IsTrue(latestReport.TableStats.Count > 0);
                Assert.IsFalse(string.IsNullOrEmpty(latestReport.TableStats[0].TableName));
                Assert.IsTrue(latestReport.TableStats.Where(s => s.TotalSpaceMB > 0).Any());
                Assert.IsTrue(latestReport.TableStats.Where(s => s.Rows > 0).Any());
                Assert.IsTrue(latestReport.ConfiguredSolutionsEnabledDescription == "Adoptify");
                Assert.IsTrue(latestReport.ConfiguredImportsEnabledDescription == cfg.SolutionConfig.ImportTaskSettings.ToSettingsString());
            }
        }

        [TestMethod]
        public void AnonUsageStatsModelTests()
        {
            var tenantId = Guid.NewGuid();

            var statsModel1 = AnonUsageStatsModelLoader.Load(tenantId, null);
            var statsModel2 = AnonUsageStatsModelLoader.Load(tenantId, null);
            var statsModelDifferentId = AnonUsageStatsModelLoader.Load(Guid.NewGuid(), null);

            // Make sure we can resolve same tenant ID to same anon ID
            Assert.IsNotNull(statsModel1.AnonClientId);
            Assert.AreEqual(statsModel1.AnonClientId, statsModel2.AnonClientId);
            Assert.AreNotEqual(statsModelDifferentId.AnonClientId, statsModel2.AnonClientId);

            var m1 = new AnonUsageStatsModel()
            {
                AnonClientId = "123",
                BuildVersionLabel = "Build 1",
                DataPointsFromAITotal = 1
            };
            var m1Update = new AnonUsageStatsModel()
            {
                Generated = DateTime.Now,
                AnonClientId = "123",
                BuildVersionLabel = "Build 2",
                TableStats = new System.Collections.Generic.List<AnonUsageStatsModel.TableStat> { new AnonUsageStatsModel.TableStat { Rows = 1, TableName = "Whatevs" } }
            };

            var updated = m1.UpdateWith(m1Update);
            Assert.IsTrue(updated.TableStats.Count == 1);
            Assert.IsTrue(updated.TableStats[0].Rows == 1);
            Assert.IsTrue(updated.BuildVersionLabel == "Build 2");
            Assert.IsTrue(updated.DataPointsFromAITotal == 1);      // not updated as update didn't include

        }



        [TestMethod]
        public void AnonUsageStatsModelDecryptTests()
        {
            const string SECRET = "Test123";

            var tenantId = Guid.NewGuid();

            var statsModel1 = AnonUsageStatsModelLoader.Load(tenantId, null);
            Thread.Sleep(10); // Make sure there's difference between generated dates
            var statsModel2 = AnonUsageStatsModelLoader.Load(tenantId, null);

            Assert.AreNotEqual(statsModel1.Generated, statsModel2.Generated);

            var s = statsModel1.GenerateSecretFromObjectProps(SECRET);
            var sWrongSecret = statsModel1.GenerateSecretFromObjectProps(SECRET + "2");
            Assert.IsNotNull(s);

            // Same shared secret & model. Should work.
            Assert.IsTrue(statsModel1.IsValidSecretForThisObject(s, SECRET));

            // the secret from one model should not work for another model
            Assert.IsFalse(statsModel2.IsValidSecretForThisObject(s, SECRET));

            // ...or the wrong shared secret
            Assert.IsFalse(statsModel2.IsValidSecretForThisObject(sWrongSecret, SECRET));

        }

        [TestMethod]
        public async Task InMemoryStatsDatesLoader_PersistsAcrossCallsOnSameInstance()
        {
            // Program.cs hoists a single IStatsDatesLoader instance outside the import-cycle
            // while(runAgain) loop precisely so the in-memory fallback can throttle across
            // cycles. This test pins that contract: a single instance must survive Register +
            // Get round-trips.
            var loader = new InMemoryStatsDatesLoader();
            Assert.IsNull(await loader.GetLastUploadDt(), "Fresh loader should return null until something registers.");

            var before = DateTime.Now;
            await loader.RegisterLastUploadDt();
            var after = DateTime.Now;

            var seen = await loader.GetLastUploadDt();
            Assert.IsNotNull(seen, "Same-instance GetLastUploadDt must observe the just-registered timestamp.");
            Assert.IsTrue(seen.Value >= before && seen.Value <= after, $"Recorded timestamp {seen} should fall within [{before}, {after}].");
        }

        [TestMethod]
        public async Task InMemoryStatsDatesLoader_FreshInstancesAreIndependent()
        {
            // Conversely, two distinct instances must NOT share state. If Program.cs ever
            // regressed to constructing a new loader per import cycle, this guards us against
            // the cycle-N+1 "always thinks last upload was never" behaviour.
            var loaderA = new InMemoryStatsDatesLoader();
            await loaderA.RegisterLastUploadDt();
            Assert.IsNotNull(await loaderA.GetLastUploadDt());

            var loaderB = new InMemoryStatsDatesLoader();
            Assert.IsNull(await loaderB.GetLastUploadDt(),
                "Per-instance state: a separate loader instance must NOT see the timestamp written to loaderA. " +
                "Program.cs must construct exactly one loader for the process lifetime.");
        }

        [TestMethod]
        public async Task InMemoryStatsDatesLoader_HonoursMinWaitThrottleAcrossCycles()
        {
            // End-to-end: the SAME loader instance threaded through two UsageStatsManager
            // invocations (simulating two import cycles) should let the first through and
            // suppress the second via MIN_WAIT.
            var tracer = AnalyticsLogger.ConsoleOnlyTracer();
            var tenantId = Guid.NewGuid();
            var uploader = new FakeStatsUploader(tracer, false);

            // Single shared loader, mirroring Program.cs hoisting it outside the loop.
            var sharedLoader = new InMemoryStatsDatesLoader();

            var mgr1 = new UsageStatsManager(new AlwaysWorksUsageStatsBuilder(tracer, tenantId), sharedLoader, uploader, tracer);
            Assert.IsTrue(await mgr1.ProcessAndUploadStats(), "First cycle should upload.");

            var mgr2 = new UsageStatsManager(new AlwaysWorksUsageStatsBuilder(tracer, tenantId), sharedLoader, uploader, tracer);
            Assert.IsFalse(await mgr2.ProcessAndUploadStats(), "Second cycle (same loader) must be throttled by cycle 1's RegisterLastUploadDt.");
        }

        [TestMethod]
        public async Task UsageStatsManager_AllowTelemetryFalse_DoesNotUpload()
        {
            // Tenant operators can opt out of telemetry by setting AllowTelemetry=false on the
            // saved BaseSolutionInstallConfig. Pin that kill switch end-to-end: the manager
            // must NOT call the uploader, and ProcessAndUploadStats must return false so the
            // caller knows no upload happened.
            var tracer = AnalyticsLogger.ConsoleOnlyTracer();
            var tenantId = Guid.NewGuid();
            var loader = new InMemoryStatsDatesLoader();
            var uploader = new CountingStatsUploader();

            var builder = new AlwaysWorksUsageStatsBuilder(tracer, tenantId, allowTelemetry: false);
            var mgr = new UsageStatsManager(builder, loader, uploader, tracer);

            var result = await mgr.ProcessAndUploadStats();

            Assert.IsFalse(result, "AllowTelemetry=false must short-circuit to a non-uploaded result.");
            Assert.AreEqual(0, uploader.UploadCount, "Uploader must not be invoked when telemetry is disabled.");
            Assert.IsNull(await loader.GetLastUploadDt(), "RegisterLastUploadDt must not run when no upload happened — otherwise opt-out would silently throttle the next opt-in.");
        }

        [TestMethod]
        public async Task WebApiStatsUploader_BlankConfig_ThrowsInvalidOperationException()
        {
            // The uploader contract is "no URL or no secret = configuration error, throw". This is
            // by design: UsageStatsManager.ProcessAndFailSilently catches the throw and logs a
            // warning, but no upload happens. Pin both halves of that contract.
            var tracer = AnalyticsLogger.ConsoleOnlyTracer();
            var model = AnonUsageStatsModelLoader.Load(Guid.NewGuid(), null);

            using (var uploader = new WebApiStatsUploader(string.Empty, string.Empty, tracer))
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    async () => await uploader.UploadToServer(model),
                    "Empty URL + empty secret must throw — caller (ProcessAndFailSilently) relies on this to skip uploading when the operator hasn't configured StatsApiUrl/StatsApiSecret.");
            }

            using (var uploader = new WebApiStatsUploader("https://stats.example/", string.Empty, tracer))
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    async () => await uploader.UploadToServer(model),
                    "Empty secret alone must also throw — half-configured uploader is treated as misconfigured.");
            }
        }

        [TestMethod]
        public async Task UsageStatsManager_BlankUploaderConfig_FailsSilently()
        {
            // End-to-end: a misconfigured uploader must NOT escape ProcessAndFailSilently. The
            // importer's main loop calls ProcessAndFailSilently exactly so transient/config
            // failures in telemetry never break the import cycle.
            var tracer = AnalyticsLogger.ConsoleOnlyTracer();
            var tenantId = Guid.NewGuid();
            using (var uploader = new WebApiStatsUploader(string.Empty, string.Empty, tracer))
            {
                var mgr = new UsageStatsManager(new AlwaysWorksUsageStatsBuilder(tracer, tenantId), new InMemoryStatsDatesLoader(), uploader, tracer);
                var result = await mgr.ProcessAndFailSilently();
                Assert.IsFalse(result, "ProcessAndFailSilently must report failure but not throw when the uploader is misconfigured.");
            }
        }
    }

    // Always-works variant of the stats builder for end-to-end tests that don't want the
    // first-call crash behaviour of ShittyUsageStatsReporterAdaptor.
    internal class AlwaysWorksUsageStatsBuilder : BaseUsageStatsBuilder
    {
        private readonly bool _allowTelemetry;

        public AlwaysWorksUsageStatsBuilder(ILogger tracer, Guid tenantId, bool allowTelemetry = true) : base(tracer, tenantId)
        {
            _allowTelemetry = allowTelemetry;
        }

        public override Task<BaseSolutionInstallConfig> GetLastAppliedSolutionConfig()
            => Task.FromResult(new BaseSolutionInstallConfig { AllowTelemetry = _allowTelemetry });

        public override Task<AnonUsageStatsModel> LoadUsageStatsModel(BaseSolutionInstallConfig lastSettings)
            => Task.FromResult(AnonUsageStatsModelLoader.Load(_tenantId, lastSettings));

        public override Task SaveUsageStatsModelToDatabase(AnonUsageStatsModel latestStats) => Task.CompletedTask;
    }

    // Records how many times UploadToServer was invoked. Lets tests assert that a code path
    // genuinely skipped uploading (vs. just succeeding silently).
    internal class CountingStatsUploader : IStatsUploader
    {
        public int UploadCount { get; private set; }

        public Task UploadToServer(AnonUsageStatsModel stats)
        {
            UploadCount++;
            return Task.CompletedTask;
        }
    }

    // It crashes, a lot. By design. 
    internal class ShittyDatesLoader : IStatsDatesLoader
    {
        private readonly ILogger _tracer;
        private bool _returnNullGetLastUploadDt = true;
        private bool _crashRegisterLastUploadDt = true;

        public ShittyDatesLoader(ILogger tracer)
        {
            _tracer = tracer;
        }

        public Task<DateTime?> GetLastUploadDt()
        {
            if (_returnNullGetLastUploadDt)
            {
                _returnNullGetLastUploadDt = false;
                DateTime? dtNull = null;
                return Task.FromResult(dtNull);
            }
            _tracer.LogInformation($"{UsageStatsManager.LOG_PREFIX}got pretend stats uploaded date/time");

            DateTime? dt = DateTime.Now.AddDays(-2);
            return Task.FromResult(dt);
        }

        public Task RegisterLastUploadDt()
        {
            if (_crashRegisterLastUploadDt)
            {
                _crashRegisterLastUploadDt = false;
                throw new Exception();
            }

            _tracer.LogInformation($"{UsageStatsManager.LOG_PREFIX}pretend registered last stats upload date/time");
            return Task.CompletedTask;
        }
    }

    // It crashes, a lot. By design. 
    internal class ShittyUsageStatsReporterAdaptor : BaseUsageStatsBuilder
    {
        private bool _crashGetLastSettings = true;
        private bool _crashLoadUsageStatsModel = true;
        private bool _crashSaveUsageStatsModelToDatabase = true;

        public ShittyUsageStatsReporterAdaptor(ILogger tracer, Guid tenantId) : base(tracer, tenantId)
        {
        }

        public override Task<BaseSolutionInstallConfig> GetLastAppliedSolutionConfig()
        {
            if (_crashGetLastSettings)
            {
                _crashGetLastSettings = false;
                throw new Exception("Test crash");
            }
            _tracer.LogInformation($"{UsageStatsManager.LOG_PREFIX}got pretend last solution settings");

            return Task.FromResult(new BaseSolutionInstallConfig() { AllowTelemetry = true }); ;
        }

        public override Task<AnonUsageStatsModel> LoadUsageStatsModel(BaseSolutionInstallConfig lastSettings)
        {
            if (_crashLoadUsageStatsModel)
            {
                _crashLoadUsageStatsModel = false;
                throw new Exception();
            }
            _tracer.LogInformation($"{UsageStatsManager.LOG_PREFIX}pretend generated latest stats");

            return Task.FromResult(AnonUsageStatsModelLoader.Load(_tenantId, lastSettings));
        }


        public override Task SaveUsageStatsModelToDatabase(AnonUsageStatsModel latestStats)
        {
            if (_crashSaveUsageStatsModelToDatabase)
            {
                _crashSaveUsageStatsModelToDatabase = false;
                throw new Exception("crashed saving stats to DB");
            }
            _tracer.LogInformation($"{UsageStatsManager.LOG_PREFIX}pretend saved stats to DB");

            return Task.CompletedTask;
        }

    }

    internal class FakeStatsUploader : IStatsUploader
    {
        private readonly ILogger _tracer;
        private bool _crashUploadToServer = true;

        public FakeStatsUploader(ILogger tracer, bool crashFirstTime)
        {
            _tracer = tracer;
            _crashUploadToServer = crashFirstTime;
        }

        public Task UploadToServer(AnonUsageStatsModel latestStats)
        {
            if (_crashUploadToServer)
            {
                _crashUploadToServer = false;
                throw new Exception("crashed uploading to server");
            }
            _tracer.LogInformation($"{UsageStatsManager.LOG_PREFIX}pretend uploaded to stats");

            return Task.CompletedTask;
        }
    }
}
