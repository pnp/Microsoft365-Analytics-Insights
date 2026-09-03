using Common.Entities;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Port for the <c>org_urls</c> table, so the "which org URLs are missing" decision in
    /// <see cref="DatabaseUpgradeInfo"/> can be unit tested without SQL Server.
    ///
    /// Deliberately synchronous, because the whole call chain is. <c>DatabaseUpgrader.CheckDbUpgraded</c>
    /// is a <c>static void</c>, and its callers are <c>App.ControlPanel/Program.cs</c> (<c>--initdb</c>),
    /// the <c>BackgroundWorker.DoWork</c> handler in <c>App.ControlPanel/DatabaseUpgradeForm.cs</c>,
    /// <c>Tests.FakeDataGen</c> and the unit tests. An async port would buy nothing for a couple of
    /// statements that run once per install, and would force a <c>.Wait()</c>/<c>.Result</c> at that
    /// boundary or a rewrite of the entire chain.
    /// </summary>
    public interface IOrgUrlStore
    {
        /// <summary>
        /// Whether a row already exists for this URL. Matching is case-insensitive, mirroring the
        /// database's default <c>Latin1_General_CI_AS</c> collation.
        /// </summary>
        bool Exists(string urlBase);

        /// <summary>
        /// Insert a new org URL row.
        /// </summary>
        void Insert(string urlBase, int orgId);
    }

    /// <summary>
    /// Pure rules for org URL values: no database, no I/O, no EF.
    /// </summary>
    public static class OrgUrlRules
    {
        /// <summary>
        /// The only org the installer creates. The insert is raw SQL because <c>org_id</c> is not on
        /// the <see cref="OrgUrl"/> entity, and adding it is not worth a migration (see issue #380).
        /// </summary>
        public const int DefaultOrgId = 1;

        /// <summary>
        /// Normalise a configured org URL to the exact form stored in <c>org_urls</c>, or
        /// <c>null</c> when the value is unusable and should be skipped.
        ///
        /// Values are lower-cased on write because the web app feeds them straight into the CORS
        /// policy (<c>Web/AllowCorsForOrgUrlsAttribute.cs</c>), and <c>CorsPolicy.Origins</c> is
        /// matched ordinally: a stored "https://Contoso.sharepoint.com" would never match the
        /// "https://contoso.sharepoint.com" origin a browser actually sends. Normalising here - once,
        /// in C#, for both the existence check and the insert - is what lets the lookup drop its
        /// <c>ToLower()</c> and stay SARGable.
        ///
        /// <c>ToLowerInvariant</c> rather than <c>ToLower</c> so the stored value cannot depend on the
        /// installer machine's culture: under tr-TR, "FINANCE".ToLower() yields "fınance" with a
        /// dotless i, which would be written to the database and then never match anything.
        /// </summary>
        public static string Normalise(string orgUrl)
        {
            if (string.IsNullOrWhiteSpace(orgUrl))
            {
                return null;
            }

            return orgUrl.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Entity Framework / SQL Server adapter for <see cref="IOrgUrlStore"/>.
    /// </summary>
    public class SqlOrgUrlStore : IOrgUrlStore
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlOrgUrlStore(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// No <c>ToLower()</c> on either side. The database collation is already case-insensitive, so the
        /// call was redundant, and <c>LOWER([url_base])</c> makes the predicate non-SARGable - it cannot
        /// seek the unique index <c>IX_org_urls (url_base)</c> that <c>Create DB.sql</c> builds.
        /// <c>Any()</c> rather than <c>SingleOrDefault()</c> because this is an existence check: it
        /// becomes an <c>IF EXISTS</c>, never materialises an entity, and still answers correctly on a
        /// database where that unique index happens to be absent.
        /// </summary>
        public bool Exists(string urlBase)
        {
            return _db.org_urls.Any(u => u.UrlBase == urlBase);
        }

        public void Insert(string urlBase, int orgId)
        {
            if (string.IsNullOrWhiteSpace(urlBase))
            {
                throw new ArgumentException("An org URL is required.", nameof(urlBase));
            }

            // Parameterised, never interpolated: org URLs come from installer configuration, so a value
            // containing an apostrophe would otherwise break the statement outright and a hostile value
            // would be executed. NVarChar is explicit so non-Latin scripts survive the round trip into
            // the nvarchar(500) column.
            var urlParam = new SqlParameter("@urlBase", SqlDbType.NVarChar) { Value = urlBase };
            var orgParam = new SqlParameter("@orgId", SqlDbType.Int) { Value = orgId };

            _db.Database.ExecuteSqlCommand(
                "insert into org_urls([url_base], [org_id]) values (@urlBase, @orgId)",
                urlParam, orgParam);
        }
    }
}
