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
        [TestMethod]
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
