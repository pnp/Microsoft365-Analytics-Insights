extern alias AnalyticsWeb;

using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebCorsAttribute = AnalyticsWeb::Web.AnalyticsWeb.AllowCorsForOrgUrlsAttribute;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for <see cref="DatabaseUpgradeInfo.EnsureOrgURLs(IOrgUrlStore)"/> and the org URL
    /// normalisation rule behind it (issue #380).
    ///
    /// These run entirely in memory - no SQL Server, no Graph, no Redis - because the data access now
    /// sits behind <see cref="IOrgUrlStore"/>. The parameterised insert itself is proved separately by
    /// <see cref="OrgUrlStoreSqlIntegrationTests"/>, which needs a real database.
    /// </summary>
    [TestClass]
    public class OrgUrlStoreTests
    {
        private const string SiteUrl = "https://contoso.sharepoint.com";

        private static DatabaseUpgradeInfo InfoFor(params string[] urls)
        {
            return new DatabaseUpgradeInfo { OrgURLs = new List<string>(urls) };
        }

        [TestMethod]
        public void EnsureOrgUrls_UrlNotPresent_IsInserted()
        {
            var store = new FakeOrgUrlStore();

            InfoFor(SiteUrl).EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count, "A URL not already in the table must be inserted.");
            Assert.AreEqual(SiteUrl, store.Inserts[0].UrlBase);
            Assert.AreEqual(OrgUrlRules.DefaultOrgId, store.Inserts[0].OrgId, "org_id must stay 1, as the raw insert always used.");
        }

        [TestMethod]
        public void EnsureOrgUrls_UrlAlreadyPresent_IsNotInsertedTwice()
        {
            var store = new FakeOrgUrlStore(SiteUrl);

            InfoFor(SiteUrl).EnsureOrgURLs(store);

            Assert.AreEqual(0, store.Inserts.Count, "An existing URL must not be inserted again.");
        }

        [TestMethod]
        public void EnsureOrgUrls_UrlDifferingOnlyByCase_IsTreatedAsAlreadyPresent()
        {
            // The database collation (Latin1_General_CI_AS) is case-insensitive, so this row already matches.
            var store = new FakeOrgUrlStore("https://contoso.sharepoint.com");

            InfoFor("https://CONTOSO.SharePoint.com").EnsureOrgURLs(store);

            Assert.AreEqual(0, store.Inserts.Count, "A case-differing URL must reuse the existing row, not create a duplicate.");
        }

        [TestMethod]
        public void EnsureOrgUrls_SameUrlTwiceInConfig_IsInsertedOnce()
        {
            var store = new FakeOrgUrlStore();

            InfoFor(SiteUrl, "https://CONTOSO.sharepoint.com", SiteUrl).EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count, "A config listing the same URL more than once must still insert it once.");
        }

        [TestMethod]
        public void EnsureOrgUrls_UrlContainingApostrophe_IsInsertedIntact()
        {
            // Regression test for the old interpolated statement: an apostrophe closed the string literal,
            // so this URL either failed the whole upgrade step or silently truncated.
            const string withApostrophe = "https://contoso.sharepoint.com/sites/o'brien";
            var store = new FakeOrgUrlStore();

            InfoFor(withApostrophe).EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual(withApostrophe, store.Inserts[0].UrlBase, "The apostrophe must reach the store untouched - escaping is the adapter's job, via a parameter.");
        }

        [TestMethod]
        public void EnsureOrgUrls_UrlContainingSqlKeywords_IsStoredVerbatim_AndNotExecuted()
        {
            // Injection-shape guard. The rule layer must pass the value straight through; nothing here
            // may try to sanitise, strip or escape it, because that is what parameters are for.
            const string injection = "https://contoso.sharepoint.com/'); drop table org_urls; --";
            var store = new FakeOrgUrlStore();

            InfoFor(injection).EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual(injection, store.Inserts[0].UrlBase, "The value must be stored verbatim, never rewritten.");
        }

        [TestMethod]
        public void EnsureOrgUrls_GreekUrl_RoundTripsUnchanged()
        {
            // url_base is nvarchar, so non-Latin scripts must survive intact (repo Unicode rule).
            const string greekUrl = "https://contoso.sharepoint.com/sites/καλημέρα-κόσμε";
            var store = new FakeOrgUrlStore();

            InfoFor(greekUrl).EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual(greekUrl, store.Inserts[0].UrlBase, "A Greek URL must round trip unchanged - no '?' substitution, no mangling.");
        }

        [TestMethod]
        public void EnsureOrgUrls_EmptyOrNullUrl_IsRejectedWithoutWriting()
        {
            var store = new FakeOrgUrlStore();

            InfoFor(null, "", "   ").EnsureOrgURLs(store);

            Assert.AreEqual(0, store.Inserts.Count, "Blank entries must never produce a row.");
        }

        [TestMethod]
        public void EnsureOrgUrls_BlankEntry_DoesNotAbandonTheRemainingUrls()
        {
            // The old code called orgUrl.ToLower() directly, so a null entry threw a
            // NullReferenceException that DatabaseUpgrader swallowed - silently dropping every URL after it.
            var store = new FakeOrgUrlStore();

            InfoFor(null, SiteUrl).EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count, "A blank entry must not stop later URLs being inserted.");
            Assert.AreEqual(SiteUrl, store.Inserts[0].UrlBase);
        }

        [TestMethod]
        public void EnsureOrgUrls_MixedCaseUrl_IsStoredLowerCased()
        {
            // Pins the normalisation decision. The web app pushes these values straight into
            // CorsPolicy.Origins, which is matched ordinally, so a stored upper-case host would never
            // match the origin a browser sends.
            var store = new FakeOrgUrlStore();

            InfoFor("https://CONTOSO.SharePoint.com/sites/ΚΑΛΗΜΈΡΑ").EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual("https://contoso.sharepoint.com/sites/καλημέρα", store.Inserts[0].UrlBase,
                "Values are normalised to lower case on write, for Greek as well as Latin.");
        }

        [TestMethod]
        public async Task CorsPolicy_NormalisesCachedOrgUrlsToBrowserOrigins()
        {
            var loadCount = 0;
            var provider = new WebCorsAttribute(() =>
            {
                loadCount++;
                return Task.FromResult(new List<string>
                {
                    "HTTPS://CONTOSO.SHAREPOINT.COM/",
                    "contoso.sharepoint.com/sites/ignored-by-origin",
                    "ftp://contoso.sharepoint.com",
                });
            });

            var first = await provider.GetCorsPolicyAsync(new HttpRequestMessage(), CancellationToken.None);
            var second = await provider.GetCorsPolicyAsync(new HttpRequestMessage(), CancellationToken.None);

            CollectionAssert.AreEquivalent(
                new[] { "https://contoso.sharepoint.com" },
                first.Origins.ToArray(),
                "CORS origins are ordinal/case-sensitive, so cached org_urls rows must be normalised on read.");
            CollectionAssert.AreEquivalent(first.Origins.ToArray(), second.Origins.ToArray());
            Assert.AreEqual(1, loadCount, "Normalisation must happen while populating the existing cache, not per request.");
        }

        [TestMethod]
        public void EnsureOrgUrls_SurroundingWhitespace_IsTrimmed()
        {
            var store = new FakeOrgUrlStore();

            InfoFor("  " + SiteUrl + "  ").EnsureOrgURLs(store);

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual(SiteUrl, store.Inserts[0].UrlBase, "A stray space would be stored and then never match a CORS origin.");
        }

        [TestMethod]
        public void EnsureOrgUrls_TurkishCulture_NormalisesInvariantly()
        {
            // Guards the ToLowerInvariant choice. Under tr-TR, "I".ToLower() is the dotless "ı", so a
            // culture-sensitive lower-casing would store "https://contoso.sharepoint.com/sites/fınance"
            // - a value that can never match anything.
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                var store = new FakeOrgUrlStore();

                InfoFor("https://CONTOSO.sharepoint.com/sites/FINANCE").EnsureOrgURLs(store);

                Assert.AreEqual(1, store.Inserts.Count);
                Assert.AreEqual("https://contoso.sharepoint.com/sites/finance", store.Inserts[0].UrlBase,
                    "Normalisation must not depend on the installer machine's culture.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void EnsureOrgUrls_ChecksEachUrlOnce_NotPerRow()
        {
            // 200k-user scale guard: the lookup must stay one existence check per configured URL.
            var store = new FakeOrgUrlStore();

            InfoFor("https://contoso.sharepoint.com", "https://fabrikam.sharepoint.com").EnsureOrgURLs(store);

            Assert.AreEqual(2, store.ExistsCallCount, "One existence check per distinct URL, no more.");
        }

        [TestMethod]
        public void EnsureOrgUrls_NoUrlsConfigured_DoesNothing()
        {
            var store = new FakeOrgUrlStore();

            new DatabaseUpgradeInfo { OrgURLs = null }.EnsureOrgURLs(store);
            InfoFor().EnsureOrgURLs(store);

            Assert.AreEqual(0, store.Inserts.Count);
            Assert.AreEqual(0, store.ExistsCallCount);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void EnsureOrgUrls_NullStore_Throws()
        {
            InfoFor(SiteUrl).EnsureOrgURLs((IOrgUrlStore)null);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void EnsureOrgUrls_NullContext_Throws()
        {
            InfoFor(SiteUrl).EnsureOrgURLs((AnalyticsEntitiesContext)null);
        }
    }

    /// <summary>
    /// Database-backed proof that <see cref="SqlOrgUrlStore"/>'s parameterised insert writes the same
    /// row the old interpolated statement did, and that values the old statement could not survive -
    /// an apostrophe, SQL keywords, non-Latin script - now round trip byte for byte.
    /// </summary>
    [TestClass]
    public class OrgUrlStoreSqlIntegrationTests
    {
        private const string Prefix = "https://orgurl-test-380.contoso.sharepoint.com";

        [ClassInitialize]
        public static void InitializeDatabase(TestContext context)
        {
            var config = new AppConfig();
            var initInfo = new DatabaseUpgradeInfo { ConnectionString = config.ConnectionStrings.DatabaseConnectionString };
            DatabaseUpgrader.CheckDbUpgraded(initInfo, s => context.WriteLine($"[DatabaseUpgrader] {s}"));
            RemoveTestRows();
        }

        [ClassCleanup]
        public static void CleanupDatabase()
        {
            RemoveTestRows();
        }

        private static void RemoveTestRows()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = db.org_urls.Where(u => u.UrlBase.StartsWith(Prefix)).ToList();
                if (rows.Count > 0)
                {
                    db.org_urls.RemoveRange(rows);
                    db.SaveChanges();
                }
            }
        }

        private static void AssertRoundTrips(string url, string because)
        {
            var expected = OrgUrlRules.Normalise(url);

            using (var db = new AnalyticsEntitiesContext())
            {
                var store = new SqlOrgUrlStore(db);
                Assert.IsFalse(store.Exists(expected), "Test row should not exist yet.");

                store.Insert(expected, OrgUrlRules.DefaultOrgId);

                Assert.IsTrue(store.Exists(expected), "The row must be found straight after insert.");
            }

            // Re-read on a fresh context so the assertion cannot be served from EF's change tracker.
            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = db.org_urls.Where(u => u.UrlBase.StartsWith(Prefix)).Select(u => u.UrlBase).ToList();
                CollectionAssert.Contains(stored, expected, because);
            }
        }

        [TestMethod]
        public void SqlOrgUrlStore_UrlContainingApostrophe_IsStoredIntact()
        {
            AssertRoundTrips(Prefix + "/sites/o'brien", "The apostrophe broke the old interpolated statement.");
        }

        [TestMethod]
        public void SqlOrgUrlStore_GreekUrl_IsStoredIntact()
        {
            AssertRoundTrips(Prefix + "/sites/καλημέρα-κόσμε", "url_base is nvarchar, so Greek must not degrade to '?'.");
        }

        [TestMethod]
        public void SqlOrgUrlStore_SqlInjectionPayload_IsStoredAsDataAndNotExecuted()
        {
            var payload = Prefix + "/'); drop table org_urls; --";

            AssertRoundTrips(payload, "The payload must be stored as data, verbatim.");

            // The decisive assertion: had the value been interpolated, org_urls would now be gone.
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.IsTrue(db.org_urls.Any(), "org_urls must still exist and be readable - the payload must never have been executed.");
            }
        }

        [TestMethod]
        public void EnsureOrgUrls_AgainstRealDatabase_PreExistingRowInDifferentCase_IsNotInsertedAgain()
        {
            // THE guard that matters on a real upgrade. A row already stored in a different case (a
            // legacy row, or one written by another tool) must be recognised by the database's
            // case-insensitive collation - otherwise the insert collides with the UNIQUE index
            // IX_org_urls, and the catch in CheckDbUpgraded logs and returns, abandoning every
            // remaining URL.
            //
            // The row is seeded through the store directly so it bypasses Normalise(). Going through
            // EnsureOrgURLs would lower-case both values into byte-identical strings, and the collation
            // would never be asked to fold case at all - which is why the config-side test below cannot
            // stand in for this one.
            var preExistingRow = (Prefix + "/sites/legacycasing").ToUpperInvariant();
            var configured = Prefix + "/sites/legacycasing";
            var normalised = OrgUrlRules.Normalise(configured);

            using (var db = new AnalyticsEntitiesContext())
            {
                new SqlOrgUrlStore(db).Insert(preExistingRow, OrgUrlRules.DefaultOrgId);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                // Must not throw: a unique-key violation here is the upgrade-aborting failure itself.
                new DatabaseUpgradeInfo { OrgURLs = new List<string> { configured } }.EnsureOrgURLs(db);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = db.org_urls.Where(u => u.UrlBase.StartsWith(Prefix)).Select(u => u.UrlBase).ToList();
                var matching = stored.Where(s => string.Equals(s, normalised, StringComparison.OrdinalIgnoreCase)).ToList();

                Assert.AreEqual(1, matching.Count, "The differently-cased row must be reused, not duplicated.");
                Assert.AreEqual(preExistingRow, matching[0], "The pre-existing row must be left exactly as it was stored.");
            }
        }

        [TestMethod]
        public void EnsureOrgUrls_AgainstRealDatabase_CaseDifferingConfig_NormalisesToASingleRow()
        {
            // Config-side counterpart: two upgrade runs whose configured URLs differ only by case must
            // converge on one row. Note this is settled by Normalise() in C# before SQL sees either
            // value, so it does NOT exercise the collation or the unique index - the test above does.
            var stored = Prefix + "/sites/casecollision";
            var reconfigured = stored.ToUpperInvariant();
            var normalised = OrgUrlRules.Normalise(stored);

            using (var db = new AnalyticsEntitiesContext())
            {
                new DatabaseUpgradeInfo { OrgURLs = new List<string> { stored } }.EnsureOrgURLs(db);
            }
            using (var db = new AnalyticsEntitiesContext())
            {
                new DatabaseUpgradeInfo { OrgURLs = new List<string> { reconfigured } }.EnsureOrgURLs(db);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                // Normalise() is evaluated outside the expression tree - EF6 cannot translate it.
                var matches = db.org_urls.Count(u => u.UrlBase == normalised);
                Assert.AreEqual(1, matches, "A case-differing org URL must reuse the existing row.");
            }
        }

        [TestMethod]
        public void EnsureOrgUrls_AgainstRealDatabase_InsertsOnceAndIsIdempotent()
        {
            var url = Prefix + "/sites/idempotency";
            var normalised = OrgUrlRules.Normalise(url);
            var info = new DatabaseUpgradeInfo { OrgURLs = new List<string> { url } };

            using (var db = new AnalyticsEntitiesContext())
            {
                info.EnsureOrgURLs(db);
            }
            using (var db = new AnalyticsEntitiesContext())
            {
                info.EnsureOrgURLs(db);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                // Normalise() is evaluated here, not inside the expression tree - EF cannot translate it.
                var matches = db.org_urls.Count(u => u.UrlBase == normalised);
                Assert.AreEqual(1, matches, "Running the upgrade twice must not create a duplicate org URL.");
            }
        }
    }
}
