namespace App.ControlPanel.Engine.Entities
{
    /// <summary>
    /// Configuration for what install tasks to run
    /// </summary>
    public class InstallTasksConfig
    {
        public InstallTasksConfig()
        {
            this.InstallLatestSolutionContent = true;
            this.UpgradeSchema = true;
            this.RegisterConfig = true;
            this.OpenAdminSitePostInstall = false;
        }

        public bool InstallLatestSolutionContent { get; set; }

        /// <summary>
        /// Use downloaded control-panel app to init the DB with EF6 migration
        /// </summary>
        public bool UpgradeSchema { get; set; }

        /// <summary>
        /// Register this configuration details in DB afterwards
        /// </summary>
        public bool RegisterConfig { get; set; }

        public bool OpenAdminSitePostInstall { get; set; }
    }
}
