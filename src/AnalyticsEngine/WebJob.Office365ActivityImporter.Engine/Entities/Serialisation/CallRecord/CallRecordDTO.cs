using Azure.Core;
using Common.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.UserScope;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Which Teams calls <c>UserGroupsFilter</c> lets through: a call is kept when anyone on it - the organiser or a
    /// session participant - is in the scope, and skipped when nobody is.
    /// </summary>
    public static class CallRecordScopeRules
    {
        /// <summary>The organiser and every session caller/callee that has an email address.</summary>
        public static IEnumerable<string> ParticipantEmails(CallRecordDTO call)
        {
            if (call == null)
            {
                yield break;
            }
            if (!string.IsNullOrEmpty(call.OrganizerEmail))
            {
                yield return call.OrganizerEmail;
            }
            foreach (var session in call.Sessions ?? new List<CallSessionDTO>())
            {
                if (session?.Caller?.HaveUserEmail == true) yield return session.Caller.UserEmailAddress;
                if (session?.Callee?.HaveUserEmail == true) yield return session.Callee.UserEmailAddress;
            }
        }

        public static bool AnyParticipantInScope(CallRecordDTO call, UserImportScope scope)
        {
            if (scope == null || !scope.IsFiltered)
            {
                return true;
            }
            return ParticipantEmails(call).Any(scope.IsInScope);
        }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-callrecord?view=graph-rest-beta
    public class CallRecordDTO : BaseCallRecordDTOWithModalities
    {
        #region Props

        [JsonProperty("organizer", NullValueHandling = NullValueHandling.Ignore)]
        public IdentitySetDTO Organizer { get; set; }
        public string OrganizerEmail { get; set; }

        [JsonProperty("sessions")]
        public List<CallSessionDTO> Sessions { get; set; }

        /// <summary>
        /// Graph returns at most 60 sessions inline, even with <c>$expand=sessions</c>, and links to the rest here.
        /// Null once <see cref="LoadRemainingSessionPages"/> has read them all.
        /// </summary>
        [JsonProperty("sessions@odata.nextLink", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionsNextLink { get; set; }

        [JsonIgnore]
        public string JsonText { get; set; }
        #endregion

        /// <summary>
        /// Load a Call Record from Graph, using call ID
        /// </summary>
        /// <exception cref="CallRecordSessionPagingException">
        /// Not every page of the call's sessions could be read. Nothing is returned: a partial call must not be saved,
        /// nor its queue message completed.
        /// </exception>
        public static async Task<CallRecordDTO> LoadFromGraphByID(string callId, ManualGraphCallClient manualClient, TeamsLoadContext teamsLoadContext, ILogger logger, string thisTenantId)
        {
            string callJsonText = string.Empty;
            var callRecordUrl = $"https://graph.microsoft.com/v1.0/communications/callRecords/{callId}?$expand=sessions($expand=segments)";
            var callDTO = await manualClient.GetAsyncWithThrottleRetries<CallRecordDTO>(callRecordUrl,
                jsonStringAction: s => callJsonText = s);

            if (callDTO == null)
            {
                logger.LogWarning($"Got null/unparseable response loading call record '{callId}'. Skipping.");
                return null;
            }

            callDTO.JsonText = callJsonText;

            // Every session first: the email look-ups, the UserGroupsFilter check and the save only see what is in
            // Sessions, so a call whose in-scope people were all on a later page would be skipped as out of scope.
            await callDTO.LoadRemainingSessionPages(manualClient, callRecordUrl, logger);

            // Find email addresses for user IDs
            await callDTO.PopulateEmailAddresses(teamsLoadContext, thisTenantId, logger);


            return callDTO;
        }

        /// <summary>
        /// Follows <see cref="SessionsNextLink"/> until Graph has returned every session, appending each page to
        /// <see cref="Sessions"/>. Each page is requested with the client's throttling retries.
        /// </summary>
        /// <param name="callRecordUrl">The absolute URL the call record was read from; continuation links must stay on its host.</param>
        /// <exception cref="CallRecordSessionPagingException">
        /// A page failed or was unreadable (retryable), or the paging ran away - more than
        /// <see cref="CallRecordSessionPaging.MaxPages"/> pages, or a link repeated (not retryable).
        /// </exception>
        public async Task LoadRemainingSessionPages(ManualGraphCallClient manualClient, string callRecordUrl, ILogger logger)
        {
            if (string.IsNullOrEmpty(SessionsNextLink))
            {
                return;
            }

            if (Sessions == null)
            {
                Sessions = new List<CallSessionDTO>();
            }

            var requestedUrls = new HashSet<string>(StringComparer.Ordinal);
            var pageNumber = 1;     // The call record itself carried the first page
            var nextLink = SessionsNextLink;
            while (!string.IsNullOrEmpty(nextLink))
            {
                pageNumber++;
                if (pageNumber > CallRecordSessionPaging.MaxPages)
                {
                    throw new CallRecordSessionPagingException(GraphCallID,
                        $"Call record '{GraphCallID}' has more than {CallRecordSessionPaging.MaxPages} pages of sessions " +
                        $"({Sessions.Count} read so far). It is not saved, rather than saved without the rest.",
                        isPermanent: true);
                }

                var pageUrl = CallRecordSessionPaging.ResolveContinuationUrl(nextLink, callRecordUrl, GraphCallID);
                if (!requestedUrls.Add(pageUrl))
                {
                    throw new CallRecordSessionPagingException(GraphCallID,
                        $"Call record '{GraphCallID}': Graph returned the same sessions continuation link twice (page {pageNumber}). " +
                        "It is not saved, rather than saved without the rest.",
                        isPermanent: true);
                }

                CallSessionListDTO page;
                try
                {
                    page = await manualClient.GetAsyncWithThrottleRetries<CallSessionListDTO>(pageUrl);
                }
                catch (Exception ex)
                {
                    throw new CallRecordSessionPagingException(GraphCallID,
                        $"Call record '{GraphCallID}': sessions page {pageNumber} could not be read ({GraphHttpException.DescribeForStorage(ex)}).",
                        isPermanent: false, innerException: ex);
                }

                if (page?.Sessions == null)
                {
                    throw new CallRecordSessionPagingException(GraphCallID,
                        $"Call record '{GraphCallID}': sessions page {pageNumber} was empty or unreadable.",
                        isPermanent: false);
                }

                Sessions.AddRange(page.Sessions);
                nextLink = page.ODataNextLink;
            }

            SessionsNextLink = null;
            logger.LogInformation($"Call record '{GraphCallID}': read {Sessions.Count} sessions over {pageNumber} pages.");
        }

        /// <summary>
        /// Debug testing method
        /// </summary>
        /// <param name="userScope">The <c>UserGroupsFilter</c> scope to apply; null means unfiltered.</param>
        public static async Task<CallRecord> SaveNewCallToDB(string callId, ManualGraphCallClient manualClient, TokenCredential graphServiceClientAuthenticationProvider, ILogger logger, string thisTenantId,
            UserImportScope userScope = null)
        {
            var teamsLoadContext = new TeamsLoadContext(GraphServiceClientFactory.CreateWithTimeout(graphServiceClientAuthenticationProvider, GraphServiceClientFactory.DefaultGraphSdkTimeout));

            var newCall = await LoadFromGraphByID(callId, manualClient, teamsLoadContext, logger, thisTenantId);

            logger.LogInformation($"Response payload from Graph:\n{newCall.JsonText}");

            var scope = userScope ?? UserImportScope.Unfiltered;
            if (!CallRecordScopeRules.AnyParticipantInScope(newCall, scope))
            {
                logger.LogInformation("Nobody on this call is in UserGroupsFilter, so it is not saved.");
                return null;
            }

            logger.LogInformation("\nSaving call to SQL...");
            using (var db = new AnalyticsEntitiesContext())
            {
                return await newCall.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), logger, scope);
            }
        }

        static SemaphoreSlim saveCallRecordSemaphoreSlim = new SemaphoreSlim(1, 1);

        /// <param name="userScope">
        /// The <c>UserGroupsFilter</c> scope. People on the call who are outside it are stored as the anonymous
        /// "Unknown User" and none of their feedback is kept, so the call still counts for the in-scope people on it
        /// without recording who else was there. Null means unfiltered.
        /// </param>
        public async Task<CallRecord> SaveOrReplaceCallRecord(TeamsAndCallsDBLookupManager context, ILogger logger, UserImportScope userScope = null)
        {
            var scope = userScope ?? UserImportScope.Unfiltered;

            // Make sure we only save call records one at a time
            await saveCallRecordSemaphoreSlim.WaitAsync();

            try
            {
                var existingCallRecord = await CallRecord.LoadByGraphID(this.GraphCallID, context.Database);

                var call = new CallRecord();
                using (var trans = context.Database.Database.BeginTransaction())
                {
                    if (existingCallRecord != null)
                    {
                        logger.LogWarning($"Detected previous call in database with Graph ID '{this.GraphCallID}'. Replacing with this call data.");

                        await existingCallRecord.DeleteAll(context.Database);
                    }

                    var feedbackList = new Dictionary<Common.Entities.User, CallFeedback>();
                    // Agregate session data
                    foreach (var session in this.Sessions)
                    {
                        bool saveSession = session.Callee.HaveUserEmail || session.Caller.HaveUserEmail;

                        if (saveSession)
                        {
                            // Decided on the real identities, before anyone is anonymised: two different people
                            // outside the scope are both "Unknown User", and neither is the organiser.
                            var otherPersonEmail = GetOtherUserEmail(session, this.OrganizerEmail);

                            if (string.Equals(otherPersonEmail, this.OrganizerEmail, StringComparison.OrdinalIgnoreCase))
                            {
                                // Session is for the organiser. Ignore
                            }
                            else
                            {
                                var otherPerson = await GetUserForStorage(context, otherPersonEmail, scope);

                                // Session is unique. Add to DB
                                var dbSession = new CallSession()
                                {
                                    Attendee = otherPerson,
                                    Start = session.StartDateTime,
                                    End = session.EndDateTime,
                                    ParentRecord = call
                                };
                                call.Sessions.Add(dbSession);

                                // Aggregate modes of using call in all sessions, where unique
                                if (session.Modalities != null)
                                {
                                    foreach (var modalityString in session.Modalities)
                                    {
                                        var m = await context.GetOrCreateCallModality(modalityString);
                                        dbSession.AddCallModality(m);
                                    }
                                }

                                // Add feedback from either
                                AddFeedbackIfUnique(session.Callee, otherPerson, call, feedbackList, scope);
                                AddFeedbackIfUnique(session.Caller, otherPerson, call, feedbackList, scope);

                                // Save failure info
                                if (session.FailureInfo != null)
                                {
                                    var newFailure = new CallFailureReasonLookup() { Call = call, Reason = session.FailureInfo.Reason, Stage = session.FailureInfo.Stage };
                                    context.Database.CallFailures.Add(newFailure);
                                }
                            }
                        }
                        else
                        {
                            logger.LogInformation($"Found a session ID '{session.GraphCallID}' without a caller/callee email address. Skipping session.");
                        }
                    }

                    if (string.IsNullOrEmpty(this.CallType))
                    {
                        throw new ArgumentNullException(CallType);
                    }
                    call.CallType = await context.GetOrCreateCallType(this.CallType);
                    call.StartDateTime = this.StartDateTime;
                    call.EndDateTime = this.EndDateTime;
                    call.Organizer = await GetUserForStorage(context, this.OrganizerEmail, scope);
                    call.GraphID = this.GraphCallID;

                    // Save
                    context.Database.CallRecords.Add(call);
                    context.Database.CallFeedback.AddRange(feedbackList.Values);
                    await context.Database.SaveChangesAsync();

                    // Commit transaction
                    trans.Commit();
                }
                return call;
            }
            finally
            {
                saveCallRecordSemaphoreSlim.Release();
            }
        }

        /// <summary>
        /// The database user to record for <paramref name="email"/>: the person themselves when they are in the
        /// <c>UserGroupsFilter</c> scope, otherwise the anonymous "Unknown User".
        /// </summary>
        private static Task<Common.Entities.User> GetUserForStorage(TeamsAndCallsDBLookupManager context, string email, UserImportScope scope)
        {
            return scope.IsInScope(email)
                ? context.GetOrCreateUser(email, false)
                : context.GetOrCreateUnknownUser(false);
        }

        /// <summary>
        /// The email address of the session participant who is not the organiser.
        /// </summary>
        internal static string GetOtherUserEmail(CallSessionDTO session, string organiserEmail)
        {
            if (string.IsNullOrEmpty(organiserEmail))
            {
                throw new ArgumentNullException(nameof(organiserEmail));
            }

            if (!(session.Callee.HaveUserEmail && session.Caller.HaveUserEmail))
            {
                if (session.Callee.HaveUserEmail)
                {
                    return session.Callee.UserEmailAddress;
                }
                else if (session.Caller.HaveUserEmail)
                {
                    return session.Caller.UserEmailAddress;
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(session));
                }
            }
            else
            {
                // Have human caller & callee. Find the one that's not the organiser
                if (string.Equals(session.Caller.UserEmailAddress, organiserEmail, StringComparison.OrdinalIgnoreCase))
                {
                    return session.Callee.UserEmailAddress;
                }
                else if (string.Equals(session.Callee.UserEmailAddress, organiserEmail, StringComparison.OrdinalIgnoreCase))
                {
                    return session.Caller.UserEmailAddress;
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(session), "Two human contacts for session, neither of which is the organiser. Can't pick the other person.");
                }
            }
        }

        /// <remarks>
        /// Feedback is written by the endpoint's own user, so it is kept only when that user is in the
        /// <c>UserGroupsFilter</c> scope: the rating and free text of someone outside it are never stored.
        /// </remarks>
        private void AddFeedbackIfUnique(ParticipantEndpointDTO userEndpointContext, Common.Entities.User userLookup, CallRecord relatedCall,
            Dictionary<Common.Entities.User, CallFeedback> feedbackList, UserImportScope scope)
        {
            if (userEndpointContext.Feedback != null && !feedbackList.ContainsKey(userLookup) && scope.IsInScope(userEndpointContext.UserEmailAddress))
            {
                var dbFeedback = new CallFeedback()
                {
                    Call = relatedCall,
                    Rating = userEndpointContext.Feedback.Rating,
                    Text = userEndpointContext.Feedback.Text,
                    User = userLookup
                };
                feedbackList.Add(userLookup, dbFeedback);
            }
        }


        public async Task PopulateEmailAddresses(TeamsLoadContext teamsLoadContext, string thisTenantId, ILogger logger)
        {
            // One look-up per person on the call rather than per endpoint: with every page of sessions read, the same
            // people (the organiser above all) can appear in hundreds of sessions.
            var emailsByUserId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (Sessions != null)
            {
                foreach (var callSession in this.Sessions)
                {
                    await PopuplateCallerEmail(callSession.Callee, teamsLoadContext, thisTenantId, logger, emailsByUserId);
                    await PopuplateCallerEmail(callSession.Caller, teamsLoadContext, thisTenantId, logger, emailsByUserId);
                }
            }

            if (this.Organizer != null)
            {
                this.OrganizerEmail = await GetEmailAddress(this.Organizer, teamsLoadContext, logger, emailsByUserId);
            }
        }

        private static async Task PopuplateCallerEmail(ParticipantEndpointDTO callee, TeamsLoadContext teamsLoadContext, string thisTenantId, ILogger logger,
            Dictionary<string, string> emailsByUserId)
        {
            if (callee?.Identity?.User != null)
            {
                if (callee.Identity.User.TenantId == thisTenantId)
                {
                    callee.UserEmailAddress = await GetEmailAddress(callee.Identity, teamsLoadContext, logger, emailsByUserId);
                }
                else
                {
                    logger.LogInformation($"Ignoring external user from tenant Id {callee.Identity.User.TenantId}");
                }
            }
        }

        private static async Task<string> GetEmailAddress(IdentitySetDTO callee, TeamsLoadContext teamsLoadContext, ILogger logger,
            Dictionary<string, string> emailsByUserId)
        {
            var userId = callee?.User?.Id;
            if (!string.IsNullOrEmpty(userId) && emailsByUserId.TryGetValue(userId, out var known))
            {
                return known;
            }

            var email = await GetEmailAddress(callee, teamsLoadContext, logger);
            if (!string.IsNullOrEmpty(userId))
            {
                emailsByUserId[userId] = email;
            }
            return email;
        }

        private static async Task<string> GetEmailAddress(IdentitySetDTO callee, TeamsLoadContext teamsLoadContext, ILogger logger)
        {
            Microsoft.Graph.Models.User graphUser = null;
            try
            {
                graphUser = await teamsLoadContext.UserCache.Load(callee?.User?.Id);
            }
            catch (ODataError ex)
            {
                if (ex.Message.Contains("Request_ResourceNotFound"))
                {
                    logger.LogInformation($"Cannot find user with id '{callee?.User?.Id} in tenant '{callee?.User?.TenantId}' - user not found.");
                    return string.Empty;
                }
                else
                {
                    throw;
                }
            }
            if (graphUser != null)
            {
                return graphUser.UserPrincipalName;
            }
            else
            {
                return string.Empty;
            }
        }
    }

}
