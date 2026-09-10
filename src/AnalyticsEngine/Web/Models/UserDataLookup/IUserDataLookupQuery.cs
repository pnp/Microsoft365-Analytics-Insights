using System.Collections.Generic;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserDataLookup
{
    /// <summary>
    /// Read port over everything the admin user-data lookup needs from storage. The SQL/EF adapter is
    /// <see cref="SqlUserDataLookupQuery"/>; tests use an in-memory fake, so the service logic runs with
    /// zero SQL Server dependency. See issues #379 / #381.
    /// </summary>
    public interface IUserDataLookupQuery
    {
        /// <summary>The user's profile (with its de-normalised lookups), or null when no such UPN exists.</summary>
        Task<UserProfileModel> GetProfileAsync(string upn);

        /// <summary>The user's id, or null when no such UPN exists.</summary>
        Task<int?> GetUserIdAsync(string upn);

        /// <summary>
        /// Record counts for <em>every</em> category, keyed by category key, in a single round trip.
        /// Callers must tolerate a missing key (treat it as 0), which is what a user id that no longer
        /// exists produces.
        /// </summary>
        Task<IReadOnlyDictionary<string, int>> GetCountsByCategoryAsync(int userId);

        /// <summary>The record count for one category (0 for a category key we don't know).</summary>
        Task<int> GetCountForCategoryAsync(int userId, string categoryKey);

        /// <summary>The most recent <paramref name="take"/> rows for one category, newest first.</summary>
        Task<IReadOnlyList<UserDataDetailRowModel>> GetRowsForCategoryAsync(int userId, string categoryKey, int take);
    }
}
