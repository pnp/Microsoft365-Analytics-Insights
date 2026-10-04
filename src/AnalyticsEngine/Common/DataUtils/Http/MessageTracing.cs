using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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
                catch (HttpRequestException)
                {
                    throw;
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

                patterns.Add(IsContainsLiteral(raw)
                    ? Pattern.Contains(raw, raw.Substring(1, raw.Length - 2))
                    : Pattern.Wildcard(raw));
            }

            if (patterns.Count == 0) return false;
            matcher = new MessageTracePatternMatcher(patterns, logger);
            return true;
        }

        public bool TryMatch(byte[] utf8Message, out string matchedPattern)
        {
            matchedPattern = null;
            if (utf8Message == null) return false;

            foreach (var pattern in _patterns)
            {
                if (pattern.TryMatchBytes(utf8Message))
                {
                    matchedPattern = pattern.Raw;
                    return true;
                }
            }

            if (!_patterns.Any(p => p.RequiresTextMatch)) return false;

            var message = System.Text.Encoding.UTF8.GetString(utf8Message);
            foreach (var pattern in _patterns.Where(p => p.RequiresTextMatch))
            {
                if (pattern.IsMatch(message))
                {
                    matchedPattern = pattern.Raw;
                    return true;
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

        private sealed class Pattern
        {
            private readonly string _literal;
            private readonly byte[] _asciiLiteralUpper;
            private readonly Segment[] _segments;
            private readonly bool _startsWithWildcard;
            private readonly bool _endsWithWildcard;

            private Pattern(string raw, string literal, Segment[] segments, bool startsWithWildcard, bool endsWithWildcard)
            {
                Raw = raw;
                _literal = literal;
                _asciiLiteralUpper = literal != null && literal.All(c => c <= 127)
                    ? literal.Select(c => (byte)char.ToUpperInvariant(c)).ToArray()
                    : null;
                _segments = segments;
                _startsWithWildcard = startsWithWildcard;
                _endsWithWildcard = endsWithWildcard;
            }

            public string Raw { get; }
            public bool RequiresTextMatch => _asciiLiteralUpper == null;

            public static Pattern Contains(string raw, string literal) => new Pattern(raw, literal, null, true, true);
            public static Pattern Wildcard(string raw)
            {
                var segments = raw.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Segment.Create)
                    .ToArray();
                return new Pattern(raw, null, segments, raw[0] == '*', raw[raw.Length - 1] == '*');
            }

            public bool TryMatchBytes(byte[] message)
            {
                if (_asciiLiteralUpper == null) return false;
                for (var i = 0; i <= message.Length - _asciiLiteralUpper.Length; i++)
                {
                    var matched = true;
                    for (var j = 0; j < _asciiLiteralUpper.Length; j++)
                    {
                        var b = message[i + j];
                        if (b >= (byte)'a' && b <= (byte)'z') b = (byte)(b - 32);
                        if (b != _asciiLiteralUpper[j])
                        {
                            matched = false;
                            break;
                        }
                    }
                    if (matched) return true;
                }
                return false;
            }

            public bool IsMatch(string message)
            {
                if (_literal != null) return message.IndexOf(_literal, StringComparison.OrdinalIgnoreCase) >= 0;
                if (_segments.Length == 0) return message.Length == 0;

                var pos = 0;
                for (var i = 0; i < _segments.Length; i++)
                {
                    var last = i == _segments.Length - 1;
                    int match;
                    if (i == 0 && !_startsWithWildcard)
                    {
                        match = _segments[i].MatchesAt(message, 0) ? 0 : -1;
                    }
                    else if (last && !_endsWithWildcard)
                    {
                        var exact = message.Length - _segments[i].Length;
                        match = exact >= pos && _segments[i].MatchesAt(message, exact) ? exact : -1;
                    }
                    else
                    {
                        match = _segments[i].IndexIn(message, pos);
                    }

                    if (match < 0) return false;
                    pos = match + _segments[i].Length;
                }
                return _endsWithWildcard || pos == message.Length;
            }
        }

        private sealed class Segment
        {
            private readonly string[] _literalParts;

            private Segment(string raw)
            {
                Raw = raw;
                _literalParts = raw.Split('?');
            }

            public string Raw { get; }
            public int Length => Raw.Length;

            public static Segment Create(string raw) => new Segment(raw);

            public int IndexIn(string message, int start)
            {
                for (var i = start; i <= message.Length - Length; i++)
                {
                    if (MatchesAt(message, i)) return i;
                }
                return -1;
            }

            public bool MatchesAt(string message, int index)
            {
                if (index < 0 || index + Length > message.Length) return false;

                var offset = index;
                for (var i = 0; i < _literalParts.Length; i++)
                {
                    var part = _literalParts[i];
                    if (part.Length > 0 && string.Compare(message, offset, part, 0, part.Length, ignoreCase: true, culture: CultureInfo.InvariantCulture) != 0)
                    {
                        return false;
                    }
                    offset += part.Length;
                    if (i < _literalParts.Length - 1) offset++;
                }
                return true;
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
            _maxBodyBytes = maxBodyBytes > 0 ? Math.Min(maxBodyBytes, 256L * 1024 * 1024) : 32L * 1024 * 1024;
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
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is HttpRequestException || ex is IOException)
            {
                throw new HttpRequestException("Message tracing could not buffer the HTTP response body before the caller read it.", ex);
            }

            if (body.LongLength > _maxBodyBytes)
            {
                Interlocked.Increment(ref _skippedOversize);
                return;
            }

            if (ContainsTokenBody(body))
            {
                Interlocked.Increment(ref _skippedSecrets);
                return;
            }

            if (!_matcher.TryMatch(body, out var matchedPattern)) return;
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

        private static bool ContainsTokenBody(byte[] body)
        {
            return ContainsAscii(body, "\"access_token\"") || ContainsAscii(body, "\"refresh_token\"");
        }

        private static bool ContainsAscii(byte[] body, string needle)
        {
            if (body == null || body.Length < needle.Length) return false;
            var upper = needle.Select(c => (byte)char.ToUpperInvariant(c)).ToArray();
            for (var i = 0; i <= body.Length - upper.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < upper.Length; j++)
                {
                    var b = body[i + j];
                    if (b >= (byte)'a' && b <= (byte)'z') b = (byte)(b - 32);
                    if (b != upper[j])
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched) return true;
            }
            return false;
        }
    }

    public interface IMessageTraceSink
    {
        bool TryEnqueue(HttpMessageTraceEnvelope envelope);
        void RecordDroppedByHourlyCap();
    }
}
