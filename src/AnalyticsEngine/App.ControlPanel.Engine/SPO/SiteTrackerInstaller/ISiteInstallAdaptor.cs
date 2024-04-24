using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.SiteTrackerInstaller
{
    /// <summary>
    /// Abstract implementation of something that installs AITracker
    /// </summary>
    /// <typeparam name="WEBTYPE">Type of web object</typeparam>
    public interface ISiteInstallAdaptor<WEBTYPE> : IDisposable
    {
        string SiteUrl { get; }
        Task<bool> Init();
        Task AddTrackerToLibraryOnRootSite(string listTitle, byte[] aiTrackerContents, bool publish);
        Task<ListInfo> ConfirmDocLibOnRootSite(string listTitle);
        Task RemoveDocLibOnRootSite(string listTitle);
        Task<bool> RemoveTrackerIfExistsOnRootSite(string listTitle);

        Task AddAITrackerCustomActionToWeb(WEBTYPE web, ClassicPageCustomAction classicPageCustomAction);
        Task AddModernUIAITrackerCustomActionToWeb(WEBTYPE web, ModernAppCustomAction modernAppCustomAction);

        Task RemoveAITrackerCustomActionFromWeb(WEBTYPE web);
        Task RemoveModernUIAITrackerCustomActionFromWeb(WEBTYPE web);

        List<WEBTYPE> SubWebs { get; }
        WEBTYPE RootWeb { get; }

        string GetUrl(WEBTYPE web);
        Task SecureList(string listTitle);
    }
}
