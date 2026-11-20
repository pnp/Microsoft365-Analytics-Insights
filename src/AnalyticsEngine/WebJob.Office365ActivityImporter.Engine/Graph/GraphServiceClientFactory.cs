using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Authentication.Azure;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using DataUtils.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Factory helpers for <see cref="GraphServiceClient"/> v5+. The v4 client exposed
    /// <c>HttpProvider.OverallTimeout</c> directly; in v5+ that property is gone, so we
    /// build a custom <see cref="HttpClient"/> with bounded request and retry budgets.
    /// </summary>
    public static class GraphServiceClientFactory
    {
        private static readonly string[] DefaultScopes = new[] { "https://graph.microsoft.com/.default" };
        public static readonly TimeSpan DefaultGraphSdkTimeout = TimeSpan.FromSeconds(100);

        /// <summary>
        /// Create the SDK-backed Graph client used by the Graph importer.
        /// Microsoft.Graph 6.5.0 installs Kiota's RetryHandler in the default pipeline; we
        /// keep it for service-directed transient HTTP retries (429/503/504 and Retry-After) and
        /// add a bounded handler inside it for local per-attempt request-deadline timeouts. The
        /// deadline applies to every method; only idempotent reads get local timeout retries.
        /// </summary>
        public static GraphServiceClient CreateForGraphImport(TokenCredential credential, ILogger logger = null)
            => CreateForGraphImport(credential, GraphRequestBudgetOptions.GraphImportDefault, logger);

        public static GraphServiceClient CreateForUserImport(TokenCredential credential, ILogger logger = null)
            => CreateForGraphImport(credential, logger);

        internal static GraphServiceClient CreateForGraphImport(TokenCredential credential, GraphRequestBudgetOptions budget, ILogger logger, HttpMessageHandler finalHandler = null)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            var authProvider = new AzureIdentityAuthenticationProvider(credential, scopes: DefaultScopes);
            var httpClient = CreateHttpClient(authProvider, budget, logger, finalHandler);
            return new GraphServiceClient(httpClient, authProvider);
        }

        internal static HttpClient CreateHttpClient(AzureIdentityAuthenticationProvider authProvider, GraphRequestBudgetOptions budget, ILogger logger, HttpMessageHandler finalHandler = null)
        {
            if (authProvider == null) throw new ArgumentNullException(nameof(authProvider));
            if (budget == null) throw new ArgumentNullException(nameof(budget));

            var handlers = BuildGraphImportHandlers(budget, logger);
            if (HttpMessageTracing.Current.IsEnabled)
            {
                handlers.Insert(0, new MessageTraceHandler("graph"));
            }
            var httpClient = GraphClientFactory.Create(authProvider, handlers, finalHandler: finalHandler);
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
            budget.Log(logger);
            return httpClient;
        }

        /// <summary>
        /// The SDK pipeline for Graph import, outermost first. The bounded deadline handler sits
        /// directly INSIDE Kiota's <see cref="RetryHandler"/>, so its deadline covers one HTTP
        /// attempt and never a Retry-After wait (#846 R8-10). Placed outside, a Retry-After longer
        /// than the per-request deadline was cancelled part-way, counted as a timeout and re-sent
        /// without honouring Retry-After.
        /// </summary>
        internal static System.Collections.Generic.List<DelegatingHandler> BuildGraphImportHandlers(GraphRequestBudgetOptions budget, ILogger logger)
        {
            if (budget == null) throw new ArgumentNullException(nameof(budget));

            var handlers = GraphClientFactory.CreateDefaultHandlers(new GraphClientOptions()).ToList();
            var retryIndex = handlers.FindIndex(h => h is RetryHandler);
            if (retryIndex < 0)
            {
                throw new InvalidOperationException("The Graph SDK default pipeline no longer contains Kiota's RetryHandler; the per-attempt request deadline cannot be placed inside it.");
            }

            handlers[retryIndex] = new RetryHandler(new RetryHandlerOption
            {
                MaxRetry = budget.SdkRetryCount,
                Delay = budget.SdkRetryDelaySeconds,
                RetriesTimeLimit = budget.SdkRetryTimeLimit
            });
            handlers.Insert(retryIndex + 1, new BoundedGraphRequestHandler(budget, logger));
            handlers.Insert(0, new GraphTransientErrorHandler());
            return handlers;
        }

        /// <summary>
        /// Legacy helper kept for callers outside the user import path. Prefer
        /// <see cref="CreateForGraphImport"/> for SDK-backed importer work.
        /// </summary>
        public static GraphServiceClient CreateWithTimeout(TokenCredential credential, TimeSpan timeout)
        {
            var authProvider = new AzureIdentityAuthenticationProvider(credential, scopes: DefaultScopes);
            return CreateWithTimeout(authProvider, timeout);
        }

        public static GraphServiceClient CreateWithTimeout(IAuthenticationProvider authProvider, TimeSpan timeout)
            => CreateWithTimeout(authProvider, timeout, null);

        internal static GraphServiceClient CreateWithTimeout(IAuthenticationProvider authProvider, TimeSpan timeout, HttpMessageHandler finalHandler)
        {
            var handlers = GraphClientFactory.CreateDefaultHandlers(new GraphClientOptions()).ToList();
            handlers.Insert(0, new GraphTransientErrorHandler());
            if (HttpMessageTracing.Current.IsEnabled)
            {
                handlers.Insert(0, new MessageTraceHandler("graph"));
            }
            var httpClient = GraphClientFactory.Create(handlers, finalHandler: finalHandler);
            httpClient.Timeout = timeout;
            return new GraphServiceClient(httpClient, authProvider);
        }
    }
}
