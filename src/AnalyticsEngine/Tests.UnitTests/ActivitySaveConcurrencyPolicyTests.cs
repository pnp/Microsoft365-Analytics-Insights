using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// The AUDIT_MAX_CONCURRENT_SAVES operator safety-valve, extracted by issue #373 so it is assertable
    /// without SQL Server. Default (1) is the original strictly-serial path against the single shared staging
    /// table; above 1 each save loads its own sharded staging table.
    ///
    /// Scope note: these tests cover the <b>policy</b>, which is what
    /// <c>ActivityReportSqlPersistenceManager</c> now asks. They do not observe the manager choosing a
    /// branch, taking the shared-write semaphore, or the merge SQL it emits - none of that is reachable
    /// without SQL Server until the collaborator split (#373 part 2) puts the staging writer and save
    /// session behind interfaces. #373's <c>Concurrency_ShardedMode_StillSerialisesSharedTableWrites</c>
    /// belongs to that part, not this one.
    /// </summary>
    [TestClass]
    public class ActivitySaveConcurrencyPolicyTests
    {
        [TestMethod]
        public void Concurrency_MaxConcurrentSavesOne_PolicySelectsSerialPathAndSharedStagingTable()
        {
            Assert.IsFalse(ActivitySaveConcurrencyPolicy.UseShardedStaging(1));

            // No shard => the merge SQL is pointed at the single shared staging table.
            Assert.AreEqual(ActivityImportConstants.STAGING_TABLE_ACTIVITY,
                ActivitySaveConcurrencyPolicy.EffectiveStagingTableName(null));
        }

        [TestMethod]
        public void Concurrency_UnsetOrInvalidMaxConcurrentSaves_PolicyFallsBackToTheSerialPath()
        {
            // A bad app setting must degrade to the safe default rather than break the import.
            Assert.AreEqual(1, ActivitySaveConcurrencyPolicy.NormaliseMaxConcurrentSaves(0));
            Assert.AreEqual(1, ActivitySaveConcurrencyPolicy.NormaliseMaxConcurrentSaves(-5));
            Assert.AreEqual(1, ActivitySaveConcurrencyPolicy.NormaliseMaxConcurrentSaves(1));
            Assert.AreEqual(4, ActivitySaveConcurrencyPolicy.NormaliseMaxConcurrentSaves(4));

            Assert.IsFalse(ActivitySaveConcurrencyPolicy.UseShardedStaging(0));
            Assert.IsFalse(ActivitySaveConcurrencyPolicy.UseShardedStaging(-5));
        }

        [TestMethod]
        public void Concurrency_MaxConcurrentSavesGreaterThanOne_PolicyIssuesADistinctShardedStagingTablePerSave()
        {
            Assert.IsTrue(ActivitySaveConcurrencyPolicy.UseShardedStaging(2));
            Assert.IsTrue(ActivitySaveConcurrencyPolicy.UseShardedStaging(16));

            var names = new HashSet<string>();
            for (var i = 0; i < 200; i++)
            {
                Assert.IsTrue(names.Add(ActivitySaveConcurrencyPolicy.NewShardedStagingTableName()),
                    "Two concurrent saves sharing a staging table name would interleave rows into one table.");
            }
        }

        [TestMethod]
        public void Concurrency_ShardedStagingTable_IsAGlobalTempTableNotTheDebugStagingTable()
        {
            // ActivityImportConstants.STAGING_TABLE_ACTIVITY is a permanent "debug_"-prefixed table in DEBUG
            // builds. The sharded name has always been a "##" global temp table regardless of configuration,
            // which is what makes it visible to the merge on the same connection and self-cleaning. Deriving
            // it from the constant would change that in one of the two configurations.
            //
            // Note this test only observes the configuration it is compiled in, and CI builds RELEASE ONLY
            // (the Debug matrix entry is commented out in ci.yml / pr.yml / tests.yml) - so the DEBUG case
            // is covered by local Debug runs, not by CI.
            var name = ActivitySaveConcurrencyPolicy.NewShardedStagingTableName();
            StringAssert.StartsWith(name, "##");
            StringAssert.StartsWith(name, ActivitySaveConcurrencyPolicy.ShardedStagingTablePrefix);
        }

        [TestMethod]
        public void Concurrency_ShardedMode_PolicyPointsTheMergeSqlAtTheSavesOwnShard()
        {
            var shard = ActivitySaveConcurrencyPolicy.NewShardedStagingTableName();
            Assert.AreEqual(shard, ActivitySaveConcurrencyPolicy.EffectiveStagingTableName(shard));
        }
    }
}
