using System;

namespace CloudInstallEngine.Models
{
    public class AppInsightsInfo
    {
        public AppInsightsInfo(string id, string name, string connectionString)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"'{nameof(id)}' cannot be null or empty.", nameof(id));
            }

            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));
            }

            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }

            this.ID = id;
            this.Name = name;
            this.ConnectionString = connectionString;
        }
        public string ConnectionString { get; set; }
        public string ID { get; set; }
        public string Name { get; internal set; }
    }

    public class AppInsightsInfoWithApiAccess : AppInsightsInfo
    {
        public AppInsightsInfoWithApiAccess(string id, string name, string connectionString, string apiKey, string appId) : base(id, name, connectionString)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException($"'{nameof(apiKey)}' cannot be null or empty.", nameof(apiKey));
            }

            if (string.IsNullOrEmpty(appId))
            {
                throw new ArgumentException($"'{nameof(appId)}' cannot be null or empty.", nameof(appId));
            }

            this.ApiKey = apiKey;
            this.AppId = appId;
        }
        public string ApiKey { get; set; }
        public string AppId { get; set; }
    }

    public class LogWorkspaceInfo
    {
        public string AzureID { get; set; }
        public string WorkspaceID { get; set; }
    }
}
