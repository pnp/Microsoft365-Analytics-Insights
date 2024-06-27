using Common.Entities;
using DataUtils;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.Properties;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    [TestClass]
    public class CallRecordTests
    {
        // Verify call & feedback counts
        [TestMethod]
        public async Task CallRecordSaveTests()
        {
            var testDate = DateTime.Now;
            var call = new CallRecordDTO
            {
                GraphCallID = GetRandomID(),
                StartDateTime = testDate.AddHours(-3),
                EndDateTime = testDate.AddHours(1),
                CallType = "Unit test call",
                //Modalities = new string[] { "call", "vid", "whatevs" },   // Modalities on call are ignored
                OrganizerEmail = "unittests@contoso.local",
                Sessions = new List<CallSessionDTO>
                {
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO{ UserEmailAddress = "unittests@contoso.local" },
                        Callee = new ParticipantEndpointDTO {
                            UserEmailAddress = "whoever@contoso.local",
                            Feedback = new UserFeedbackDTO{ Rating = "bad", Text="bad text"},
                        },
                        StartDateTime = testDate.AddMinutes(10),
                        EndDateTime = testDate.AddMinutes(20),
                        Modalities = new string[] { "call", "vid", "whatevs" },
                        FailureInfo = new FailureInfoDTO { Reason = "Dropped connection", Stage = "Start?" }
                    },
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO {
                            UserEmailAddress = "whatevs@contoso.local",
                            Feedback = new UserFeedbackDTO{ Rating = "good", Text="good text"}
                        },
                        Callee = new ParticipantEndpointDTO { UserEmailAddress = "unittests@contoso.local" },
                        StartDateTime = testDate.AddMinutes(15),
                        EndDateTime = testDate.AddMinutes(25),
                        Modalities = new string[] { "call", "vid", "screenshare" }
                    }
                }
            };
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {

                int preSaveFeedbackCount = await db.CallFeedback.CountAsync();
                int preSaveFailureCount = await db.CallFailures.CountAsync();

                var savedCall = await call.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), AnalyticsLogger.ConsoleOnlyTracer());
                Assert.IsTrue(savedCall.Sessions.Count == 2);
                Assert.AreEqual(call.StartDateTime, savedCall.StartDateTime);
                Assert.AreEqual(call.EndDateTime, savedCall.EndDateTime);
                Assert.IsTrue(savedCall.Organizer.IsSavedToDB);
                Assert.AreEqual(savedCall.Organizer.UserPrincipalName, call.OrganizerEmail);

                // Check feedback
                int postSaveFeedbackCount = await db.CallFeedback.CountAsync();
                Assert.IsTrue(postSaveFeedbackCount - preSaveFeedbackCount == 2);
                var lastInsertedFeedback = await db.CallFeedback.OrderByDescending(s => s.ID).FirstOrDefaultAsync();

                Assert.IsTrue(lastInsertedFeedback.Call.ID == savedCall.ID);

                // Check failures
                int postSaveFailuresCount = await db.CallFailures.CountAsync();
                Assert.IsTrue(postSaveFailuresCount - preSaveFailureCount == 1);
                var lastInsertedFailure = await db.CallFeedback.OrderByDescending(s => s.ID).FirstOrDefaultAsync();

                Assert.IsTrue(lastInsertedFailure.Call.ID == savedCall.ID);
            }
        }


        //[TestMethod]      // Removing test as test data is from an expired tenant
        public async Task CallRecordRealSaveTests()
        {
            var call3way = JsonConvert.DeserializeObject<CallRecordDTO>(Resources.TeamsCall_3way);
            var failedGroupCall = JsonConvert.DeserializeObject<CallRecordDTO>(Resources.TeamsCall_Failed_Call);
            var p2pCall = JsonConvert.DeserializeObject<CallRecordDTO>(Resources.TeamsCall_Peer2PeerCall);

            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            var config = new Common.Entities.Config.AppConfig();
            var auth = new WebJob.Office365ActivityImporter.Engine.GraphAppIndentityOAuthContext
                (telemetry, config.ClientID,
                config.TenantGUID.ToString(),
                config.ClientSecret,
                config.KeyVaultUrl,
                config.UseClientCertificate
                );

            await auth.InitClientCredential();
            var teamsLoadContext = new TeamsLoadContext(new GraphServiceClient(auth.Creds));

            // Populate email addresses using demo v4 tenant context. Won't work with any other environment.
            await call3way.PopulateEmailAddresses(teamsLoadContext, config.TenantGUID.ToString(), telemetry);
            await failedGroupCall.PopulateEmailAddresses(teamsLoadContext, config.TenantGUID.ToString(), telemetry);
            await p2pCall.PopulateEmailAddresses(teamsLoadContext, config.TenantGUID.ToString(), telemetry);

            using (var db = new AnalyticsEntitiesContext())
            {
                // Delete old calls
                var existingCalls = await db.CallRecords
                    .Where(c => c.GraphID == call3way.GraphCallID || c.GraphID == failedGroupCall.GraphCallID || c.GraphID == p2pCall.GraphCallID)
                    .ToListAsync();
                var existingCallSessions = await db.CallSessions.ToListAsync();
                var existingCallSessionModalities = await db.CallModalityLookups.ToListAsync();
                var existingCallfeedback = await db.CallFeedback.ToListAsync();

                db.CallSessions.RemoveRange(existingCallSessions);
                db.CallRecords.RemoveRange(existingCalls);
                db.CallModalityLookups.RemoveRange(existingCallSessionModalities);
                db.CallFeedback.RemoveRange(existingCallfeedback);

                db.SaveChanges();

                var context = new TeamsAndCallsDBLookupManager(db);
                var call3wayDB = await call3way.SaveOrReplaceCallRecord(context, telemetry);
                var failedGroupCallDB = await failedGroupCall.SaveOrReplaceCallRecord(context, telemetry);
                var p2pCallDB = await p2pCall.SaveOrReplaceCallRecord(context, telemetry);

                // DTOs always include a session for initiator too (Teams endpoint). 
                // For a group call, that means x2 sessions for initiator user (user -> endpoint; endpoint -> user) - something we ignore
                Assert.IsTrue(call3wayDB.Sessions.Count == 3);

                // Usually failed calls include a session from caller to failer callee. We ignore that too.
                Assert.IsTrue(failedGroupCallDB.Sessions.Count == 0);

                // p2p calls are as in Graph - 1 session
                Assert.IsTrue(p2pCallDB.Sessions.Count == 1);
            }
        }


        [TestMethod]
        public async Task CallRecordMultithreadedTests()
        {
            const int CALL_LOOP_COUNT = 10;
            int preCallRecordCount = 0;
            var testDate = DateTime.Now;
            using (var db = new AnalyticsEntitiesContext())
            {
                preCallRecordCount = await db.CallRecords.CountAsync();
            }

            var callerEmail = "unittests@contoso.local" + testDate.Ticks;
            var tasks = new List<Task>();
            for (int i = 0; i < CALL_LOOP_COUNT; i++)
            {
                int callIteration = i;
                var t = Task.Run(async () =>
                {
                    Console.WriteLine($"Saving call {callIteration}...");
                    var call = new CallRecordDTO
                    {
                        GraphCallID = GetRandomID(),
                        StartDateTime = testDate.AddHours(-3),
                        EndDateTime = testDate.AddHours(i),
                        CallType = "Unit test call" + testDate.Ticks,
                        OrganizerEmail = callerEmail,
                        Sessions = new List<CallSessionDTO>
                {
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO{ UserEmailAddress = callerEmail},
                        Callee = new ParticipantEndpointDTO {
                            UserEmailAddress = "whoever@contoso.local",
                            Feedback = new UserFeedbackDTO{ Rating = "bad", Text="bad text"},
                        },
                        StartDateTime = testDate.AddMinutes(10),
                        EndDateTime = testDate.AddMinutes(20),
                        Modalities = new string[] { "call" + testDate.Ticks, "vid" + testDate.Ticks, "whatevs" + testDate.Ticks },
                        FailureInfo = new FailureInfoDTO { Reason = "Dropped connection", Stage = "Start?" }
                    },
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO {
                            UserEmailAddress = callerEmail,
                            Feedback = new UserFeedbackDTO{ Rating = "good", Text="good text"}
                        },
                        Callee = new ParticipantEndpointDTO { UserEmailAddress = "unittests@contoso.local" },
                        StartDateTime = testDate.AddMinutes(15),
                        EndDateTime = testDate.AddMinutes(25),
                        Modalities = new string[] { "call" + testDate.Ticks, "vid" + testDate.Ticks, "screenshare" + testDate.Ticks }
                    }
                }
                    };
                    using (var db = new AnalyticsEntitiesContext())
                    {

                        int preSaveFeedbackCount = await db.CallFeedback.CountAsync();
                        int preSaveFailureCount = await db.CallFailures.CountAsync();

                        var savedCall = await call.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), AnalyticsLogger.ConsoleOnlyTracer());
                    }

                    Console.WriteLine($"Done saving call {callIteration}...");
                });
                tasks.Add(t);

            }
            await Task.WhenAll(tasks);


            Console.WriteLine("Testing results...");
            using (var db = new AnalyticsEntitiesContext())
            {
                var postCallRecordCount = await db.CallRecords.CountAsync();
                Assert.IsTrue(postCallRecordCount == preCallRecordCount + CALL_LOOP_COUNT);
            }
        }
        static string GetRandomID()
        {
            return Guid.NewGuid().ToString();
        }
    }
}
