using Common.Entities.Entities.Email;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class EmailAddressCache : DBLookupCache<EmailAddress>
    {
        public EmailAddressCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<EmailAddress> EntityStore => this.DB.EmailAddresses;

        public async Task<EmailAddress> GetOrCreateEmailAddress(string address)
        {
            return await GetOrCreateNewResource(address, new EmailAddress { Address = address }, true);
        }

        public override async Task<EmailAddress> Load(string address)
        {
            return await EntityStore.Where(e => e.Address == address).FirstOrDefaultAsync();
        }
    }
}
