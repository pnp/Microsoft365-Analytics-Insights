using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.LookupCaches;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class AuditTests
    {
        public AuditTests()
        {
        }

        #region Private Methods

        private async Task<User> GetTestingUser(UserCache lookupManager)
        {
            string username = "unit test user";
            return await lookupManager.GetOrCreateUser(username, true);
        }

        private UserSession _session = null;
        private async Task<UserSession> GetTestingSession(UserCache lookupManager)
        {
            if (_session == null)
            {
                _session = new UserSession() { ai_session_id = "1" };
                _session.user = await GetTestingUser(lookupManager);
            }
            return _session;
        }

        #endregion


        [TestMethod]
        public async Task AddExchangeEvent()
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                var userCache = new UserCache(db);
                // Create generic event
                Office365Event newEvent = new Office365Event();
                newEvent.Id = Guid.NewGuid();

                newEvent.Operation = new EventOperation { Name = "test op " + DateTime.Now.Ticks };
                newEvent.User = await GetTestingUser(userCache);
                newEvent.TimeStamp = DateTime.Now;

                db.AuditEventsCommon.Add(newEvent);

                // Create Exchange event
                ExchangeEventMetadata exchangeEvent = new ExchangeEventMetadata();
                exchangeEvent.Properties.Add(new ExchangeExtendedProperties()
                {
                    name = SharePointLookupManager.GetAuditPropertyName("Name", db),
                    value = SharePointLookupManager.GetAuditPropertyValue("Val", db)
                }
                );
                exchangeEvent.Event = newEvent;
                db.exchange_events.Add(exchangeEvent);

                db.SaveChanges();

                // Find again
                ExchangeEventMetadata saved = db.exchange_events.Where(e => e.EventID == exchangeEvent.EventID).FirstOrDefault();
                Assert.IsNotNull(saved, "Couldn't find previously saved Exchange event");


                Assert.IsTrue(saved.Properties.Count > 0, "Couldn't find previously saved Exchange event properties");
            }
        }

        [TestMethod]
        public async Task AddGeneralEvent()
        {

            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                var userCache = new UserCache(db);

                // Create generic event
                Office365Event newEvent = new Office365Event();
                newEvent.Id = Guid.NewGuid();

                newEvent.Operation = new EventOperation { Name = "test op " + DateTime.Now.Ticks };
                newEvent.User = await GetTestingUser(userCache);
                newEvent.TimeStamp = DateTime.Now;

                db.AuditEventsCommon.Add(newEvent);


                GeneralEventMetada generalEvent = new GeneralEventMetada();
                generalEvent.json = "unit-test jSon";
                generalEvent.Event = newEvent;
                db.general_audit_events.Add(generalEvent);
                db.SaveChanges();

            }
        }
    }
}
