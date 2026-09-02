using Common.Entities.Config;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeEntities;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests.FailureHandling
{
    /// <summary>
    /// Deterministic, DB-free harness that drives the REAL <see cref="ActivityImporter{T}.LoadReportsAndSave"/>
    /// orchestration so we can reproduce the exact conditions that broke the audit import in a customer's
    /// production tenant (a transient "the connection is broken ... unrecoverable" SqlException thrown mid-save
    /// that aborted the whole cycle) and prove the retry + batch-isolation logic now handles them.
    ///
    /// Everything below the importer is faked: the loaders emit N content blobs (one event each), and the
    /// persistence manager (<see cref="FailureInjectingPersistenceManager"/>) fails whichever blobs the test
    /// tells it to - so no SQL Server or Activity API is needed and the failure modes are exact and repeatable.
    /// </summary>
    public class FailureTestActivityImporter : ActivityImporter<ActivityReportInfo>
    {
        private readonly FailureTestContentMetaDataLoader _meta;
        private readonly FailureTestReportLoader _reports;
        private readonly FakeActivitySubscriptionManager _subs = new FakeActivitySubscriptionManager();
        private readonly int _maxAttempts;

        public FailureTestActivityImporter(AppConfig settings, AnalyticsLogger logger, int blobCount,
            int eventsPerBlob, int maxSavesPerBatch, IProcessedBlobStore store, int maxConcurrentSaves, int maxAttempts)
            : base(settings, logger, maxSavesPerBatch, store, maxConcurrentSaves)
        {
            _meta = new FailureTestContentMetaDataLoader(logger, settings, blobCount, eventsPerBlob);
            _reports = new FailureTestReportLoader(eventsPerBlob);
            _maxAttempts = maxAttempts;
        }

        // Keep the retry count controllable and the backoff at ~0 so a permanent-failure test doesn't sit
        // through the production 3s / 12s / 48s backoff.
        protected override int BatchSaveMaxAttempts => _maxAttempts;
        protected override TimeSpan BatchSaveRetryBaseDelay => TimeSpan.FromMilliseconds(1);

        public override IActivityReportLoader<ActivityReportInfo> ReportLoader => _reports;
        public override ContentMetaDataLoader<ActivityReportInfo> ContentMetaDataLoader => _meta;
        public override IActivitySubscriptionManager ActivitySubscriptionManager => _subs;
    }

    /// <summary>Emits <c>blobCount</c> content-blob summaries exactly once, each with a stable BlobId.</summary>
    public class FailureTestContentMetaDataLoader : ContentMetaDataLoader<ActivityReportInfo>
    {
        public const string BlobIdPrefix = "fail-test-blob-";
        public static string BlobId(int i) => BlobIdPrefix + i;

        private readonly int _blobCount;
        private int _emitted; // 0 = not yet, 1 = emitted

        public FailureTestContentMetaDataLoader(ILogger logger, AppConfig settings, int blobCount, int eventsPerBlob)
            : base(logger, settings)
        {
            _blobCount = blobCount;
        }

        protected override Task<List<ActivityReportInfo>> LoadAllActivityReports(string auditContentType, TimePeriod chunk, int batchId)
        {
            // First (content-type x time-chunk) call emits every blob; the rest return empty, so the blob set
            // is deterministic regardless of how many time-chunks the config produces.
            if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0)
            {
                return Task.FromResult(new List<ActivityReportInfo>());
            }

            var list = new List<ActivityReportInfo>(_blobCount);
            for (int i = 0; i < _blobCount; i++)
            {
                list.Add(new ActivityReportInfo
                {
                    ContentId = BlobId(i),
                    ContentUri = new Uri($"https://fail-test.local/blob/{i}"),
                    ContentType = auditContentType,
                    BatchID = batchId,
                    Created = DateTime.UtcNow.AddMinutes(-i)
                });
            }
            return Task.FromResult(list);
        }
    }

    /// <summary>Returns a fixed number of (clean-download) SharePoint events per blob.</summary>
    public class FailureTestReportLoader : IActivityReportLoader<ActivityReportInfo>
    {
        private readonly int _eventsPerBlob;

        public FailureTestReportLoader(int eventsPerBlob)
        {
            _eventsPerBlob = eventsPerBlob;
        }

        public Task<ActivityReportSet> Load(ActivityReportInfo metadata)
        {
            var set = new WebActivityReportSet(metadata);
            for (int i = 0; i < _eventsPerBlob; i++)
            {
                set.Add(DataGenerators.GetRandomSharePointLog());
            }
            set.DownloadComplete = true;
            return Task.FromResult((ActivityReportSet)set);
        }
    }

    /// <summary>
    /// In-memory <see cref="IProcessedBlobStore"/> that records exactly which blob ids were checkpointed, so a
    /// test can assert that FAILED batches' blobs are NOT marked processed (and therefore retried next cycle)
    /// while successful ones are.
    /// </summary>
    public class RecordingProcessedBlobStore : IProcessedBlobStore, IActivityMetadataRecoveryStore
    {
        private readonly HashSet<string> _processed = new HashSet<string>();
        private readonly HashSet<string> _metadataRecoveryPending = new HashSet<string>();
        private readonly object _lock = new object();

        public Exception ReadFailure { get; set; }
        public Exception RecoveryReadFailure { get; set; }
        public Exception RecoveryWriteFailure { get; set; }

        public Task<ISet<string>> GetProcessedBlobIdsAsync()
        {
            if (ReadFailure != null)
            {
                return Task.FromException<ISet<string>>(ReadFailure);
            }

            lock (_lock)
            {
                return Task.FromResult<ISet<string>>(new HashSet<string>(_processed));
            }
        }

        public Task MarkProcessedAsync(IReadOnlyCollection<string> blobIds)
        {
            lock (_lock)
            {
                foreach (var b in blobIds) _processed.Add(b);
            }
            return Task.CompletedTask;
        }

        public Task<ISet<string>> GetMetadataRecoveryPendingBlobIdsAsync()
        {
            if (RecoveryReadFailure != null)
            {
                return Task.FromException<ISet<string>>(RecoveryReadFailure);
            }

            lock (_lock)
            {
                return Task.FromResult<ISet<string>>(new HashSet<string>(_metadataRecoveryPending));
            }
        }

        public Task MarkMetadataRecoveryPendingAsync(IReadOnlyCollection<string> blobIds)
        {
            if (RecoveryWriteFailure != null)
            {
                return Task.FromException(RecoveryWriteFailure);
            }

            lock (_lock)
            {
                foreach (var b in blobIds) _metadataRecoveryPending.Add(b);
            }
            return Task.CompletedTask;
        }

        public Task ClearMetadataRecoveryPendingAsync(IReadOnlyCollection<string> blobIds)
        {
            lock (_lock)
            {
                foreach (var b in blobIds) _metadataRecoveryPending.Remove(b);
            }
            return Task.CompletedTask;
        }

        public void SeedMetadataRecovery(string blobId)
        {
            lock (_lock) { _metadataRecoveryPending.Add(blobId); }
        }

        public bool IsMetadataRecoveryPending(string blobId)
        {
            lock (_lock) { return _metadataRecoveryPending.Contains(blobId); }
        }

        public bool IsProcessed(string blobId)
        {
            lock (_lock) { return _processed.Contains(blobId); }
        }

        public int ProcessedCount
        {
            get { lock (_lock) { return _processed.Count; } }
        }
    }

    /// <summary>
    /// Fake saver that fails whichever blobs the test configures, keyed on the event's <c>SourceContentId</c>
    /// (which the importer stamps onto every event before saving). It reproduces the two production failure
    /// shapes exactly: a transient dropped/unrecoverable connection (retryable) and a PRIMARY KEY violation
    /// (non-transient - must NOT be retried). Attempt counts are recorded per blob so tests can assert the
    /// retry behaviour. Thread-safe so it works in concurrent-save mode too.
    /// </summary>
    public class FailureInjectingPersistenceManager : IActivityReportPersistenceManager
    {
        // The exact message logged when Azure SQL dropped the connection mid-merge.
        public const string TransientConnectionBrokenMessage =
            "Couldn't merge batch insert using given SQL: The connection is broken and recovery is not possible.  " +
            "The connection is marked by the server as unrecoverable.  No attempt was made to restore the connection.";

        // A deterministic constraint violation - the non-transient shape (the concurrent-save PK dupe bug).
        public const string PrimaryKeyViolationMessage =
            "Couldn't merge batch insert using given SQL: Violation of PRIMARY KEY constraint 'PK_dbo.audit_events'. " +
            "Cannot insert duplicate key in object 'dbo.audit_events'. The duplicate key value is (00000000-0000-0000-0000-000000000000).";

        // blobId -> number of transient failures to throw before succeeding (int.MaxValue = never succeed).
        private readonly Dictionary<string, int> _transientFailuresBeforeSuccess;
        // blobId -> throw a non-transient constraint violation on every attempt.
        private readonly HashSet<string> _constraintFailBlobs;

        private readonly ConcurrentDictionary<string, int> _attempts = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, bool> _metadataRecoveryRequested =
            new ConcurrentDictionary<string, bool>();
        private int _committedEvents;
        private int _committedBatches;

        public FailureInjectingPersistenceManager(
            Dictionary<string, int> transientFailuresBeforeSuccess = null,
            HashSet<string> constraintFailBlobs = null)
        {
            _transientFailuresBeforeSuccess = transientFailuresBeforeSuccess ?? new Dictionary<string, int>();
            _constraintFailBlobs = constraintFailBlobs ?? new HashSet<string>();
        }

        public int AttemptsFor(string blobId) => _attempts.TryGetValue(blobId, out var n) ? n : 0;
        public int TotalCommitAttempts => _attempts.Values.Sum();
        public int CommittedEvents => Interlocked.CompareExchange(ref _committedEvents, 0, 0);
        public int CommittedBatches => Interlocked.CompareExchange(ref _committedBatches, 0, 0);
        public bool MetadataRecoveryRequestedFor(string blobId)
            => _metadataRecoveryRequested.TryGetValue(blobId, out var requested) && requested;

        public async Task<ImportStat> CommitAll(ActivityReportSet activities)
        {
            // Each test batch carries events from a single blob (eventsPerBlob == maxSavesPerBatch), so the
            // first event's SourceContentId identifies the batch's blob.
            var blobId = activities.FirstOrDefault()?.SourceContentId ?? "(no-source-content-id)";
            int attempt = _attempts.AddOrUpdate(blobId, 1, (k, v) => v + 1);
            _metadataRecoveryRequested[blobId] = activities.Any(activities.RequiresMetadataRecovery);

            if (_constraintFailBlobs.Contains(blobId))
            {
                throw new BatchSaveException(PrimaryKeyViolationMessage);
            }

            if (_transientFailuresBeforeSuccess.TryGetValue(blobId, out var failCount) && attempt <= failCount)
            {
                throw new BatchSaveException(TransientConnectionBrokenMessage);
            }

            Interlocked.Add(ref _committedEvents, activities.Count);
            Interlocked.Increment(ref _committedBatches);
            return await Task.FromResult(new ImportStat { Imported = activities.Count, Total = activities.Count });
        }
    }
}
