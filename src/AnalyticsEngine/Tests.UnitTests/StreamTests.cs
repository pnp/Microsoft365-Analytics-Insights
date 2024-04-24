using Common.Entities;
using Common.Entities.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class StreamTests
    {
        [TestMethod()]
        public async Task StreamLinksInYammerMsgTests()
        {
            var randomGuid = Guid.NewGuid();

            using (var db = new AnalyticsEntitiesContext())
            {
                var lookupManager = new StreamLookupManager(db);

                var newLookup = await lookupManager.GetOrCreateStreamVideo(randomGuid);

                Assert.IsNotNull(newLookup);

                var newLookup2 = await lookupManager.GetCreateOrUpdateStreamVideo(randomGuid, "Test name");
                Assert.IsFalse(string.IsNullOrEmpty(newLookup2.Name));

                // Save
                await db.SaveChangesAsync();

            }
            using (var db = new AnalyticsEntitiesContext())
            {
                // Get again with new context
                var lookupManager = new StreamLookupManager(db);


                var newLookupDB = await lookupManager.GetCreateOrUpdateStreamVideo(randomGuid, "Test name");
                Assert.IsFalse(string.IsNullOrEmpty(newLookupDB.Name));

                // Make sure it actually came from the DB
                Assert.IsTrue(newLookupDB.IsSavedToDB);

                // Make sure we can't insert duplicates
                db.Streams.Add(new StreamVideo { StreamID = randomGuid });

                await Assert.ThrowsExceptionAsync<System.Data.Entity.Infrastructure.DbUpdateException>(() => db.SaveChangesAsync());


            }
        }
    }

}


