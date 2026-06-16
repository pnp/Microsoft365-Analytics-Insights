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
    }
}
