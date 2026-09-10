using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Authentication.Azure;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;

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

        /// <summary>
        /// Create the SDK-backed Graph client used by user metadata and licence imports.
        /// Microsoft.Graph 6.5.0 installs Kiota's RetryHandler in the default pipeline; we
        /// keep it for service-directed transient HTTP retries and add only a bounded wrapper
        /// for local request-deadline timeouts.
        /// </summary>
        public static GraphServiceClient CreateForUserImport(TokenCredential credential, ILogger logger = null)
            => CreateForUserImport(credential, GraphRequestBudgetOptions.UserImportDefault, logger);

        internal static GraphServiceClient CreateForUserImport(TokenCredential credential, GraphRequestBudgetOptions budget, ILogger logger, HttpMessageHandler finalHandler = null)
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

            var graphOptions = new GraphClientOptions();
            var handlers = GraphClientFactory.CreateDefaultHandlers(graphOptions).ToList();

            for (var i = 0; i < handlers.Count; i++)
            {
                if (handlers[i] is RetryHandler)
                {
                    handlers[i] = new RetryHandler(new RetryHandlerOption
                    {
                        MaxRetry = budget.SdkRetryCount,
                        Delay = budget.SdkRetryDelaySeconds,
                        RetriesTimeLimit = budget.SdkRetryTimeLimit
                    });
                }
            }

            handlers.Insert(0, new BoundedGraphRequestHandler(budget, logger));
            var httpClient = GraphClientFactory.Create(authProvider, handlers, finalHandler: finalHandler);
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
            budget.Log(logger);
            return httpClient;
        }

        /// <summary>
        /// Legacy helper kept for callers outside the user import path. Prefer
        /// <see cref="CreateForUserImport"/> for SDK-backed user/licence enumeration.
        /// </summary>
        public static GraphServiceClient CreateWithTimeout(TokenCredential credential, TimeSpan timeout)
        {
            var authProvider = new AzureIdentityAuthenticationProvider(credential, scopes: DefaultScopes);
            var httpClient = GraphClientFactory.Create(authProvider);
            httpClient.Timeout = timeout;
            return new GraphServiceClient(httpClient, authProvider);
        }
    }
}

