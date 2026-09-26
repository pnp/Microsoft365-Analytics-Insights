namespace Common.Entities
{
    /// <summary>
    /// Build-time constants compiled into the assembly.
    /// The build pipeline (see .github/workflows/ci.yml) replaces the default value of
    /// <see cref="BuildLabel"/> with the real build label during a release build.
    /// Local/debug builds keep the default "DEV_BUILD" value.
    /// </summary>
    public static class BuildConstants
    {
        public const string BuildLabel = "DEV_BUILD";

        /// <summary>
        /// The GitHub repository releases are published to: the installer downloads the release
        /// packages from it and the web app's update check compares against its latest release.
        /// </summary>
        public const string GitHubRepoOwner = "pnp";

        /// <inheritdoc cref="GitHubRepoOwner"/>
        public const string GitHubRepoName = "Microsoft365-Analytics-Insights";
    }
}
