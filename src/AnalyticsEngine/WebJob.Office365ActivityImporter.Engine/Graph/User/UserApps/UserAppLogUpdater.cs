using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Updates all apps installed for each user
    /// </summary>
    public class UserAppLogUpdater : AbstractApiLoader
    {
        public UserAppLogUpdater(AnalyticsLogger telemetry, AppConfig settings) : base(telemetry, settings)
        {
        }

        public async Task<bool> UpdateUserInstalledApps(GraphServiceClient graphClient)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var l = new GraphAndSqlUserAppLoader(db, _telemetry, graphClient);

                await l.LoadAndSave();

                return true;
            }
        }
    }
}
