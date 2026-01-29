using App.ControlPanel.Engine;
using Azure.Messaging.ServiceBus;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using Newtonsoft.Json;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models
{
    public class SystemStatus
    {
        #region Constructors

        public SystemStatus(string json)
        {
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    this.Config = JsonConvert.DeserializeObject<SolutionInstallConfig>(json);
                }
                catch (JsonReaderException)
                {
                    // Nothing. Show no config object
                }
            }

            this.ConfigJson = json;
        }
        protected SystemStatus() { }

        #endregion

        #region Props

        public SolutionInstallConfig Config { get; set; }

        public string ConfigJson { get; set; }

        public int HitCount { get; set; }
        public int ActivityCount { get; set; }

        public int TeamsCount { get; set; }
        public int TeamsBeingTrackedCount { get; set; }

        public string BuildLabel { get; set; }

        public bool HasValidConfig
        {
            get { return this.Config != null; }
        }

        public string WebAppConfigSQL { get; set; }
        public string WebAppConfigRedis { get; set; }
        public string WebAppConfigServiceBus { get; set; }
        public string WebAppConfigCognitive { get; set; }
        public bool CognitiveServiceEnabled { get; set; }
        public string WebAppBaseURL { get; set; }
        public bool YammerAuth { get; set; }

        #endregion

        internal async static Task<SystemStatus> LoadFrom(AnalyticsEntitiesContext db, CacheConnectionManager cache)
        {
            SystemStatus status = null;


            // Load config
            var latestConfig = await db.ConfigStates.OrderByDescending(s => s.DateApplied).Take(1).ToListAsync();
            if (latestConfig.Count == 1 && !string.IsNullOrEmpty(latestConfig[0].ConfigJson))
            {
                try
                {
                    status = new SystemStatus(latestConfig[0].ConfigJson);
                }
                catch (JsonReaderException)
                {
                    status = new UnknownConfigSystemStatus();
                }
            }
            else
            {
                status = new UnknownConfigSystemStatus();
            }

            status.BuildLabel = System.Configuration.ConfigurationManager.AppSettings["BuildLabel"];

            // DB counts
            status.HitCount = await db.hits.CountAsync();
            status.ActivityCount = await db.AuditEventsCommon.CountAsync();
            status.TeamsCount = await db.Teams.CountAsync();
            status.TeamsBeingTrackedCount = await db.Teams.Where(t => t.HasRefreshToken).CountAsync();

            // Config
            var config = new AppConfig();
            status.WebAppConfigCognitive = config.CognitiveEndpoint;
            status.WebAppConfigRedis = StackExchange.Redis.ConfigurationOptions.Parse(config.ConnectionStrings.RedisConnectionString).SslHost;
            status.WebAppConfigSQL = new System.Data.SqlClient.SqlConnectionStringBuilder(config.ConnectionStrings.DatabaseConnectionString).DataSource;
            status.WebAppConfigServiceBus = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString).Endpoint.ToString();
            status.CognitiveServiceEnabled = config.IsValidCognitiveConfig;
            status.WebAppBaseURL = config.WebAppURL;
            return status;
        }
    }

    public class UnknownConfigSystemStatus : SystemStatus
    {
        public UnknownConfigSystemStatus() : this(string.Empty)
        {
        }
        public UnknownConfigSystemStatus(string json) : base(json)
        {
            base.Config = SolutionInstallConfig.NewConfig();
        }
    }
}