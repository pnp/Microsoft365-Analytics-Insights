using Common.Entities.LicenceActivity;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.LicenceActivity
{
    internal sealed class LicenceActivityBusyException : Exception { }
    internal sealed class LicenceActivityExpiredException : Exception { }

    internal sealed class LicenceActivityFailedException : Exception
    {
        public LicenceActivityFailedException(string runId)
            : base("Licence activity could not be loaded. Retry the request. Reference: " + runId) { }
    }

    internal static class LicenceActivityConcurrency
    {
        internal static readonly SemaphoreSlim Slots = new SemaphoreSlim(4, 4);
    }

    // Only bounded result pages live here, never the user-by-licence population.
    internal sealed class LicenceActivitySnapshotCache<T> where T : LicenceActivitySnapshot
    {
        internal const int MaximumJsonBytes = 1024 * 1024;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int _capacity;
        private readonly TimeSpan _ttl;
        private readonly TimeSpan _runTimeout;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _slots;
        private readonly Func<string, LicenceActivityRunDiagnostics> _diagnostics;
        private readonly Action<string, Exception> _reportFailure;

        internal LicenceActivitySnapshotCache(
            int capacity, TimeSpan ttl, SemaphoreSlim slots = null, Func<DateTime> utcNow = null,
            Func<string, LicenceActivityRunDiagnostics> diagnostics = null, TimeSpan? runTimeout = null,
            Action<string, Exception> reportFailure = null)
        {
            if (capacity < 1 || ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _ttl = ttl;
            _slots = slots ?? LicenceActivityConcurrency.Slots;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _diagnostics = diagnostics ?? LicenceActivityTelemetry.Start;
            _runTimeout = runTimeout ?? TimeSpan.FromSeconds(30);
            if (_runTimeout <= TimeSpan.Zero || _runTimeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(runTimeout));
            _reportFailure = reportFailure ?? LicenceActivityTelemetry.ReportFailure;
        }

        internal Task<T> GetAsync(
            string scope, string key, Func<ILicenceActivityDiagnostics, CancellationToken, Task<T>> load,
            DateTime? notAfterUtc = null)
        {
            if (load == null) throw new ArgumentNullException(nameof(load));
            Entry entry;
            var compoundKey = scope + "\n" + key;
            lock (_gate)
            {
                Prune();
                if (notAfterUtc.HasValue && notAfterUtc <= _utcNow())
                    throw new LicenceActivityExpiredException();
                if (_entries.TryGetValue(compoundKey, out var existing)) return existing.Completion.Task;
                Entry oldest = null;
                if (_entries.Count >= _capacity)
                {
                    oldest = _entries.Values.Where(e => e.Value != null).OrderBy(e => e.Value.ExpiresUtc).FirstOrDefault();
                    if (oldest == null) throw new LicenceActivityBusyException();
                }
                if (!_slots.Wait(0)) throw new LicenceActivityBusyException();
                if (oldest != null) _entries.Remove(oldest.Key);
                entry = new Entry(compoundKey, scope);
                _entries.Add(compoundKey, entry);
            }

            // The response deadline starts at admission, not whenever a thread-pool worker starts.
            entry.Deadline = new Timer(_ => ExpireRun(entry), null, _runTimeout, Timeout.InfiniteTimeSpan);
            // Inner awaits must never capture the initiating ASP.NET request's SynchronizationContext.
            _ = Task.Run(() => LoadAndPublishAsync(entry, load, notAfterUtc));
            return entry.Completion.Task;
        }

        internal T Find(string scope, string snapshotId)
        {
            if (string.IsNullOrEmpty(snapshotId)) throw new LicenceActivityExpiredException();
            lock (_gate)
            {
                Prune();
                var result = _entries.Values.FirstOrDefault(e => e.Scope == scope && e.Value?.SnapshotId == snapshotId);
                return result?.Value ?? throw new LicenceActivityExpiredException();
            }
        }

        internal static async Task<T> WaitForCallerAsync(Task<T> task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(cancellationToken);
                return await task.ConfigureAwait(false);
            }
        }

        private async Task LoadAndPublishAsync(
            Entry entry, Func<ILicenceActivityDiagnostics, CancellationToken, Task<T>> load, DateTime? notAfterUtc)
        {
            LicenceActivityRunDiagnostics diagnostics = null;
            Exception failure = null;
            T value = null;
            try
            {
                try { diagnostics = _diagnostics(entry.RunId); }
                catch (Exception)
                {
                    // Telemetry construction is optional; a failed query still uses the fallback below.
                }
                Volatile.Write(ref entry.Diagnostics, diagnostics);
                entry.Lifetime.Token.ThrowIfCancellationRequested();
                value = await load(diagnostics ?? (ILicenceActivityDiagnostics)NullLicenceActivityDiagnostics.Instance,
                    entry.Lifetime.Token).ConfigureAwait(false);
                entry.Lifetime.Token.ThrowIfCancellationRequested();
                if (value == null) throw new InvalidOperationException("The report loader returned no result.");
                value.SnapshotId = Guid.NewGuid().ToString("N");
                value.GeneratedUtc = _utcNow();
                value.ExpiresUtc = value.GeneratedUtc.Add(_ttl);
                if (notAfterUtc.HasValue && value.ExpiresUtc > notAfterUtc.Value) value.ExpiresUtc = notAfterUtc.Value;
                if (value.ExpiresUtc <= value.GeneratedUtc) throw new LicenceActivityExpiredException();
                EnsureJsonWithinBudget(value);
                entry.Lifetime.Token.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // A timed-out response does not free a still-running SQL operation's capacity. The
                // store owns its connection and must finish cancellation/disposal before this release.
                _slots.Release();
            }

            try
            {
                if (failure == null)
                {
                    var published = false;
                    lock (_gate)
                    {
                        if (!entry.Completion.Task.IsCompleted
                            && _entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                        {
                            entry.Value = value;
                            published = entry.Completion.TrySetResult(value);
                        }
                    }
                    if (published) diagnostics?.Stage("CachePublished");
                }
                else
                {
                    FailRun(entry, failure);
                }
            }
            finally
            {
                entry.Deadline.Dispose();
                entry.Lifetime.Dispose();
                diagnostics?.Dispose();
            }
        }

        internal static void EnsureJsonWithinBudget(object value)
        {
            // Count the same UTF-8 JSON without allocating a response-sized UTF-16 string on the LOH.
            using (var stream = new JsonSizeStream())
            using (var text = new StreamWriter(stream, new UTF8Encoding(false), 4096))
            using (var json = new JsonTextWriter(text))
            {
                JsonSerializer.CreateDefault().Serialize(json, value);
            }
        }

        private sealed class JsonSizeStream : Stream
        {
            private long _length;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _length;
            public override long Position { get => _length; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                _length += count;
                if (_length > MaximumJsonBytes)
                    throw new InvalidOperationException("The bounded report response exceeds its size budget.");
            }
        }

        private void ExpireRun(Entry entry)
        {
            Exception failure = new TimeoutException("The shared report response deadline elapsed.");
            if (!FailRun(entry, failure, report: false))
                return;
            try { entry.Lifetime.Cancel(); }
            catch (ObjectDisposedException)
            {
                // The completed loader can dispose its lifetime after the deadline wins publication.
            }
            catch (AggregateException cancellationFailure)
            {
                failure = cancellationFailure;
            }
            ReportFailure(entry, failure);
        }

        private bool FailRun(Entry entry, Exception failure, bool report = true)
        {
            lock (_gate)
            {
                if (entry.Completion.Task.IsCompleted) return false;
                if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(entry.Key);
                entry.Completion.TrySetException(failure is LicenceActivityExpiredException
                    ? failure : new LicenceActivityFailedException(entry.RunId));
                _ = entry.Completion.Task.Exception;
            }
            if (report) ReportFailure(entry, failure);
            return true;
        }

        private void ReportFailure(Entry entry, Exception failure)
        {
            var diagnostics = Volatile.Read(ref entry.Diagnostics);
            if (diagnostics == null || !diagnostics.Failed(failure)) _reportFailure(entry.RunId, failure);
        }

        private void Prune()
        {
            var now = _utcNow();
            foreach (var key in _entries.Where(e => e.Value.Value != null && e.Value.Value.ExpiresUtc <= now)
                .Select(e => e.Key).ToArray())
                _entries.Remove(key);
        }

        private sealed class Entry
        {
            internal Entry(string key, string scope) { Key = key; Scope = scope; }
            internal string Key { get; }
            internal string Scope { get; }
            internal string RunId { get; } = Guid.NewGuid().ToString("N");
            internal CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();
            internal Timer Deadline { get; set; }
            internal LicenceActivityRunDiagnostics Diagnostics;
            internal T Value { get; set; }
            internal TaskCompletionSource<T> Completion { get; } =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
