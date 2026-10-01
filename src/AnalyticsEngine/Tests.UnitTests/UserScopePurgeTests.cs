using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserScope;
using Common.Entities.UserScope.Purge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// The User scope purge against LocalDB.
    /// </summary>
    /// <remarks>
    /// A purge removes everyone outside the scope, which in this shared test database would be every other test's
    /// data. So these tests never let a purge work out its own candidates: each one seeds its own synthetic people and
    /// gives the engine exactly those (<c>CandidatesOverride</c>). The snapshot itself is tested read-only. Purge records
    /// are kept in memory, a new store for each test.
    /// </remarks>
    [TestClass]
    public class UserScopePurgeTests
    {
        private static string ConnectionString => new AppConfig().ConnectionStrings.DatabaseConnectionString;

        private static UserScopePurgeDatabase Database => new UserScopePurgeDatabase(ConnectionString);

        private UserScopePurgeStateStore State;
        private int[] _candidates;

        [TestInitialize]
        public async Task EnsureSchemaAndNoPurgeRunning()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
            }

            State = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false);
            Assert.IsFalse(await Database.IsPurgeRunningAsync(), "No session should still hold the purge lock from an earlier test.");
        }

        #region Seed

        private sealed class Seed
        {
            public string Token;
            public int Pilot, Outsider, Report, Unknown;
            public Guid PilotEvent, OutsiderEvent, PilotShareEvent, OutsiderShareEvent;
            public int PilotSession, OutsiderSession;
            public int OutsiderComment, PilotReply;
            public int PilotMail, OutsiderMail;
            public int SharedCall, OutsiderOnlyCall, ExternalCall, OutsiderOrganisedCall;
            public int Url, SearchTerm, Address, CallType;
            public int OrgType;
        }

        /// <summary>
        /// A pilot (in scope), an outsider (being purged), and someone the outsider manages, with data of each kind that
        /// a purge deletes, anonymises or must leave alone.
        /// </summary>
        private static async Task<Seed> SeedAsync()
        {
            var token = "usp" + Guid.NewGuid().ToString("N").Substring(0, 12);
            const string sql = @"
SET NOCOUNT ON;
DECLARE @now datetime = SYSUTCDATETIME();
DECLARE @pilot int, @outsider int, @report int, @unknown int;
INSERT INTO dbo.users (user_name, mail, azure_ad_id, account_enabled) VALUES (@token + '-pilot@contoso.local', @token + '-pilot@contoso.local', CONVERT(nvarchar(36), NEWID()), 1);
SET @pilot = SCOPE_IDENTITY();
INSERT INTO dbo.users (user_name, mail, azure_ad_id, account_enabled) VALUES (@token + '-outsider@contoso.local', @token + '-outsider@contoso.local', CONVERT(nvarchar(36), NEWID()), 1);
SET @outsider = SCOPE_IDENTITY();
INSERT INTO dbo.users (user_name, mail, azure_ad_id, account_enabled, manager_id) VALUES (@token + '-report@contoso.local', @token + '-report@contoso.local', CONVERT(nvarchar(36), NEWID()), 1, @outsider);
SET @report = SCOPE_IDENTITY();
SET @unknown = (SELECT TOP (1) id FROM dbo.users WHERE user_name = 'Unknown User' ORDER BY id);
IF @unknown IS NULL BEGIN INSERT INTO dbo.users (user_name) VALUES ('Unknown User'); SET @unknown = SCOPE_IDENTITY(); END

-- Audit events, with metadata and Copilot chats hanging off them; each person also shared something with the other.
DECLARE @pilotEvent uniqueidentifier = NEWID(), @outsiderEvent uniqueidentifier = NEWID(),
        @pilotShareEvent uniqueidentifier = NEWID(), @outsiderShareEvent uniqueidentifier = NEWID();
INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES
    (@pilotEvent, @now, @pilot), (@outsiderEvent, @now, @outsider), (@pilotShareEvent, @now, @pilot), (@outsiderShareEvent, @now, @outsider);
INSERT INTO dbo.event_meta_general (event_id) VALUES (@pilotEvent), (@outsiderEvent);
INSERT INTO dbo.copilot_chats (event_id, user_id) VALUES (@pilotEvent, @pilot), (@outsiderEvent, @outsider);
INSERT INTO dbo.event_meta_power_app_share (event_id, shared_with_user_id) VALUES (@pilotShareEvent, @outsider), (@outsiderShareEvent, @pilot);

-- Web traffic.
DECLARE @url int, @term int;
INSERT INTO dbo.urls (full_url) VALUES (N'https://contoso.sharepoint.com/sites/example/SitePages/Καλημέρα κόσμε ' + @token + N'.aspx');
SET @url = SCOPE_IDENTITY();
INSERT INTO dbo.search_terms (search_term) VALUES (N'Καλημέρα ' + @token);
SET @term = SCOPE_IDENTITY();
DECLARE @pilotSession int, @outsiderSession int;
INSERT INTO dbo.sessions (user_id) VALUES (@pilot); SET @pilotSession = SCOPE_IDENTITY();
INSERT INTO dbo.sessions (user_id) VALUES (@outsider); SET @outsiderSession = SCOPE_IDENTITY();
INSERT INTO dbo.hits (url_id, hit_timestamp, page_request_id, session_id) VALUES (@url, @now, NEWID(), @pilotSession), (@url, @now, NEWID(), @outsiderSession);
INSERT INTO dbo.searches (session_id, search_term_id) VALUES (@pilotSession, @term), (@outsiderSession, @term);

-- The pilot replied to the outsider's page comment.
DECLARE @outsiderComment int, @pilotReply int;
INSERT INTO dbo.page_comments (user_id, url_id, created, sp_id, comment) VALUES (@outsider, @url, @now, 1, N'Καλημέρα κόσμε');
SET @outsiderComment = SCOPE_IDENTITY();
INSERT INTO dbo.page_comments (user_id, url_id, created, sp_id, comment, parent_id) VALUES (@pilot, @url, @now, 2, N'reply', @outsiderComment);
SET @pilotReply = SCOPE_IDENTITY();
INSERT INTO dbo.page_likes (user_id, url_id, created, sp_id) VALUES (@pilot, @url, @now, 3), (@outsider, @url, @now, 4);

-- Sent email.
DECLARE @address int, @pilotMail int, @outsiderMail int;
INSERT INTO dbo.email_addresses (address) VALUES (@token + '-recipient@contoso.local'); SET @address = SCOPE_IDENTITY();
INSERT INTO dbo.sent_emails (sent_date, graph_message_id, from_address_id, user_id) VALUES (@now, @token + '-pilot', @address, @pilot);
SET @pilotMail = SCOPE_IDENTITY();
INSERT INTO dbo.sent_emails (sent_date, graph_message_id, from_address_id, user_id) VALUES (@now, @token + '-outsider', @address, @outsider);
SET @outsiderMail = SCOPE_IDENTITY();
INSERT INTO dbo.sent_email_recipients (sent_email_id, recipient_address_id) VALUES (@pilotMail, @address), (@outsiderMail, @address);

-- A usage report row each.
INSERT INTO dbo.teams_user_activity_log (private_chat_count, team_chat_count, calls_count, meetings_count, user_id, date)
VALUES (0, 0, 0, 0, @pilot, '2026-09-01'), (0, 0, 0, 0, @outsider, '2026-09-01');

-- Calls: the pilot called the outsider (both left feedback); the outsider called the pilot; the outsider called nobody
-- identifiable; and a call between two unknown participants that has nothing to do with the purge.
DECLARE @callType int, @sharedCall int, @outsiderOnlyCall int, @externalCall int, @outsiderOrganisedCall int;
INSERT INTO dbo.call_types (name) VALUES (@token); SET @callType = SCOPE_IDENTITY();
INSERT INTO dbo.call_records (organizer_id, call_type_id, start, [end], graph_id) VALUES (@pilot, @callType, @now, @now, @token + '-shared');
SET @sharedCall = SCOPE_IDENTITY();
INSERT INTO dbo.call_sessions (attendee_user_id, start, [end], call_record_id) VALUES (@outsider, @now, @now, @sharedCall);
INSERT INTO dbo.call_feedback (user_id, call_id, rating, text) VALUES (@outsider, @sharedCall, N'bad', N'outsider words'), (@pilot, @sharedCall, N'good', N'pilot words');
INSERT INTO dbo.call_records (organizer_id, call_type_id, start, [end], graph_id) VALUES (@outsider, @callType, @now, @now, @token + '-outsider');
SET @outsiderOnlyCall = SCOPE_IDENTITY();
INSERT INTO dbo.call_sessions (attendee_user_id, start, [end], call_record_id) VALUES (@unknown, @now, @now, @outsiderOnlyCall);
INSERT INTO dbo.call_records (organizer_id, call_type_id, start, [end], graph_id) VALUES (@unknown, @callType, @now, @now, @token + '-external');
SET @externalCall = SCOPE_IDENTITY();
INSERT INTO dbo.call_sessions (attendee_user_id, start, [end], call_record_id) VALUES (@unknown, @now, @now, @externalCall);
INSERT INTO dbo.call_records (organizer_id, call_type_id, start, [end], graph_id) VALUES (@outsider, @callType, @now, @now, @token + '-organised');
SET @outsiderOrganisedCall = SCOPE_IDENTITY();
INSERT INTO dbo.call_sessions (attendee_user_id, start, [end], call_record_id) VALUES (@pilot, @now, @now, @outsiderOrganisedCall);

-- User organisations: both are in the same one.
DECLARE @orgType int, @orgValue int;
INSERT INTO dbo.user_org_types (name, source_kind) VALUES (@token, 2); SET @orgType = SCOPE_IDENTITY();
INSERT INTO dbo.user_org_values (org_type_id, name) VALUES (@orgType, N'Ομάδα Πωλήσεων'); SET @orgValue = SCOPE_IDENTITY();
INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id) VALUES (@pilot, @orgType, @orgValue), (@outsider, @orgType, @orgValue);

