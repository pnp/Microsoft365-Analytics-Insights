using DataUtils.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// An open CSV report stream. Owns the underlying HTTP response, so disposing this releases the socket.
    /// </summary>
    public sealed class CopilotReportCsvStream : IDisposable
    {
        private readonly IDisposable _response;
        private readonly IDisposable _deadline;

        public CopilotReportCsvStream(Stream stream, IDisposable response = null, IDisposable deadline = null)
        {
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _response = response;
            _deadline = deadline;
        }

        public Stream Stream { get; }

        /// <summary>Graph's report streams are UTF-8, with a byte-order mark.</summary>
        public TextReader CreateReader() => new StreamReader(Stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        public void Dispose()
        {
            Stream.Dispose();
            _response?.Dispose();
            _deadline?.Dispose();
        }
    }

    /// <summary>
    /// Fetches a Copilot usage report as an open CSV stream. Abstracted so the loaders can be unit tested
    /// against canned report payloads with no HTTP and no tenant.
    /// </summary>
    public interface ICopilotReportCsvSource
    {
        Task<CopilotReportCsvStream> OpenReportCsvAsync(CopilotReportRequest request);
    }

    public static class CopilotReportCsvSourceExtensions
    {
        /// <summary>
        /// Reads a whole report into a string. Only for the tenant-aggregate reports, which are a few
        /// thousand rows at most whatever the tenant size. The per-user report is streamed instead - at
        /// ~200,000 licensed users buffering it would cost hundreds of MB before a single row is saved.
        /// </summary>
        public static async Task<string> GetReportCsvAsync(this ICopilotReportCsvSource source, CopilotReportRequest request)
        {
            using (var csv = await source.OpenReportCsvAsync(request))
            using (var reader = csv.CreateReader())
            {
                return await reader.ReadToEndAsync();
            }
        }
    }

    /// <summary>
    /// Downloads a Copilot usage report from Graph as CSV.
    ///
    /// The v1.0 (GA) Copilot report endpoints answer with a CSV <b>stream</b>
    /// (<c>Content-Type: application/octet-stream</c>), not JSON - only the beta endpoints return JSON, and
    /// beta is explicitly unsupported for production. Every other Graph usage-report loader in this codebase
    /// asks for <c>$format=application/json</c> and deserialises, so these reports need this separate path.
    ///
    /// The response is handed back as an open stream (<see cref="HttpCompletionOption.ResponseHeadersRead"/>)
    /// rather than a buffered string, so the per-user report can be parsed row by row.
    ///
    /// Redirects are followed manually. Graph usage reports can answer 302 with a <c>Location</c> pointing at
    /// a storage endpoint; auto-following would carry the Graph bearer token to a host it was not issued for,
    /// which that host can reject. We therefore disable auto-redirect and re-issue the request without any
    /// Authorization header.
    /// </summary>
    public class GraphCopilotReportCsvSource : ICopilotReportCsvSource, IDisposable
    {
        // A large tenant's per-user report is a single long response; the default 100s HttpClient timeout is
        // not enough, and the ctor that takes a handler doesn't apply the 1h default the others do.
        private static readonly TimeSpan ReportDownloadTimeout = TimeSpan.FromHours(1);

        // One shared unauthenticated client for redirect targets. A per-call HttpClient would leak sockets.
        private static readonly HttpClient RedirectFollower = CreateRedirectFollower();

        private readonly ManualGraphCallClient _client;
        private readonly ILogger _logger;
        private readonly bool _ownsClient;

        public GraphCopilotReportCsvSource(ImportAppIndentityOAuthContext appIdentity, ILogger logger)
            : this(new ManualGraphCallClient(
                       new ConfidentialClientApplicationHttpHandler(appIdentity, new HttpClientHandler { AllowAutoRedirect = false }),
                       logger),
                   logger,
                   ownsClient: true)
        {
        }

        /// <summary>Test seam: supply a client built over a fake message handler.</summary>
        public GraphCopilotReportCsvSource(ManualGraphCallClient client, ILogger logger, bool ownsClient = false)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ownsClient = ownsClient;
            _client.Timeout = ReportDownloadTimeout;
        }

        private static HttpClient CreateRedirectFollower()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = ReportDownloadTimeout;
            return client;
        }

        public async Task<CopilotReportCsvStream> OpenReportCsvAsync(CopilotReportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // ResponseHeadersRead means HttpClient.Timeout only covers receiving the HEADERS - the body is
            // then read lazily by the parser and is no longer covered. Without a deadline of its own a stalled
            // download would hold the import open indefinitely, so this cancellation source bounds the whole
            // stream lifetime and aborts the read by disposing the response.
            var deadline = new CancellationTokenSource(ReportDownloadTimeout);
            HttpResponseMessage response = null;
            try
            {
                response = await _client.GetAsyncWithThrottleRetries(request.Url, HttpCompletionOption.ResponseHeadersRead, _logger);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        throw new HttpRequestException(
                            $"Graph returned {(int)response.StatusCode} for {request} but no Location header to follow.");
                    }

                    var target = location.IsAbsoluteUri ? location : new Uri(new Uri(request.Url), location);
                    _logger.LogInformation($"Copilot report {request} redirected to a download endpoint; following without the Graph token.");

                    response.Dispose();
                    response = await RedirectFollower.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                }

                await EnsureSuccess(response, request);

                var completed = response;
                deadline.Token.Register(() =>
                {
                    _logger.LogError($"Copilot report {request} exceeded the {ReportDownloadTimeout.TotalMinutes:N0} minute download deadline; aborting the stream.");
                    completed.Dispose();
                });

                return new CopilotReportCsvStream(await response.Content.ReadAsStreamAsync(), response, deadline);
            }
            catch
            {
                response?.Dispose();
                deadline.Dispose();
                throw;
            }
        }

        private async Task EnsureSuccess(HttpResponseMessage response, CopilotReportRequest request)
        {
            if (response.IsSuccessStatusCode) return;

            // Graph reports the "wrong period for this version" mistake as a plain 400, so surface the body -
            // it names the offending parameter and is the difference between a five-minute fix and an
            // afternoon. Safe to buffer: this is an error payload, not the report.
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError($"Copilot report {request} failed with HTTP {(int)response.StatusCode}. Response body: {body}");
            response.EnsureSuccessStatusCode();
        }

        private static bool IsRedirect(HttpStatusCode status)
        {
            var code = (int)status;
            return code == 301 || code == 302 || code == 303 || code == 307 || code == 308;
        }

        public void Dispose()
        {
            if (_ownsClient) _client?.Dispose();
        }
    }
}
