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
using System.Threading.Tasks;
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
        // This factory uses GraphClientFactory's Global cloud. Bind the token to that cloud's
        // host and audience together; a redirect can point to a preauthenticated download host.
        private const string GraphHost = "graph.microsoft.com";
        private static readonly string[] GraphHosts = { GraphHost };
        private static readonly string[] DefaultScopes = { "https://" + GraphHost + "/.default" };
        public static readonly TimeSpan DefaultGraphSdkTimeout = TimeSpan.FromSeconds(100);

        /// <summary>
        /// Create the SDK-backed Graph client used by the Graph importer.
        /// Microsoft.Graph 6.5.0 installs Kiota's RetryHandler in the default pipeline; we
        /// keep it for service-directed transient HTTP retries (429/503/504 and Retry-After) and
        /// add a bounded handler inside it for local per-attempt request-deadline timeouts. The
        /// deadline covers headers and the body transfer; only bodyless GET/HEAD requests get local
        /// timeout retries. The handler buffers up to 32 MiB per SDK page, with at most 16 fills at a time.
        /// Kiota owns redirects; only the Global Graph HTTPS origin receives the Graph credential.
        /// Bulk usage reports use the separate streaming client.
        /// </summary>
        public static GraphServiceClient CreateForGraphImport(TokenCredential credential, ILogger logger = null)
            => CreateForGraphImport(credential, GraphRequestBudgetOptions.GraphImportDefault, logger);

        public static GraphServiceClient CreateForUserImport(TokenCredential credential, ILogger logger = null)
            => CreateForGraphImport(credential, logger);

        internal static GraphServiceClient CreateForGraphImport(TokenCredential credential, GraphRequestBudgetOptions budget, ILogger logger, HttpMessageHandler finalHandler = null)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            var authProvider = CreateAuthenticationProvider(credential);
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
            // The SDK appends an AuthorizationHandler after the redirect handler. It must use the
            // same host-restricted provider as the request adapter, including on redirected hops.
            var httpClient = GraphClientFactory.Create(authProvider, handlers, nationalCloud: GraphClientFactory.Global_Cloud,
                finalHandler: new GraphOriginHandler(finalHandler));
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
            budget.Log(logger);
            return httpClient;
        }

        internal static AzureIdentityAuthenticationProvider CreateAuthenticationProvider(TokenCredential credential)
            => new AzureIdentityAuthenticationProvider(credential, allowedHosts: GraphHosts, scopes: DefaultScopes);

        internal static HttpMessageHandler CreateTransport()
        {
            // Keep the SDK's proxy, decompression and timeout defaults, but expose each redirect
            // to Kiota. In the net48 transport WinHTTP otherwise rewrites the request URI in place.
            var transport = GraphClientFactory.CreatePipeline(Array.Empty<DelegatingHandler>());
            if (transport is WinHttpHandler winHttp)
                winHttp.AutomaticRedirection = false;
            else if (transport is HttpClientHandler http)
                http.AllowAutoRedirect = false;
            else
            {
                transport.Dispose();
                throw new InvalidOperationException("Cannot disable native redirects on the Graph SDK transport.");
            }
            return transport;
        }

        private sealed class GraphOriginHandler : DelegatingHandler
        {
            public GraphOriginHandler(HttpMessageHandler transport) : base(transport ?? CreateTransport()) { }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri;
                // Kiota's token allowlist checks host, not port, and its authorization handler
                // passes through an existing header. Enforce the credential boundary at the wire.
                request.Headers.Host = null; // Let the transport derive the authority from the URI we checked.
                if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.Equals(uri.Host, GraphHost, StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Authorization = null;
                    request.Headers.ProxyAuthorization = null;
                    request.Headers.Remove("Cookie");
                    request.Headers.Remove("Cookie2");
                }
                return base.SendAsync(request, cancellationToken);
            }
        }

        /// <summary>
        /// The SDK pipeline for Graph import, outermost first. The bounded deadline handler sits
        /// directly INSIDE Kiota's <see cref="RetryHandler"/>, so its deadline covers one HTTP
        /// attempt and never a Retry-After wait (#846 R8-10). Placed outside, a Retry-After longer
        /// than the per-request deadline was cancelled part-way, counted as a timeout and re-sent
        /// without honouring Retry-After. The body reader sits below Kiota's redirect and inspection
        /// handlers, which can consume a response before returning it to the deadline handler.
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
            handlers.Add(new BoundedGraphResponseBodyHandler());
            handlers.Insert(0, new GraphTransientErrorHandler());
            return handlers;
        }

        /// <summary>
        /// Legacy helper kept for callers outside the user import path. Prefer
        /// <see cref="CreateForGraphImport"/> for SDK-backed importer work.
        /// </summary>
        public static GraphServiceClient CreateWithTimeout(TokenCredential credential, TimeSpan timeout)
        {
            var authProvider = CreateAuthenticationProvider(credential);
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
            var httpClient = GraphClientFactory.Create(handlers, nationalCloud: GraphClientFactory.Global_Cloud,
                finalHandler: new GraphOriginHandler(finalHandler));
            httpClient.Timeout = timeout;
            return new GraphServiceClient(httpClient, authProvider);
        }
    }
}
