using Common.Entities.Installer;
using DataUtils;
using System;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    public class AnonUsageStatsModelLoader
    {
        public static AnonUsageStatsModel Load(Guid tenantId, BaseSolutionInstallConfig lastSettings)
        {
            // UTC: Generated.Ticks feeds both the payload signature and the server-side document id, so a
            // local-time value would make those inconsistent across servers in different timezones (and shift
            // by an hour at DST boundaries).
            var model = new AnonUsageStatsModel() { Generated = DateTime.UtcNow };
            model.AnonClientId = StringUtils.GetHashedStringSimple(tenantId.ToString());
            if (lastSettings != null && lastSettings.SolutionConfig != null)
            {
                model.ConfiguredImportsEnabledDescription = lastSettings.SolutionConfig.ImportTaskSettings?.ToSettingsString();
            }

            return model;
        }
    }
}
