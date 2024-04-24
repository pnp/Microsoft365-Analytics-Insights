using CloudInstallEngine.Models;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    public class AdoptifyAssetsInfo
    {
        public string RootUrl { get; set; } = string.Empty;
    }

    public class AdoptifyInstallException : InstallException
    {
        public AdoptifyInstallException(string message) : base(message)
        {
        }
    }

}
