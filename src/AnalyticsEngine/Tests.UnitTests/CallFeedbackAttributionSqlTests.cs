using Common.Entities;
using Common.Entities.UserScope;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Call feedback is stored against the person who gave it. Runs against a throwaway database with every migration
    /// applied, so the user each feedback row points at is read back from SQL.
    /// </summary>
    /// <remarks>
    /// Graph puts a participant's rating and words on their own endpoint of a session (<c>participantEndpoint.feedback</c>).
    /// In a peer-to-peer call one session joins the caller and the callee. In a meeting, each participant's session joins
    /// them to the meeting service, the organiser's included.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class CallFeedbackAttributionSqlTests
    {
        private static ScratchDatabase _database;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("CallFeedback");
            new DbMigrator(new Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        private static string NewUpn(string who) => $"{who}.{Guid.NewGuid():N}@contoso.com";

        private static ParticipantEndpointDTO Person(string upn, string words = null) => new ParticipantEndpointDTO
        {
            UserEmailAddress = upn,
            Feedback = words == null ? null : new UserFeedbackDTO { Rating = "good", Text = words },
        };

        /// <summary>The meeting service's end of a participant's session, which has no user.</summary>
        private static ParticipantEndpointDTO MeetingService() => new ParticipantEndpointDTO();

        private static CallRecordDTO Call(string organiser, string callType, params CallSessionDTO[] sessions) => new CallRecordDTO
        {
            GraphCallID = Guid.NewGuid().ToString(),
            OrganizerEmail = organiser,
            CallType = callType,
            StartDateTime = DateTime.UtcNow.AddHours(-2),
            EndDateTime = DateTime.UtcNow.AddHours(-1),
            Sessions = sessions.ToList(),
        };

        private static CallSessionDTO Session(ParticipantEndpointDTO caller, ParticipantEndpointDTO callee) => new CallSessionDTO
        {
            Caller = caller,
            Callee = callee,
            StartDateTime = DateTime.UtcNow.AddMinutes(-110),
            EndDateTime = DateTime.UtcNow.AddMinutes(-70),
        };

        /// <summary>Saves the call, then reads back each stored feedback's words and the UPN of the user it is stored against.</summary>
        private static async Task<(Common.Entities.Entities.Teams.CallRecord Saved, Dictionary<string, string> AuthorByWords)> SaveAndReadFeedback(
            CallRecordDTO call, UserImportScope scope = null)
        {
            using (var db = new AnalyticsEntitiesContext(_database.ConnectionString, true, false))
            {
                var saved = await call.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), NullLogger.Instance, scope);
                var callId = saved.ID;
                var feedback = await db.CallFeedback.Where(f => f.Call.ID == callId)
                    .Select(f => new { f.Text, f.User.UserPrincipalName })
                    .ToListAsync();
                return (saved, feedback.ToDictionary(f => f.Text, f => f.UserPrincipalName));
            }
        }

        [DataTestMethod]
        [DataRow(true, true, DisplayName = "Organiser calls; both give feedback")]
        [DataRow(true, false, DisplayName = "Organiser calls; only the organiser gives feedback")]
        [DataRow(false, true, DisplayName = "Organiser is called; both give feedback")]
        [DataRow(false, false, DisplayName = "Organiser is called; only the organiser gives feedback")]
        public async Task PeerToPeerCall_EachPersonsFeedback_IsStoredAgainstThem(bool organiserIsCaller, bool attendeeGivesFeedback)
        {
            var organiser = NewUpn("organiser");
            var attendee = NewUpn("attendee");
            var organiserEnd = Person(organiser, "organiser's words");
            var attendeeEnd = Person(attendee, attendeeGivesFeedback ? "attendee's words" : null);
            var call = Call(organiser, "peerToPeer",
                organiserIsCaller ? Session(organiserEnd, attendeeEnd) : Session(attendeeEnd, organiserEnd));

            var (saved, authorByWords) = await SaveAndReadFeedback(call);

            Assert.AreEqual(attendee, saved.Sessions.Single().Attendee.UserPrincipalName, "The session is still stored against the attendee.");
            Assert.AreEqual(organiser, authorByWords["organiser's words"], "The organiser's feedback is theirs, not the attendee's.");
            if (attendeeGivesFeedback)
            {
                Assert.AreEqual(attendee, authorByWords["attendee's words"], "The attendee's feedback is theirs.");
            }
            Assert.AreEqual(attendeeGivesFeedback ? 2 : 1, authorByWords.Count, "One row for each person who gave feedback.");
        }

        [TestMethod]
        public async Task Meeting_TheOrganisersFeedback_IsStoredAgainstThem()
        {
            var organiser = NewUpn("organiser");
            var attendee = NewUpn("attendee");
            var call = Call(organiser, "groupCall",
                Session(Person(organiser, "organiser's words"), MeetingService()),
                Session(Person(attendee, "attendee's words"), MeetingService()));

            var (saved, authorByWords) = await SaveAndReadFeedback(call);

            Assert.AreEqual(attendee, saved.Sessions.Single().Attendee.UserPrincipalName, "The organiser's own session is still not stored as an attendance.");
            Assert.AreEqual(organiser, authorByWords["organiser's words"], "The organiser's feedback, from their own session with the meeting service, is stored.");
            Assert.AreEqual(attendee, authorByWords["attendee's words"]);
            Assert.AreEqual(2, authorByWords.Count);
        }

        /// <summary>
        /// #847 (R9-9): a session nobody can be attributed to - two people neither of whom organised the call (after a
        /// transfer, say), one without a callee, or a missing one - used to throw out of the save, so the whole call was
        /// fetched again ten times and then dead-lettered. It is now left out, and the rest of the call is saved.
        /// </summary>
        [TestMethod]
        public async Task Call_WithMalformedSessions_IsSavedWithoutThem()
        {
            var organiser = NewUpn("organiser");
            var attendee = NewUpn("attendee");
            var call = Call(organiser, "groupCall",
                Session(Person(organiser), Person(attendee, "attendee's words")),
                Session(Person(NewUpn("transferred"), "transferred person's words"), Person(NewUpn("transferee"))),
                Session(Person(organiser), null),
                null);

            var (saved, authorByWords) = await SaveAndReadFeedback(call);

            Assert.AreEqual(attendee, saved.Sessions.Single().Attendee.UserPrincipalName, "Only the session that can be attributed is stored.");
            Assert.AreEqual(3, call.LoadStats.SkippedSessions);
            Assert.AreEqual(2, authorByWords.Count, "Feedback is the person's own, so a skipped session's feedback is still kept.");
        }

        [TestMethod]
        public async Task PeerToPeerCall_InScopeOrganisersFeedback_IsStoredAgainstThem_NotTheAnonymousAttendee()
        {
            var organiser = NewUpn("pilot");
            var outsider = NewUpn("outsider");
            var call = Call(organiser, "peerToPeer", Session(Person(organiser, "pilot's words"), Person(outsider, "outsider's words")));

            var (saved, authorByWords) = await SaveAndReadFeedback(call, TestUserScopes.Of(organiser));

            Assert.AreEqual("Unknown User", saved.Sessions.Single().Attendee.UserPrincipalName, "The out-of-scope attendee is anonymous.");
            Assert.AreEqual(organiser, authorByWords["pilot's words"], "Not stored against the anonymous attendee.");
            Assert.AreEqual(1, authorByWords.Count, "Nothing the outsider wrote is stored.");
        }
    }
}
