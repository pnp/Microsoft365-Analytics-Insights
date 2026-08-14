using Common.Entities;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace Tests.UnitTests
{
    [TestClass]
    public class DuplicateUrlTests
    {
        [TestMethod]
        public async Task CleanDuplicateUrls_IsNoOpWhenUniqueIndexIsEnforced()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var url = "https://contoso.sharepoint.com/sites/example/Καλημέρα-" + DateTime.Now.Ticks;
                db.urls.Add(new Url { FullUrl = url });
                await db.SaveChangesAsync();

                await ImportDbHacks.CleanDuplicateUrls(db);

                Assert.AreEqual(1, await db.urls.CountAsync(item => item.FullUrl == url));
                Assert.AreEqual(url, (await db.urls.SingleAsync(item => item.FullUrl == url)).FullUrl);

                db.urls.RemoveRange(db.urls.Where(item => item.FullUrl == url));
                await db.SaveChangesAsync();
            }
        }
    }
}
