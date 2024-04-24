
using Common.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Data about what database to initialise & org URLs to ensure
    /// </summary>
    public class DatabaseUpgradeInfo : Base64Serialisable<DatabaseUpgradeInfo>
    {
        public string ConnectionString { get; set; }
        public List<string> OrgURLs { get; set; }

        /// <summary>
        /// Save the URLs in the database if they're not there already
        /// </summary>
        public void EnsureOrgURLs(AnalyticsEntitiesContext db)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }
            if (OrgURLs != null && OrgURLs.Count > 0)
            {
                foreach (var orgUrl in OrgURLs)
                {
                    var existingEntry = db.org_urls.Where(u => u.UrlBase.ToLower() == orgUrl.ToLower()).SingleOrDefault();
                    if (existingEntry == null)
                    {
                        // We have to manually insert for now, as there's no "org_id" in the model. Not pushing another EF migration just for this.
                        db.Database.ExecuteSqlCommand($"insert into org_urls([url_base], [org_id]) values ('{orgUrl.ToLower()}', 1)");
                    }
                }

                db.SaveChanges();
            }
        }
    }
}
