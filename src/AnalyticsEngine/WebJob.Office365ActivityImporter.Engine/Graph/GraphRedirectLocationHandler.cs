using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    internal sealed class GraphRedirectLocationHandler : DelegatingHandler
    {
        private static readonly Func<HttpResponseMessage, bool> DefaultDecision = new RedirectHandlerOption().ShouldRedirect;
        private static readonly string OptionKey = typeof(RedirectHandlerOption).FullName;

        internal static bool UsesDefaultDecision(RedirectHandlerOption option) => option.ShouldRedirect == DefaultDecision;

        internal static void ResolveLocation(HttpResponseMessage response, Uri currentHop)
        {
            var location = response.Headers.Location;
            // Kiota 2.0 otherwise appends a relative Location to just the origin, losing the path.
            if (location != null && !location.IsAbsoluteUri)
                response.Headers.Location = new Uri(currentHop, location);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var option = request.GetRequestOption<RedirectHandlerOption>();
            // The inner body handler can resolve automatic redirects as soon as their headers arrive.
            if (option == null || option.MaxRedirect == 0 || UsesDefaultDecision(option))
                return base.SendAsync(request, cancellationToken);
            return SendWithCustomDecisionAsync(request, option, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendWithCustomDecisionAsync(HttpRequestMessage request,
            RedirectHandlerOption option, CancellationToken cancellationToken)
        {
            // A custom predicate must see the original header and bounded body, exactly once.
            // Resolve only an accepted response; never mutate an option shared by other requests.
            var scoped = new RedirectHandlerOption
            {
                MaxRedirect = option.MaxRedirect,
                AllowRedirectOnSchemeChange = option.AllowRedirectOnSchemeChange,
                ScrubSensitiveHeaders = option.ScrubSensitiveHeaders,
                ShouldRedirect = response =>
                {
                    var currentHop = response.RequestMessage.RequestUri;
                    if (!option.ShouldRedirect(response)) return false;
                    ResolveLocation(response, currentHop);
                    return true;
                }
            };
            request.Properties[OptionKey] = scoped;
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var lastHop = response.RequestMessage;
                if (lastHop != null && lastHop != request && lastHop.GetRequestOption<RedirectHandlerOption>() == scoped)
                    lastHop.Properties[OptionKey] = option;
                return response;
            }
            finally { request.Properties[OptionKey] = option; }
        }
    }
}
