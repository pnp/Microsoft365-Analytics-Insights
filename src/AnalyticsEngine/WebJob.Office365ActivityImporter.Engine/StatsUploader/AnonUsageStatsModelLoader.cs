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
            var model = new AnonUsageStatsModel() { Generated = DateTime.Now };
            model.AnonClientId = StringUtils.GetHashedStringSimple(tenantId.ToString());
            if (lastSettings != null && lastSettings.SolutionConfig != null)
            {
                model.ConfiguredImportsEnabledDescription = lastSettings.SolutionConfig.ImportTaskSettings?.ToSettingsString();

                // Just one for now
                model.ConfiguredSolutionsEnabledDescription = Enum.GetName(typeof(SolutionImportType), lastSettings.SolutionConfig.SolutionTargeted);
            }

            return model;
        }
    }
}
