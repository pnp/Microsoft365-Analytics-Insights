using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Static-bearer-token <see cref="IAuthenticationProvider"/> for Microsoft.Graph v5+.
    /// Replaces the v4 <c>DelegateAuthenticationProvider</c> when an access token is
    /// already in hand (e.g. obtained from a refresh-token flow). Not for use with
    /// long-lived clients - tokens are static for the lifetime of this provider.
    /// </summary>
    public sealed class BearerTokenAuthenticationProvider : IAuthenticationProvider
    {
        private readonly string _accessToken;

        public BearerTokenAuthenticationProvider(string accessToken)
        {
            _accessToken = accessToken;
        }

        public Task AuthenticateRequestAsync(RequestInformation request,
            Dictionary<string, object> additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            return Task.CompletedTask;
        }
    }
}
