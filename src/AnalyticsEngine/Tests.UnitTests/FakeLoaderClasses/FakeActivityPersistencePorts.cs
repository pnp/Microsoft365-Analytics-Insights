using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IActivityStagingWriter"/>: hands out recording batches instead of an
    /// <c>EFInsertBatch</c>, so the audit-log save path's staging + merge decisions can be asserted with no
    /// SQL Server. See issue #373.
    ///
    /// Internal because the staging entity it carries is internal.
    /// </summary>
    internal class InMemoryActivityStagingWriter : IActivityStagingWriter
    {
        private readonly object _lock = new object();

        public List<InMemoryActivityStagingBatch> Batches { get; } = new List<InMemoryActivityStagingBatch>();

        /// <summary>Optional hook run inside the merge, e.g. to make the merge phase measurably slow.</summary>
        public Func<Task> OnMerge { get; set; }

        public InMemoryActivityStagingBatch LastBatch
        {
            get { lock (_lock) { return Batches.Count == 0 ? null : Batches[Batches.Count - 1]; } }
        }

        public IActivityStagingBatch CreateBatch(AnalyticsEntitiesContext db)
        {
            var batch = new InMemoryActivityStagingBatch { OnMerge = OnMerge };
            lock (_lock) { Batches.Add(batch); }
            return batch;
        }
    }

    /// <summary>
    /// Records everything one save batch staged and exactly how the merge was invoked - which staging table
    /// the merge SQL was pointed at, and whether the shared-write lock was handed down to it.
    /// </summary>
    internal class InMemoryActivityStagingBatch : IActivityStagingBatch
    {
        private readonly object _lock = new object();

        public List<AuditLogTempEntity> Rows { get; } = new List<AuditLogTempEntity>();

        /// <summary>How many times the batch was merged (production: exactly once per save).</summary>
        public int MergeCount { get; private set; }

        /// <summary>The merge script as it was actually handed to SQL, with the table name substituted.</summary>
        public string LastMergeSql { get; private set; }

        /// <summary>
        /// The staging-table override. <c>null</c> means the shared table named by the entity's
        /// <c>[TempTableName]</c> attribute - i.e. the default serial path.
        /// </summary>
        public string LastStagingTableName { get; private set; }

        /// <summary>
        /// The shared-write lock passed down to the merge, or <c>null</c> on the serial path. Kept as the
        /// instance (not a bool) so a test can assert it is the SAME semaphore the save was given.
        /// </summary>
        public SemaphoreSlim LastMergeLock { get; private set; }

        public int LastInsertsPerThread { get; private set; }

        /// <summary>Rows staged at the moment the merge ran, so a later mutation cannot fake it.</summary>
        public int RowCountAtMerge { get; private set; }

        public Func<Task> OnMerge { get; set; }

        public void AddRow(AuditLogTempEntity row)
        {
            lock (_lock) { Rows.Add(row); }
        }

        public async Task<int> LoadAndMergeAsync(int insertsPerThread, string mergeSql, string stagingTableName, SemaphoreSlim mergeLock)
        {
            lock (_lock)
            {
                MergeCount++;
                LastInsertsPerThread = insertsPerThread;
                LastMergeSql = mergeSql;
                LastStagingTableName = stagingTableName;
                LastMergeLock = mergeLock;
                RowCountAtMerge = Rows.Count;
            }

            if (OnMerge != null)
            {
                await OnMerge();
            }

            return RowCountAtMerge;
        }
    }

    /// <summary>
    /// In-memory <see cref="IActivityImportCacheLoader"/>. Records every window it was asked to load so a
    /// test can tell a run-scoped (whole-window, built once) load from the per-batch safety-valve's
    /// rebuild-every-batch behaviour. See issue #373.
    /// </summary>
    public class FakeActivityImportCacheLoader : IActivityImportCacheLoader
    {
        private readonly object _lock = new object();
        private readonly List<CacheLoad> _loads = new List<CacheLoad>();

        /// <summary>A single load request.</summary>
        public class CacheLoad
        {
            public DateTime FromUtc { get; set; }
            public DateTime ToUtc { get; set; }
        }

        /// <summary>Optional: how long each load "takes", to exercise the build-once locking.</summary>
        public TimeSpan LoadDuration { get; set; } = TimeSpan.Zero;

        /// <summary>Optional: seeds the cache each load returns (e.g. to give it a non-zero id count).</summary>
        public Action<ActivityImportCache> SeedCache { get; set; }

        public IReadOnlyList<CacheLoad> Loads
        {
            get { lock (_lock) { return _loads.ToArray(); } }
        }

        public int LoadCount
        {
            get { lock (_lock) { return _loads.Count; } }
        }

        public ActivityImportCache Load(DateTime fromUtc, DateTime toUtc)
        {
            lock (_lock) { _loads.Add(new CacheLoad { FromUtc = fromUtc, ToUtc = toUtc }); }
            if (LoadDuration > TimeSpan.Zero)
            {
                Thread.Sleep(LoadDuration);
            }
            var cache = ActivityImportCache.GetEmptyCache();
            SeedCache?.Invoke(cache);
            return cache;
        }
    }

    /// <summary>
    /// In-memory <see cref="ICopilotMetadataLoaderFactory"/>. Can be made to fail, which is the realistic
    /// case (no Graph credentials), and counts how many times it was asked - the run-scoped loader must be
    /// built at most once per import cycle even when it fails.
    /// </summary>
    public class FakeCopilotMetadataLoaderFactory : ICopilotMetadataLoaderFactory
    {
        private readonly ICopilotMetadataLoader _loader;
        private readonly Exception _failWith;
        private int _createCallCount;

        public FakeCopilotMetadataLoaderFactory(ICopilotMetadataLoader loader)
        {
            _loader = loader;
        }

        /// <summary>
        /// A factory that fails. The failure is returned as a FAULTED TASK rather than thrown
        /// synchronously: a synchronous throw would land in the caller's catch block whether or not the
        /// caller awaited it, so it could not detect a dropped <c>await</c>.
        /// </summary>
        public static FakeCopilotMetadataLoaderFactory FailingWith(Exception ex)
        {
            return new FakeCopilotMetadataLoaderFactory(null, ex);
        }

        private FakeCopilotMetadataLoaderFactory(ICopilotMetadataLoader loader, Exception failWith)
        {
            _loader = loader;
            _failWith = failWith;
        }

        public int CreateCallCount => Interlocked.CompareExchange(ref _createCallCount, 0, 0);

        public Task<ICopilotMetadataLoader> CreateAsync()
        {
            Interlocked.Increment(ref _createCallCount);
            if (_failWith != null)
            {
                return Task.FromException<ICopilotMetadataLoader>(_failWith);
            }
            return Task.FromResult(_loader);
        }
    }

    /// <summary>
    /// In-memory <see cref="ICopilotMetadataLoader"/> that records every Graph lookup it was asked for,
    /// including the peak number running at once - which is what the pre-warm's concurrency cap controls.
    /// </summary>
    public class RecordingCopilotMetadataLoader : ICopilotMetadataLoader
    {
        private readonly object _lock = new object();
        private readonly List<KeyValuePair<string, string>> _fileLookups = new List<KeyValuePair<string, string>>();
        private int _inFlight;
        private int _peakInFlight;

        /// <summary>Optional: how long each file lookup "takes", so concurrency is observable.</summary>
        public TimeSpan FileLookupDuration { get; set; } = TimeSpan.Zero;

        /// <summary>Optional: context ids whose lookup fails (as a faulted task, not a synchronous throw).</summary>
        public HashSet<string> FailForContextIds { get; } = new HashSet<string>();

        /// <summary>(contextId, eventUpn) pairs, in the order the lookups started.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> FileLookups
        {
            get { lock (_lock) { return _fileLookups.ToArray(); } }
        }

        public int PeakConcurrentFileLookups
        {
            get { lock (_lock) { return _peakInFlight; } }
        }

        public async Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn)
        {
            lock (_lock)
            {
                _fileLookups.Add(new KeyValuePair<string, string>(copilotId, eventUpn));
                _inFlight++;
                if (_inFlight > _peakInFlight) _peakInFlight = _inFlight;
            }
            try
            {
                bool fail;
                lock (_lock) { fail = FailForContextIds.Contains(copilotId); }
                if (fail)
                {
                    // Faulted task, never a synchronous throw - see FakeCopilotMetadataLoaderFactory.
                    await Task.FromException<SpoDocumentFileInfo>(new InvalidOperationException($"Graph lookup failed for {copilotId}"));
                }
                if (FileLookupDuration > TimeSpan.Zero)
                {
                    await Task.Delay(FileLookupDuration);
                }
                return new SpoDocumentFileInfo { Url = copilotId };
            }
            finally
            {
                lock (_lock) { _inFlight--; }
            }
        }

        public Task<MeetingMetadata> GetMeetingInfo(string threadId, string userGuid) => Task.FromResult(new MeetingMetadata());

        public Task<string> GetUserIdFromUpn(string userPrincipalName) => Task.FromResult(Guid.Empty.ToString());
    }

    /// <summary>
    /// <see cref="AuditFilterConfig"/> driven by a predicate, so a test can put specific events outside the
    /// org-URL whitelist without loading org_urls from SQL.
    /// </summary>
    public class PredicateAuditFilterConfig : AuditFilterConfig
    {
        private readonly Func<AbstractAuditLogContent, bool> _inScope;

        public PredicateAuditFilterConfig(Func<AbstractAuditLogContent, bool> inScope)
        {
            _inScope = inScope;
        }

        public override bool InScope(AbstractAuditLogContent content) => _inScope(content);
    }

    /// <summary>Minimal <see cref="ILogger"/> that records level, formatted message and exception.</summary>
    public class RecordingLogger : ILogger
    {
        public class Entry
        {
            public LogLevel Level { get; set; }
            public string Message { get; set; }
            public Exception Exception { get; set; }
        }

        private readonly object _lock = new object();
        private readonly List<Entry> _entries = new List<Entry>();

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_lock) { return _entries.ToArray(); } }
        }

        public IDisposable BeginScope<TState>(TState state) => new NoopScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add(new Entry { Level = logLevel, Message = formatter(state, exception), Exception = exception });
            }
        }

        private class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
