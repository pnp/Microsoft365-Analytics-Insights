using Azure;
using Azure.AI.TextAnalytics;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DataUtils
{
    public class TextAnalysisSample<T> where T : class
    {
        public string Text { get; set; }
        public string Id { get; set; }

        public T Parent { get; set; } = null;
    }

    public class TextAnalysisResult<T> where T : class
    {
        public CognitiveStat CognitiveStat { get; set; }

        public T Parent { get; set; } = null;
    }

    public class CognitiveStat
    {
        public double? SentimentScore { get; set; } = null;
        public string LanguageName { get; set; }
    }

    /// <summary>
    /// Builds <see cref="TextAnalyticsClient"/> instances that work against both classic
    /// key-authenticated Azure AI Language resources and resources that have disabled key
    /// auth ("Local authentication") and require RBAC / Entra ID tokens.
    /// </summary>
    /// <remarks>
    /// Resources configured with <c>disableLocalAuth = true</c> return
    /// <c>403 AuthenticationTypeDisabled</c> when called with a key, so callers must use a
    /// <see cref="ClientSecretCredential"/> obtained from the runtime service principal
    /// (tenant / client / secret) instead. Per the repo convention for AAD fallbacks we
    /// always use <see cref="ClientSecretCredential"/> here - never <c>DefaultAzureCredential</c>
    /// or managed identity - so behaviour is identical between the webjob, the page-update
    /// worker and unit tests.
    /// <para>
    /// Production callers should prefer <see cref="CognitiveServicesClient"/> rather than this
    /// raw factory because it also retries with RBAC when key auth is rejected at runtime.
    /// </para>
    /// </remarks>
    public static class TextAnalyticsClientFactory
    {
        /// <summary>
        /// Build a <see cref="TextAnalyticsClient"/> from the cognitive endpoint plus either a
        /// key (preferred when set) or a service-principal credential (used when the resource
        /// has key auth disabled). Returns <c>null</c> if <paramref name="endpoint"/> is missing
        /// or no usable credential is supplied so callers can skip cognitive scoring cleanly.
        /// </summary>
        public static TextAnalyticsClient Create(
            string endpoint,
            string key,
            string tenantId,
            string clientId,
            string clientSecret,
            ILogger logger = null)
        {
            if (string.IsNullOrEmpty(endpoint))
                return null;

            var uri = new Uri(endpoint);

            if (!string.IsNullOrEmpty(key))
            {
                logger?.LogInformation("Building TextAnalyticsClient using key-based authentication.");
                return new TextAnalyticsClient(uri, new AzureKeyCredential(key));
            }

            return CreateRbacClient(uri, tenantId, clientId, clientSecret, logger);
        }

        /// <summary>
        /// Build a <see cref="TextAnalyticsClient"/> using RBAC / Entra ID
        /// (<see cref="ClientSecretCredential"/>). Returns <c>null</c> if any
        /// of the service-principal fields are missing.
        /// </summary>
        internal static TextAnalyticsClient CreateRbacClient(
            Uri endpoint, string tenantId, string clientId, string clientSecret, ILogger logger)
        {
            if (endpoint == null) return null;
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                logger?.LogWarning(
                    "Cognitive endpoint is set but no key and no service-principal credentials were supplied; " +
                    "cannot build TextAnalyticsClient.");
                return null;
            }

            logger?.LogInformation("Building TextAnalyticsClient using RBAC/Entra ID (ClientSecretCredential).");
            return new TextAnalyticsClient(endpoint, new ClientSecretCredential(tenantId, clientId, clientSecret));
        }
    }

    /// <summary>
    /// Thread-safe wrapper around a single cached <see cref="TextAnalyticsClient"/> that
    /// automatically falls back to RBAC (<see cref="ClientSecretCredential"/>) when key
    /// authentication is rejected by the Azure AI Language resource (HTTP 401 / 403
    /// <c>AuthenticationTypeDisabled</c>). The fallback is permanent for the wrapper's
    /// lifetime so we only pay the round-trip cost once.
    /// </summary>
    /// <remarks>
    /// The Azure SDK guidance is to reuse <see cref="TextAnalyticsClient"/> instances
    /// (they're thread-safe and pool HTTP connections), so callers should construct one
    /// wrapper per logical scope (importer run, web job, etc.) and reuse it rather than
    /// rebuilding per batch.
    /// </remarks>
    public class CognitiveServicesClient
    {
        private readonly Uri _endpoint;
        private readonly string _key;
        private readonly string _tenantId;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly ILogger _logger;
        private readonly object _swapLock = new object();
        // _inner is swapped at most once (key -> RBAC) under _swapLock. Reads happen on
        // the hot path outside the lock; marking it volatile guarantees readers see the
        // post-swap reference without paying the lock cost on every Execute call.
        private volatile TextAnalyticsClient _inner;
        private volatile bool _usingRbac;

        /// <summary>
        /// Try to build a <see cref="CognitiveServicesClient"/>; returns <c>null</c> when
        /// the endpoint is missing or no usable credential (key or service principal) is
        /// available so callers can skip cognitive scoring cleanly.
        /// </summary>
        public static CognitiveServicesClient TryCreate(
            string endpoint, string key, string tenantId, string clientId, string clientSecret, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(endpoint)) return null;

            var hasKey = !string.IsNullOrEmpty(key);
            var hasRbac =
                !string.IsNullOrEmpty(tenantId) &&
                !string.IsNullOrEmpty(clientId) &&
                !string.IsNullOrEmpty(clientSecret);

            if (!hasKey && !hasRbac)
            {
                logger?.LogWarning(
                    "Cognitive endpoint is set but no key and no service-principal credentials were supplied; " +
                    "cannot build CognitiveServicesClient.");
                return null;
            }

            return new CognitiveServicesClient(endpoint, key, tenantId, clientId, clientSecret, logger);
        }

        public CognitiveServicesClient(
            string endpoint, string key, string tenantId, string clientId, string clientSecret, ILogger logger = null)
        {
            if (string.IsNullOrEmpty(endpoint))
                throw new ArgumentException("Cognitive endpoint required.", nameof(endpoint));

            _endpoint = new Uri(endpoint);
            _key = key;
            _tenantId = tenantId;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _logger = logger;

            if (!string.IsNullOrEmpty(_key))
            {
                _inner = new TextAnalyticsClient(_endpoint, new AzureKeyCredential(_key));
                _usingRbac = false;
                _logger?.LogInformation("CognitiveServicesClient initialized using key authentication.");
            }
            else
            {
                _inner = TextAnalyticsClientFactory.CreateRbacClient(_endpoint, _tenantId, _clientId, _clientSecret, _logger);
                if (_inner == null)
                    throw new InvalidOperationException("Cognitive endpoint set but no key and no service-principal credentials supplied.");
                _usingRbac = true;
            }
        }

        /// <summary>True once the wrapper has switched to RBAC after a key-auth failure.</summary>
        public bool UsingRbac => _usingRbac;

        private bool CanFallbackToRbac =>
            !string.IsNullOrEmpty(_tenantId) &&
            !string.IsNullOrEmpty(_clientId) &&
            !string.IsNullOrEmpty(_clientSecret);

        /// <summary>
        /// Invoke an Azure AI Language call against the cached <see cref="TextAnalyticsClient"/>.
        /// If the call fails with an authentication error that looks like the resource has key
        /// auth disabled (<c>403 AuthenticationTypeDisabled</c> or a generic 401), and RBAC
        /// credentials are available, the inner client is swapped for an RBAC one and the
        /// call is retried once.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<TextAnalyticsClient, Task<T>> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                return await action(_inner).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ShouldFallbackToRbac(ex))
            {
                _logger?.LogWarning(
                    "Cognitive call failed with HTTP {Status} (ErrorCode '{Code}') - key auth appears to be disabled on the resource. " +
                    "Falling back to RBAC (ClientSecretCredential) and retrying.",
                    ex.Status, ex.ErrorCode ?? "");

                SwitchToRbac();
                return await action(_inner).ConfigureAwait(false);
            }
        }

        /// <summary>Convenience overload for fire-and-forget style calls returning <see cref="Task"/>.</summary>
        public Task ExecuteAsync(Func<TextAnalyticsClient, Task> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return ExecuteAsync<bool>(async c => { await action(c).ConfigureAwait(false); return true; });
        }

        private bool ShouldFallbackToRbac(RequestFailedException ex)
        {
            if (_usingRbac) return false;
            if (!CanFallbackToRbac) return false;

            // Signature of "key auth disabled" - prefer the explicit ErrorCode when present.
            if (string.Equals(ex.ErrorCode, "AuthenticationTypeDisabled", StringComparison.OrdinalIgnoreCase))
                return true;

            // Generic auth failures while using a key are also worth retrying with RBAC.
            return ex.Status == 401 || ex.Status == 403;
        }

        private void SwitchToRbac()
        {
            lock (_swapLock)
            {
                if (_usingRbac) return;
                var rbacClient = TextAnalyticsClientFactory.CreateRbacClient(_endpoint, _tenantId, _clientId, _clientSecret, _logger);
                if (rbacClient == null)
                {
                    // Defensive - CanFallbackToRbac should already have guarded this.
                    throw new InvalidOperationException("Cannot fall back to RBAC: no service-principal credentials configured.");
                }
                _inner = rbacClient;
                _usingRbac = true;
            }
        }
    }

    /// <summary>
    /// Common cognitive services extension methods for text analysis; Teams messages, etc.
    /// </summary>
    public static class CognitiveExtensions
    {
        const int API_MAX_BATCH_SIZE = 10;

        /// <summary>
        /// Loads Azure Cognitive data for a message. Routes calls through
        /// <see cref="CognitiveServicesClient"/> so key-auth failures auto-retry with RBAC.
        /// </summary>
        public static async Task<List<TextAnalysisResult<T>>> GetCognitiveDataStats<T>(this IEnumerable<TextAnalysisSample<T>> inputData, CognitiveServicesClient client, ILogger logger) where T : class
        {
            if (inputData == null || inputData.Count() == 0) return null;
            if (client == null) return null;

            var validInput = new ConcurrentBag<TextAnalysisSample<T>>();

            // Step 1: detect language msg
            var detectedLanguages = new List<DetectLanguageResult>();
            bool success = false;
            try
            {
                // Break down API calls into chunks of 10 max and compile results
                var listProcessorLang = new ParallelCallsForSingleReturnListHander<TextAnalysisSample<T>, DetectLanguageResult>();
                detectedLanguages = await listProcessorLang.CallAndCompileToSingleList(inputData, async (chunk) =>
                {
                    var languageBatchInput = new List<DetectLanguageInput>(chunk.Count);
                    foreach (var d in chunk)
                    {
                        if (d.Id != null && !string.IsNullOrEmpty(d.Text) && d.Parent != null)
                        {
                            validInput.Add(d);

                            // Detect lang
                            languageBatchInput.Add(new DetectLanguageInput(d.Id, d.Text));
                        }
                    }

                    if (languageBatchInput.Count > 0)
                    {
                        try
                        {
                            var result = await client.ExecuteAsync(c => c.DetectLanguageBatchAsync(languageBatchInput));
                            return result.Value.ToList();
                        }
                        catch (RequestFailedException ex)
                        {
                            logger.LogError(ex, $"Couldn't detect languages: cognitive services error - {ex.Message}");
                            return new List<DetectLanguageResult>();
                        }
                    }
                    else
                    {
                        return new List<DetectLanguageResult>();
                    }
                }, API_MAX_BATCH_SIZE);

                success = true;
            }
            catch (RequestFailedException ex)
            {
                logger.LogError(ex, $"Couldn't detect languages: cognitive services error - {ex.Message}");
                return null;
            }
            if (validInput.Count == 0) return null;
            if (success)
            {
                // Remove for now, too much noise
                //telemetry.TrackEvent(DebugTracer.AnalyticsEvent.AzureAIQuery, "DetectLanguage");
            }


            // Step 2: Send msg content to Azure AI with pre-detected language
            // Build a cognitive input batch using language results

            // O(1) lookups by Id - the Where().FirstOrDefault() pattern below used to be O(N^2).
            var languagesById = new Dictionary<string, DetectLanguageResult>(detectedLanguages.Count, StringComparer.Ordinal);
            foreach (var lang in detectedLanguages)
            {
                if (lang.Id != null)
                    languagesById[lang.Id] = lang;
            }
            var validInputById = new Dictionary<string, TextAnalysisSample<T>>(StringComparer.Ordinal);
            foreach (var item in validInput)
            {
                if (item.Id != null)
                    validInputById[item.Id] = item;
            }

            var sentimentBatchInput = new List<TextDocumentInput>();
            foreach (var dataItem in validInputById.Values)
            {
                // Don't bother getting cognitive stats for anything we can't detect language on
                if (languagesById.ContainsKey(dataItem.Id))
                {
                    sentimentBatchInput.Add(new TextDocumentInput(dataItem.Id, dataItem.Text));
                }
            }

            var analysisResults = new List<TextAnalysisResult<T>>();

            // Is there anything to analyse? Messages might be empty
            if (sentimentBatchInput.Count > 0)
            {
                bool sentimentSuccess = false;

                var listProcessorSentiment = new ParallelCallsForSingleReturnListHander<TextDocumentInput, AnalyzeSentimentResult>();
                var allAnalyzeSentimentResults = new List<AnalyzeSentimentResult>();
                try
                {
                    // Break down API calls into chunks of 10 max and compile results
                    allAnalyzeSentimentResults = await listProcessorSentiment.CallAndCompileToSingleList(sentimentBatchInput, async (chunk) =>
                    {
                        var result = await client.ExecuteAsync(c => c.AnalyzeSentimentBatchAsync(chunk));
                        return result.Value.ToList();
                    }, API_MAX_BATCH_SIZE);

                    sentimentSuccess = true;
                }
                catch (RequestFailedException ex)
                {
                    logger.LogError(ex, $"Cognitive services error {ex.Message}");
                    return null;
                }
                if (sentimentSuccess)
                {
                    logger.LogInformation($"Sentiment results for chat messages: {allAnalyzeSentimentResults.Count} documents processed");

                    foreach (var sentimentResult in allAnalyzeSentimentResults)
                    {
                        languagesById.TryGetValue(sentimentResult.Id, out var langResult);
                        validInputById.TryGetValue(sentimentResult.Id, out var originalInput);

                        if (originalInput != null)
                        {
                            if (!sentimentResult.HasError && langResult != null && !langResult.HasError)
                            {
                                var overralScore = sentimentResult.DocumentSentiment.Sentiment == TextSentiment.Neutral ? 0.5 :
                                sentimentResult.DocumentSentiment.Sentiment == TextSentiment.Positive ? 1 : 0;

                                var stat = new CognitiveStat { SentimentScore = overralScore, LanguageName = langResult.PrimaryLanguage.Name };

                                analysisResults.Add(new TextAnalysisResult<T> { Parent = originalInput.Parent, CognitiveStat = stat });
                            }
                            else
                            {
                                logger.LogError($"Error in sentiment analysis for message: {originalInput.Text}");
                            }
                        }
                        else
                        {
                            logger.LogWarning($"Error in sentiment analysis for message {sentimentResult.Id}. Original message not found.");
                        }
                    }
                }
            }

            return analysisResults;
        }
    }
}
