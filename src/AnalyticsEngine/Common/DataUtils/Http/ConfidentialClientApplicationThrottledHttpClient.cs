using Azure.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Http
{
    /// <summary>
    /// HttpClient that can handle HTTP 429s automatically
    /// </summary>
    public class ConfidentialClientApplicationThrottledHttpClient : AutoThrottleHttpClient
    {
        public ConfidentialClientApplicationThrottledHttpClient(HttpMessageHandler server, ILogger logger) : base(server, logger)
        {
        }

        public ConfidentialClientApplicationThrottledHttpClient(ImportAppIndentityOAuthContext appIndentity, bool ignoreRetryHeader, ILogger logger)
            : base(ignoreRetryHeader, logger, new ConfidentialClientApplicationHttpHandler(appIndentity))
        {
        }
    }

    public class ConfidentialClientApplicationHttpHandler : DelegatingHandler
    {
        private readonly ImportAppIndentityOAuthContext appIndentity;
        private AccessToken auth;
        public ConfidentialClientApplicationHttpHandler(ImportAppIndentityOAuthContext appIndentity)
            : this(appIndentity, new HttpClientHandler())
        {
        }

        /// <summary>
        /// Lets a caller supply the inner handler, so it can control transport-level behaviour that this
        /// handler must not override. The one use today is <c>AllowAutoRedirect = false</c> for Graph usage
        /// reports that answer with a 302 to a storage endpoint: auto-following would carry the bearer token
        /// set below onto a host it wasn't issued for, which the storage endpoint can reject outright.
        /// Following the redirect explicitly (unauthenticated) keeps the token scoped to Graph.
        /// </summary>
        public ConfidentialClientApplicationHttpHandler(ImportAppIndentityOAuthContext appIndentity, HttpMessageHandler innerHandler)
        {
            InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
            this.appIndentity = appIndentity;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (auth.ExpiresOn < DateTimeOffset.Now.AddMinutes(5))
            {
                auth = await appIndentity.GetAccessToken();
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
