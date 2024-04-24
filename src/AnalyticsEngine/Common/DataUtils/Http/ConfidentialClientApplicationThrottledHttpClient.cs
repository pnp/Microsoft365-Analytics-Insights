using Azure.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Common.DataUtils.Http
{
    /// <summary>
    /// HttpClient that can handle HTTP 429s automatically
    /// </summary>
    public class ConfidentialClientApplicationThrottledHttpClient : AutoThrottleHttpClient
    {
        public ConfidentialClientApplicationThrottledHttpClient(HttpMessageHandler server, ILogger debugTracer) : base(server, debugTracer)
        {
        }

        public ConfidentialClientApplicationThrottledHttpClient(ImportAppIndentityOAuthContext appIndentity, bool ignoreRetryHeader, ILogger debugTracer)
            : base(ignoreRetryHeader, debugTracer, new ConfidentialClientApplicationHttpHandler(appIndentity))
        {
        }
    }

    public class ConfidentialClientApplicationHttpHandler : DelegatingHandler
    {
        private readonly ImportAppIndentityOAuthContext appIndentity;
        private AccessToken auth;
        public ConfidentialClientApplicationHttpHandler(ImportAppIndentityOAuthContext appIndentity)
        {
            InnerHandler = new HttpClientHandler();
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
