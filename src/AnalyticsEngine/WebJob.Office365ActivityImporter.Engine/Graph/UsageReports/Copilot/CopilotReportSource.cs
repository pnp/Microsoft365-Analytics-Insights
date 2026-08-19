using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
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

    /// <summary>
    /// Loads a Copilot usage report over the same plumbing every other Graph usage report in this solution
    /// uses: <see cref="ManualGraphCallClient"/> with <c>$format=application/json</c>, paged by
    /// <see cref="PageableGraphLoaderExtensions.LoadAllPagesWithThrottleRetries{T}"/>, sharing its 429
    /// handling and retry budget.
    ///
    /// Rows come back as <see cref="JObject"/> rather than a fixed DTO on purpose. The reports carry one
    /// property pair per Copilot surface (<c>wordEnabledUsers</c> / <c>wordActiveUsers</c>) and Microsoft
    /// keeps adding surfaces - Edge, Microsoft 365 Copilot and Copilot Chat work/web all arrived in one
    /// revision. Reading properties dynamically means a new app becomes new rows in the narrow/tall table
    /// instead of a new column, a new DTO property and a schema migration on every customer database. It also
    /// means the report-version 2 fields, whose beta JSON names Microsoft has not published, are picked up if
    /// present and simply absent if not, rather than silently binding to null.
    /// </summary>
    public class GraphCopilotReportSource : ICopilotReportSource
    {
        private readonly ManualGraphCallClient _client;
        private readonly ILogger _logger;

        public GraphCopilotReportSource(ManualGraphCallClient client, ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<JObject>> LoadReportAsync(CopilotReportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            _logger.LogInformation($"Loading Copilot report {request}...");
            return await _client.LoadAllPagesWithThrottleRetries<JObject>(request.Url, _logger);
        }
    }
}
