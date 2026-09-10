using System.Collections.Generic;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// For reporting a config & install messages
    /// </summary>
    public class InstallStatus : Base64Serialisable<InstallStatus>
    {
        public string ConfigurationJSon { get; set; }
        public List<InstallLogEventArgs> Events { get; set; }

        public string ConnectionString { get; set; }

        public string SetupUserName { get; set; }

        /// <summary>
        /// Service principal to authenticate to Azure SQL with when <see cref="ConnectionString"/> carries
        /// no login, i.e. the server has SQL authentication disabled.
        /// </summary>
        /// <remarks>
        /// Config registration runs in the separately downloaded control-panel process, exactly like the
        /// schema upgrade, and that process has no managed identity of its own to fall back on. Left empty
        /// for a SQL-authentication database. See issue #117.
        /// </remarks>
        public string EntraTenantId { get; set; }

        /// <summary>Client ID of the service principal to authenticate to Azure SQL with.</summary>
        public string EntraClientId { get; set; }

        /// <summary>Client secret of the service principal to authenticate to Azure SQL with.</summary>
        public string EntraClientSecret { get; set; }

        /// <summary>Whether all three parts of the Microsoft Entra ID credential are present.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasEntraCredential =>
            !string.IsNullOrWhiteSpace(EntraTenantId)
            && !string.IsNullOrWhiteSpace(EntraClientId)
            && !string.IsNullOrWhiteSpace(EntraClientSecret);
    }
}
