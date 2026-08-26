using Microsoft.SharePoint.Client;
using System;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.Auth
{
    /// <summary>
    /// Supplies authenticated CSOM contexts &amp; raw access-tokens for SharePoint Online.
    /// </summary>
    public interface ISpoAuthenticator : IDisposable
    {
        /// <summary>
        /// A CSOM context for the given site, with an OAuth bearer token attached to every request.
        /// </summary>
        ClientContext GetContext(string siteUrl);

        /// <summary>
        /// A bearer token for the SharePoint tenant that hosts <paramref name="siteUrl"/>. Needed for the
        /// app-catalog REST calls, which CSOM doesn't expose.
        /// </summary>
        Task<string> GetAccessTokenAsync(string siteUrl);
    }
}
