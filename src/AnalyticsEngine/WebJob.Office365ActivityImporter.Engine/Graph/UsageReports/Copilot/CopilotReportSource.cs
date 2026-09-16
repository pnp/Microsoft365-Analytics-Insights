using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Net;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Loads a Copilot usage report from Graph. Abstracted so the loaders can be unit tested against canned
    /// report payloads with no HTTP and no tenant.
    /// </summary>
    public interface ICopilotReportSource
    {
        /// <summary>
        /// Returns the report's <c>value</c> array, one <see cref="JObject"/> per element, following paging.
        /// </summary>
        Task<List<JObject>> LoadReportAsync(CopilotReportRequest request);
    }

    public interface ICopilotReportLoadProvenance
    {
        string LastSuccessfulVersion { get; }
        string LastSuccessfulPeriod { get; }
    }

    /// <summary>
    /// Loads a Copilot usage report from the GA v1.0 /copilot path, with explicit mid-rollout fallbacks. The
    /// v1.0 endpoint returns a CSV stream, which is converted back to the JSON-shaped objects the existing
    /// parsers consume. If a tenant rejects v2, the source retries v1 on /copilot; if /copilot itself has not
    /// rolled out to that tenant, it retries the legacy beta /reports JSON endpoint to preserve existing
    /// behaviour.
    /// </summary>
    public class GraphCopilotReportSource : ICopilotReportSource, ICopilotReportLoadProvenance
    {
        private readonly ManualGraphCallClient _client;
        private readonly ILogger _logger;

        public GraphCopilotReportSource(ManualGraphCallClient client, ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string LastSuccessfulVersion { get; private set; }
        public string LastSuccessfulPeriod { get; private set; }

        public async Task<List<JObject>> LoadReportAsync(CopilotReportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            _logger.LogInformation($"Loading Copilot report {request} from the v1.0 /copilot endpoint...");

            Exception last = null;
            foreach (var attempt in Attempts(request))
            {
                try
                {
                    List<JObject> rows;
                    if (attempt.IsJson)
                    {
                        rows = await _client.LoadAllPagesWithThrottleRetries<JObject>(attempt.Url, _logger, throwOnNotFound: true, throwOnHttpError: true);
                    }
                    else
                    {
                        var csv = await _client.GetStringAsyncWithThrottleRetries(attempt.Url);
                        rows = CopilotReportCsvParser.Parse(request.ReportName, csv);
                    }

                    LastSuccessfulVersion = attempt.Version;
                    LastSuccessfulPeriod = attempt.Period;
                    return rows;
                }
                catch (Exception ex) when (CanTryFallback(ex, attempt, request))
                {
                    last = ex;
                    _logger.LogWarning($"Copilot report {request}: {attempt.Description} failed ({GraphHttpException.DescribeForStorage(ex)}). Trying fallback.");
                }
            }

            if (last != null) throw last;
            return new List<JObject>();
        }

        private static bool CanTryFallback(Exception ex, ReportAttempt attempt, CopilotReportRequest request)
        {
            if (!attempt.HasFallback) return false;

            if (ex is GraphResourceNotFoundException) return true;
            var graph = ex as GraphHttpException;
            if (graph == null) return false;

            return graph.StatusCode == HttpStatusCode.BadRequest || graph.StatusCode == HttpStatusCode.NotFound;
        }

        private static ReportAttempt[] Attempts(CopilotReportRequest request)
        {
            var attempts = new List<ReportAttempt>
            {
                new ReportAttempt(request.Url, false, "v1.0 /copilot " + request.Version, request.Version, request.Period),
            };

            if (request.Version == CopilotReportVersions.V2)
            {
                attempts.Add(new ReportAttempt(request.V1FallbackUrl, false, "v1.0 /copilot v1", CopilotReportVersions.V1, CopilotReportRequest.V1PeriodFor(request.Period)));
            }

            attempts.Add(new ReportAttempt(request.LegacyJsonFallbackUrl, true, "legacy beta /reports JSON", CopilotReportVersions.V1, CopilotReportRequest.V1PeriodFor(request.Period)));

            for (var i = 0; i < attempts.Count; i++) attempts[i].HasFallback = i < attempts.Count - 1;
            return attempts.ToArray();
        }

        private class ReportAttempt
        {
            public ReportAttempt(string url, bool isJson, string description, string version, string period)
            {
                Url = url;
                IsJson = isJson;
                Description = description;
                Version = version;
                Period = period;
            }

            public string Url { get; }
            public bool IsJson { get; }
            public string Description { get; }
            public string Version { get; }
            public string Period { get; }
            public bool HasFallback { get; set; }
        }
    }
}
