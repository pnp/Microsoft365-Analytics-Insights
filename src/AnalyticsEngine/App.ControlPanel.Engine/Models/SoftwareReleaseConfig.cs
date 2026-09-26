using Common.Entities;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Where solution engine binaries are located. Downloads from the GitHub repo releases.
    /// </summary>
    public class SoftwareReleaseConfig
    {
        public const string GITHUB_REPO_OWNER = BuildConstants.GitHubRepoOwner;
        public const string GITHUB_REPO_NAME = BuildConstants.GitHubRepoName;

        public string RepoOwner => GITHUB_REPO_OWNER;
        public string RepoName => GITHUB_REPO_NAME;
    }
}
