using DataUtils;
using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
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
                try
                {
                    NewObjectCreating?.Invoke(this, newTemplate);

                    this.EntityStore.Add(newTemplate);
                    if (commitChangeOnSaveNew)
                    {
                        await DB.SaveChangesAsync();
                    }
                    return newTemplate;
                }
                catch (DbUpdateException ex)
                {
                    // Handle duplicate key constraint violations that can occur in batch processing scenarios
                    // Check if it's a unique constraint/index violation
                    var sqlException = ex.InnerException?.InnerException as SqlException;
                    if (sqlException != null && (sqlException.Number == 2601 || sqlException.Number == 2627))
                    {
                        // SQL Error 2601: Cannot insert duplicate key row with unique index
                        // SQL Error 2627: Violation of %ls constraint '%.*ls'. Cannot insert duplicate key

                        // Remove the failed entity from context to prevent further issues
                        DB.Entry(newTemplate).State = EntityState.Detached;

                        // Try to reload from database - another batch may have inserted it
                        var existing = await this.Load(key);
                        if (existing != null)
                        {
                            return existing;
                        }

                        // If still not found, this is an unexpected state - rethrow
                        throw new InvalidOperationException(
                            $"Duplicate key constraint violation for lookup '{typeof(T).Name}' with key '{key}', but entity not found in database after reload.",
                            ex);
                    }

                    // Not a duplicate key error, rethrow
                    throw;
                }
            });
        }


        public abstract DbSet<T> EntityStore { get; }

    }

    public abstract class DBLookupCacheForEntityWithName<T> : DBLookupCache<T> where T : AbstractEFEntityWithName
    {
        protected DBLookupCacheForEntityWithName(AnalyticsEntitiesContext context) : base(context)
        {
        }


        public async override Task<T> Load(string searchName)
        {
            // Use FirstOrDefaultAsync instead of SingleOrDefaultAsync to handle existing duplicate records gracefully
            // Order by ID to ensure consistent results if duplicates exist
            return await EntityStore.Where(t => t.Name == searchName).OrderBy(t => t.ID).FirstOrDefaultAsync();
        }
    }
}
