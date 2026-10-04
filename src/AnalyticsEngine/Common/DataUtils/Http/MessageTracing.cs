using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Http
{
    public interface IHttpMessageTracer
    {
        bool IsEnabled { get; }
        Task TraceAsync(string source, HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken);
    }

    public static class HttpMessageTracing
    {
        private sealed class NoOpTracer : IHttpMessageTracer
        {
            public bool IsEnabled => false;
            public Task TraceAsync(string source, HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public static readonly IHttpMessageTracer Disabled = new NoOpTracer();
        private static IHttpMessageTracer _current = Disabled;

        public static IHttpMessageTracer Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value ?? Disabled);
        }
    }

    public sealed class MessageTraceHandler : DelegatingHandler
    {
        private readonly string _source;

        public MessageTraceHandler(string source)
        {
            _source = string.IsNullOrWhiteSpace(source) ? "http" : source;
        }

        public MessageTraceHandler(string source, HttpMessageHandler innerHandler)
            : this(source)
        {
            InnerHandler = innerHandler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var tracer = HttpMessageTracing.Current;
            if (tracer?.IsEnabled == true)
            {
                try
                {
                    await tracer.TraceAsync(_source, request, response, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            return response;
        }
    }

    public sealed class MessageTracePatternMatcher
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
        private readonly IReadOnlyList<Pattern> _patterns;
        private readonly ILogger _logger;

        private MessageTracePatternMatcher(IReadOnlyList<Pattern> patterns, ILogger logger)
        {
            _patterns = patterns;
            _logger = logger;
        }

        public IReadOnlyList<string> Patterns => _patterns.Select(p => p.Raw).ToArray();

        public static bool TryCreate(string configuredPatterns, ILogger logger, out MessageTracePatternMatcher matcher, out string failure)
        {
            matcher = null;
            failure = null;
            if (string.IsNullOrWhiteSpace(configuredPatterns)) return false;

            var patterns = new List<Pattern>();
            foreach (var rawPart in configuredPatterns.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var raw = rawPart.Trim();
                if (raw.Length == 0) continue;

                var literalChars = raw.Count(c => c != '*' && c != '?');
                if (literalChars < 4)
                {
                    failure = $"MessageTraceMatch pattern '{raw}' is not allowed because it has fewer than 4 literal characters.";
                    logger?.LogWarning(failure);
                    return false;
                }

                if (raw.All(c => c == '*' || c == '?'))
                {
                    failure = $"MessageTraceMatch pattern '{raw}' is not allowed because it contains only wildcards.";
                    logger?.LogWarning(failure);
                    return false;
                }

                if (IsContainsLiteral(raw))
                {
                    patterns.Add(Pattern.Contains(raw, raw.Substring(1, raw.Length - 2)));
                }
                else
                {
                    try
                    {
                        patterns.Add(Pattern.Wildcard(raw, new Regex("^" + WildcardToRegex(raw) + "$",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                            RegexTimeout)));
                    }
                    catch (ArgumentException ex)
                    {
                        failure = $"MessageTraceMatch pattern '{raw}' could not be compiled: {ex.Message}";
                        logger?.LogWarning(failure);
                        return false;
                    }
                }
            }

            if (patterns.Count == 0) return false;
            matcher = new MessageTracePatternMatcher(patterns, logger);
            return true;
        }

        public bool TryMatch(string message, out string matchedPattern)
        {
            matchedPattern = null;
            if (message == null) return false;

            foreach (var pattern in _patterns)
            {
                try
                {
                    if (pattern.IsMatch(message))
                    {
                        matchedPattern = pattern.Raw;
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger?.LogWarning($"Message tracing wildcard pattern '{pattern.Raw}' timed out and was treated as no match.");
                }
            }

            return false;
        }

        private static bool IsContainsLiteral(string raw)
        {
            if (raw.Length < 3 || raw[0] != '*' || raw[raw.Length - 1] != '*') return false;
            for (var i = 1; i < raw.Length - 1; i++)
            {
                if (raw[i] == '*' || raw[i] == '?') return false;
            }
            return true;
        }

        private static string WildcardToRegex(string raw)
        {
            var parts = raw.Select(c =>
            {
                if (c == '*') return ".*";
                if (c == '?') return ".";
                return Regex.Escape(c.ToString(CultureInfo.InvariantCulture));
            });
            return string.Concat(parts);
        }

        private sealed class Pattern
        {
            private readonly string _literal;
            private readonly Regex _regex;

            private Pattern(string raw, string literal, Regex regex)
            {
                Raw = raw;
                _literal = literal;
                _regex = regex;
            }

            public string Raw { get; }

            public static Pattern Contains(string raw, string literal) => new Pattern(raw, literal, null);
            public static Pattern Wildcard(string raw, Regex regex) => new Pattern(raw, null, regex);

            public bool IsMatch(string message)
            {
                return _literal != null
                    ? message.IndexOf(_literal, StringComparison.OrdinalIgnoreCase) >= 0
                    : _regex.IsMatch(message);
            }
        }
    }

    public sealed class HttpMessageTraceEnvelope
    {
        public string Source { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string Method { get; set; }
        public Uri RequestUri { get; set; }
        public int StatusCode { get; set; }
        public string ContentType { get; set; }
        public string MatchedPattern { get; set; }
        public byte[] Body { get; set; }
    }

    public sealed class MessageTraceInspectingTracer : IHttpMessageTracer
    {
        private static readonly string[] SecretQueryNames = { "sig", "code", "token", "key", "secret" };
        private readonly MessageTracePatternMatcher _matcher;
        private readonly IMessageTraceSink _sink;
        private readonly long _maxBodyBytes;
        private readonly int _maxPerHour;
        private readonly ILogger _logger;
        private DateTime _hourUtc;
        private int _acceptedThisHour;
        private int _capLogged;

        public MessageTraceInspectingTracer(MessageTracePatternMatcher matcher, IMessageTraceSink sink, long maxBodyBytes, int maxPerHour, ILogger logger)
        {
            _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _maxBodyBytes = maxBodyBytes > 0 ? maxBodyBytes : 32L * 1024 * 1024;
            _maxPerHour = maxPerHour > 0 ? maxPerHour : 500;
            _logger = logger;
            _hourUtc = TruncateToHour(DateTime.UtcNow);
        }

        public bool IsEnabled => true;

        public long SkippedOversize => _skippedOversize;
        public long SkippedSecrets => _skippedSecrets;
        private long _skippedOversize;
        private long _skippedSecrets;

        public async Task TraceAsync(string source, HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (request?.RequestUri == null || response?.Content == null) return;
            if (IsExcludedEndpoint(request.RequestUri)) return;
            if (!IsJson(response.Content.Headers.ContentType?.MediaType)) return;

            var length = response.Content.Headers.ContentLength;
            if (length.HasValue && length.Value > _maxBodyBytes)
            {
                Interlocked.Increment(ref _skippedOversize);
                return;
            }

            byte[] body;
            try
            {
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is HttpRequestException || ex is IOException)
            {
                Interlocked.Increment(ref _skippedOversize);
                return;
            }

            if (body.LongLength > _maxBodyBytes)
            {
                Interlocked.Increment(ref _skippedOversize);
                return;
            }

            var text = System.Text.Encoding.UTF8.GetString(body);
            if (ContainsTokenBody(text))
            {
                Interlocked.Increment(ref _skippedSecrets);
                return;
            }

            if (!_matcher.TryMatch(text, out var matchedPattern)) return;
            if (!TryTakeHourlySlot())
            {
                _sink.RecordDroppedByHourlyCap();
                if (Interlocked.Exchange(ref _capLogged, 1) == 0)
                {
                    _logger?.LogWarning($"Message tracing hourly cap reached ({_maxPerHour} saved messages/hour/process). Further matching messages are dropped until the next hour.");
                }
                return;
            }

            try
            {
                _sink.TryEnqueue(new HttpMessageTraceEnvelope
                {
                    CapturedUtc = DateTime.UtcNow,
                    Source = source,
                    Method = request.Method?.Method,
                    RequestUri = SanitizeUrl(request.RequestUri),
                    StatusCode = (int)response.StatusCode,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    MatchedPattern = matchedPattern,
                    Body = body,
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Message tracing enqueue failed and was ignored: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool TryTakeHourlySlot()
        {
            var nowHour = TruncateToHour(DateTime.UtcNow);
            if (nowHour != _hourUtc)
            {
                _hourUtc = nowHour;
                Interlocked.Exchange(ref _acceptedThisHour, 0);
                Interlocked.Exchange(ref _capLogged, 0);
            }
            return Interlocked.Increment(ref _acceptedThisHour) <= _maxPerHour;
        }

        private static DateTime TruncateToHour(DateTime value) => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);

        public static bool IsExcludedEndpoint(Uri uri)
        {
            var host = uri.Host;
            if (host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Equals("login.microsoft.com", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase)) return true;
            var path = uri.AbsolutePath ?? string.Empty;
            return path.IndexOf("aiInteractionHistory", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("getAllEnterpriseInteractions", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("/interactionHistory/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static Uri SanitizeUrl(Uri uri)
        {
            if (uri == null || string.IsNullOrEmpty(uri.Query)) return uri;
            var builder = new UriBuilder(uri) { Query = string.Empty };
            var query = uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (var part in query)
            {
                var name = part.Split('=')[0];
                if (SecretQueryNames.Any(secret => name.IndexOf(secret, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                kept.Add(part);
            }
            builder.Query = string.Join("&", kept);
            return builder.Uri;
        }

        private static bool IsJson(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType)) return false;
            mediaType = mediaType.Trim();
            return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsTokenBody(string text)
        {
            return text?.IndexOf("\"access_token\"", StringComparison.OrdinalIgnoreCase) >= 0
                || text?.IndexOf("\"refresh_token\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public interface IMessageTraceSink
    {
        bool TryEnqueue(HttpMessageTraceEnvelope envelope);
        void RecordDroppedByHourlyCap();
    }
}
