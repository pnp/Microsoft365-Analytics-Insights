using Common.Entities;
using Common.Entities.LookupCaches;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeEntities;

namespace Tests.UnitTests
{
    [TestClass]
    public class EntityTests : IDisposable
    {
        public AnalyticsEntitiesContext DB { get; set; }
        private SharePointLookupManager _lookupManager = null;
        public EntityTests()
        {
            this.DB = new AnalyticsEntitiesContext();
            _lookupManager = new SharePointLookupManager(DB);
        }

        [ClassInitialize]
        public static void TestStartup(TestContext context)
        {
        }

        #region Private Methods

        private UserSession _session = null;
        private UserSession GetTestingSession()
        {
            if (_session == null)
            {
                _session = new UserSession() { ai_session_id = "test session " + DataGenerators.GetRandomString(20) };
                _session.user = new User { UserPrincipalName = "Test User " + DateTime.Now.Ticks };
            }
            return _session;
        }

        #endregion


        [TestMethod()]
        public void SPWebResolverTests()
        {
            var randomRootWebUrl = "https://whatevers/" + DataGenerators.GetRandomString();
            var randomSub1URL = randomRootWebUrl + "/sub " + DataGenerators.GetRandomString();

            // Generate URL that won't have been used before
            var webResolver = new SPWebResolver();
            var rootNewWeb = webResolver.GetWeb(randomRootWebUrl, true);

            // Make sure both are new
            Assert.IsTrue(rootNewWeb.ID == 0, "Expected a new object");
            Assert.IsTrue(rootNewWeb.site.ID == 0, "Expected a new object");

            // Make sure new site was created for this
            Assert.IsTrue(rootNewWeb.url_base.ToLower() == rootNewWeb.site.UrlBase.ToLower(), "Got an unexpected site URL");

            // Get random sub-site
            var subNewWeb = webResolver.GetWeb(randomSub1URL, true);

            // Make sure we're using the pre-created site with root URL
            Assert.IsTrue(subNewWeb.site.UrlBase.ToLower() == randomRootWebUrl.ToLower());

            // Both webs should share same site
            Assert.AreEqual(subNewWeb.site, rootNewWeb.site);

            // Get random sub-site from cache
            var subNewWeb2 = webResolver.GetWeb(randomSub1URL.ToUpper(), true);
            Assert.AreEqual(subNewWeb, subNewWeb2);

            // Get web for random doc URL from a subsite we should have cached
            var randoDocWeb = webResolver.GetWeb(randomSub1URL + "/randoDoc.docx", true);
            Assert.AreEqual(randoDocWeb, subNewWeb);

            var neverSeenWeb = webResolver.GetWeb("https://whatevers/newsitenotcached/randoDoc.docx", false);
            Assert.IsNull(neverSeenWeb);
        }


        [TestMethod()]
        public async Task SitesAndWebsTests()
        {

            string randomRootWebUrl = "https://whatevers/" + DataGenerators.GetRandomString();
            string randomSubURL = randomRootWebUrl + "/sub " + DataGenerators.GetRandomString();

            using (var db = new AnalyticsEntitiesContext())
            {
                // Save 
                db.webs.Add(new Web { url_base = randomRootWebUrl, site = new Site { UrlBase = randomRootWebUrl } });
                db.SaveChanges();

                var lookupManager = new SharePointLookupManager(db);

                // Get random sub-site
                var subNewWeb = await lookupManager.GetWebOrCreateWebPlusSite(randomSubURL, true);

                // Make sure we're using the pre-created site with root URL
                Assert.IsTrue(subNewWeb.site.UrlBase.ToLower() == randomRootWebUrl.ToLower());

                // Get random sub-site from cache
                var subNewWeb2 = await lookupManager.GetWebOrCreateWebPlusSite(randomSubURL.ToUpper(), true);
                Assert.AreEqual(subNewWeb, subNewWeb2);
            }

            // Try case tests with different context
            using (var db = new AnalyticsEntitiesContext())
            {
                var lookupManager = new SharePointLookupManager(db);
                var subNewWeb = await lookupManager.GetWebOrCreateWebPlusSite(randomSubURL.ToUpper(), true);

                // Make sure we're using the pre-created site with root URL
                Assert.IsTrue(subNewWeb.site.UrlBase.ToLower() == randomRootWebUrl.ToLower());
            }
        }

