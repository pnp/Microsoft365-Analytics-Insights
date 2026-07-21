using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common.Entities.Config;
using Tests.UnitTests.FailureHandling;
using Tests.UnitTests.StressHarness;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests
{
    /// <summary>
    /// Reproduces the audit-import failure conditions seen in a customer's production tenant - most importantly
    /// a transient
    /// "the connection is broken ... unrecoverable" SqlException thrown during a batch save - and proves the
    /// new retry + batch-isolation logic keeps the import cycle running to completion instead of aborting it.
    ///
    /// These drive the REAL <see cref="WebJob.Office365ActivityImporter.Engine.ActivityAPI.ActivityImporter{T}.LoadReportsAndSave"/>
    /// pipeline with fake loaders / saver (see AuditImportFailureHarness.cs), so no SQL Server or Activity API
    /// is required. <see cref="PermanentTransientFailure_BatchIsolated_CycleCompletes_BlobNotCheckpointed"/> is
    /// the key regression guard: before the fix a single failed batch aborted the whole cycle, so that test
    /// would throw (exactly as production did) rather than complete.
    /// </summary>
    [TestClass]
    public class AuditImportFailureHandlingTests
    {
        private const int EventsPerBlob = 1;   // 1 event per blob + 1 save-per-batch => each batch == one blob
        private const int MaxSavesPerBatch = 1;

        private static AppConfig BuildConfig() =>
            // Fake saver never touches the DB; the connection string just has to be non-empty/parseable.
            StressAppConfigFactory.Create(@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=fake;Integrated Security=true");

        private static FailureTestActivityImporter BuildImporter(int blobCount, RecordingProcessedBlobStore store,
            int maxConcurrentSaves = 1, int maxAttempts = 4)
        {
            return new FailureTestActivityImporter(BuildConfig(), AnalyticsLogger.ConsoleOnlyTracer(),
                blobCount, EventsPerBlob, MaxSavesPerBatch, store, maxConcurrentSaves, maxAttempts);
        }

        private static string Blob(int i) => FailureTestContentMetaDataLoader.BlobId(i);

        [TestMethod]
        public async Task HealthyRun_AllBlobsImportedAndCheckpointed()
        {
            var store = new RecordingProcessedBlobStore();
            var pm = new FailureInjectingPersistenceManager();

            var stats = await BuildImporter(blobCount: 6, store).LoadReportsAndSave(pm);

            Assert.AreEqual(0, stats.FailedBatches);
            Assert.AreEqual(6, stats.Imported);
            Assert.AreEqual(6, store.ProcessedCount, "every blob should be checkpointed on a clean run");
        }

        /// <summary>
        /// The production condition: a batch save hits a transient dropped/unrecoverable connection. Pre-fix this
        /// aborted the whole cycle; now it is retried and the data still lands.
        /// </summary>
        [TestMethod]
        public async Task TransientConnectionBroken_RetriedAndRecovers_NoAbort_AllImported()
        {
            var store = new RecordingProcessedBlobStore();
            var failing = Blob(2);
            var pm = new FailureInjectingPersistenceManager(
                transientFailuresBeforeSuccess: new Dictionary<string, int> { [failing] = 1 }); // fail once, then succeed

            var stats = await BuildImporter(blobCount: 5, store, maxAttempts: 4).LoadReportsAndSave(pm);

            Assert.AreEqual(0, stats.FailedBatches, "a transient blip that recovers must not be counted as a failure");
            Assert.AreEqual(5, stats.Imported, "all five blobs' events should import (the flaky one after a retry)");
            Assert.AreEqual(2, pm.AttemptsFor(failing), "the flaky blob should have been attempted twice (1 fail + 1 success)");
            Assert.IsTrue(store.IsProcessed(failing), "a recovered blob is fully committed, so it should be checkpointed");
            Assert.AreEqual(5, store.ProcessedCount);
        }

        /// <summary>
        /// KEY REGRESSION GUARD. A batch that keeps failing with a transient fault is retried up to the limit,
        /// then isolated (logged + counted) so the rest of the cycle completes. Before the retry/isolation fix
        /// this exception propagated out of LoadReportsAndSave and aborted the whole cycle - i.e. this test
        /// would throw exactly as it did in production.
        /// </summary>
        [TestMethod]
        public async Task PermanentTransientFailure_BatchIsolated_CycleCompletes_BlobNotCheckpointed()
        {
            var store = new RecordingProcessedBlobStore();
            var failing = Blob(2);
            var pm = new FailureInjectingPersistenceManager(
                transientFailuresBeforeSuccess: new Dictionary<string, int> { [failing] = int.MaxValue }); // never recovers

            // Must complete normally (no throw) despite the un-recoverable batch.
            var stats = await BuildImporter(blobCount: 5, store, maxAttempts: 3).LoadReportsAndSave(pm);

            Assert.AreEqual(1, stats.FailedBatches, "the un-saveable batch should be isolated and counted");
            Assert.AreEqual(1, stats.FailedBatchEvents);
            Assert.AreEqual(4, stats.Imported, "the other four blobs must still import");
            Assert.AreEqual(3, pm.AttemptsFor(failing), "the transient fault should be retried up to maxAttempts before giving up");
            Assert.IsFalse(store.IsProcessed(failing), "a failed batch's blob must NOT be checkpointed, so it retries next cycle");
            Assert.AreEqual(4, store.ProcessedCount, "only the four successful blobs are checkpointed");
        }

        /// <summary>
        /// A constraint violation (e.g. the concurrent-save PK duplicate) is deterministic - retrying just
        /// fails again - so it must be isolated immediately, not retried.
        /// </summary>
        [TestMethod]
        public async Task ConstraintViolation_NotRetried_IsolatedImmediately_CycleCompletes()
        {
            var store = new RecordingProcessedBlobStore();
            var failing = Blob(1);
            var pm = new FailureInjectingPersistenceManager(
                constraintFailBlobs: new HashSet<string> { failing });

            var stats = await BuildImporter(blobCount: 4, store, maxAttempts: 4).LoadReportsAndSave(pm);

            Assert.AreEqual(1, stats.FailedBatches);
            Assert.AreEqual(1, pm.AttemptsFor(failing), "a non-transient constraint violation must NOT be retried");
            Assert.AreEqual(3, stats.Imported);
            Assert.IsFalse(store.IsProcessed(failing));
        }

        /// <summary>
        /// Worst case: every batch fails. The cycle must still complete without throwing, import nothing, and
        /// checkpoint nothing (so it all retries next cycle) - rather than crashing the importer.
        /// </summary>
        [TestMethod]
        public async Task AllBatchesFail_CycleStillCompletes_NothingImportedOrCheckpointed()
        {
            var store = new RecordingProcessedBlobStore();
            var fails = new Dictionary<string, int>();
            for (int i = 0; i < 4; i++) fails[Blob(i)] = int.MaxValue;
            var pm = new FailureInjectingPersistenceManager(transientFailuresBeforeSuccess: fails);

            var stats = await BuildImporter(blobCount: 4, store, maxAttempts: 2).LoadReportsAndSave(pm);

            Assert.AreEqual(4, stats.FailedBatches);
            Assert.AreEqual(0, stats.Imported);
            Assert.AreEqual(0, store.ProcessedCount, "nothing durably saved => nothing checkpointed");
        }

        /// <summary>
        /// Concurrent-save mode (AUDIT_MAX_CONCURRENT_SAVES &gt; 1 - the configuration that failed in production):
        /// batches
        /// commit in parallel. A couple of un-recoverable batches must still be isolated without aborting the
        /// cycle or corrupting the checkpoint of the good ones.
        /// </summary>
        [TestMethod]
        public async Task ConcurrentSaveMode_TransientFailuresIsolated_CycleCompletes()
        {
            var store = new RecordingProcessedBlobStore();
            var fails = new Dictionary<string, int>
            {
                [Blob(1)] = int.MaxValue,
                [Blob(4)] = int.MaxValue,
            };
            var pm = new FailureInjectingPersistenceManager(transientFailuresBeforeSuccess: fails);

            var stats = await BuildImporter(blobCount: 8, store, maxConcurrentSaves: 3, maxAttempts: 2).LoadReportsAndSave(pm);

            Assert.AreEqual(2, stats.FailedBatches, "both un-saveable batches should be isolated");
            Assert.AreEqual(6, stats.Imported, "the other six blobs import fine in parallel");
            Assert.IsFalse(store.IsProcessed(Blob(1)));
            Assert.IsFalse(store.IsProcessed(Blob(4)));
            Assert.AreEqual(6, store.ProcessedCount);
        }
    }
}
