using Common.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserDataLookup
{
    /// <summary>
    /// Builds the admin user-data lookup responses from <see cref="IUserDataLookupQuery"/>. All the
    /// validation, category mapping and shaping that used to live inside
    /// <c>UserDataLookupAPIController</c> lives here, so it can be tested without a database or an
    /// ASP.NET pipeline. See issues #379 / #381.
    /// </summary>
    public class UserDataLookupService : IUserDataLookupService
    {
        private readonly IUserDataLookupQuery _query;

        public UserDataLookupService(IUserDataLookupQuery query)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
        }

        public async Task<UserDataLookupResult<UserDataSummaryModel>> GetSummaryAsync(string upn, Func<ImportTaskSettings> importSettingsProvider)
        {
            if (importSettingsProvider == null) throw new ArgumentNullException(nameof(importSettingsProvider));

            upn = UserDataLookupRules.Normalise(upn);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<UserDataSummaryModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var profile = await _query.GetProfileAsync(upn);
            if (profile == null)
            {
                return UserDataLookupResult<UserDataSummaryModel>.UserNotFound($"No user found with UPN '{upn}'.");
            }

            var summary = new UserDataSummaryModel { Profile = profile };

            // Which import workloads are enabled for this deployment - shown so an admin can see
            // why a category might legitimately have 0 records (nothing is importing it). Read only
            // now, once the user is known to exist, as the controller used to.
            var importSettings = importSettingsProvider();
            foreach (var def in UserDataLookupRules.Workloads)
            {
                summary.Workloads.Add(new WorkloadModel
                {
                    Name = def.Name,
                    Description = def.Description,
                    Enabled = UserDataLookupRules.WorkloadEnabled(importSettings, def.Flag),
                });
            }

            // One round trip for every category count (this used to be one query per category).
            var counts = await _query.GetCountsByCategoryAsync(profile.UserId);

            foreach (var meta in UserDataLookupRules.Categories)
            {
                summary.Categories.Add(new UserDataCategoryModel
                {
                    Key = meta.Key,
                    Label = meta.Label,
                    Description = meta.Description,
                    Count = CountFor(counts, meta.Key),
                    SupportsDetail = meta.SupportsDetail,
                    SqlQuery = UserDataLookupRules.BuildCountSql(meta, upn),
                    Workloads = meta.WorkloadFlags.Select(UserDataLookupRules.WorkloadName).ToList(),
                    WorkloadsEnabled = meta.WorkloadFlags.Any(f => UserDataLookupRules.WorkloadEnabled(importSettings, f)),
                });
            }

            return UserDataLookupResult<UserDataSummaryModel>.Ok(summary);
        }

        public async Task<UserDataLookupResult<UserDataDetailResponseModel>> GetDetailAsync(string upn, string category, int take)
        {
            upn = UserDataLookupRules.Normalise(upn);
            category = UserDataLookupRules.Normalise(category);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<UserDataDetailResponseModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var meta = UserDataLookupRules.FindCategory(category);
            if (meta == null)
            {
                return UserDataLookupResult<UserDataDetailResponseModel>.BadRequest($"Unknown category '{category}'.");
            }
            if (!meta.SupportsDetail)
            {
                return UserDataLookupResult<UserDataDetailResponseModel>.BadRequest($"Category '{category}' does not support drill-down.");
            }

            take = UserDataLookupRules.ClampTake(take);

            var userId = await _query.GetUserIdAsync(upn);
            if (userId == null)
            {
                return UserDataLookupResult<UserDataDetailResponseModel>.UserNotFound($"No user found with UPN '{upn}'.");
            }

            var total = await _query.GetCountForCategoryAsync(userId.Value, meta.Key);
            var rows = await _query.GetRowsForCategoryAsync(userId.Value, meta.Key, take);
            var rowList = rows as List<UserDataDetailRowModel> ?? rows.ToList();

            return UserDataLookupResult<UserDataDetailResponseModel>.Ok(new UserDataDetailResponseModel
            {
                Category = meta.Key,
                Label = meta.Label,
                TotalCount = total,
                ReturnedCount = rowList.Count,
                Rows = rowList,
            });
        }

        /// <summary>
        /// A category the batched query didn't answer counts as 0 - matching the per-category count's
        /// own "unknown category / no such user" behaviour.
        /// </summary>
        private static int CountFor(IReadOnlyDictionary<string, int> counts, string key)
        {
            if (counts != null && counts.TryGetValue(key, out var count)) return count;
            return 0;
        }
    }
}
