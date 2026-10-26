using Common.Entities;
using Newtonsoft.Json;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    /// <summary>
    /// A Graph activity record with a lookup to another record (usually "user")
    /// </summary>
    public abstract class AbstractActivityRecord<T> where T : AbstractEFEntity
    {
        [JsonProperty("lastActivityDate")]
        public string LastActivityDateString { get; set; }

        /// <summary>
        /// Get associated DB lookup record associated with this activity. Usually a user.
        /// Must save to the DB if creating new so we get a PK ID.
        /// </summary>
        public abstract Task<AbstractEFEntity> GetOrCreateLookup(DBLookupCache<T> lookupCache);

        /// <summary>
        /// Field-value that points to the activity record lookup name
        /// </summary>
        public abstract string LookupFieldValue { get; }

        /// <summary>
        /// The key <see cref="GetOrCreateLookup"/> resolves the lookup by: <see cref="LookupFieldValue"/> after
        /// any normalisation the record applies. The save path's batched pre-resolution (#705) asks for exactly
        /// this key, so it finds the same record the per-row path would.
        /// </summary>
        public virtual string LookupKey => LookupFieldValue;

        /// <summary>
        /// True when Graph replaced this row's identity with a pseudonym because the tenant conceals user, group and
        /// site names in usage reports. Such a row can never be linked to a real record, so the save path skips it
        /// instead of creating a placeholder lookup for it. Must depend only on <see cref="LookupFieldValue"/>: the
        /// save path evaluates it once per distinct value. Never true for a blank value.
        /// </summary>
        public virtual bool IsLookupIdentityConcealed => false;
    }

    public abstract class AbstractUserActivityUserDetail : AbstractActivityRecord<User>
    {

        /// <summary>
        /// The field-value in a user activity report that identifies the related user
        /// </summary>
        public abstract string UserEmailFieldVal { get; }

        public override string LookupFieldValue => UserEmailFieldVal;

        public override string LookupKey => UserEmailFieldVal?.ToLower();

        public override bool IsLookupIdentityConcealed => UsageReportIdentityPolicy.IsConcealedUserIdentity(UserEmailFieldVal);

        public override async Task<AbstractEFEntity> GetOrCreateLookup(DBLookupCache<User> lookupCache)
        {
            return await lookupCache.GetOrCreateNewResource(LookupKey, new User { UserPrincipalName = LookupKey }, true);
        }
    }

    public abstract class AbstractUserActivityUserDetailWithUpn : AbstractUserActivityUserDetail
    {
        public override string UserEmailFieldVal => UserPrincipalName;

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }
    }
}
