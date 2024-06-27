using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps
{
    /// <summary>
    /// Base loader & saver. Retries failed loads
    /// </summary>
    public abstract class AbstractUserAppLoader<APPTYPE>
    {
        protected readonly AnalyticsLogger _telemetry;

        private int _totalSaves = 0;
        private int _lastReportedPercentDone = 0;

        public AbstractUserAppLoader(AnalyticsLogger telemetry)
        {
            _telemetry = telemetry;
        }

        public abstract TimeSpan GetDelay(int throttledAttempts);


        public abstract Task<Dictionary<string, UserAppsLoadState<APPTYPE>>> LoadAppsForUsers(List<string> emailAddresses, int attemptCount);
        public abstract Task<List<string>> GetUserEmailAddressesToFindAppsFor();

        public abstract Task<bool> TestReadPermissionsForUser(string testUserEmail);

        public abstract Task Save(Dictionary<string, List<APPTYPE>> usersAndApps);

        public async Task<int> LoadAndSave()
        {
            var timer = new JobTimer(_telemetry, "User Apps load");
            timer.Start();

            _totalSaves = 0;
            _lastReportedPercentDone = 0;

            var results = await GetUserEmailAddressesToFindAppsFor();
            var usersEmailsToUpdate = new List<string>();
            foreach (var usersEmailResult in results)
            {
                if (StringUtils.IsEmail(usersEmailResult))
                {
                    usersEmailsToUpdate.Add(usersEmailResult);
                }
            }

            if (usersEmailsToUpdate.Count > 0)
            {
                var havePerms = await TestReadPermissions(usersEmailsToUpdate);
                if (havePerms)
                {
                    // Process users in chunks
                    var list = new ListBatchProcessor<string>(1000, async chunk => await LoadAndSaveAppsForUsers(chunk, usersEmailsToUpdate.Count));

                    // Add all users
                    list.AddRange(usersEmailsToUpdate);

                    // Process any remaining
                    list.Flush();
                }
            }
            else
            {
                _telemetry.LogWarning("User Apps Load - no users to process");
            }

            timer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);

            return _totalSaves;
        }

        private async Task<bool> TestReadPermissions(List<string> userEmails)
        {
            if (userEmails is null || userEmails.Count == 0)
            {
                throw new ArgumentNullException(nameof(userEmails));
            }

            var success = false;
            int userNotFoundCount = 0;

            // Keep going until a positive result or an exception
            while (true)
            {
                var testUser = userEmails[userNotFoundCount];
                _telemetry.LogInformation($"User Apps Load - testing API permissions for 'TeamsAppInstallation.ReadForUser.All' read with '{testUser}'...");
                try
                {
                    success = await TestReadPermissionsForUser(testUser);
                }
                catch (AccessDeniedToTeamsAppsException)
                {
                    // We tried to get apps for a user & got a very specific "access denied". No point trying any more users.
                    return false;
                }
                catch (UserNotFoundException)
                {
                    // The user we tried with doesn't exist. We still don't know if we have access or not, so try with another
                    userNotFoundCount++;
                    _telemetry.LogWarning($"User Apps Load - '{testUser}' wasn't found...");
                    if (userNotFoundCount == userEmails.Count)
                    {
                        // We've run out of email addresses to test. Give up. 
                        throw;
                    }
                }

                // If we didn't have a success it'll be because the user wasn't found
                if (success)
                {
                    _telemetry.LogInformation($"User Apps Load - 'TeamsAppInstallation.ReadForUser.All' permissions verified");
                    return true;
                }
            }
        }


        /// <summary>
        /// Load and save a chunk of users at a time so we handle batch throttling nicely
        /// </summary>
        private async Task LoadAndSaveAppsForUsers(List<string> emailAddressesChunk, int totalEmailAddressesCount)
        {
            var chunkUserApps = new Dictionary<string, List<APPTYPE>>();

            // Loop until it works
            var usersLoadPending = true;
            int loopCount = 0;

            var usersInChunk = emailAddressesChunk;
            while (usersLoadPending)
            {
                var loadResults = await LoadAppsForUsers(usersInChunk, loopCount);
                var usersLoadedFine = loadResults.Where(r => !r.Value.Retry).ToList();

                // Concat results of load
                foreach (var userLoadedFine in usersLoadedFine)
                {
                    // Update existing failed result or new?
                    if (chunkUserApps.ContainsKey(userLoadedFine.Key))
                        chunkUserApps[userLoadedFine.Key] = userLoadedFine.Value.Results;
                    else
                        chunkUserApps.Add(userLoadedFine.Key, userLoadedFine.Value.Results);
                }

                var throttledUsers = loadResults.Where(r => r.Value.Retry).Select(r => r.Key).ToList();

                Console.WriteLine($"DEBUG: User Apps Load - attempt #{loopCount}: read apps for {usersLoadedFine.Count.ToString("n0")} users " +
                    $"(out of {usersInChunk.Count.ToString("n0")}); still pending {throttledUsers.Count.ToString("n0")}");


                // What to retry?
                usersInChunk = throttledUsers;
                loopCount++;
                usersLoadPending = usersInChunk.Count > 0;

                // Are we being throttled?
                if (usersLoadPending)
                {
                    var timeout = GetDelay(loopCount);
                    Console.WriteLine($"DEBUG: User Apps Load - attempt #{loopCount}: waiting {timeout.TotalSeconds} seconds before repeating throttled requests");
                    await Task.Delay(timeout);
                }
            }

            // Save chunk
            await Save(chunkUserApps);

            _totalSaves += chunkUserApps.Count;

            // Update progress
            var percentDone = (_totalSaves / (float)totalEmailAddressesCount) * 100;
            if (percentDone < 100 && percentDone > 0)
            {
                int pcDone = Convert.ToInt32(Math.Round(percentDone, 0));
                if (_lastReportedPercentDone < pcDone)
                {
                    _telemetry.LogInformation($"User Apps Load: processed {pcDone.ToString("n0")}% of users for Teams Apps...");
                    _lastReportedPercentDone = pcDone;
                }
            }
        }
    }
}
