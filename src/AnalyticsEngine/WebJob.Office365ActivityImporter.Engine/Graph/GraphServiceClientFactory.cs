using Azure.Core;
using Microsoft.Graph;
using Microsoft.Kiota.Authentication.Azure;
using System;
using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Factory helpers for <see cref="GraphServiceClient"/> v5+. The v4 client exposed
    /// <c>HttpProvider.OverallTimeout</c> directly; in v5+ that property is gone, so we
    /// build a custom <see cref="HttpClient"/> with the desired timeout and pass it to
    /// <see cref="GraphServiceClient(HttpClient, Microsoft.Kiota.Abstractions.Authentication.IAuthenticationProvider, string)"/>.
    /// </summary>
    public static class GraphServiceClientFactory
    {
        private static readonly string[] DefaultScopes = new[] { "https://graph.microsoft.com/.default" };

        /// <summary>
        /// Create a client with a per-request HTTP timeout. Use for long-running enumeration
        /// (e.g. /users/delta or large tenant scans) where the default 100s would fire mid-page.
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
