using System;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.Auth
{
    /// <summary>
    /// Supplies access tokens for SharePoint Online.
    /// </summary>
    public interface ISpoAuthenticator : IDisposable
    {
        /// <summary>
        /// A bearer token for the SharePoint tenant that hosts <paramref name="siteUrl"/>. A SharePoint token
        /// covers the whole tenant, so one sign-in serves every target site and the app catalog.
        /// </summary>
        Task<string> GetAccessTokenAsync(string siteUrl);
    }
}