        // https://stackoverflow.com/questions/17876478/why-the-sql-server-ignore-the-empty-space-at-the-end-automatically
        [TestMethod()]
        public async Task LookupCacheWhiteSpaceTests()
        {
            var name1 = DateTime.Now.Ticks.ToString();
            var nameWithWhiteSpace = name1 + " ";
            using (var context = new AnalyticsEntitiesContext())
            {
                var cache1 = new OfficeLocationCache(context);

                var o1 = await cache1.GetOrCreateNewResource(name1, new UserOfficeLocation { Name = name1 });
                var o2 = await cache1.GetOrCreateNewResource(nameWithWhiteSpace, new UserOfficeLocation { Name = nameWithWhiteSpace });

                Assert.AreEqual(o1, o2);
                context.UserOfficeLocations.Add(o1);
                context.UserOfficeLocations.Add(o2);

                await context.SaveChangesAsync();

            }

        }

        [TestMethod()]
        public async Task LookupCaseTests()
        {
            const string USERNAME = "user";
            using (AnalyticsEntitiesContext preTestContext = new AnalyticsEntitiesContext())
            {
                User deleteUser = preTestContext.users.Where(u => u.UserPrincipalName == USERNAME).SingleOrDefault();
                if (deleteUser != null)
                {
                    preTestContext.users.Remove(deleteUser);
                    preTestContext.SaveChanges();
                }
            }

            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                SharePointLookupManager l = new SharePointLookupManager(db);
                User u1 = new User() { UserPrincipalName = USERNAME.ToLower() };
                User u2 = new User() { UserPrincipalName = USERNAME.ToUpper() };

                db.users.Add(u1);
                db.users.Add(u2);
                try
                {
                    db.SaveChanges();
                }
                catch (System.Data.Entity.Infrastructure.DbUpdateException)
                {
                    // Expected due to FK constraint
                }
            }

            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                var userCache = new UserCache(db);
                User u1 = await userCache.GetOrCreateUser("USER", true);
                User u2 = await userCache.GetOrCreateUser("user", true);
                Assert.AreSame(u1, u2);