SELECT @pilot, @outsider, @report, @unknown, @pilotEvent, @outsiderEvent, @pilotShareEvent, @outsiderShareEvent,
       @pilotSession, @outsiderSession, @outsiderComment, @pilotReply, @pilotMail, @outsiderMail,
       @sharedCall, @outsiderOnlyCall, @externalCall, @url, @term, @address, @callType, @outsiderOrganisedCall, @orgType;";

            using (var connection = new SqlConnection(ConnectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                await connection.OpenAsync();
                command.Parameters.Add("@token", SqlDbType.NVarChar, 40).Value = token;
                using (var reader = await command.ExecuteReaderAsync())
                {
                    Assert.IsTrue(await reader.ReadAsync());
                    return new Seed
                    {
                        Token = token,
                        Pilot = reader.GetInt32(0), Outsider = reader.GetInt32(1), Report = reader.GetInt32(2), Unknown = reader.GetInt32(3),
                        PilotEvent = reader.GetGuid(4), OutsiderEvent = reader.GetGuid(5), PilotShareEvent = reader.GetGuid(6), OutsiderShareEvent = reader.GetGuid(7),
                        PilotSession = reader.GetInt32(8), OutsiderSession = reader.GetInt32(9),
                        OutsiderComment = reader.GetInt32(10), PilotReply = reader.GetInt32(11),
                        PilotMail = reader.GetInt32(12), OutsiderMail = reader.GetInt32(13),
                        SharedCall = reader.GetInt32(14), OutsiderOnlyCall = reader.GetInt32(15), ExternalCall = reader.GetInt32(16),
                        Url = reader.GetInt32(17), SearchTerm = reader.GetInt32(18), Address = reader.GetInt32(19), CallType = reader.GetInt32(20),
                        OutsiderOrganisedCall = reader.GetInt32(21),
                        OrgType = reader.GetInt32(22),
                    };
                }
            }
        }

        /// <summary>Removes everything <see cref="SeedAsync"/> created that is still there, children first.</summary>
        private static async Task CleanupAsync(Seed seed, params int[] extraUsers)
        {
            if (seed == null) return;
            var users = string.Join(",", new[] { seed.Pilot, seed.Outsider, seed.Report }.Concat(extraUsers));
            await ExecuteAsync($@"
DELETE m FROM dbo.call_session_call_modalities m JOIN dbo.call_sessions s ON s.id = m.call_session_id JOIN dbo.call_records r ON r.id = s.call_record_id WHERE r.call_type_id = {seed.CallType};
DELETE s FROM dbo.call_sessions s JOIN dbo.call_records r ON r.id = s.call_record_id WHERE r.call_type_id = {seed.CallType};
DELETE f FROM dbo.call_feedback f JOIN dbo.call_records r ON r.id = f.call_id WHERE r.call_type_id = {seed.CallType};
DELETE FROM dbo.call_records WHERE call_type_id = {seed.CallType};
DELETE FROM dbo.call_types WHERE id = {seed.CallType};
DELETE FROM dbo.teams_user_activity_log WHERE user_id IN ({users});
DELETE r FROM dbo.sent_email_recipients r JOIN dbo.sent_emails e ON e.id = r.sent_email_id WHERE e.user_id IN ({users});
DELETE FROM dbo.sent_emails WHERE user_id IN ({users});
DELETE FROM dbo.email_addresses WHERE id = {seed.Address};
UPDATE dbo.page_comments SET parent_id = NULL WHERE url_id = {seed.Url};
DELETE FROM dbo.page_comments WHERE url_id = {seed.Url};
DELETE FROM dbo.page_likes WHERE url_id = {seed.Url};
DELETE FROM dbo.searches WHERE search_term_id = {seed.SearchTerm};
DELETE FROM dbo.hits WHERE url_id = {seed.Url};
DELETE FROM dbo.sessions WHERE user_id IN ({users});
DELETE FROM dbo.search_terms WHERE id = {seed.SearchTerm};
DELETE FROM dbo.urls WHERE id = {seed.Url};
DELETE FROM dbo.event_meta_power_app_share WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.copilot_chats WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.event_meta_general WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.audit_events WHERE user_id IN ({users});
UPDATE dbo.users SET manager_id = NULL WHERE id IN ({users});
DELETE FROM dbo.user_org_assignments WHERE org_type_id = {seed.OrgType};
DELETE FROM dbo.user_org_values WHERE org_type_id = {seed.OrgType};
DELETE FROM dbo.user_org_types WHERE id = {seed.OrgType};
DELETE FROM dbo.users WHERE id IN ({users});");
        }

        #endregion

        #region Helpers

        /// <summary>Keeps what the engine logs, to explain a failure.</summary>
        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly List<string> _lines = new List<string>();

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add($"{logLevel}: {formatter(state, exception)}{(exception == null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}");
                }
            }

            public override string ToString()
            {
                lock (_lines) return string.Join(Environment.NewLine, _lines);
            }
        }

        private static async Task ExecuteAsync(string sql)
        {
            using (var connection = new SqlConnection(ConnectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 300;
                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();
            }
        }

        private static async Task<object> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
        {
            using (var connection = new SqlConnection(ConnectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                await connection.OpenAsync();
                foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
                return await command.ExecuteScalarAsync();
            }
        }

        private static async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters)
            => Convert.ToInt32(await ScalarAsync(sql, parameters));

        /// <summary>A queued purge that removes exactly <paramref name="userIds"/> (see <see cref="NewEngine"/>).</summary>
        private Task<int> StartJobWithCandidatesAsync(params int[] userIds) => StartJobWithCandidatesAsync(null, userIds);

        /// <summary>The same, confirmed against the filter with <paramref name="filterFingerprint"/>.</summary>
        private async Task<int> StartJobWithCandidatesAsync(string filterFingerprint, int[] userIds)
        {
            _candidates = userIds;
            var job = await State.CreateAsync("admin@contoso.local", filterFingerprint, 1);
            Assert.AreEqual(UserScopePurgeStates.Queued, job.State);
            return job.Id;
        }

        /// <summary>
        /// An engine that removes the candidates given to <see cref="StartJobWithCandidatesAsync(int[])"/> (none: it works them
        /// out from the scope), and saves its progress and looks for a stop request after every window.
        /// </summary>
        private UserScopePurgeEngine NewEngine() => new UserScopePurgeEngine(Database, State, NullLogger.Instance)
        {
            RetryDelayOverride = TimeSpan.Zero,
            StateCheckInterval = TimeSpan.Zero,
            CandidatesOverride = _candidates,
        };

        private static async Task AssertPurgedAsync(Seed s)
        {
            async Task Exists(string what, string sql, bool expected, params (string, object)[] ps)
                => Assert.AreEqual(expected, await CountAsync(sql, ps) > 0, what);

            await Exists("The outsider's user row is gone.", "SELECT COUNT(*) FROM dbo.users WHERE id = @id", false, ("@id", s.Outsider));
            await Exists("The pilot is untouched.", "SELECT COUNT(*) FROM dbo.users WHERE id = @id", true, ("@id", s.Pilot));
            await Exists("The Unknown User stays.", "SELECT COUNT(*) FROM dbo.users WHERE id = @id", true, ("@id", s.Unknown));
            Assert.AreEqual(DBNull.Value, await ScalarAsync("SELECT manager_id FROM dbo.users WHERE id = @id", ("@id", s.Report)),
                "The person the outsider managed keeps their record, without a manager.");

            await Exists("The outsider's audit events are gone.", "SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", false, ("@id", s.Outsider));
            await Exists("The pilot's audit events stay.", "SELECT COUNT(*) FROM dbo.audit_events WHERE id IN (@a, @b)", true, ("@a", s.PilotEvent), ("@b", s.PilotShareEvent));
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.event_meta_general WHERE event_id IN (@a, @b)", ("@a", s.PilotEvent), ("@b", s.OutsiderEvent)),
                "Only the pilot's event metadata stays.");
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.copilot_chats WHERE event_id IN (@a, @b)", ("@a", s.PilotEvent), ("@b", s.OutsiderEvent)),
                "Only the pilot's Copilot chat stays.");
            Assert.AreEqual(DBNull.Value, await ScalarAsync("SELECT shared_with_user_id FROM dbo.event_meta_power_app_share WHERE event_id = @e", ("@e", s.PilotShareEvent)),
                "What the pilot shared with the outsider stays, without its recipient.");
            await Exists("What the outsider shared is gone with their event.", "SELECT COUNT(*) FROM dbo.event_meta_power_app_share WHERE event_id = @e", false, ("@e", s.OutsiderShareEvent));

            await Exists("The outsider's session is gone.", "SELECT COUNT(*) FROM dbo.sessions WHERE id = @id", false, ("@id", s.OutsiderSession));
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.hits WHERE url_id = @u", ("@u", s.Url)), "Only the pilot's page view stays.");
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.searches WHERE search_term_id = @t", ("@t", s.SearchTerm)), "Only the pilot's search stays.");

            await Exists("The outsider's comment is gone.", "SELECT COUNT(*) FROM dbo.page_comments WHERE id = @id", false, ("@id", s.OutsiderComment));
            Assert.AreEqual(DBNull.Value, await ScalarAsync("SELECT parent_id FROM dbo.page_comments WHERE id = @id", ("@id", s.PilotReply)),
                "The pilot's reply stays, no longer linked to the removed comment.");
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.page_likes WHERE url_id = @u", ("@u", s.Url)), "Only the pilot's like stays.");

            await Exists("The outsider's sent email is gone.", "SELECT COUNT(*) FROM dbo.sent_emails WHERE id = @id", false, ("@id", s.OutsiderMail));
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.sent_email_recipients WHERE sent_email_id = @id", ("@id", s.PilotMail)),
                "The pilot's email keeps its recipient.");
            await Exists("The outsider's usage rows are gone.", "SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = @id", false, ("@id", s.Outsider));
            await Exists("The pilot's usage rows stay.", "SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = @id", true, ("@id", s.Pilot));
            Assert.AreEqual(s.Pilot.ToString(), await ScalarAsync("SELECT STRING_AGG(user_id, ',') FROM dbo.user_org_assignments WHERE org_type_id = @t", ("@t", s.OrgType)),
                "Only the pilot keeps their organisation value; the value itself stays for them.");

            Assert.AreEqual(s.Pilot, await ScalarAsync("SELECT organizer_id FROM dbo.call_records WHERE id = @id", ("@id", s.SharedCall)),
                "The pilot's call stays, organised by the pilot...");
            Assert.AreEqual(s.Unknown, await ScalarAsync("SELECT attendee_user_id FROM dbo.call_sessions WHERE call_record_id = @id", ("@id", s.SharedCall)),
                "...with the outsider as the anonymous Unknown User...");
            Assert.AreEqual("pilot words", await ScalarAsync("SELECT STRING_AGG(text, '|') FROM dbo.call_feedback WHERE call_id = @id", ("@id", s.SharedCall)),
                "...and none of the outsider's feedback.");
            await Exists("A call with nobody in scope left on it is deleted.", "SELECT COUNT(*) FROM dbo.call_records WHERE id = @id", false, ("@id", s.OutsiderOnlyCall));
            await Exists("...with its sessions.", "SELECT COUNT(*) FROM dbo.call_sessions WHERE call_record_id = @id", false, ("@id", s.OutsiderOnlyCall));
            await Exists("A call that never involved the outsider is untouched.", "SELECT COUNT(*) FROM dbo.call_records WHERE id = @id", true, ("@id", s.ExternalCall));
            Assert.AreEqual(s.Unknown, await ScalarAsync("SELECT organizer_id FROM dbo.call_records WHERE id = @id", ("@id", s.OutsiderOrganisedCall)),
                "The call the outsider organised for the pilot stays, organised by the Unknown User...");
            Assert.AreEqual(s.Pilot, await ScalarAsync("SELECT attendee_user_id FROM dbo.call_sessions WHERE call_record_id = @id", ("@id", s.OutsiderOrganisedCall)),
                "...with the pilot still on it.");
        }

        #endregion

        [TestMethod]
        public async Task Purge_RemovesWhatIsAboutTheOutsider_AnonymisesWhatIsShared_AndLeavesEveryoneElse()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);

                // A batch of one forces every step to re-run windows and resume inside them.
                var engine = NewEngine();
                engine.BatchSizeOverride = 1;
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(jobId, scopeProvider: null));

                await AssertPurgedAsync(seed);

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeStates.Completed, job.State);
                Assert.AreEqual(UserScopePurgePhases.Done, job.Phase);
                Assert.AreEqual(job.StepCount, job.StepIndex);
                Assert.AreEqual(1, job.CandidateCount);
                Assert.AreEqual(1, job.UsersDeleted);
                Assert.AreEqual(0, job.UsersSkipped);
                Assert.IsNotNull(job.CompletedUtc);
                Assert.AreEqual(2L, job.RowsAffected["audit_events"]);
                Assert.AreEqual(1L, job.RowsAffected["users"], "Deleted...");
                Assert.AreEqual(1L, job.RowsAffected["users.manager_id"], "...and cleared in place are counted apart.");
                Assert.AreEqual(1L, job.RowsAffected["page_comments.parent_id"]);
                Assert.AreEqual(1L, job.RowsAffected["event_meta_power_app_share.shared_with_user_id"]);
                Assert.AreEqual(1L, job.RowsAffected["call_records"], "The call with nobody in scope left is deleted...");
                Assert.AreEqual(1L, job.RowsAffected["call_sessions"], "...with its session...");
                Assert.AreEqual(1L, job.RowsAffected["call_sessions.attendee_user_id"], "...while the outsider's place on the shared call is anonymised...");
                Assert.AreEqual(1L, job.RowsAffected["call_feedback"], "...and their feedback on it deleted...");
                Assert.AreEqual(1L, job.RowsAffected["call_records.organizer_id"], "...and the call they organised for the pilot kept, anonymised.");
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "The purge let go of its lock when it ended.");
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM sys.tables WHERE name LIKE N'user[_]scope[_]purge%'"),
                    "The purge keeps nothing of its own in the analytics database.");
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// A purge interrupted by the host shutting down starts again from the beginning, with a fresh list of who to
        /// remove, and finishes: every step removes whatever still matches, so what the first run did is simply found done.
        /// </summary>
        [TestMethod]
        public async Task Purge_StoppedByTheHost_StartsAgainFromTheBeginningAndFinishes()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);

                // Stop the host part-way through: after the first window of the web-traffic phase.
                var hostStopping = new CancellationTokenSource();
                var first = NewEngine();
                first.AfterWindow = (step, window) =>
                {
                    if (UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity) hostStopping.Cancel();
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await first.RunAsync(jobId, null, null, hostStopping.Token));

                var paused = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeStates.Running, paused.State);
                Assert.AreEqual(UserScopePurgePhases.WebActivity, paused.Phase, "It saved where it had got to...");
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "...and let go of the purge lock, so it can start again straight away.");
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", seed.Outsider)),
                    "The steps before the stop are done.");

                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await NewEngine().RunAsync(jobId, null));
                await AssertPurgedAsync(seed);

                var finished = await State.GetAsync(jobId);
                Assert.AreEqual(1, finished.UsersDeleted);
                Assert.AreEqual(2L, finished.RowsAffected["audit_events"], "Counts carry on from the first run rather than starting again.");
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// Azure Table storage, where a purge keeps its progress, fails a save part-way. The purge can't carry on without
        /// recording where it is, so it stops and says why, keeps what it removed and lets go of the purge lock; a purge
        /// started again finishes the job.
        /// </summary>
        [TestMethod]
        public async Task Purge_ThatCannotSaveItsProgress_StopsAndSaysWhy_AndStartingAgainFinishes()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var store = new OutageKeyValueStore();
                State = new UserScopePurgeStateStore(store, isDurable: true);
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);

                var first = NewEngine();
                first.AfterWindow = (step, window) =>
                {
                    if (UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity) store.FailNextWrites = 1;
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await first.RunAsync(jobId, null));
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "It let go of the purge lock.");

                var failed = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeStates.Failed, failed.State);
                Assert.AreEqual(UserScopePurgeErrorCodes.StateUnavailable, failed.ErrorCode);
                Assert.AreEqual(UserScopePurgeErrorCodes.StateUnavailable, first.Job.ErrorCode, "The audit event reads the run's own record.");
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", seed.Outsider)),
                    "What it removed before the failure stays removed.");

                var again = await StartJobWithCandidatesAsync(seed.Outsider);
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await NewEngine().RunAsync(again, null));
                await AssertPurgedAsync(seed);
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// Changing <c>UserGroupsFilter</c> restarts the web app, and a running purge starts again. It must not go on
        /// removing people under a filter nobody confirmed - some may be inside the new one - so it stops, keeping what it
        /// had removed and removing nothing more.
        /// </summary>
        [TestMethod]
        public async Task Purge_StartedAgainAfterTheFilterChanged_StopsAndRemovesNothingMore()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var confirmed = new UserGroupsFilterModel("Copilot pilot");
                var jobId = await StartJobWithCandidatesAsync(confirmed.Fingerprint, new[] { seed.Outsider });

                IUserImportScopeProvider ProviderFor(UserGroupsFilterModel filter) => new FixedUserImportScopeProvider(TestUserScopes.Of("pilot@contoso.local"),
                    UserImportScopeResolution.Resolved(TestMembers("pilot@contoso.local"), null, null, DateTime.UtcNow), filter);

                var hostStopping = new CancellationTokenSource();
                var first = NewEngine();
                first.AfterWindow = (step, window) =>
                {
                    if (UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity) hostStopping.Cancel();
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await first.RunAsync(jobId, ProviderFor(confirmed), null, hostStopping.Token),
                    "Under the filter it was confirmed against, the purge runs.");
                var sessionsBefore = await CountAsync("SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @id", ("@id", seed.Outsider));

                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await NewEngine().RunAsync(jobId, ProviderFor(new UserGroupsFilterModel("Copilot pilot*"))));

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeStates.Failed, job.State);
                Assert.AreEqual(UserScopePurgeErrorCodes.FilterChangedWhileRunning, job.ErrorCode);
                Assert.AreEqual(UserScopePurgePhases.WebActivity, job.Phase, "It reports where it had got to.");
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", seed.Outsider)),
                    "What it removed before the change stays removed...");
                Assert.AreEqual(sessionsBefore, await CountAsync("SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @id", ("@id", seed.Outsider)),
                    "...but it removed nothing after it...");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id = @id", ("@id", seed.Outsider)), "...so the outsider is still there.");
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "It let go of the purge lock.");
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        [TestMethod]
        public async Task Purge_Cancelled_StopsAfterTheBatchItIsOn_AndKeepsWhatItRemoved()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);

                var engine = NewEngine();
                engine.AfterWindow = async (step, window) =>
                {
                    if (UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity) await State.RequestCancelAsync(jobId);
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Cancelled, await engine.RunAsync(jobId, null));

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeStates.Cancelled, job.State);
                Assert.IsNotNull(job.CompletedUtc);
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", seed.Outsider)),
                    "What was removed before the stop stays removed.");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id = @id", ("@id", seed.Outsider)),
                    "The user row is only removed at the very end, so it is still there.");
                Assert.IsFalse(await State.RequestCancelAsync(jobId), "A finished purge can't be cancelled again.");
                using (var next = await Database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
                {
                    Assert.IsNotNull(next, "A new purge can start once the cancelled one has ended.");
                }
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// The importers keep running during a purge. Someone whose data arrives after their share of a table was purged
        /// is kept - their user row and the new data - and counted as skipped, rather than failing the purge or leaving
        /// orphaned rows. The others are still removed, even from the same batch.
        /// </summary>
        [TestMethod]
        public async Task Purge_PeopleWhoseDataArrivesWhileItRuns_AreKeptAndCounted_AndTheRestAreStillRemoved()
        {
            Seed seed = null;
            int lateEvent = 0, lateSession = 0;
            try
            {
                seed = await SeedAsync();
                lateEvent = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-late-event@contoso.local")));
                lateSession = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-late-session@contoso.local")));
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider, lateEvent, lateSession);

                var arrived = false;
                var engine = NewEngine();
                engine.AfterWindow = async (step, window) =>
                {
                    if (!arrived && UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.PageComments)
                    {
                        arrived = true;
                        // An audit event (no foreign key: the purge must notice it itself) and a web session (a foreign
                        // key: deleting the user would fail) arrive after their tables were done.
                        await ExecuteAsync($"INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES (NEWID(), SYSUTCDATETIME(), {lateEvent});" +
                                           $"INSERT INTO dbo.sessions (user_id) VALUES ({lateSession});");
                    }
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(jobId, null));

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(2, job.UsersSkipped, "Both late arrivals are kept and counted.");
                Assert.AreEqual(1, job.UsersDeleted, "The outsider is still removed.");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", lateEvent)),
                    "The late audit event is not orphaned...");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id = @id", ("@id", lateEvent)), "...because its user is kept.");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id = @id", ("@id", lateSession)));
                await AssertPurgedAsync(seed);
            }
            finally
            {
                await ExecuteAsync($"DELETE FROM dbo.audit_events WHERE user_id IN ({lateEvent}, {lateSession}); DELETE FROM dbo.sessions WHERE user_id IN ({lateEvent}, {lateSession});");
                await CleanupAsync(seed, lateEvent, lateSession);
            }
        }

        /// <summary>
        /// <c>audit_events.user_id</c> has no foreign key, so only the purge's own check stops it deleting someone whose
        /// audit event arrived after the audit step, and orphaning the event. With no foreign key failing in the batch
        /// (unlike the test above, where one sends the whole batch down the one-at-a-time path), the batch delete itself
        /// has to leave them.
        /// </summary>
        [TestMethod]
        public async Task Purge_SomeoneWhoseAuditEventArrivesWhileItRuns_IsKeptByTheBatchDelete()
        {
            Seed seed = null;
            int lateEvent = 0;
            try
            {
                seed = await SeedAsync();
                lateEvent = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-late-event@contoso.local")));
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider, lateEvent);

                var arrived = false;
                var engine = NewEngine();
                engine.AfterWindow = async (step, window) =>
                {
                    if (!arrived && UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.PageComments)
                    {
                        arrived = true;
                        await ExecuteAsync($"INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES (NEWID(), SYSUTCDATETIME(), {lateEvent});");
                    }
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(jobId, null));

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(1, job.UsersSkipped, "The late arrival is kept and counted...");
                Assert.AreEqual(1, job.UsersDeleted, "...while the outsider is removed.");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id = @id", ("@id", lateEvent)),
                    "Kept, so the audit event that arrived is not orphaned.");
                await AssertPurgedAsync(seed);
            }
            finally
            {
                await ExecuteAsync($"DELETE FROM dbo.audit_events WHERE user_id = {lateEvent};");
                await CleanupAsync(seed, lateEvent);
            }
        }

        /// <summary>
        /// A purge can take hours, and its database session can drop in that time. The purge opens a new one - taking the
        /// purge lock again and reloading who it is removing - and carries on.
        /// </summary>
        [TestMethod]
        public async Task Purge_CarriesOn_WhenItsDatabaseSessionIsKilledPartWay()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);

                var killed = false;
                var log = new CapturingLogger();
                var engine = new UserScopePurgeEngine(Database, State, log)
                {
                    RetryDelayOverride = TimeSpan.Zero,
                    StateCheckInterval = TimeSpan.Zero,
                    CandidatesOverride = _candidates,
                };
                engine.AfterWindow = async (step, window) =>
                {
                    if (!killed && UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity)
                    {
                        killed = true;
                        var session = Convert.ToInt32(await ScalarAsync(
                            "SELECT TOP (1) request_session_id FROM sys.dm_tran_locks " +
                            "WHERE resource_type = 'APPLICATION' AND request_mode = 'X' AND resource_description LIKE N'%AnalyticsInsights.UserScopePurge%';"));
                        await ExecuteAsync($"KILL {session};");
                    }
                };

                var outcome = await engine.RunAsync(jobId, null);
                var ended = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, outcome, $"{ended.ErrorCode}: {ended.ErrorDetail}{Environment.NewLine}{log}");
                Assert.IsTrue(killed, "The purge's session was killed part-way through.");
                await AssertPurgedAsync(seed);
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "The new session let go of the lock at the end too.");
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// How a failed statement is classified, without depending on timing. A killed session's next command fails with
        /// -1 while its connection may still say <c>Open</c> - the case the kill test above only hits when the race goes
        /// that way - or with 0 once SqlClient has closed it. Either way the session is opened again. A deadlock leaves the
        /// session, and the purge lock it holds, intact.
        /// </summary>
        [TestMethod]
        public void AKilledSession_IsTreatedAsLost_EvenWhileItsConnectionStillSaysOpen()
        {
            Assert.IsTrue(UserScopePurgeEngine.IsConnectionLost(-1, ConnectionState.Open), "-1 while the connection still says Open.");
            Assert.IsTrue(UserScopePurgeEngine.IsRetryable(-1, ConnectionState.Open));
            Assert.IsTrue(UserScopePurgeEngine.IsConnectionLost(0, ConnectionState.Closed), "Any error once the connection has closed.");
            Assert.IsTrue(UserScopePurgeEngine.IsRetryable(0, ConnectionState.Closed));

            Assert.IsFalse(UserScopePurgeEngine.IsConnectionLost(1205, ConnectionState.Open), "A deadlock keeps the session...");
            Assert.IsTrue(UserScopePurgeEngine.IsRetryable(1205, ConnectionState.Open), "...and is tried again on it.");
            Assert.IsFalse(UserScopePurgeEngine.IsRetryable(547, ConnectionState.Open), "A constraint violation is not retried.");
        }

        [TestMethod]
        public async Task Purge_RemovesNobody_WhenTheGroupsCannotBeRead_OrResolveToNobody_OrTheFilterChanged()
        {
            var filter = new UserGroupsFilterModel("Copilot pilot");
            var unavailable = new FixedUserImportScopeProvider(UserImportScope.Everyone("down"),
                UserImportScopeResolution.Unavailable(null, null, "Graph said no", DateTime.UtcNow, UserImportScopeFailureKind.DirectoryRead, 403), filter);
            var empty = new FixedUserImportScopeProvider(TestUserScopes.OfMembers(),
                UserImportScopeResolution.Resolved(new UserScopeMembers(), null, null, DateTime.UtcNow), filter);
            var otherFilter = new FixedUserImportScopeProvider(TestUserScopes.Of("pilot@contoso.local"),
                UserImportScopeResolution.Resolved(TestMembers("pilot@contoso.local"), null, null, DateTime.UtcNow), new UserGroupsFilterModel("Another group"));

            foreach (var (provider, expected) in new[]
            {
                (provider: (IUserImportScopeProvider)unavailable, expected: UserScopePurgeErrorCodes.ScopeUnavailable),
                (provider: empty, expected: UserScopePurgeErrorCodes.ScopeEmpty),
                (provider: otherFilter, expected: UserScopePurgeErrorCodes.FilterChanged),
            })
            {
                var job = await State.CreateAsync("admin@contoso.local", filter.Fingerprint, 1);

                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await NewEngine().RunAsync(job.Id, provider), expected);

                var ended = await State.GetAsync(job.Id);
                Assert.AreEqual(UserScopePurgeStates.Failed, ended.State, expected);
                Assert.AreEqual(expected, ended.ErrorCode);
                Assert.AreEqual(0, ended.CandidateCount, "Nobody was chosen, so nobody was removed.");
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "The refused purge let go of the lock.");
            }
        }

        private static UserScopeMembers TestMembers(params string[] upns)
        {
            var members = new UserScopeMembers();
            foreach (var upn in upns) members.Add(Guid.NewGuid().ToString(), upn, upn);
            return members;
        }

        [TestMethod]
        public async Task Snapshot_ChoosesEveryoneTheScopeLeavesOut_ButNeverTheUnknownUser()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var pilotUpn = seed.Token + "-pilot@contoso.local";
                var scope = TestUserScopes.Of(pilotUpn);

                var outside = await NewEngine().FindUsersOutsideScopeAsync(scope, seed.Unknown);

                CollectionAssert.Contains(outside, seed.Outsider);
                CollectionAssert.Contains(outside, seed.Report);
                CollectionAssert.DoesNotContain(outside, seed.Pilot, "Matched by UPN.");
                CollectionAssert.DoesNotContain(outside, seed.Unknown, "The anonymous Unknown User stands in for people; it isn't one.");

                var counts = await Database.CountUsersAsync(scope);
                Assert.AreEqual(counts.Total - 1, counts.OutsideScope, "Everyone but the pilot is outside a one-person scope.");
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// The one-purge-at-a-time rule is an application lock on the purge's own session: nothing is stored for it, only
        /// one session can hold it, and it goes when the session does.
        /// </summary>
        [TestMethod]
        public async Task PurgeLock_IsHeldByOneSessionAtATime_AndGoesWithIt()
        {
            var database = Database;
            Assert.IsFalse(await database.IsPurgeRunningAsync());

            using (var first = await database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                Assert.IsNotNull(first, "Free, so taken.");
                Assert.IsTrue(await database.IsPurgeRunningAsync(), "Seen as running from any other session - another web app instance, say.");
                Assert.IsNull(await database.TryOpenPurgeSessionAsync(TimeSpan.Zero), "A second purge can't start while one runs.");
            }

            Assert.IsFalse(await database.IsPurgeRunningAsync(), "Closing the session releases the lock...");
            using (var second = await database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                Assert.IsNotNull(second, "...so the next purge can start.");
            }
        }

        /// <summary>
        /// Purge records live in the state store, never the analytics database: the latest purge, a stop request under a
        /// key of its own, and a finished purge that is kept for a while and then forgotten.
        /// </summary>
        [TestMethod]
        public async Task StateStore_KeepsRecordsAndStopRequests_AndForgetsFinishedPurgesInTime()
        {
            var now = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
            var values = new InMemoryKeyValueStore(() => now);
            var state = new UserScopePurgeStateStore(values, isDurable: false, () => now);
            Assert.IsFalse(state.IsDurable, "Memory doesn't survive a restart.");
            Assert.IsNull(await state.GetLatestAsync());

            var first = await state.CreateAsync("admin@contoso.local", "fingerprint", 5);
            Assert.AreEqual(UserScopePurgeStates.Queued, first.State);
            Assert.AreEqual(UserScopePurgePlan.StepCount, first.StepCount);
            var second = await state.CreateAsync("admin@contoso.local", "fingerprint", 5);
            Assert.IsTrue(second.Id > first.Id, "Ids count up.");
            Assert.AreEqual(second.Id, (await state.GetLatestAsync()).Id);

            second.State = UserScopePurgeStates.Running;
            second.RowsAffected["users.manager_id"] = 3;
            await state.SaveAsync(second);
            Assert.IsTrue(await state.RequestCancelAsync(second.Id));
            var read = await state.GetAsync(second.Id);
            Assert.IsTrue(read.CancelRequested, "A stop request is read with the record...");
            Assert.IsTrue(await state.IsCancelRequestedAsync(second.Id), "...and on its own.");
            Assert.AreEqual(3L, read.RowsAffected["users.manager_id"]);

            read.State = UserScopePurgeStates.Cancelled;
            await state.SaveAsync(read);
            Assert.IsFalse(await state.RequestCancelAsync(read.Id), "Only an active purge can be stopped.");
            Assert.IsFalse(await state.RequestCancelAsync(int.MaxValue), "Nor one that doesn't exist.");

            now = now.Add(UserScopePurgeStateStore.FinishedRetention).AddMinutes(1);
            Assert.IsNull(await state.GetAsync(read.Id), "A finished purge is forgotten after the retention period.");
            Assert.IsNotNull(await state.GetAsync(first.Id), "One still queued is not.");

            var restarted = new UserScopePurgeStateStore(new InMemoryKeyValueStore(() => now), isDurable: false, () => now);
            Assert.IsTrue((await restarted.CreateAsync("admin@contoso.local", "fingerprint", 5)).Id > second.Id,
                "After a restart loses the records, a new purge still doesn't reuse an id a browser may still be asking about.");
        }

        /// <summary>
        /// The purge's session outlives every window. A window's temporary tables are its own - it runs through
        /// sp_executesql, so they go when it ends, even if it fails part-way - and tables of the same name already on the
        /// session neither clash with them nor get picked up.
        /// </summary>
        [TestMethod]
        public async Task CallsWindow_KeepsItsTempTablesToItself_OnTheLongLivedPurgeSession()
        {
            Seed seed = null;
            try
            {
                // A call to walk, so the window gets as far as creating its temporary tables.
                seed = await SeedAsync();
                using (var session = await Database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
                {
                    Assert.IsNotNull(session);
                    await session.LoadCandidatesAsync(new int[0]);
                    using (var leftovers = new SqlCommand("CREATE TABLE #calls (id int NOT NULL PRIMARY KEY); CREATE TABLE #doomed (id int NOT NULL PRIMARY KEY);", session.Connection))
                    {
                        await leftovers.ExecuteNonQueryAsync();
                    }

                    using (var command = new SqlCommand(UserScopePurgeEngine.BuildCallsWindowSql("int", hasAfter: false), session.Connection))
                    {
                        command.Parameters.Add("@window", SqlDbType.Int).Value = int.MaxValue;
                        command.Parameters.Add("@batch", SqlDbType.Int).Value = 4000;
                        command.Parameters.Add("@unknownUserId", SqlDbType.Int).Value = seed.Unknown;
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            Assert.IsTrue(await reader.ReadAsync(), "The window runs and reports its result...");
                            Assert.IsFalse(reader.GetBoolean(2), "...having found calls to walk.");
                        }
                    }
                }
            }
            finally
            {
                await CleanupAsync(seed);
            }
        }

        /// <summary>
        /// The plan has to take care of every foreign key that would otherwise block a delete (or silently cascade into it),
        /// before the delete runs. A new table referencing <c>dbo.users</c> that nobody added to the plan fails here.
        /// </summary>
        [TestMethod]
        public async Task Plan_TakesCareOfEveryForeignKeyBeforeTheRowItPointsAtIsDeleted()
        {
            var steps = UserScopePurgePlan.Steps;
            var handledAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstDeleteOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < steps.Count; i++)
            {
                foreach (var reference in steps[i].Handles)
                {
                    if (!handledAt.ContainsKey(reference)) handledAt[reference] = i;
                }
                if (steps[i].Kind == UserScopePurgeStepKind.Delete || steps[i].Kind == UserScopePurgeStepKind.Users)
                {
                    if (!firstDeleteOf.ContainsKey(steps[i].Table)) firstDeleteOf[steps[i].Table] = i;
                }
                if (steps[i].Kind == UserScopePurgeStepKind.Calls)
                {
                    firstDeleteOf["call_records"] = i;
                    firstDeleteOf["call_sessions"] = i;
                }
            }

            var problems = new List<string>();
            using (var connection = new SqlConnection(ConnectionString))
            using (var command = new SqlCommand(@"
SELECT OBJECT_NAME(fk.parent_object_id), c.name, OBJECT_NAME(fk.referenced_object_id), fk.delete_referential_action_desc
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id;", connection))
            {
                await connection.OpenAsync();
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var child = reader.GetString(0);
                        var column = reader.GetString(1);
                        var parent = reader.GetString(2);
                        var onDelete = reader.GetString(3);
                        var reference = $"{child}.{column}";
                        if (!firstDeleteOf.TryGetValue(parent, out var parentDeletedAt))
                        {
                            continue;
                        }

                        // Every reference to a user is handled explicitly - even the cascading ones - so the user delete
                        // at the end never cascades into a large table in one statement.
                        var mustBeHandled = parent == "users" || onDelete == "NO_ACTION";
                        if (!mustBeHandled) continue;

                        if (!handledAt.TryGetValue(reference, out var handled))
                        {
                            problems.Add($"{reference} -> {parent} ({onDelete}) is not handled by any step");
                        }
                        else if (handled > parentDeletedAt)
                        {
                            problems.Add($"{reference} is handled at step {handled}, after {parent} is deleted at step {parentDeletedAt}");
                        }
                    }
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            CollectionAssert.Contains(handledAt.Keys.ToList(), "audit_events.user_id",
                "audit_events.user_id has no foreign key, so nothing else would catch it being left behind.");
        }

        /// <summary>
        /// Each step walks the leading column of its table's clustered index, so its windows are range seeks rather than
        /// scans. The column need not be unique on its own (<c>user_org_assignments</c> is keyed on user and organisation
        /// type): a window is a range of its values.
        /// </summary>
        [TestMethod]
        public async Task Plan_EveryStepWalksTheLeadingColumnOfItsTablesClusteredKey()
        {
            var problems = new List<string>();
            using (var connection = new SqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                foreach (var step in UserScopePurgePlan.Steps)
                {
                    using (var command = new SqlCommand(@"
SELECT COUNT(*), MAX(CASE WHEN ic.key_ordinal = 1 THEN c.name END)
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@table) AND i.type = 1;", connection))
                    {
                        command.Parameters.AddWithValue("@table", "dbo." + step.Table);
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            await reader.ReadAsync();
                            var keys = reader.GetInt32(0);
                            var leading = reader.IsDBNull(1) ? null : reader.GetString(1);
                            if (keys < 1 || !string.Equals(leading, step.KeyColumn, StringComparison.OrdinalIgnoreCase))
                            {
                                problems.Add($"{step}: clustered key has {keys} column(s), leading with '{leading}', not '{step.KeyColumn}'");
                            }
                        }
                    }
                }
            }
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// Every window reads only its own range of the clustered key it walks. Left to the optimiser - which can't estimate
        /// the window's end, a local variable - <c>audit_events</c> was read through <c>IX_user_id</c> instead, a walk of the
        /// whole index for every window: twice as slow per window on 2M audit events, over three times as slow on 5M.
        /// </summary>
        [TestMethod]
        public void WindowSql_ReadsOnlyTheWindowsOwnRangeOfTheClusteredKey()
        {
            foreach (var step in UserScopePurgePlan.Steps.Where(s => s.Kind != UserScopePurgeStepKind.Calls))
            {
                foreach (var hasAfter in new[] { false, true })
                {
                    StringAssert.Contains(UserScopePurgeEngine.BuildWindowSql(step, "int", hasAfter), $"FROM dbo.[{step.Table}] t WITH (INDEX(1)) WHERE", step.ToString());
                }
            }
            StringAssert.Contains(UserScopePurgeEngine.BuildCallsWindowSql("int", hasAfter: true), "FROM dbo.call_records r WITH (INDEX(1))", "The calls window's INSERT INTO #calls.");
        }
    }

    /// <summary>
    /// An in-memory store that fails like an unreachable Azure Table storage account: every operation while
    /// <see cref="Down"/>, or just the next <see cref="FailNextWrites"/> writes - a blip.
    /// </summary>
    internal sealed class OutageKeyValueStore : IKeyValueStore
    {
        private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
        private readonly object _gate = new object();
        private int _failNextWrites;

        public bool Down { get; set; }

        public int FailNextWrites
        {
            get { lock (_gate) return _failNextWrites; }
            set { lock (_gate) _failNextWrites = value; }
        }

        public string Description => "a test store";

        public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            return _inner.GetStringAsync(key, cancellationToken);
        }

        public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            lock (_gate)
            {
                if (_failNextWrites > 0)
                {
                    _failNextWrites--;
                    throw Outage();
                }
            }
            return _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            return _inner.DeleteAsync(key, cancellationToken);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDown();
            return _inner.ExistsAsync(key, cancellationToken);
        }

        private void ThrowIfDown()
        {
            if (Down) throw Outage();
        }

        private static Azure.RequestFailedException Outage() => new Azure.RequestFailedException(503, "Synthetic storage outage.");
    }
}
