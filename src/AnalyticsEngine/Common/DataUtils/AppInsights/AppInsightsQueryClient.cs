using Azure.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.AppInsights
{
    /// <summary>
    /// Minimal, self-contained client for running Kusto (KQL) queries against the Application Insights
    /// REST query API (<c>https://api.applicationinsights.io</c>), authenticated with the app's existing
    /// Entra ID credential.
    ///
    /// This deliberately does NOT depend on the importer engine's <c>AppInsightsAPIClient</c> (which is
    /// coupled to the importer's response parsers). It only needs the App Insights connection string (to
    /// read the <c>ApplicationId</c>) and a <see cref="TokenCredential"/> - the same credential and config
    /// the web app already has - so surfacing App Insights data (e.g. the in-app Health dashboard) requires
    /// no new API key or configuration. See HEALTH-MONITORING-DESIGN.md and issue #144.
    /// </summary>
    public class AppInsightsQueryClient : IDisposable
    {
        private readonly string _appInsightsId;
        private readonly TokenCredential _credential;
        private readonly ILogger _logger;
        private static readonly string[] AppInsightsScope = new[] { "https://api.applicationinsights.io/.default" };
        private AccessToken? _cachedToken;
        private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);
        private readonly HttpClient _client = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

        private const int MaxRetries = 3;
        private const int MaxBackoffSeconds = 30;

        /// <param name="appInsightsConnectionString">The Application Insights connection string. The <c>ApplicationId</c> is parsed from it for the query URL.</param>
        /// <param name="credential">A <see cref="TokenCredential"/> (e.g. ClientSecretCredential) for Entra ID authentication.</param>
        /// <param name="logger">Logger.</param>
        public AppInsightsQueryClient(string appInsightsConnectionString, TokenCredential credential, ILogger logger)
        {
            if (string.IsNullOrEmpty(appInsightsConnectionString))
            {
                throw new ArgumentException($"'{nameof(appInsightsConnectionString)}' cannot be null or empty.", nameof(appInsightsConnectionString));
            }

            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _logger = logger;

            _appInsightsId = ParseConnectionStringValue(appInsightsConnectionString, "ApplicationId");
            if (string.IsNullOrEmpty(_appInsightsId))
            {
                throw new ArgumentException("Could not parse ApplicationId from the provided connection string.", nameof(appInsightsConnectionString));
            }

            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <summary>
        /// Parses a named value (e.g. "ApplicationId", "InstrumentationKey") from an App Insights connection string.
        /// </summary>
        public static string ParseConnectionStringValue(string connectionString, string keyName)
        {
            if (string.IsNullOrEmpty(connectionString)) return null;
            foreach (var part in connectionString.Split(';'))
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = part.Substring(0, separatorIndex).Trim();
                    var value = part.Substring(separatorIndex + 1).Trim();
                    if (key.Equals(keyName, StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Runs a KQL query and returns the primary result table, or an empty table if the query yielded no tables.
        /// </summary>
        public async Task<AppInsightsQueryTable> RunQueryAsync(string kql)
        {
            var response = await RunQueryRawAsync(kql);
            return response?.PrimaryTable ?? AppInsightsQueryTable.Empty;
        }

        /// <summary>
        /// Runs a KQL query and returns the full deserialised response (all tables).
        /// </summary>
        public async Task<AppInsightsQueryResponse> RunQueryRawAsync(string kql)
        {
            if (string.IsNullOrWhiteSpace(kql)) throw new ArgumentException("Query cannot be empty.", nameof(kql));

            await SetBearerToken();

            var url = $"https://api.applicationinsights.io/v1/apps/{_appInsightsId}/query?query={Uri.EscapeDataString(kql)}";
            var response = await GetWithRetry(url);
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"App Insights query failed (HTTP {(int)response.StatusCode}): {body}");
                }
                return JsonConvert.DeserializeObject<AppInsightsQueryResponse>(body);
            }
        }

        private async Task SetBearerToken()
        {
            // Refresh only when missing or within 5 minutes of expiry, to avoid a token call per query.
            await _tokenLock.WaitAsync();
            try
            {
                if (!_cachedToken.HasValue || _cachedToken.Value.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    var tokenRequestContext = new TokenRequestContext(AppInsightsScope);
                    _cachedToken = await _credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);
                    _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken.Value.Token);
                    _logger?.LogInformation("Refreshed App Insights query bearer token.");
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private async Task<HttpResponseMessage> GetWithRetry(string url)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var response = await _client.GetAsync(url);
                    if (response.IsSuccessStatusCode || !IsTransientStatusCode(response.StatusCode) || attempt >= MaxRetries)
                    {
                        return response;
                    }

                    var backoff = Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
                    _logger?.LogWarning($"Transient HTTP {(int)response.StatusCode} from App Insights query API. Retrying in {backoff}s (attempt {attempt + 1}/{MaxRetries})...");
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(backoff));
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && attempt < MaxRetries)
                {
                    var backoff = Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
                    _logger?.LogWarning($"Transient error calling App Insights query API: {ex.Message}. Retrying in {backoff}s (attempt {attempt + 1}/{MaxRetries})...");
                    await Task.Delay(TimeSpan.FromSeconds(backoff));
                }
            }
        }

        private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == (HttpStatusCode)429
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout
                || statusCode == HttpStatusCode.RequestTimeout;
        }

        public void Dispose()
        {
            _client.Dispose();
            _tokenLock.Dispose();
        }
    }

    /// <summary>Deserialised App Insights query response (the <c>tables</c> array).</summary>
    public class AppInsightsQueryResponse
    {
        [JsonProperty("tables")]
        public List<AppInsightsQueryTable> Tables { get; set; }

        /// <summary>The primary result table (named "PrimaryResult" if present, else the first table).</summary>
        [JsonIgnore]
        public AppInsightsQueryTable PrimaryTable
        {
            get
            {
                if (Tables == null || Tables.Count == 0) return AppInsightsQueryTable.Empty;
                return Tables.FirstOrDefault(t => string.Equals(t.Name, "PrimaryResult", StringComparison.OrdinalIgnoreCase)) ?? Tables[0];
            }
        }
    }

    /// <summary>A single App Insights result table with typed cell accessors.</summary>
    public class AppInsightsQueryTable
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("columns")]
        public List<AppInsightsQueryColumn> Columns { get; set; } = new List<AppInsightsQueryColumn>();

        [JsonProperty("rows")]
        public List<List<object>> Rows { get; set; } = new List<List<object>>();

        public static AppInsightsQueryTable Empty => new AppInsightsQueryTable();

        public int RowCount => Rows?.Count ?? 0;

        /// <summary>Zero-based index of a column by name, or -1 if the column is not present.</summary>
        public int ColumnIndex(string columnName)
        {
            if (Columns == null) return -1;
            for (int i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public string GetString(IReadOnlyList<object> row, string columnName)
        {
            var value = GetRaw(row, columnName);
            return value?.ToString();
        }

        public long? GetLong(IReadOnlyList<object> row, string columnName)
        {
            var value = GetRaw(row, columnName);
            if (value == null) return null;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            return long.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null;
        }

        public int? GetInt(IReadOnlyList<object> row, string columnName)
        {
            var value = GetLong(row, columnName);
            return value.HasValue ? (int?)value.Value : null;
        }

        public DateTime? GetDateTimeUtc(IReadOnlyList<object> row, string columnName)
        {
            var value = GetRaw(row, columnName);
            if (value == null) return null;
            if (value is DateTime dt) return dt.ToUniversalTime();
            return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        private object GetRaw(IReadOnlyList<object> row, string columnName)
        {
            if (row == null) return null;
            var idx = ColumnIndex(columnName);
            if (idx < 0 || idx >= row.Count) return null;
            var value = row[idx];
            return value is string s && string.IsNullOrEmpty(s) ? null : value;
        }
    }

    /// <summary>A column descriptor in an App Insights result table.</summary>
    public class AppInsightsQueryColumn
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }
}
