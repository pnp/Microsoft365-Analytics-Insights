using Common.Entities;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Figure out if a team should be crawled or not
    /// </summary>
    public class TeamsCrawlConfig
    {
        public static async Task<TeamsCrawlConfig> LoadFromDb(AnalyticsEntitiesContext db)
        {
            var cfg = new TeamsCrawlConfig();
            var config = await db.GroupsCrawlConfig.ToListAsync();
            cfg.WhitelistTeamsIds = config.Where(c => c.Include).Select(l => l.TeamGraphId).ToList();
            cfg.BlacklistTeamsIds = config.Where(c => !c.Include).Select(l => l.TeamGraphId).ToList();

            return cfg;
        }

        public List<string> WhitelistTeamsIds { get; set; } = new List<string>();
        public List<string> BlacklistTeamsIds { get; set; } = new List<string>();
        public static TeamsCrawlConfig AllGroupsConfig => new TeamsCrawlConfig();

        /// <summary>
        /// Whether this group should be crawled. The rule itself lives in
        /// <see cref="TeamsCrawlRules.ShouldCrawlGroup"/> so it can be tested without a database.
        /// </summary>
        public bool CrawlGroup(string groupId)
        {
            return TeamsCrawlRules.ShouldCrawlGroup(WhitelistTeamsIds, BlacklistTeamsIds, groupId);
        }
    }
}
