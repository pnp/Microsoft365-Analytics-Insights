using Common.DataUtils;
using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps
{
    /// <summary>
    /// UserAppLoader that loads from Graph and saves to SQL.
    /// </summary>
    public class GraphAndSqlUserAppLoader : AbstractUserAppLoader<UserTeamApp>
    {

#if DEBUG
        public const string STAGING_TABLE_NAME = "debug_user_app_log";
#else
        public const string STAGING_TABLE_NAME = "##tmp_user_app_log";

#endif

        private readonly AnalyticsEntitiesContext _db;
        private readonly GraphServiceClient _graphClient;
        public GraphAndSqlUserAppLoader(AnalyticsEntitiesContext db, AnalyticsLogger telemetry, GraphServiceClient graphClient) : base(telemetry)
        {
            _db = db;
            _graphClient = graphClient;
        }

        public override async Task<List<string>> GetUserEmailAddressesToFindAppsFor()
        {
            // Get users with account enabled set, or without any account enabled value (exclude users with account definately disabled).
            // ...plus users with email address that's not external
            var usersWithLicenses = await _db.users.Include(u => u.LicenseLookups)
                                .Where(u => ((u.AccountEnabled.HasValue && u.AccountEnabled.HasValue) || !u.AccountEnabled.HasValue)
                                    && !string.IsNullOrEmpty(u.UserPrincipalName) && u.UserPrincipalName.Contains("@") && !u.UserPrincipalName.Contains("#ext#"))
                                .ToListAsync();

            _telemetry.LogInformation($"User Apps Load - updating Teams Apps for {usersWithLicenses.Count.ToString("N0")} users...");

            return usersWithLicenses.Select(u => u.UserPrincipalName).ToList();
        }

        public override async Task<bool> TestReadPermissionsForUser(string testUserEmail)
        {
            // Test permissions
            var permissionsConfirmed = false;
            try
            {
                await _graphClient.Users[testUserEmail].Teamwork.InstalledApps
                    .Request().Expand("TeamsAppDefinition,TeamsApp").GetAsync();
                permissionsConfirmed = true;
            }
            catch (ServiceException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _telemetry.LogError(ex, $"User Apps Load - access denied reading user Teams Apps. Check 'TeamsAppInstallation.ReadForUser.All' is granted.");
                    throw new AccessDeniedToTeamsAppsException();
                }
                else if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _telemetry.LogError($"User Apps Load - user not found reading user Teams Apps.");
                    throw new UserNotFoundException();
                }
                else
                {
                    _telemetry.LogError(ex, "Unexpected error reading installed Teams apps for user");
                    // Permissions test failed but not for permissions. Blow up. 
                    throw;
                }
            }
            return permissionsConfirmed;
        }

        public override async Task<Dictionary<string, UserAppsLoadState<UserTeamApp>>> LoadAppsForUsers(List<string> emailAddresses, int attemptCount)
        {
            var results = new Dictionary<string, UserAppsLoadState<UserTeamApp>>();
#if DEBUG
            const int REPORT_EVERY = 1000;
#else
            const int REPORT_EVERY = 10000;
#endif

            const int MAX_REQS = 20;
            var reqs = new Dictionary<string, UserBatchRequestKeyPair>();
            var batchRequestContent = new BatchRequestContent();

            int i = 0;
            foreach (var emailAddress in emailAddresses)
            {
                var reqId = batchRequestContent.AddBatchRequestStep(_graphClient.Users[emailAddress].Teamwork.InstalledApps.Request().Expand("TeamsAppDefinition,TeamsApp"));
                reqs.Add(emailAddress, new UserBatchRequestKeyPair { BatchRequestId = reqId, EmailAddress = emailAddress });

                if (batchRequestContent.BatchRequestSteps.Count == MAX_REQS)
                {
                    var segmentR = await ProcessBatchRequests(batchRequestContent, reqs);
                    foreach (var item in segmentR)
                    {
                        results.Add(item.Key, item.Value);
                    }
                    reqs.Clear();
                    batchRequestContent = new BatchRequestContent();
                }

                if (i > 0 && i % REPORT_EVERY == 0)
                {
                    _telemetry.LogInformation($"User Apps Load - user {i.ToString("N0")}/{emailAddresses.Count.ToString("N0")} Teams app info loaded from Graph");
                }

                i++;
            }

            // Process remaining
            var r = await ProcessBatchRequests(batchRequestContent, reqs);
            foreach (var item in r)
            {
                results.Add(item.Key, item.Value);
            }

            return results;
        }

        async Task<Dictionary<string, UserAppsLoadState<UserTeamApp>>> ProcessBatchRequests(BatchRequestContent batchRequestContent, Dictionary<string, UserBatchRequestKeyPair> requestsSource)
        {
            var results = new Dictionary<string, UserAppsLoadState<UserTeamApp>>();

            if (requestsSource.Count == 0) return new Dictionary<string, UserAppsLoadState<UserTeamApp>>();

            var returnedResponse = await _graphClient.Batch.Request().PostAsync(batchRequestContent);

            var throttledRequestInBatch = false;
            foreach (var r in requestsSource)
            {
                UserTeamAppResponse userApps = null;

                var response = await returnedResponse.GetResponseByIdAsync(r.Value.BatchRequestId);
                var responseBody = await response.Content.ReadAsStringAsync();
                try
                {
                    response.EnsureSuccessStatusCode();

                    userApps = JsonConvert.DeserializeObject<UserTeamAppResponse>(responseBody);
                }
                catch (HttpRequestException)
                {
                    if ((int)response.StatusCode == 429)
                    {
                        throttledRequestInBatch = true;
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _telemetry.LogWarning($"User Apps Load - user with ID '{r.Key}' not found in configured tenant");
                    }
                    else
                    {
                        // Usually means user has no Teams license
                        _telemetry.LogWarning($"User Apps Load - got unexpected error on batch response for user with ID '{r.Key}'. Check user has Teams license? HTTP response: {response.StatusCode}.");
                    }
                }

                // To-do: add paging
                results.Add(r.Key, new UserAppsLoadState<UserTeamApp> { Results = userApps?.Apps, Retry = throttledRequestInBatch });
            }

            return results;
        }

        public override async Task Save(Dictionary<string, List<UserTeamApp>> pendingSave)
        {
            var logsToInsert = new EFInsertBatch<UserAppLogTempEntity>(_db, _telemetry);

            // Add user + apps to flat temp table
            foreach (var userAndState in pendingSave)
            {
                if (userAndState.Value == null)
                {
                    Console.WriteLine($"DEBUG: No apps loaded for '{userAndState.Key}'. Will skip this import.");
                }
                else
                {
                    var userLogs = new List<UserAppLogTempEntity>();

                    // Save to SQL
                    foreach (var app in userAndState.Value)
                    {
                        var tmpApp = new UserAppLogTempEntity(app.TeamsAppDefinition, userAndState.Key);
                        userLogs.Add(tmpApp);
                    }

                    logsToInsert.Rows.AddRange(userLogs);
                }
            }

            // Merge all at once
            var sql = @"

-- Addin names can be distinct for the same ID (same ID with x2 seperate names). Make sure we select by ID 1st and then by name, to avoid violating unique index 'IX_graph_id'
INSERT INTO teams_addons(graph_id, name, addon_type)
	SELECT import_app_ids.app_def_id, (SELECT TOP 1 app_name app_names FROM " + STAGING_TABLE_NAME + @" b where b.app_def_id = import_app_ids.app_def_id), 0 from (
		SELECT distinct app_def_id
			FROM " + STAGING_TABLE_NAME + @"
			) import_app_ids
		left join teams_addons dups on dups.graph_id = import_app_ids.app_def_id
		where dups.graph_id is null and import_app_ids.app_def_id is not null;

INSERT INTO teams_addons(name, graph_id, addon_type)
	SELECT distinct imports.app_name, imports.app_def_id, 0
	FROM " + STAGING_TABLE_NAME + @" imports
	left join teams_addons dups on dups.graph_id = imports.app_def_id
	where dups.graph_id is null and imports.app_def_id is not null;
  
-- Insert logs where they don't exist already for today
insert into teams_addons_user_installed_log(addon_id, user_id, date)
	select * from
	(SELECT teams_addons.id as addonId
		  ,users.id as userId
		  ,(SELECT CAST( GETDATE() AS Date )) as newLogDate
	  FROM 
		" + STAGING_TABLE_NAME + @" imports
	  inner join teams_addons on teams_addons.graph_id = imports.app_def_id
	  inner join users on users.user_name = imports.email
	) newLogs 
	where not exists 
	(select * from teams_addons_user_installed_log where user_id = newLogs.userId and addon_id = newLogs.addonId and date = newLogDate)
";

            // Save to SQL
            try
            {
                await logsToInsert.SaveToStagingTable(sql);
            }
            catch (System.Data.SqlClient.SqlException ex)
            {
                _telemetry.LogError(ex, $"User Apps Load - got SQL error saving user apps");
            }
        }

        public override TimeSpan GetDelay(int throttledAttempts)
        {
            var seconds = 10 * throttledAttempts;
            if (seconds > 60) seconds = 60;
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
