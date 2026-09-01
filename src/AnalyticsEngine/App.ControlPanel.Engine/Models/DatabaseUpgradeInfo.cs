
using Common.Entities;
using System;
using System.Collections.Generic;

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
        /// Save the URLs in the database if they're not there already.
        /// </summary>
        public void EnsureOrgURLs(AnalyticsEntitiesContext db)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            EnsureOrgURLs(new SqlOrgUrlStore(db));
        }

        /// <summary>
        /// Decide which of the configured URLs are missing, and insert those. This holds the rule only -
        /// <paramref name="store"/> is the sole thing that touches SQL - so it is unit testable without
        /// a database. See issue #380.
        /// </summary>
        public void EnsureOrgURLs(IOrgUrlStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            if (OrgURLs == null)
            {
                return;
            }

            // Saves a redundant round trip when a config lists the same URL more than once. It is NOT
            // what makes the upgrade safe: url_base carries a unique index, and the guard that actually
            // prevents a unique-key violation is the case-insensitive store.Exists() below - which is
            // also the only guard for a row already stored in a different case, where this set cannot help.
            var alreadyHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var orgUrl in OrgURLs)
            {
                var urlBase = OrgUrlRules.Normalise(orgUrl);
                if (urlBase == null)
                {
                    // null / empty / whitespace: skip rather than write a junk row. The previous
                    // implementation threw a NullReferenceException on a null entry, which the caller
                    // swallowed - abandoning every URL after it.
                    continue;
                }
                if (!alreadyHandled.Add(urlBase))
                {
                    continue;
                }
                if (store.Exists(urlBase))
                {
                    continue;
                }

                store.Insert(urlBase, OrgUrlRules.DefaultOrgId);
            }

            // No SaveChanges(): nothing is added to the EF change tracker here. The insert is raw SQL
            // (org_id is not on the entity) and commits itself.
        }
    }
}
