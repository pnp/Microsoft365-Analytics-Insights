using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>How a failed call import should be retried.</summary>
    public enum CallImportFailureKind
    {
        /// <summary>Something about this call (Graph had no record yet, a page failed): retry it later.</summary>
        Transient,

        /// <summary>
        /// Something that usually means every call would fail - SQL unavailable, Graph refusing the app or throttling it:
        /// retry it later, and when other calls fail the same way, stop taking more calls off the queue for a while so
        /// the backlog is not burned through (see <see cref="CallImportCircuitBreaker"/>).
        /// </summary>
        Systemic,

        /// <summary>Retrying the same notification would fail the same way: dead-letter it with the reason.</summary>
        Permanent
    }

    /// <summary>
    /// Sorts call-import failures into <see cref="CallImportFailureKind"/>s.
    /// </summary>
    public static class CallImportFailureClassifier
    {
        // Cannot reach or log in to SQL at all. TransientSqlRetry already covers timeouts, failovers, throttling and
        // dropped connections; these are the "can't connect" numbers it leaves out because a retry loop around one
        // statement can't fix them - but they stop every call, so the queue should pause on them.
        private static readonly HashSet<int> SqlUnreachableErrorNumbers = new HashSet<int>
        {
            -1, 2, 26, 53, 10061, 11001,    // server not found / not reachable
            18452, 18456,                   // login failed
            40532, 40607, 40615, 47073      // Azure SQL: login, firewall, public network access denied
        };

        private static readonly string[] TokenAcquisitionExceptionTypes =
        {
            "Azure.Identity.AuthenticationFailedException",
            "Azure.Identity.CredentialUnavailableException",
            "Microsoft.Identity.Client.MsalException"
        };

        public static CallImportFailureKind Classify(Exception failure)
        {
            if (failure == null)
            {
                return CallImportFailureKind.Transient;
            }

            var chain = Flatten(failure).ToList();
            if (chain.OfType<CallRecordSessionPagingException>().Any(p => p.IsPermanent))
            {
                return CallImportFailureKind.Permanent;
            }

            if (AnySystemic(failure) || TransientSqlRetry.IsTransient(failure))
            {
                return CallImportFailureKind.Systemic;
            }

            return CallImportFailureKind.Transient;
        }

        /// <summary>
        /// A short description of a failure to keep with the queue message: no request URLs, which can name a person.
        /// </summary>
        public static string Describe(Exception failure)
        {
            if (failure == null)
            {
                return null;
            }

            // The root cause, stopping at a Graph error response: all it wraps is the framework's status-code exception.
            var cause = failure;
            while (cause.InnerException != null && !(cause is GraphHttpException))
            {
                cause = cause.InnerException;
            }

            var description = $"{failure.GetType().Name}: {DescribeOne(failure)}";
            var causeDescription = DescribeOne(cause);
            if (!ReferenceEquals(cause, failure) && description.IndexOf(causeDescription ?? string.Empty, StringComparison.Ordinal) < 0)
            {
                description += $" Cause: {cause.GetType().Name}: {causeDescription}";
            }
            return description;
        }

        private static string DescribeOne(Exception ex)
        {
            if (ex is ODataError odata)
            {
                return $"Graph returned {odata.ResponseStatusCode} ({odata.Error?.Code ?? "unknown"}): {odata.Error?.Message}";
            }
            if (ex is ApiException api && api.ResponseStatusCode > 0)
            {
                // The Graph SDK's own error for a response it could not read as OData, such as a gateway's HTML 503.
                return $"Graph returned {api.ResponseStatusCode}: {api.Message}";
            }
            return GraphHttpException.DescribeForStorage(ex);
        }

        /// <summary>
        /// A short, stable classification of a failure for the outcome log line - exception type plus the HTTP status,
        /// Graph error code or SQL error number that decides how it is handled. No messages, so nothing a person's name
        /// or a URL could be in.
        /// </summary>
        public static string Code(Exception failure)
        {
            for (var e = failure; e != null; e = e.InnerException)
            {
                switch (e)
                {
                    case CallRecordPayloadException payload:
                        return payload.Reason;
                    case CallRecordSessionPagingException paging:
                        return paging.IsPermanent ? "SessionPagingPermanent" : "SessionPagingFailed";
                    case GraphHttpException graph:
                        return $"{nameof(GraphHttpException)}/{(int)graph.StatusCode}";
                    case ODataError odata:
                        return $"{nameof(ODataError)}/{odata.ResponseStatusCode}" + (string.IsNullOrEmpty(odata.Error?.Code) ? string.Empty : $"/{odata.Error.Code}");
                    case ApiException api when api.ResponseStatusCode > 0:
                        return $"{nameof(ApiException)}/{api.ResponseStatusCode}";
                    case SqlException sql when sql.Errors.Count > 0:
                        return $"{nameof(SqlException)}/{sql.Errors[0].Number}";
                }
            }
            return failure?.GetType().Name ?? "Unknown";
        }

        private static bool AnySystemic(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AggregateException aggregate)
                {
                    return aggregate.InnerExceptions.Any(AnySystemic);
                }

                // A Graph error response decides on its own status: what it wraps is only the framework's
                // "status code does not indicate success" exception, which would otherwise read as a transport failure.
                if (e is GraphHttpException graph)
                {
                    return IsSystemicGraphStatus((int)graph.StatusCode);
                }

                if (IsSystemic(e))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsSystemic(Exception ex)
        {
            switch (ex)
            {
                case HttpRequestException _:
                    // Not a Graph error response: the transport failed, or the client ran out of throttling retries.
                    return true;
                case ApiException api:
                    // Every Kiota status error: ODataError (a JSON error body) derives from it, and so does the plain
                    // ApiException GraphTransientErrorHandler throws for a 429/5xx whose body isn't JSON.
                    return IsSystemicGraphStatus(api.ResponseStatusCode);
                case SqlException sql:
                    return sql.Errors.Cast<SqlError>().Any(e => SqlUnreachableErrorNumbers.Contains(e.Number));
                case System.Data.Entity.Core.EntityException entity:
                    return entity.Message?.IndexOf("failed on Open", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            for (var type = ex.GetType(); type != null; type = type.BaseType)
            {
                if (TokenAcquisitionExceptionTypes.Contains(type.FullName))
                {
                    return true;
                }
            }
            return false;
        }

        // 401/403: the app's credentials or permissions; 429: the tenant is throttled; 502/503/504: Graph or its gateway is
        // unavailable. A 404 or 500 is about the one call record.
        private static bool IsSystemicGraphStatus(int status)
        {
            return status == 401 || status == 403 || status == 429 || status == 502 || status == 503 || status == 504;
        }

        private static IEnumerable<Exception> Flatten(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                yield return e;
                if (e is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions.Skip(1).SelectMany(Flatten))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Retry bookkeeping carried on a call notification in Service Bus application properties, so it survives being
    /// re-queued and process restarts.
    /// </summary>
    public sealed class CallQueueRetryState
    {
        public const string FailuresProperty = "CallImportFailures";
        public const string SystemicFailuresProperty = "CallImportSystemicFailures";
        public const string RetriesProperty = "CallImportRetries";
        public const string FirstFailureUtcProperty = "CallImportFirstFailureUtc";
        public const string OriginalMessageIdProperty = "CallImportOriginalMessageId";
        public const string LastErrorProperty = "CallImportLastError";

        private const int MaxLastErrorLength = 500;

        /// <summary>Failed attempts that were about this call. Systemic failures are counted separately.</summary>
        public int Failures { get; set; }

        /// <summary>
        /// Failed attempts of a kind that usually means an outage (<see cref="CallImportFailureKind.Systemic"/>).
        /// </summary>
        public int SystemicFailures { get; set; }

        /// <summary>Times the notification has been re-queued, for whatever reason. Sets the next delay.</summary>
        public int Retries { get; set; }

        public DateTime? FirstFailureUtc { get; set; }

        /// <summary>The Service Bus message id the webhook queued; each re-queued copy has its own id.</summary>
        public string OriginalMessageId { get; set; }

        public string LastError { get; set; }

        public static CallQueueRetryState FromMessage(CallQueueMessage message)
        {
            var properties = message?.ApplicationProperties ?? new Dictionary<string, object>();
            return new CallQueueRetryState
            {
                Failures = ReadInt(properties, FailuresProperty),
                SystemicFailures = ReadInt(properties, SystemicFailuresProperty),
                Retries = ReadInt(properties, RetriesProperty),
                FirstFailureUtc = ReadUtc(properties, FirstFailureUtcProperty),
                OriginalMessageId = ReadString(properties, OriginalMessageIdProperty) ?? message?.MessageId,
                LastError = ReadString(properties, LastErrorProperty)
            };
        }

        public void WriteTo(IDictionary<string, object> properties)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            properties[FailuresProperty] = Failures;
            properties[SystemicFailuresProperty] = SystemicFailures;
            properties[RetriesProperty] = Retries;
            if (FirstFailureUtc.HasValue)
            {
                properties[FirstFailureUtcProperty] = FirstFailureUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrEmpty(OriginalMessageId))
            {
                properties[OriginalMessageIdProperty] = OriginalMessageId;
            }
            if (!string.IsNullOrEmpty(LastError))
            {
                properties[LastErrorProperty] = LastError.Length > MaxLastErrorLength ? LastError.Substring(0, MaxLastErrorLength) : LastError;
            }
        }

        private static int ReadInt(IReadOnlyDictionary<string, object> properties, string name)
        {
            if (properties.TryGetValue(name, out var value) && value != null)
            {
                try
                {
                    return Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                }
            }
            return 0;
        }

        private static string ReadString(IReadOnlyDictionary<string, object> properties, string name)
        {
            return properties.TryGetValue(name, out var value) ? value?.ToString() : null;
        }

        private static DateTime? ReadUtc(IReadOnlyDictionary<string, object> properties, string name)
        {
            if (!properties.TryGetValue(name, out var value) || value == null)
            {
                return null;
            }
            if (value is DateTime dateTime)
            {
                return dateTime.ToUniversalTime();
            }
            if (value is DateTimeOffset offset)
            {
                return offset.UtcDateTime;
            }
            if (!DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return null;
            }
            return parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
        }
    }

    /// <summary>What to do with a call notification whose import failed.</summary>
    public sealed class CallQueueRetryDecision
    {
        public bool DeadLetter { get; private set; }
        public string DeadLetterReason { get; private set; }
        public string DeadLetterDescription { get; private set; }

        /// <summary>The bookkeeping for the re-queued copy (or for the description, when dead-lettering).</summary>
        public CallQueueRetryState NextState { get; private set; }

        public DateTime RetryAtUtc { get; private set; }

        public static CallQueueRetryDecision Retry(CallQueueRetryState next, DateTime retryAtUtc)
            => new CallQueueRetryDecision { NextState = next, RetryAtUtc = retryAtUtc };

        public static CallQueueRetryDecision GiveUp(CallQueueRetryState next, string reason, string description)
            => new CallQueueRetryDecision { NextState = next, DeadLetter = true, DeadLetterReason = reason, DeadLetterDescription = description };
    }

    /// <summary>
    /// When a failed call notification is retried, and when it is given up on.
    /// </summary>
    /// <remarks>
    /// Abandoning a message makes Service Bus redeliver it at once, so with the queue's default of 10 deliveries a
    /// minute-long SQL failover used to dead-letter every call that arrived during it. A failure now re-queues a copy
    /// to arrive after an increasing delay (1, 2, 4 ... minutes, at most an hour) and completes the original. The
    /// copy carries the bookkeeping in <see cref="CallQueueRetryState"/>, since a new message starts again at one
    /// delivery. Saves replace any earlier copy of the call, so a notification handled twice does no harm.
    /// <para>
    /// Failures of a kind that usually means an outage get a larger allowance (<see cref="MaxSystemicFailures"/>), since
    /// during a real one the queue is paused and they are not the call's fault. But one call can fail that way on its
    /// own - a 504 on its own oversized call record, say - so it is still given up on, with its own reason.
    /// </para>
    /// </remarks>
    public sealed class CallQueueRetryPolicy
    {
        public const string RetriesExhaustedReason = "CallImportRetriesExhausted";
        public const string SystemicRetriesExhaustedReason = "CallImportSystemicRetriesExhausted";
        public const string RetryWindowExpiredReason = "CallImportRetryWindowExpired";

        public static readonly CallQueueRetryPolicy Default = new CallQueueRetryPolicy(
            maxFailures: 10, maxSystemicFailures: 30, baseDelay: TimeSpan.FromMinutes(1), maxDelay: TimeSpan.FromHours(1), maxRetryWindow: TimeSpan.FromDays(3));

        public CallQueueRetryPolicy(int maxFailures, int maxSystemicFailures, TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan maxRetryWindow)
        {
            if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
            if (maxSystemicFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxSystemicFailures));
            if (baseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay));
            if (maxDelay < baseDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay));

            MaxFailures = maxFailures;
            MaxSystemicFailures = maxSystemicFailures;
            BaseDelay = baseDelay;
            MaxDelay = maxDelay;
            MaxRetryWindow = maxRetryWindow;
        }

        /// <summary>Attempts that fail for a reason about the call itself before it is dead-lettered.</summary>
        public int MaxFailures { get; }

        /// <summary>Attempts that fail in a way that usually means an outage before it is dead-lettered.</summary>
        public int MaxSystemicFailures { get; }

        public TimeSpan BaseDelay { get; }
        public TimeSpan MaxDelay { get; }

        /// <summary>How long after its first failure a notification is still retried, whatever the failures.</summary>
        public TimeSpan MaxRetryWindow { get; }

        /// <summary>The delay before the <paramref name="retryNumber"/>th retry (1-based): doubling, capped.</summary>
        public TimeSpan GetDelay(int retryNumber)
        {
            var doublings = Math.Max(0, Math.Min(retryNumber - 1, 30));
            var ticks = BaseDelay.Ticks * Math.Pow(2, doublings);
            return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
        }

        public CallQueueRetryDecision Decide(CallQueueRetryState current, CallImportFailureKind kind, DateTime nowUtc, string error)
        {
            current = current ?? new CallQueueRetryState();
            var systemic = kind == CallImportFailureKind.Systemic;
            var next = new CallQueueRetryState
            {
                Failures = current.Failures + (systemic ? 0 : 1),
                SystemicFailures = current.SystemicFailures + (systemic ? 1 : 0),
                Retries = current.Retries + 1,
                FirstFailureUtc = current.FirstFailureUtc ?? nowUtc,
                OriginalMessageId = current.OriginalMessageId,
                LastError = error
            };

            if (!systemic && next.Failures >= MaxFailures)
            {
                return CallQueueRetryDecision.GiveUp(next, RetriesExhaustedReason,
                    $"Not imported after {next.Failures} failed attempts (first failure {next.FirstFailureUtc:u}). Last error: {error}");
            }

            if (systemic && next.SystemicFailures >= MaxSystemicFailures)
            {
                return CallQueueRetryDecision.GiveUp(next, SystemicRetriesExhaustedReason,
                    $"Not imported after {next.SystemicFailures} failures of a kind that usually means an outage - Graph refusing, " +
                    $"throttling or unavailable, or SQL unreachable (first failure {next.FirstFailureUtc:u}). If other calls imported " +
                    $"meanwhile, the problem is with this call. Last error: {error}");
            }

            if (nowUtc - next.FirstFailureUtc.Value >= MaxRetryWindow)
            {
                return CallQueueRetryDecision.GiveUp(next, RetryWindowExpiredReason,
                    $"Still not imported {MaxRetryWindow.TotalDays:0.#} days after the first failure ({next.FirstFailureUtc:u}), after {next.Retries} retries. Last error: {error}");
            }

            return CallQueueRetryDecision.Retry(next, nowUtc + GetDelay(next.Retries));
        }
    }
}
