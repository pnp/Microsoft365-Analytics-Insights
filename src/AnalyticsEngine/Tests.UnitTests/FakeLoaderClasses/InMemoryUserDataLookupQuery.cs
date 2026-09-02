extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IUserDataLookupQuery"/> so the admin user-data lookup logic can be tested
    /// with zero SQL Server dependency (issue #379). UPN matching is case-insensitive to mirror the
    /// database's default <c>Latin1_General_CI_AS</c> collation.
    /// </summary>
    public class InMemoryUserDataLookupQuery : IUserDataLookupQuery
    {
        private readonly Dictionary<string, UserProfileModel> _profiles = new Dictionary<string, UserProfileModel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Dictionary<string, int>> _counts = new Dictionary<int, Dictionary<string, int>>();
        private readonly Dictionary<int, Dictionary<string, List<UserDataDetailRowModel>>> _rows = new Dictionary<int, Dictionary<string, List<UserDataDetailRowModel>>>();

        /// <summary>How many times all-category counts were asked for (the batched, single-round-trip call).</summary>
        public int CountsByCategoryCallCount { get; private set; }

        /// <summary>How many times a single category's count was asked for (one round trip each).</summary>
        public int CountForCategoryCallCount { get; private set; }

        /// <summary>Every UPN a profile was looked up for, in order.</summary>
        public List<string> ProfileLookups { get; } = new List<string>();

        /// <summary>Every UPN a user id was looked up for, in order.</summary>
        public List<string> UserIdLookups { get; } = new List<string>();

        /// <summary>The <c>take</c> the last drill-down was asked for (before the store applies it).</summary>
        public int? LastTakeRequested { get; private set; }

        /// <summary>Adds a user, and returns the profile so a test can assert on the same instance.</summary>
        public UserProfileModel AddUser(int userId, string upn)
        {
            var profile = new UserProfileModel { UserId = userId, UserPrincipalName = upn };
            _profiles[upn] = profile;
            return profile;
        }

        /// <summary>Sets the record count for one category for a user.</summary>
        public void SetCount(int userId, string categoryKey, int count)
        {
            if (!_counts.TryGetValue(userId, out var byCategory))
            {
                byCategory = new Dictionary<string, int>();
                _counts[userId] = byCategory;
            }
            byCategory[categoryKey] = count;
        }

        /// <summary>Sets the drill-down rows held for one category for a user (newest first).</summary>
        public void SetRows(int userId, string categoryKey, params UserDataDetailRowModel[] rows)
        {
            if (!_rows.TryGetValue(userId, out var byCategory))
            {
                byCategory = new Dictionary<string, List<UserDataDetailRowModel>>();
                _rows[userId] = byCategory;
            }
            byCategory[categoryKey] = rows.ToList();
        }

        public Task<UserProfileModel> GetProfileAsync(string upn)
        {
            ProfileLookups.Add(upn);
            _profiles.TryGetValue(upn ?? string.Empty, out var profile);
            return Task.FromResult(profile);
        }

        public Task<int?> GetUserIdAsync(string upn)
        {
            UserIdLookups.Add(upn);
            return Task.FromResult(_profiles.TryGetValue(upn ?? string.Empty, out var profile) ? (int?)profile.UserId : null);
        }

        public Task<IReadOnlyDictionary<string, int>> GetCountsByCategoryAsync(int userId)
        {
            CountsByCategoryCallCount++;

            // The SQL adapter answers for every known category in one round trip, so the fake does too.
            var all = new Dictionary<string, int>();
            foreach (var meta in UserDataLookupRules.Categories)
            {
                all[meta.Key] = CountOf(userId, meta.Key);
            }
            return Task.FromResult<IReadOnlyDictionary<string, int>>(all);
        }

        public Task<int> GetCountForCategoryAsync(int userId, string categoryKey)
        {
            CountForCategoryCallCount++;
            return Task.FromResult(CountOf(userId, categoryKey));
        }

        public Task<IReadOnlyList<UserDataDetailRowModel>> GetRowsForCategoryAsync(int userId, string categoryKey, int take)
        {
            LastTakeRequested = take;
            var rows = _rows.TryGetValue(userId, out var byCategory) && byCategory.TryGetValue(categoryKey, out var list)
                ? list
                : new List<UserDataDetailRowModel>();
            return Task.FromResult<IReadOnlyList<UserDataDetailRowModel>>(rows.Take(take).ToList());
        }

        private int CountOf(int userId, string categoryKey)
        {
            return _counts.TryGetValue(userId, out var byCategory) && byCategory.TryGetValue(categoryKey, out var count)
                ? count
                : 0;
        }
    }
}
