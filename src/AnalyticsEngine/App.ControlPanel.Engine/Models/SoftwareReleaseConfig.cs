namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Where solution engine binaries are located. Downloads from the GitHub repo releases.
    /// </summary>
    public class SoftwareReleaseConfig
    {
        public const string GITHUB_REPO_OWNER = "pnp";
        public const string GITHUB_REPO_NAME = "Microsoft365-Analytics-Insights";

        public string RepoOwner => GITHUB_REPO_OWNER;
        public string RepoName => GITHUB_REPO_NAME;
    }
}
