using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Common.Entities.Config;
using Common.Entities.State;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.MessageTracing
{
    public sealed class MessageTraceBlobUploader : IMessageTraceSink, IDisposable
    {
        public const int DefaultMaxQueueMessages = 100;
        public const long DefaultMaxQueueBytes = 256L * 1024 * 1024;

        /// <summary>
        /// After the container can't be opened, traces are refused (counted as dropped) and no open is attempted
        /// for this long, so unreachable storage neither holds queued bodies in memory nor repeats the SDK's
        /// retry budget for every item. The next trace after it retries the open.
        /// </summary>
        public static readonly TimeSpan StorageRetryInterval = TimeSpan.FromMinutes(10);

        private static readonly Regex ContainerName = new Regex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$", RegexOptions.CultureInvariant);
        private readonly BlockingCollection<HttpMessageTraceEnvelope> _queue;
        private readonly long _maxQueueBytes;
        private readonly ILogger _logger;
        private readonly Task _worker;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Func<CancellationToken, Task<BlobContainerClient>> _openContainer;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _openGate = new SemaphoreSlim(1, 1);
        private BlobContainerClient _container;
        private long _queuedBytes;
        private int _inFlightUploads;
        private bool _storageUnavailable;
        private long _storageRetryAfterTicks;
        private DateTime _lastFailureLogUtc = DateTime.MinValue;

        public MessageTraceBlobUploader(Func<CancellationToken, Task<BlobContainerClient>> openContainer, ILogger logger,
            int maxQueueMessages = DefaultMaxQueueMessages, long maxQueueBytes = DefaultMaxQueueBytes, Func<DateTime> utcNow = null)
        {
            _openContainer = openContainer ?? throw new ArgumentNullException(nameof(openContainer));
            _logger = logger;
            _maxQueueBytes = maxQueueBytes;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _queue = new BlockingCollection<HttpMessageTraceEnvelope>(new ConcurrentQueue<HttpMessageTraceEnvelope>(), maxQueueMessages);
            _worker = Task.Run(ProcessQueueAsync);
        }

        public long SavedCount => Interlocked.Read(ref _savedCount);
        public long DroppedCount => Interlocked.Read(ref _droppedCount);
        public long FailedCount => Interlocked.Read(ref _failedCount);
        public long QueuedCount => _queue.Count;

        /// <summary>
        /// True from a failed attempt to open the blob container until an open succeeds, so the importer's
        /// per-cycle Health check can report that traces aren't being saved.
        /// </summary>
        public bool IsStorageUnavailable => Volatile.Read(ref _storageUnavailable);

        private long _savedCount;
        private long _droppedCount;
        private long _failedCount;

        public static bool IsValidContainerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !ContainerName.IsMatch(name)) return false;
            return !name.Contains("--");
        }

        public static MessageTraceBlobUploader Create(AppConfig config, ILogger logger)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var connectionString = config.ConnectionStrings?.StorageConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Message tracing needs the Storage connection string.");
            if (!IsValidContainerName(config.MessageTraceContainer)) throw new InvalidOperationException($"MessageTraceContainer '{config.MessageTraceContainer}' is not a legal Azure Blob container name.");

            return new MessageTraceBlobUploader(ct => OpenContainerAsync(connectionString, config.MessageTraceContainer, config, logger, ct), logger);
        }

        public bool TryEnqueue(HttpMessageTraceEnvelope envelope)
        {
            if (envelope?.Body == null) return false;
            if (IsInStorageBackoff())
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            var bytes = envelope.Body.LongLength;
            var queued = Interlocked.Add(ref _queuedBytes, bytes);
            if (queued > _maxQueueBytes)
            {
                Interlocked.Add(ref _queuedBytes, -bytes);
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            if (_queue.TryAdd(envelope))
            {
                return true;
            }

            Interlocked.Add(ref _queuedBytes, -bytes);
            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        public void RecordDroppedByHourlyCap()
        {
            Interlocked.Increment(ref _droppedCount);
        }

        public void LogSummary(ILogger logger)
        {
            logger?.LogWarning($"Message tracing summary: saved={SavedCount}, dropped={DroppedCount}, failed={FailedCount}, queued={QueuedCount}.");
        }

        public async Task FlushAsync(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while ((_queue.Count > 0 || Volatile.Read(ref _inFlightUploads) > 0) && DateTime.UtcNow < until)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _cts.Cancel();
            try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _cts.Dispose();
            _queue.Dispose();
        }

        private async Task ProcessQueueAsync()
        {
            foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    Interlocked.Increment(ref _inFlightUploads);
                    var container = await GetContainerAsync(_cts.Token).ConfigureAwait(false);
                    var blob = container.GetBlobClient(BuildBlobName(item));
                    var headers = new BlobHttpHeaders { ContentType = item.ContentType ?? "application/json" };
                    using (var stream = new MemoryStream(item.Body, writable: false))
                    {
                        await blob.UploadAsync(stream, headers, metadata: BuildMetadata(item), cancellationToken: _cts.Token).ConfigureAwait(false);
                    }
                    Interlocked.Increment(ref _savedCount);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedCount);
                    if (IsStorageSideFailure(ex))
                    {
                        await MarkStorageUnavailableAsync().ConfigureAwait(false);
                    }

                    var now = DateTime.UtcNow;
                    if ((now - _lastFailureLogUtc) > TimeSpan.FromMinutes(5))
                    {
                        _lastFailureLogUtc = now;
                        _logger?.LogWarning($"Message tracing failed to upload a matching response and will continue without aborting the import: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlightUploads);
                    Interlocked.Add(ref _queuedBytes, -item.Body.LongLength);
                }
            }
        }

        /// <summary>
        /// A failure that says the storage account or container is unusable rather than that one item was bad:
        /// access refused, the container gone (deleted while tracing was on), the service failing, or the transport
        /// failing after the SDK's own retries. An item-specific 400 (e.g. oversized metadata) is not one.
        /// </summary>
        internal static bool IsStorageSideFailure(Exception ex)
        {
            switch (ex)
            {
                case AggregateException aggregate:
                    return aggregate.InnerExceptions.Any(IsStorageSideFailure);
                case RequestFailedException failed:
                    return failed.Status == 0 || failed.Status == 401 || failed.Status == 403 || failed.Status == 404
                        || failed.Status == 408 || failed.Status >= 500;
                default:
                    return ex is System.Net.Http.HttpRequestException || ex is IOException || ex is TimeoutException
                        || ex is OperationCanceledException;
            }
        }

        /// <summary>
        /// Drops the cached container so the next attempt re-runs the open (which re-creates a deleted container
        /// and redoes the key-to-RBAC fallback), and backs off like a failed open so Health reports it.
        /// </summary>
        private async Task MarkStorageUnavailableAsync()
        {
            await _openGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Volatile.Write(ref _container, null);
                Interlocked.Exchange(ref _storageRetryAfterTicks, (_utcNow() + StorageRetryInterval).Ticks);
                Volatile.Write(ref _storageUnavailable, true);
            }
            finally
            {
                _openGate.Release();
            }
        }

        private async Task<BlobContainerClient> GetContainerAsync(CancellationToken cancellationToken)
        {
            var client = Volatile.Read(ref _container);
            if (client != null) return client;

            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_container != null) return _container;
                if (IsInStorageBackoff())
                {
                    throw new InvalidOperationException("Message tracing blob storage was unavailable on the last attempt; the open is retried after the back-off.");
                }

                try
                {
                    Volatile.Write(ref _container, await _openContainer(cancellationToken).ConfigureAwait(false));
                    Volatile.Write(ref _storageUnavailable, false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _storageRetryAfterTicks, (_utcNow() + StorageRetryInterval).Ticks);
                    Volatile.Write(ref _storageUnavailable, true);
                    throw;
                }
                return _container;
            }
            finally
            {
                _openGate.Release();
            }
        }

        private bool IsInStorageBackoff()
        {
            return Volatile.Read(ref _storageUnavailable)
                && _utcNow().Ticks < Interlocked.Read(ref _storageRetryAfterTicks);
        }

        private static async Task<BlobContainerClient> OpenContainerAsync(string connectionString, string containerName, AppConfig config, ILogger logger, CancellationToken cancellationToken)
        {
            if (StorageTableClientFactory.IsDevelopmentStorage(connectionString))
            {
                var azurite = new BlobContainerClient(connectionString, containerName);
                await azurite.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
                return azurite;
            }

            BlobContainerClient client = null;
            if (StorageTableClientFactory.HasCredentials(connectionString))
            {
                try
                {
                    client = new BlobContainerClient(connectionString, containerName);
                    await client.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return client;
                }
                catch (RequestFailedException ex) when (StorageTableClientFactory.IsAccessDenied(ex))
                {
                    logger?.LogWarning($"Message tracing blob storage denied the Storage connection string credentials (HTTP {ex.Status} {ex.ErrorCode ?? "UnknownError"}); retrying with RBAC/Entra ID.");
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidOperationException)
                {
                    logger?.LogWarning($"Message tracing blob storage could not use the Storage connection string credentials ({ex.GetType().Name}); retrying with RBAC/Entra ID.");
                }
            }

            var endpoint = StorageTableClientFactory.GetBlobEndpoint(connectionString);
            if (endpoint == null) throw new InvalidOperationException("The Storage connection string does not contain a BlobEndpoint or AccountName for RBAC.");
            var tenantId = config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString();
            var credential = await StorageTableClientFactory.CreateRuntimeCredentialAsync(
                tenantId, config.ClientID, config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate, logger).ConfigureAwait(false);
            client = new BlobContainerClient(new Uri(endpoint, containerName), credential);
            await client.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
            return client;
        }

        internal static string BuildBlobName(HttpMessageTraceEnvelope item)
        {
            var source = string.IsNullOrWhiteSpace(item.Source) ? "http" : item.Source;
            source = new string(source.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
            return item.CapturedUtc.ToString("yyyy/MM/dd/HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + source + "-" + Guid.NewGuid().ToString("N") + ".json";
        }

        internal static IDictionary<string, string> BuildMetadata(HttpMessageTraceEnvelope item)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "capturedUtc", item.CapturedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture) },
                { "method", Encode(item.Method) },
                { "requestUrl", Encode(item.RequestUri?.ToString()) },
                { "statusCode", item.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "contentType", Encode(item.ContentType) },
                { "matchedPattern", Encode(item.MatchedPattern) },
            };
        }

        private static string Encode(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }
    }
}
