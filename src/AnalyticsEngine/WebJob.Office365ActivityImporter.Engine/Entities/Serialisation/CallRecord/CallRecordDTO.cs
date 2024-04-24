using Azure.Core;
using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-callrecord?view=graph-rest-beta
    public class CallRecordDTO : BaseCallRecordDTOWithModalities
    {
        #region Props

        [JsonProperty("organizer", NullValueHandling = NullValueHandling.Ignore)]
        public IdentitySetDTO Organizer { get; set; }
        public string OrganizerEmail { get; set; }

        [JsonProperty("sessions")]
        public List<CallSessionDTO> Sessions { get; set; }

        [JsonIgnore]
        public string JsonText { get; set; }
        #endregion

        /// <summary>
        /// Load a Call Record from Graph, using call ID
        /// </summary>
        public static async Task<CallRecordDTO> LoadFromGraphByID(string callId, ManualGraphCallClient manualClient, TeamsLoadContext teamsLoadContext, ILogger telemetry, string thisTenantId)
        {
            string callJsonText = string.Empty;
            var callDTO = await manualClient.GetAsyncWithThrottleRetries<CallRecordDTO>($"https://graph.microsoft.com/v1.0/communications/callRecords/{callId}?$expand=sessions($expand=segments)",
                jsonStringAction: s => callJsonText = s);

            callDTO.JsonText = callJsonText;

            // Find email addresses for user IDs
            await callDTO.PopulateEmailAddresses(teamsLoadContext, thisTenantId, telemetry);


            return callDTO;
        }

        /// <summary>
        /// Debug testing method
        /// </summary>
        public static async Task<CallRecord> SaveNewCallToDB(string callId, ManualGraphCallClient manualClient, TokenCredential graphServiceClientAuthenticationProvider, ILogger telemetry, string thisTenantId)
        {
            var teamsLoadContext = new TeamsLoadContext(new GraphServiceClient(graphServiceClientAuthenticationProvider));

            var newCall = await LoadFromGraphByID(callId, manualClient, teamsLoadContext, telemetry, thisTenantId);

            telemetry.LogInformation($"Response payload from Graph:\n{newCall.JsonText}");

            telemetry.LogInformation("\nSaving call to SQL...");
            using (var db = new AnalyticsEntitiesContext())
            {
                return await newCall.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), telemetry);
            }
        }

        static SemaphoreSlim saveCallRecordSemaphoreSlim = new SemaphoreSlim(1, 1);

        public async Task<CallRecord> SaveOrReplaceCallRecord(TeamsAndCallsDBLookupManager context, ILogger telemetry)
        {
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
                        telemetry.LogWarning($"Detected previous call in database with Graph ID '{this.GraphCallID}'. Replacing with this call data.");

                        await existingCallRecord.DeleteAll(context.Database);
                    }

                    var feedbackList = new Dictionary<Common.Entities.User, CallFeedback>();
                    // Agregate session data
                    foreach (var session in this.Sessions)
                    {
                        bool saveSession = session.Callee.HaveUserEmail || session.Caller.HaveUserEmail;

                        if (saveSession)
                        {
                            var otherPerson = await GetOtherUser(session, context, this.OrganizerEmail);

                            if (otherPerson.UserPrincipalName.ToLower() == this.OrganizerEmail?.ToLower())
                            {
                                // Session is for the organiser. Ignore
                            }
                            else
                            {
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
                                AddFeedbackIfUnique(session.Callee, otherPerson, call, feedbackList);
                                AddFeedbackIfUnique(session.Caller, otherPerson, call, feedbackList);

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
                            telemetry.LogInformation($"Found a session ID '{session.GraphCallID}' without a caller/callee email address. Skipping session.");
                        }
                    }

                    if (string.IsNullOrEmpty(this.CallType))
                    {
                        throw new ArgumentNullException(CallType);
                    }
                    call.CallType = await context.GetOrCreateCallType(this.CallType);
                    call.StartDateTime = this.StartDateTime;
                    call.EndDateTime = this.EndDateTime;
                    call.Organizer = await context.GetOrCreateUser(this.OrganizerEmail, false);
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

        private async Task<Common.Entities.User> GetOtherUser(CallSessionDTO session, TeamsAndCallsDBLookupManager context, string organiserEmail)
        {
            if (string.IsNullOrEmpty(organiserEmail))
            {
                throw new ArgumentNullException(nameof(organiserEmail));
            }

            if (!(session.Callee.HaveUserEmail && session.Caller.HaveUserEmail))
            {
                if (session.Callee.HaveUserEmail)
                {
                    return await context.GetOrCreateUser(session.Callee.UserEmailAddress, false);
                }
                else if (session.Caller.HaveUserEmail)
                {
                    return await context.GetOrCreateUser(session.Caller.UserEmailAddress, false);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(session));
                }
            }
            else
            {
                // Have human caller & callee. Find the one that's not the organiser
                if (session.Caller.UserEmailAddress.ToLower() == organiserEmail.ToLower())
                {
                    return await context.GetOrCreateUser(session.Callee.UserEmailAddress, false);
                }
                else if (session.Callee.UserEmailAddress.ToLower() == organiserEmail.ToLower())
                {
                    return await context.GetOrCreateUser(session.Caller.UserEmailAddress, false);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(session), "Two human contacts for session, neither of which is the organiser. Can't pick the other person.");
                }
            }
        }

        private void AddFeedbackIfUnique(ParticipantEndpointDTO userEndpointContext, Common.Entities.User userLookup, CallRecord relatedCall,
            Dictionary<Common.Entities.User, CallFeedback> feedbackList)
        {
            if (userEndpointContext.Feedback != null && !feedbackList.ContainsKey(userLookup))
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


        public async Task PopulateEmailAddresses(TeamsLoadContext teamsLoadContext, string thisTenantId, ILogger telemetry)
        {
            if (Sessions != null)
            {
                foreach (var callSession in this.Sessions)
                {
                    await PopuplateCallerEmail(callSession.Callee, teamsLoadContext, thisTenantId, telemetry);
                    await PopuplateCallerEmail(callSession.Caller, teamsLoadContext, thisTenantId, telemetry);
                }
            }

            if (this.Organizer != null)
            {
                this.OrganizerEmail = await GetEmailAddress(this.Organizer, teamsLoadContext, telemetry);
            }
        }

        private static async Task PopuplateCallerEmail(ParticipantEndpointDTO callee, TeamsLoadContext teamsLoadContext, string thisTenantId, ILogger telemetry)
        {
            if (callee?.Identity?.User != null)
            {
                if (callee.Identity.User.TenantId == thisTenantId)
                {
                    callee.UserEmailAddress = await GetEmailAddress(callee.Identity, teamsLoadContext, telemetry);
                }
                else
                {
                    telemetry.LogInformation($"Ignoring external user from tenant Id {callee.Identity.User.TenantId}");
                }
            }
        }

        private static async Task<string> GetEmailAddress(IdentitySetDTO callee, TeamsLoadContext teamsLoadContext, ILogger telemetry)
        {
            Microsoft.Graph.User graphUser = null;
            try
            {
                graphUser = await teamsLoadContext.UserCache.Load(callee?.User?.Id);
            }
            catch (ServiceException ex)
            {
                if (ex.Message.Contains("Request_ResourceNotFound"))
                {
                    telemetry.LogInformation($"Cannot find user with id '{callee?.User?.Id} in tenant '{callee?.User?.TenantId}' - user not found.");
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