                Assert.IsTrue(u2.ID > 0);
            }

        }

        [TestMethod]
        public void EuropeanFileNameTest()
        {
            const string ODD_NAME = "Tutkimusryhma¨n esittely.docx";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                db.Database.Log = s => Console.WriteLine(s);

                if (db.event_file_names.Where(f => f.Name == ODD_NAME).FirstOrDefault() == null)
                {
                    // Save odd name
                    SPEventFileName oddNameFile = new SPEventFileName();
                    oddNameFile.Name = ODD_NAME;
                    db.event_file_names.Add(oddNameFile);
                    db.SaveChanges();

                }

                // Find it again
                var result = db.event_file_names.Where(f => f.Name == ODD_NAME).SingleOrDefault();

                Assert.IsNotNull(result, "Nonstandard file-name save & find test failure");

                // Try and add it again. Expect error
                SPEventFileName oddNameFile2 = new SPEventFileName();
                oddNameFile2.Name = ODD_NAME;
                db.event_file_names.Add(oddNameFile2);
                Assert.ThrowsException<System.Data.Entity.Infrastructure.DbUpdateException>(() =>
                {
                    db.SaveChanges();
                }
                );
            }
        }


        [TestMethod]
        public void HebrewURLTest()
        {
            const string ODD_URL = "https://tenant.sharepoint.com/personal/user/Documents/folder/אירועי למבוטחים בסניף/הגר.docx";
            using (var db = new AnalyticsEntitiesContext())
            {
                if (db.urls.Where(f => f.FullUrl == ODD_URL).FirstOrDefault() == null)
                {
                    // Save odd url
                    Url oddNameFile = new Url();
                    oddNameFile.FullUrl = ODD_URL;
                    db.urls.Add(oddNameFile);
                    db.SaveChanges();

                }

                // Find it again. There can in theory be many as there's no index
                var result = db.urls.Where(f => f.FullUrl == ODD_URL).First();

                Assert.IsNotNull(result);
                Assert.AreEqual(result.FullUrl, ODD_URL);

                // Try and add it again. Expect error
                //No index on full_url due to size of field. 
            }
        }


        [TestMethod]
        public void GreekFilenameTest()
        {
            const string FILENAME = "Συνημμένο Α.pdf";
            using (var db = new AnalyticsEntitiesContext())
            {
                if (db.event_file_names.Where(f => f.Name == FILENAME).FirstOrDefault() == null)
                {
                    // Save odd name
                    var oddNameFile = new SPEventFileName();
                    oddNameFile.Name = FILENAME;
                    db.event_file_names.Add(oddNameFile);
                    db.SaveChanges();

                }

                // Find it again. There can in theory be many as there's no index
                var result = db.event_file_names.Where(f => f.Name == FILENAME).First();

                Assert.IsNotNull(result, "Nonstandard filename save & find test failure");
                Assert.AreEqual(result.Name, FILENAME);
            }
        }

        [TestMethod]
        public void HebrewFileNameTest()
        {
            const string ODD_NAME = "מדריך הגשת בקשות קרנות הפעלה.docx";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                db.Database.Log = s => Console.WriteLine(s);

                if (db.event_file_names.Where(f => f.Name == ODD_NAME).FirstOrDefault() == null)
                {
                    // Save odd name
                    SPEventFileName oddNameFile = new SPEventFileName();
                    oddNameFile.Name = ODD_NAME;
                    db.event_file_names.Add(oddNameFile);
                    db.SaveChanges();

                }

                // Find it again
                var result = db.event_file_names.Where(f => f.Name == ODD_NAME).SingleOrDefault();

                Assert.IsNotNull(result, "Nonstandard file-name save & find test failure");

                // Try and add it again. Expect error
                SPEventFileName oddNameFile2 = new SPEventFileName();
                oddNameFile2.Name = ODD_NAME;
                db.event_file_names.Add(oddNameFile2);
                Assert.ThrowsException<DbUpdateException>(() =>
                {
                    db.SaveChanges();
                }
                );
            }
        }


        [TestMethod]
        public void FileNameLookupTest()
        {
            var myUniqueFileName = string.Format(@"{0}¨odd-chárs-why-nõt.txt", Guid.NewGuid());
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                SharePointLookupManager lookupManager = new SharePointLookupManager(db);

                // Expect new
                var newFile = lookupManager.GetOrCreateEventFilename(myUniqueFileName);
                Assert.IsTrue(newFile.ID == 0);

                db.event_file_names.Add(newFile);
                db.SaveChanges();

                // Expect saved
                var existingName = lookupManager.GetOrCreateEventFilename(myUniqueFileName);
                Assert.IsTrue(newFile.ID > 0);
            }
        }

        [TestMethod]
        public void ImportLogBasicTest()
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                db.Database.Log = s => System.Diagnostics.Debug.WriteLine(s);

                ImportLog log = new ImportLog();
                log.hit_id = 99999;
                log.machine_name = Environment.MachineName;
                log.contents = "unit test";
                log.time_stamp = DateTime.Now;
                log.import_message = "Testing...1, 2....";

                db.import_log.Add(log);

                db.SaveChanges();
            }
        }

        /// <summary>
        /// Test save and uniqueness
        /// </summary>
        [TestMethod]
        public void UserTest()
        {
            const string NAME = "unit-testing@whatevs.com";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                User existing = db.users.Where(r => r.UserPrincipalName == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.users.Remove(existing);
                    db.SaveChanges();
                }

                User u = new User();
                u.UserPrincipalName = NAME;

                db.users.Add(u);
                db.SaveChanges();

                // Expect error
                existing = new User();
                existing.UserPrincipalName = NAME;

                db.users.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }

            }
        }


        [TestMethod]
        public void FilenameTest()
        {
            const string NAME = "file-tests.docx";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                SPEventFileName existing = db.event_file_names.Where(r => r.Name == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.event_file_names.Remove(existing);
                    db.SaveChanges();
                }

                SPEventFileName u = new SPEventFileName();

                u.Name = NAME;

                db.event_file_names.Add(u);
                db.SaveChanges();

                // Expect error
                existing = new SPEventFileName();
                existing.Name = NAME;
                db.event_file_names.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }

        [TestMethod]
        public void EventTypeTest()
        {
            const string NAME = "Unit test type";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                SPEventType existing = db.event_types.Where(r => r.type_name == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.event_types.Remove(existing);
                    db.SaveChanges();
                }

                SPEventType u = new SPEventType();

                u.type_name = NAME;

                db.event_types.Add(u);
                db.SaveChanges();

                // Expect error
                existing = new SPEventType();
                existing.type_name = NAME;
                db.event_types.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }

        [TestMethod]
        public void EventOperationTest()
        {
            const string NAME = "Unit Test Op";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                EventOperation existing = db.event_operations.Where(r => r.Name == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.event_operations.Remove(existing);
                    db.SaveChanges();
                }
                EventOperation u = new EventOperation();

                u.Name = NAME;

                db.event_operations.Add(u);
                db.SaveChanges();

                // Expect error
                existing = new EventOperation();
                existing.Name = NAME;
                db.event_operations.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }


        /// <summary>
        /// Not in use as no index, due to varchar(max) not being possible for unique indexes
        /// </summary>
        [Obsolete()]
        public void URLsTest()
        {
            const string NAME = "http://whatevs";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                Url existing = db.urls.Where(r => r.FullUrl == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.urls.Remove(existing);
                    db.SaveChanges();
                }

                Url u = new Url();
                u.FullUrl = NAME;

                db.urls.Add(u);
                db.SaveChanges();


                // Expect error
                existing = new Url();
                existing.FullUrl = NAME;
                db.urls.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }


        [TestMethod]
        public void CitiesTest()
        {
            const string NAME = "Winchester";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                City existing = db.cities.Where(r => r.city_name == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.cities.Remove(existing);
                    db.SaveChanges();
                }

                City u = new City();
                u.city_name = NAME;

                db.cities.Add(u);
                db.SaveChanges();


                // Expect error
                existing = new City();
                existing.city_name = NAME;
                db.cities.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }

        [TestMethod]
        public void CountriesTest()
        {
            const string NAME = "Ubeki-beki-beki-beki-stan-stan";
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                Country existing = db.countries.Where(r => r.country_name == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.countries.Remove(existing);
                    db.SaveChanges();
                }

                Country u = new Country();

                u.country_name = NAME;

                db.countries.Add(u);
                db.SaveChanges();


                // Expect error
                existing = new Country();
                existing.country_name = NAME;
                db.countries.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }


        [TestMethod]
        public void FileExtTest()
        {
            var NAME = "docx " + DateTime.Now.Ticks;
            using (var db = new AnalyticsEntitiesContext())
            {
                var existing = db.event_file_ext.Where(r => r.extension_name == NAME).FirstOrDefault();
                if (existing != null)
                {
                    db.event_file_ext.Remove(existing);
                    db.SaveChanges();
                }

                var u = new SPEventFileExtension();

                u.extension_name = NAME;

                db.event_file_ext.Add(u);
                db.SaveChanges();


                // Expect error
                existing = new SPEventFileExtension();
                existing.extension_name = NAME;
                db.event_file_ext.Add(existing);
                try
                {
                    db.SaveChanges();
                    Assert.Fail("Shouldn't have worked");
                }
                catch (DbUpdateException)
                {
                    // Good
                }
            }
        }



        [TestMethod]
        public void OrgsAndURLs()
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                db.Database.Log = s => System.Diagnostics.Debug.WriteLine(s);

                OrgUrl u1 = db.org_urls.Where(u => u.UrlBase == "http://").FirstOrDefault();
                if (u1 == null)
                {
                    u1 = new OrgUrl();
                    u1.UrlBase = "http://";
                }

                OrgUrl u2 = db.org_urls.Where(u => u.UrlBase == "https://").FirstOrDefault();
                if (u2 == null)
                {
                    u2 = new OrgUrl();
                    u2.UrlBase = "https://";
                }

                Org o = db.orgs.Where(u => u.org_name == "test org").FirstOrDefault();
                if (o == null)
                {
                    o = new Org() { org_name = "test org" };
                    db.orgs.Add(o);
                }
                o.org_urls.Add(u1);
                o.org_urls.Add(u2);

                // Save
                db.SaveChanges();
            }
        }

        [TestMethod]
        public async Task Searches()
        {
            SearchTerm search1 = (from allTerms in DB.search_terms
                                  where allTerms.search_term == "test search 1"
                                  select allTerms).FirstOrDefault();
            Search s1 = new Search();
            if (search1 == null)
            {
                search1 = new SearchTerm() { search_term = "test search 1" };

            }
            s1.search_term = search1;
            s1.session = GetTestingSession();
            DB.searches.Add(s1);


            SearchTerm search2 = (from allTerms in DB.search_terms
                                  where allTerms.search_term == "test search 2"
                                  select allTerms).FirstOrDefault();
            Search s2 = new Search();
            if (search2 == null)
            {
                search2 = new SearchTerm() { search_term = "test search 2" };
            }
            s2.search_term = search2;
            s2.session = GetTestingSession();
            DB.searches.Add(s2);


            await DB.SaveChangesAsync();
        }

        [TestMethod]
        [ExpectedException(typeof(DbUpdateException))]
        public async Task SearchesUniqueChecks()
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                db.Database.Log = s => System.Diagnostics.Debug.WriteLine(s);

                string randomSearchTerm = "Random Term " + DateTime.Now.Ticks;

                Search search1 = new Search();
                search1.search_term = new SearchTerm() { search_term = randomSearchTerm };
                search1.session = GetTestingSession();
                db.searches.Add(search1);

                Search search2 = new Search();
                search2.search_term = new SearchTerm() { search_term = randomSearchTerm };
                search2.session = GetTestingSession();
                db.searches.Add(search2);

                await db.SaveChangesAsync();
            }
        }

        public void Dispose()
        {
            this.DB.Dispose();
        }
    }
}
