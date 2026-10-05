using Azure.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
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
        public ConfidentialClientApplicationThrottledHttpClient(HttpMessageHandler server, ILogger logger, IAutoThrottleHttpClientClock clock = null) : base(server, logger, clock)
        {
        }

        public ConfidentialClientApplicationThrottledHttpClient(ImportAppIndentityOAuthContext appIndentity, bool ignoreRetryHeader, ILogger logger, IAutoThrottleHttpClientClock clock = null)
            : base(ignoreRetryHeader, logger, new ConfidentialClientApplicationHttpHandler(appIndentity), clock)
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

            var response = await base.SendAsync(request, cancellationToken);
            var tracer = HttpMessageTracing.Current;
            if (tracer?.IsEnabled == true)
            {
                try
                {
                    await tracer.TraceAsync(GetTraceSource(request.RequestUri), request, response, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    throw;
                }
                catch
                {
                }
            }
            return response;
        }

        public static string GetTraceSource(Uri requestUri)
        {
            var host = requestUri?.Host ?? string.Empty;
            if (host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)) return "graph";
            if (host.Equals("manage.office.com", StringComparison.OrdinalIgnoreCase)) return "activity-api";
            if (host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)) return "azure-management";

            var label = new string(host.ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
                .ToArray()).Trim('-');
            return string.IsNullOrWhiteSpace(label) ? "http" : label;
        }
    }
}
