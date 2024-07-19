using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class PageUpdatesSaveExtension
    {

        public static async Task<int> SavePageUpdatesToSQL(this CustomEventsResultCollection eventList, ILogger debugTracer, AppConfig config)
        {
            if (eventList.Rows.Count == 0) return 0;

            var updateManager = new PageUpdateManager(debugTracer, config);

            // Filter from custom events which are page-updates
            var pageUpdates = eventList.Rows
                .Where(r => r.GetType() == typeof(PageUpdateEventAppInsightsQueryResult))
                .Cast<PageUpdateEventAppInsightsQueryResult>();

            var updatedUrls = await updateManager.SaveAll(pageUpdates);
            return updatedUrls.Count;
        }
    }

    public class UrlMetadataFieldNameCache : ObjectByIdCache<FileMetadataFieldName>
    {
        private readonly AnalyticsEntitiesContext _context;

        public UrlMetadataFieldNameCache(AnalyticsEntitiesContext context)
        {
            _context = context;
        }

        public override async Task<FileMetadataFieldName> Load(string id)
        {
            return await _context.FileMetadataFields.Where(e => e.Name == id.ToLower()).FirstOrDefaultAsync();
        }
    }

}
