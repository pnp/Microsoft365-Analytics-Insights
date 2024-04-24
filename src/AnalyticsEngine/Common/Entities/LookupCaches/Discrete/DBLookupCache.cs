using Common.DataUtils;
using System;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities
{
    /// <summary>
    /// Base cache implementation
    /// </summary>
    /// <typeparam name="T">Type of entity being cached</typeparam>
    public abstract class DBLookupCache<T> : ObjectByIdCache<T> where T : AbstractEFEntity
    {
        public AnalyticsEntitiesContext DB { get; set; }
        public DBLookupCache(AnalyticsEntitiesContext context)
        {
            this.DB = context;
        }

        public static CACHETYPE Create<CACHETYPE>(AnalyticsEntitiesContext context) where CACHETYPE : DBLookupCache<T>
        {
            return (CACHETYPE)Activator.CreateInstance(typeof(CACHETYPE), context);
        }

        /// <summary>
        /// Object not found in DB. Adding to database.
        /// </summary>
        public event EventHandler<T> NewObjectCreating;

        /// <summary>
        /// Loads from cache or if doesn't exist in cache, from DB & adds to cache for next time.
        /// Doesn't save on insert by default.
        /// </summary>
        public async virtual Task<T> GetOrCreateNewResource(string key, T newTemplate)
        {
            return await GetOrCreateNewResource(key, newTemplate, false);
        }
        /// <summary>
        /// Loads from cache or if doesn't exist in cache, from DB & adds to cache for next time.
        /// </summary>
        public async virtual Task<T> GetOrCreateNewResource(string key, T newTemplate, bool commitChangeOnSaveNew)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            // Trim as SQL will also do so - https://support.microsoft.com/en-gb/topic/inf-how-sql-server-compares-strings-with-trailing-spaces-b62b1a2d-27d3-4260-216d-a605719003b0
            key = key.Trim();

            return await base.GetResource(key, async () =>
            {
                NewObjectCreating?.Invoke(this, newTemplate);

                this.EntityStore.Add(newTemplate);
                if (commitChangeOnSaveNew)
                {
                    await DB.SaveChangesAsync();
                }
                return newTemplate;
            });
        }


        public abstract DbSet<T> EntityStore { get; }

    }

}
