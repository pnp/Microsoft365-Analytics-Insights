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
            public int OrgType, LicenceType;
            public int OutsiderSearchTerm, OutsiderAddress, SharedPhrase, OutsiderPhrase;
            public Guid CostRun, PilotObject, OutsiderObject;
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

-- Web traffic. The outsider also searched for something nobody else did.
DECLARE @url int, @term int, @outsiderTerm int;
INSERT INTO dbo.urls (full_url) VALUES (N'https://contoso.sharepoint.com/sites/example/SitePages/Καλημέρα κόσμε ' + @token + N'.aspx');
SET @url = SCOPE_IDENTITY();
INSERT INTO dbo.search_terms (search_term) VALUES (N'Καλημέρα ' + @token);
SET @term = SCOPE_IDENTITY();
INSERT INTO dbo.search_terms (search_term) VALUES (N'Μόνο ' + @token);
SET @outsiderTerm = SCOPE_IDENTITY();
DECLARE @pilotSession int, @outsiderSession int;
INSERT INTO dbo.sessions (user_id) VALUES (@pilot); SET @pilotSession = SCOPE_IDENTITY();
INSERT INTO dbo.sessions (user_id) VALUES (@outsider); SET @outsiderSession = SCOPE_IDENTITY();
INSERT INTO dbo.hits (url_id, hit_timestamp, page_request_id, session_id) VALUES (@url, @now, NEWID(), @pilotSession), (@url, @now, NEWID(), @outsiderSession);
INSERT INTO dbo.searches (session_id, search_term_id) VALUES (@pilotSession, @term), (@outsiderSession, @term), (@outsiderSession, @outsiderTerm);

-- The pilot replied to the outsider's page comment.
DECLARE @outsiderComment int, @pilotReply int;
INSERT INTO dbo.page_comments (user_id, url_id, created, sp_id, comment) VALUES (@outsider, @url, @now, 1, N'Καλημέρα κόσμε');
SET @outsiderComment = SCOPE_IDENTITY();
INSERT INTO dbo.page_comments (user_id, url_id, created, sp_id, comment, parent_id) VALUES (@pilot, @url, @now, 2, N'reply', @outsiderComment);
SET @pilotReply = SCOPE_IDENTITY();
INSERT INTO dbo.page_likes (user_id, url_id, created, sp_id) VALUES (@pilot, @url, @now, 3), (@outsider, @url, @now, 4);

-- Sent email. The outsider sent theirs from their own address; the recipient's address is on both.
DECLARE @address int, @outsiderAddress int, @pilotMail int, @outsiderMail int;
INSERT INTO dbo.email_addresses (address) VALUES (@token + '-recipient@contoso.local'); SET @address = SCOPE_IDENTITY();
INSERT INTO dbo.email_addresses (address) VALUES (@token + '-outsider@contoso.local'); SET @outsiderAddress = SCOPE_IDENTITY();
INSERT INTO dbo.sent_emails (sent_date, graph_message_id, from_address_id, user_id) VALUES (@now, @token + '-pilot', @address, @pilot);
SET @pilotMail = SCOPE_IDENTITY();
INSERT INTO dbo.sent_emails (sent_date, graph_message_id, from_address_id, user_id) VALUES (@now, @token + '-outsider', @outsiderAddress, @outsider);
SET @outsiderMail = SCOPE_IDENTITY();
INSERT INTO dbo.sent_email_recipients (sent_email_id, recipient_address_id) VALUES (@pilotMail, @address), (@outsiderMail, @address);

-- Copilot prompt phrases: one both of them used, one only the outsider did.
DECLARE @pilotChats int, @outsiderChats int, @pilotPrompt int, @outsiderPrompt int, @sharedPhrase int, @outsiderPhrase int;
INSERT INTO dbo.copilot_interaction_sessions (session_ref, user_id) VALUES (@token + '-pilot', @pilot); SET @pilotChats = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interaction_sessions (session_ref, user_id) VALUES (@token + '-outsider', @outsider); SET @outsiderChats = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interactions (graph_interaction_id, session_id, user_id, created_utc, body_char_count, body_word_count, attachment_count, link_count, mention_count, context_count)
VALUES (@token + '-pilot', @pilotChats, @pilot, @now, 1, 1, 0, 0, 0, 0);
SET @pilotPrompt = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interactions (graph_interaction_id, session_id, user_id, created_utc, body_char_count, body_word_count, attachment_count, link_count, mention_count, context_count)
VALUES (@token + '-outsider', @outsiderChats, @outsider, @now, 1, 1, 0, 0, 0, 0);
SET @outsiderPrompt = SCOPE_IDENTITY();
INSERT INTO dbo.keywords (name) VALUES (N'κοινή ' + @token); SET @sharedPhrase = SCOPE_IDENTITY();
INSERT INTO dbo.keywords (name) VALUES (N'ιδιωτική ' + @token); SET @outsiderPhrase = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interaction_keywords (interaction_id, keyword_id)
VALUES (@pilotPrompt, @sharedPhrase), (@outsiderPrompt, @sharedPhrase), (@outsiderPrompt, @outsiderPhrase);

-- Copilot credit history (preview), keyed by object id. The outsider's day is not linked to their user row yet.
DECLARE @costRun uniqueidentifier = NEWID(), @day date = '2026-09-01';
DECLARE @pilotObject uniqueidentifier = (SELECT TRY_CONVERT(uniqueidentifier, azure_ad_id) FROM dbo.users WHERE id = @pilot);
DECLARE @outsiderObject uniqueidentifier = (SELECT TRY_CONVERT(uniqueidentifier, azure_ad_id) FROM dbo.users WHERE id = @outsider);
INSERT INTO dbo.copilot_cost_runs (run_id, generation, inventory_date, last_served) VALUES (@costRun, @token, @day, SYSDATETIMEOFFSET());
INSERT INTO dbo.copilot_cost_work (run_id, object_id, user_id, next_attempt)
VALUES (@costRun, @pilotObject, @pilot, SYSDATETIMEOFFSET()), (@costRun, @outsiderObject, @outsider, SYSDATETIMEOFFSET());
INSERT INTO dbo.copilot_cost_observations (observation_date, object_id, user_id, observed_at, quality, selection_status)
VALUES (@day, @pilotObject, @pilot, SYSDATETIMEOFFSET(), 2, N'{}'), (@day, @outsiderObject, NULL, SYSDATETIMEOFFSET(), 1, N'{}');
INSERT INTO dbo.copilot_cost_services (observation_date, object_id, category_hash, category, policy_id)
VALUES (@day, @pilotObject, HASHBYTES('SHA2_256', N'chat'), N'chat', N'policy'), (@day, @outsiderObject, HASHBYTES('SHA2_256', N'chat'), N'chat', N'policy');
INSERT INTO dbo.copilot_cost_unevaluated (observation_date, object_id, ordinal, category, reason) VALUES (@day, @outsiderObject, 0, N'agents', N'pending');
INSERT INTO dbo.copilot_cost_history (run_id, publication_id, observation_date, object_id, observed_at, quality, bundle)
VALUES (@costRun, NEWID(), @day, @pilotObject, SYSDATETIMEOFFSET(), 2, N'{}'), (@costRun, NEWID(), @day, @outsiderObject, SYSDATETIMEOFFSET(), 1, N'{}');

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

-- Licence history: both current and historical rows are per-person.
DECLARE @licenceType int;
INSERT INTO dbo.license_types (name, sku_id) VALUES (@token + N'-licence', @token + N'_SKU'); SET @licenceType = SCOPE_IDENTITY();
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@pilot, @licenceType), (@outsider, @licenceType);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source, valid_from_previous_refresh_utc, valid_to_previous_refresh_utc)
VALUES (@pilot, @licenceType, DATEADD(day, -1, @now), NULL, 0, NULL, NULL),
       (@outsider, @licenceType, DATEADD(day, -2, @now), DATEADD(day, -1, @now), 1, DATEADD(day, -3, @now), DATEADD(day, -1, @now));

-- User organisations: both are in the same one.
DECLARE @orgType int, @orgValue int;
INSERT INTO dbo.user_org_types (name, source_kind) VALUES (@token, 2); SET @orgType = SCOPE_IDENTITY();
INSERT INTO dbo.user_org_values (org_type_id, name) VALUES (@orgType, N'Ομάδα Πωλήσεων'); SET @orgValue = SCOPE_IDENTITY();
INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id) VALUES (@pilot, @orgType, @orgValue), (@outsider, @orgType, @orgValue);

SELECT @pilot, @outsider, @report, @unknown, @pilotEvent, @outsiderEvent, @pilotShareEvent, @outsiderShareEvent,
       @pilotSession, @outsiderSession, @outsiderComment, @pilotReply, @pilotMail, @outsiderMail,
       @sharedCall, @outsiderOnlyCall, @externalCall, @url, @term, @address, @callType, @outsiderOrganisedCall, @orgType, @licenceType,
       @outsiderTerm, @outsiderAddress, @sharedPhrase, @outsiderPhrase, @costRun, @pilotObject, @outsiderObject;";

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
                        OrgType = reader.GetInt32(22), LicenceType = reader.GetInt32(23),
                        OutsiderSearchTerm = reader.GetInt32(24), OutsiderAddress = reader.GetInt32(25),
                        SharedPhrase = reader.GetInt32(26), OutsiderPhrase = reader.GetInt32(27),
                        CostRun = reader.GetGuid(28), PilotObject = reader.GetGuid(29), OutsiderObject = reader.GetGuid(30),
                    };
                }
            }
        }

        /// <summary>Removes everything <see cref="SeedAsync"/> created that is still there, children first.</summary>
        private static async Task CleanupAsync(Seed seed, params int[] extraUsers)
        {
            if (seed == null) return;
            var users = string.Join(",", new[] { seed.Pilot, seed.Outsider, seed.Report }.Concat(extraUsers));
            var objects = $"'{seed.PilotObject}', '{seed.OutsiderObject}'";
            await ExecuteAsync($@"
DELETE FROM dbo.copilot_cost_services WHERE object_id IN ({objects});
DELETE FROM dbo.copilot_cost_unevaluated WHERE object_id IN ({objects});
DELETE FROM dbo.copilot_cost_observations WHERE object_id IN ({objects});
DELETE FROM dbo.copilot_cost_history WHERE object_id IN ({objects}) OR run_id = '{seed.CostRun}';
DELETE FROM dbo.copilot_cost_work WHERE run_id = '{seed.CostRun}';
DELETE FROM dbo.copilot_cost_runs WHERE run_id = '{seed.CostRun}';
DELETE k FROM dbo.copilot_interaction_keywords k JOIN dbo.copilot_interactions i ON i.id = k.interaction_id WHERE i.user_id IN ({users});
DELETE FROM dbo.copilot_interactions WHERE user_id IN ({users});
DELETE FROM dbo.copilot_interaction_sessions WHERE user_id IN ({users});
DELETE FROM dbo.keywords WHERE id IN ({seed.SharedPhrase}, {seed.OutsiderPhrase});
DELETE m FROM dbo.call_session_call_modalities m JOIN dbo.call_sessions s ON s.id = m.call_session_id JOIN dbo.call_records r ON r.id = s.call_record_id WHERE r.call_type_id = {seed.CallType};
DELETE s FROM dbo.call_sessions s JOIN dbo.call_records r ON r.id = s.call_record_id WHERE r.call_type_id = {seed.CallType};
DELETE f FROM dbo.call_feedback f JOIN dbo.call_records r ON r.id = f.call_id WHERE r.call_type_id = {seed.CallType};
DELETE FROM dbo.call_records WHERE call_type_id = {seed.CallType};
DELETE FROM dbo.call_types WHERE id = {seed.CallType};
DELETE FROM dbo.user_license_history WHERE user_id IN ({users});
DELETE FROM dbo.user_license_type_lookups WHERE user_id IN ({users});
DELETE FROM dbo.license_types WHERE id = {seed.LicenceType};
DELETE FROM dbo.teams_user_activity_log WHERE user_id IN ({users});
DELETE r FROM dbo.sent_email_recipients r JOIN dbo.sent_emails e ON e.id = r.sent_email_id WHERE e.user_id IN ({users});
DELETE FROM dbo.sent_emails WHERE user_id IN ({users});
DELETE FROM dbo.email_addresses WHERE id IN ({seed.Address}, {seed.OutsiderAddress});
UPDATE dbo.page_comments SET parent_id = NULL WHERE url_id = {seed.Url};
DELETE FROM dbo.page_comments WHERE url_id = {seed.Url};
DELETE FROM dbo.page_likes WHERE url_id = {seed.Url};
DELETE FROM dbo.searches WHERE search_term_id IN ({seed.SearchTerm}, {seed.OutsiderSearchTerm});
DELETE FROM dbo.hits WHERE url_id = {seed.Url};
DELETE FROM dbo.sessions WHERE user_id IN ({users});
DELETE FROM dbo.search_terms WHERE id IN ({seed.SearchTerm}, {seed.OutsiderSearchTerm});
DELETE FROM dbo.urls WHERE id = {seed.Url};
DELETE FROM dbo.event_meta_power_app_share WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.copilot_chats WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.event_meta_general WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.event_meta_exchange WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
DELETE FROM dbo.event_meta_azure_ad WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id IN ({users}));
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

        /// <summary>A queued purge that removes exactly <paramref name="userIds"/> (see <see cref="NewEngine"/>), confirmed for that many.</summary>
        private Task<int> StartJobWithCandidatesAsync(params int[] userIds) => StartJobWithCandidatesAsync(null, userIds);

        /// <summary>The same, confirmed against the filter with <paramref name="filterFingerprint"/>.</summary>
        private async Task<int> StartJobWithCandidatesAsync(string filterFingerprint, int[] userIds, string membershipFingerprint = null)
        {
            _candidates = userIds;
            var job = await State.CreateAsync("admin@contoso.local", filterFingerprint, 1, userIds.Length, membershipFingerprint);
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
            await Exists("Search text only the outsider used goes with their search...", "SELECT COUNT(*) FROM dbo.search_terms WHERE id = @id", false, ("@id", s.OutsiderSearchTerm));
            await Exists("...while search text the pilot used too stays.", "SELECT COUNT(*) FROM dbo.search_terms WHERE id = @id", true, ("@id", s.SearchTerm));

            await Exists("The outsider's own email address goes once no email refers to it...", "SELECT COUNT(*) FROM dbo.email_addresses WHERE id = @id", false, ("@id", s.OutsiderAddress));
            await Exists("...while an address the pilot's email uses stays.", "SELECT COUNT(*) FROM dbo.email_addresses WHERE id = @id", true, ("@id", s.Address));
            await Exists("A prompt phrase only the outsider used goes...", "SELECT COUNT(*) FROM dbo.keywords WHERE id = @id", false, ("@id", s.OutsiderPhrase));
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.copilot_interaction_keywords WHERE keyword_id = @id", ("@id", s.SharedPhrase)),
                "...while one the pilot used too stays, used by the pilot only.");
            await Exists("The outsider's Copilot interactions are gone.", "SELECT COUNT(*) FROM dbo.copilot_interactions WHERE user_id = @id", false, ("@id", s.Outsider));

            foreach (var table in new[] { "copilot_cost_observations", "copilot_cost_services", "copilot_cost_history", "copilot_cost_work" })
            {
                Assert.AreEqual(0, await CountAsync($"SELECT COUNT(*) FROM dbo.{table} WHERE object_id = @o", ("@o", s.OutsiderObject)),
                    $"The outsider's Copilot credit history ({table}) is gone, linked to their user row or not.");
                Assert.AreEqual(1, await CountAsync($"SELECT COUNT(*) FROM dbo.{table} WHERE object_id = @o", ("@o", s.PilotObject)),
                    $"The pilot's Copilot credit history ({table}) stays.");
            }
            await Exists("The outsider's unevaluated Copilot services are gone.", "SELECT COUNT(*) FROM dbo.copilot_cost_unevaluated WHERE object_id = @o", false, ("@o", s.OutsiderObject));

            await Exists("The outsider's comment is gone.", "SELECT COUNT(*) FROM dbo.page_comments WHERE id = @id", false, ("@id", s.OutsiderComment));
            Assert.AreEqual(DBNull.Value, await ScalarAsync("SELECT parent_id FROM dbo.page_comments WHERE id = @id", ("@id", s.PilotReply)),
                "The pilot's reply stays, no longer linked to the removed comment.");
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.page_likes WHERE url_id = @u", ("@u", s.Url)), "Only the pilot's like stays.");

            await Exists("The outsider's sent email is gone.", "SELECT COUNT(*) FROM dbo.sent_emails WHERE id = @id", false, ("@id", s.OutsiderMail));
            Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.sent_email_recipients WHERE sent_email_id = @id", ("@id", s.PilotMail)),
                "The pilot's email keeps its recipient.");
            await Exists("The outsider's usage rows are gone.", "SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = @id", false, ("@id", s.Outsider));
            await Exists("The pilot's usage rows stay.", "SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = @id", true, ("@id", s.Pilot));
            await Exists("The outsider's licence history is gone.", "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @id", false, ("@id", s.Outsider));
            await Exists("The pilot's licence history stays.", "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @id", true, ("@id", s.Pilot));
            await Exists("The outsider's current licence lookup is gone.", "SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = @id", false, ("@id", s.Outsider));
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

        /// <summary>
        /// The administrator confirmed removing one person, but by the time the purge works out who to remove there are
        /// two: the importers added a user, or the groups shrank, after the confirmation. It removes nobody - not even the
        /// one it was confirmed for, nor any unlinked credit rows - rather than remove somebody nobody agreed to.
        /// </summary>
        [TestMethod]
        public async Task Purge_RemovesNobody_WhenMorePeopleAreOutsideTheScopeThanWereConfirmed()
        {
            Seed seed = null;
            var newcomer = 0;
            try
            {
                seed = await SeedAsync();
                newcomer = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-newcomer@contoso.local")));
                _candidates = new[] { seed.Outsider, newcomer };
                var job = await State.CreateAsync("admin@contoso.local", null, 1, confirmedOutsideScopeCount: 1);

                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await NewEngine().RunAsync(job.Id, scopeProvider: null));

                var ended = await State.GetAsync(job.Id);
                Assert.AreEqual(UserScopePurgeStates.Failed, ended.State);
                Assert.AreEqual(UserScopePurgeErrorCodes.PurgeExceedsConfirmed, ended.ErrorCode);
                Assert.AreEqual(0, ended.CandidateCount, "Nobody was chosen...");
                Assert.AreEqual(0, ended.RowsAffected.Count, "...and nothing was removed:");
                Assert.AreEqual(2, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", seed.Outsider)));
                Assert.AreEqual(2, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id IN (@a, @b)", ("@a", seed.Outsider), ("@b", newcomer)));
                Assert.IsFalse(await Database.IsPurgeRunningAsync(), "It let go of the purge lock.");

                // Confirmed for both, it goes ahead.
                var confirmedForBoth = await State.CreateAsync("admin@contoso.local", null, 1, confirmedOutsideScopeCount: 2);
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await NewEngine().RunAsync(confirmedForBoth.Id, scopeProvider: null));
                await AssertPurgedAsync(seed);
            }
            finally
            {
                await CleanupAsync(seed, newcomer);
            }
        }

        /// <summary>
        /// A purge confirmed for one person starts again after a restart and finds a second outside the scope. The total it
        /// would have removed is over what was confirmed, so it stops where it was, removing nobody more.
        /// </summary>
        [TestMethod]
        public async Task Purge_StartedAgainAfterARestart_NeverRemovesMoreThanWasConfirmedInAll()
        {
            Seed seed = null;
            var newcomer = 0;
            try
            {
                seed = await SeedAsync();
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);

                var hostStopping = new CancellationTokenSource();
                var first = NewEngine();
                first.AfterWindow = (step, window) =>
                {
                    if (UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity) hostStopping.Cancel();
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await first.RunAsync(jobId, null, null, hostStopping.Token));
                var sessionsBefore = await CountAsync("SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @id", ("@id", seed.Outsider));

                newcomer = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-newcomer@contoso.local")));
                _candidates = new[] { seed.Outsider, newcomer };
                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await NewEngine().RunAsync(jobId, null));

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeErrorCodes.PurgeExceedsConfirmed, job.ErrorCode);
                Assert.AreEqual(UserScopePurgePhases.WebActivity, job.Phase, "It reports where it had got to.");
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @id", ("@id", seed.Outsider)),
                    "What it removed before the restart stays removed...");
                Assert.AreEqual(sessionsBefore, await CountAsync("SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @id", ("@id", seed.Outsider)),
                    "...but it removed nothing after it.");
                Assert.AreEqual(2, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id IN (@a, @b)", ("@a", seed.Outsider), ("@b", newcomer)));
            }
            finally
            {
                await CleanupAsync(seed, newcomer);
            }
        }

        /// <summary>
        /// A purge that has to read the groups again - here, starting again after a restart - carries on while they resolve
        /// to the same people, however they come back ordered or cased, and stops, removing nobody more, once they resolve
        /// to anyone else: the people it would now remove are not the ones the administrator was shown.
        /// </summary>
        [TestMethod]
        public async Task Purge_ThatReadsTheGroupsAgain_StopsWhenTheyNoLongerResolveToThePeopleItWasConfirmedAgainst()
        {
            Seed seed = null;
            try
            {
                seed = await SeedAsync();
                var filter = new UserGroupsFilterModel("Copilot pilot");
                string idA = Guid.NewGuid().ToString(), idB = Guid.NewGuid().ToString(), idC = Guid.NewGuid().ToString();
                var confirmed = new UserScopeMembers();
                confirmed.Add(idA, "pilot.a@contoso.local", "pilot.a@contoso.local");
                confirmed.Add(idB, "pilot.b@contoso.local", "Pilot.B@Contoso.local");
                var jobId = await StartJobWithCandidatesAsync(filter.Fingerprint, new[] { seed.Outsider }, confirmed.MembershipFingerprint);

                IUserImportScopeProvider ProviderFor(params (string Id, string Upn)[] people)
                {
                    var members = new UserScopeMembers();
                    foreach (var person in people) members.Add(person.Id, person.Upn, person.Upn);
                    return new FixedUserImportScopeProvider(TestUserScopes.Of("pilot.a@contoso.local"),
                        UserImportScopeResolution.Resolved(members, null, null, DateTime.UtcNow), filter);
                }

                // The same two people, read back in another order and case: the purge runs.
                var hostStopping = new CancellationTokenSource();
                var first = NewEngine();
                first.AfterWindow = (step, window) =>
                {
                    if (UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.WebActivity) hostStopping.Cancel();
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused,
                    await first.RunAsync(jobId, ProviderFor((idB.ToUpperInvariant(), "PILOT.B@CONTOSO.LOCAL"), (idA, "Pilot.A@contoso.local")), null, hostStopping.Token));
                var sessionsBefore = await CountAsync("SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @id", ("@id", seed.Outsider));

                // One of them has left the groups and somebody else has joined: it stops.
                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await NewEngine().RunAsync(jobId, ProviderFor((idA, "pilot.a@contoso.local"), (idC, "pilot.c@contoso.local"))));

                var job = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeStates.Failed, job.State);
                Assert.AreEqual(UserScopePurgeErrorCodes.ScopeMembershipChanged, job.ErrorCode);
                Assert.AreEqual(sessionsBefore, await CountAsync("SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @id", ("@id", seed.Outsider)),
                    "It removed nothing after the restart...");
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
                var job = await State.CreateAsync("admin@contoso.local", filter.Fingerprint, 1, 1);

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
        /// The Copilot credit history preview reads every directory user, so it holds readings about people who never got a
        /// user row. The snapshot finds the ones outside the scope by object id - and only those: someone in scope who has no
        /// user row yet keeps their readings, as does anyone with a user row (the candidates' own are added from their row).
        /// </summary>
        [TestMethod]
        public async Task Snapshot_FindsCreditHistoryOfPeopleOutsideTheScopeWithNoUserRow()
        {
            var token = "usp" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Guid outsider = Guid.NewGuid(), member = Guid.NewGuid(), run = Guid.NewGuid();
            try
            {
                await ExecuteAsync($@"
INSERT INTO dbo.copilot_cost_runs (run_id, generation, inventory_date, last_served) VALUES ('{run}', '{token}', '2026-09-01', SYSDATETIMEOFFSET());
INSERT INTO dbo.copilot_cost_work (run_id, object_id, next_attempt) VALUES ('{run}', '{outsider}', SYSDATETIMEOFFSET()), ('{run}', '{member}', SYSDATETIMEOFFSET());
INSERT INTO dbo.copilot_cost_history (run_id, publication_id, observation_date, object_id, observed_at, quality, bundle)
VALUES ('{run}', NEWID(), '2026-09-01', '{outsider}', SYSDATETIMEOFFSET(), 2, N'{{}}');");
                var members = new UserScopeMembers();
                members.Add(member.ToString(), token + "-member@contoso.local", token + "-member@contoso.local");
                var scope = UserImportScope.ForMembers(members, "test");

                var found = await NewEngine().FindUnlinkedCostObjectsOutsideScopeAsync(scope);

                Assert.AreEqual(1, found.Count(id => id == outsider), "Found once, though it is in two of the preview's tables.");
                CollectionAssert.DoesNotContain(found, member, "In scope, though it has no user row.");
            }
            finally
            {
                await ExecuteAsync($"DELETE FROM dbo.copilot_cost_history WHERE run_id = '{run}'; DELETE FROM dbo.copilot_cost_work WHERE run_id = '{run}'; DELETE FROM dbo.copilot_cost_runs WHERE run_id = '{run}';");
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

            var first = await state.CreateAsync("admin@contoso.local", "fingerprint", 5, 5);
            Assert.AreEqual(UserScopePurgeStates.Queued, first.State);
            Assert.AreEqual(UserScopePurgePlan.StepCount, first.StepCount);
            var second = await state.CreateAsync("admin@contoso.local", "fingerprint", 5, 5);
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
            Assert.IsTrue((await restarted.CreateAsync("admin@contoso.local", "fingerprint", 5, 5)).Id > second.Id,
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
        /// The one-at-a-time fallback - taken when something new about one of the users makes the batch delete fail - has to
        /// keep the audit-event check in each delete, not only in the select that chose them: an audit event (no foreign key)
        /// that arrives between the two would otherwise be orphaned.
        /// </summary>
        [TestMethod]
        public async Task Purge_OneAtATimeFallback_KeepsSomeoneWhoseAuditEventArrivesJustBeforeTheirDelete()
        {
            Seed seed = null;
            int lateSession = 0, lateEvent = 0;
            try
            {
                seed = await SeedAsync();
                lateSession = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-late-session@contoso.local")));
                lateEvent = Convert.ToInt32(await ScalarAsync("INSERT INTO dbo.users (user_name) OUTPUT inserted.id VALUES (@n);", ("@n", seed.Token + "-late-event@contoso.local")));
                var jobId = await StartJobWithCandidatesAsync(seed.Outsider, lateSession, lateEvent);

                bool sessionArrived = false, eventArrived = false;
                var engine = NewEngine();
                engine.AfterWindow = async (step, window) =>
                {
                    if (!sessionArrived && UserScopePurgePlan.PhaseOf(step) == UserScopePurgePhases.PageComments)
                    {
                        // A web session (a foreign key) sends the users step down the one-at-a-time path.
                        sessionArrived = true;
                        await ExecuteAsync($"INSERT INTO dbo.sessions (user_id) VALUES ({lateSession});");
                    }
                };
                engine.BeforeOneByOneDelete = async id =>
                {
                    if (id == lateEvent && !eventArrived)
                    {
                        eventArrived = true;
                        await ExecuteAsync($"INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES (NEWID(), SYSUTCDATETIME(), {lateEvent});");
                    }
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(jobId, null));

                Assert.IsTrue(eventArrived, "The audit event arrived after the fallback chose who to delete.");
                var job = await State.GetAsync(jobId);
                Assert.AreEqual(1, job.UsersDeleted, "The outsider is removed...");
                Assert.AreEqual(2, job.UsersSkipped, "...and both late arrivals are kept and counted.");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.users WHERE id = @id", ("@id", lateEvent)),
                    "Kept, so the audit event that arrived just before its delete is not orphaned.");
                await AssertPurgedAsync(seed);
            }
            finally
            {
                await ExecuteAsync($"DELETE FROM dbo.audit_events WHERE user_id IN ({lateSession}, {lateEvent}); DELETE FROM dbo.sessions WHERE user_id IN ({lateSession}, {lateEvent});");
                await CleanupAsync(seed, lateSession, lateEvent);
            }
        }

        /// <summary>
        /// Upgrades keep a retired feature's tables, with their original foreign keys, wherever they still held data. The
        /// purge has to clear those too: an extended property blocks its event's metadata, a Stream event blocks the audit
        /// event, and a Viva Engage message blocks its sender - for ever, however often the purge runs. Other people's
        /// replies to the person's messages stay, unlinked.
        /// </summary>
        [TestMethod]
        public async Task Purge_ClearsWhatRetiredFeaturesKept_AndKeepsOtherPeoplesReplies()
        {
            Seed seed = null;
            try
            {
                await DropRetainedTablesAsync();
                await ExecuteAsync(@"
CREATE TABLE dbo.audit_event_exchange_props (id int IDENTITY NOT NULL PRIMARY KEY CLUSTERED, prop_name_id int NOT NULL, prop_val_id int NOT NULL,
    event_id uniqueidentifier NULL CONSTRAINT FK_test_exchange_props FOREIGN KEY REFERENCES dbo.event_meta_exchange (event_id));
CREATE TABLE dbo.audit_event_azure_ad_props (id int IDENTITY NOT NULL PRIMARY KEY CLUSTERED, prop_name_id int NOT NULL, prop_val_id int NOT NULL,
    event_id uniqueidentifier NULL CONSTRAINT FK_test_azure_ad_props FOREIGN KEY REFERENCES dbo.event_meta_azure_ad (event_id));
CREATE TABLE dbo.event_meta_stream (event_id uniqueidentifier NOT NULL PRIMARY KEY CLUSTERED
    CONSTRAINT FK_test_event_meta_stream FOREIGN KEY REFERENCES dbo.audit_events (id), o365_client_application_id int NOT NULL, video_id int NOT NULL);
CREATE TABLE dbo.yammer_messages (id int IDENTITY NOT NULL PRIMARY KEY CLUSTERED,
    sender_id int NOT NULL CONSTRAINT FK_test_yammer_sender FOREIGN KEY REFERENCES dbo.users (id), created datetime NOT NULL,
    yammer_msg_id bigint NOT NULL, reply_to_yammer_msg_id bigint NULL, likes_count int NOT NULL, followers_count int NOT NULL,
    parent_msg_id int NULL CONSTRAINT FK_test_yammer_parent FOREIGN KEY REFERENCES dbo.yammer_messages (id));
CREATE TABLE dbo.yammer_msg_to_stream (id int IDENTITY NOT NULL PRIMARY KEY CLUSTERED,
    message_id int NOT NULL CONSTRAINT FK_test_yammer_stream FOREIGN KEY REFERENCES dbo.yammer_messages (id), stream_id int NOT NULL);
CREATE TABLE dbo.teams_addons_user_installed_log (id int IDENTITY NOT NULL PRIMARY KEY CLUSTERED, date datetime NOT NULL, addon_id int NOT NULL,
    user_id int NOT NULL CONSTRAINT FK_test_addons_user FOREIGN KEY REFERENCES dbo.users (id) ON DELETE CASCADE);");

                seed = await SeedAsync();
                await ExecuteAsync($@"
DECLARE @outsiderExchange uniqueidentifier = NEWID(), @outsiderEntra uniqueidentifier = NEWID(), @outsiderStream uniqueidentifier = NEWID(),
        @pilotExchange uniqueidentifier = NEWID();
INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES
    (@outsiderExchange, SYSUTCDATETIME(), {seed.Outsider}), (@outsiderEntra, SYSUTCDATETIME(), {seed.Outsider}),
    (@outsiderStream, SYSUTCDATETIME(), {seed.Outsider}), (@pilotExchange, SYSUTCDATETIME(), {seed.Pilot});
INSERT INTO dbo.event_meta_exchange (event_id) VALUES (@outsiderExchange), (@pilotExchange);
INSERT INTO dbo.event_meta_azure_ad (event_id) VALUES (@outsiderEntra);
INSERT INTO dbo.audit_event_exchange_props (prop_name_id, prop_val_id, event_id) VALUES (1, 1, @outsiderExchange), (1, 1, @pilotExchange);
INSERT INTO dbo.audit_event_azure_ad_props (prop_name_id, prop_val_id, event_id) VALUES (1, 1, @outsiderEntra);
INSERT INTO dbo.event_meta_stream (event_id, o365_client_application_id, video_id) VALUES (@outsiderStream, 1, 1);

DECLARE @outsiderMessage int, @pilotMessage int;
INSERT INTO dbo.yammer_messages (sender_id, created, yammer_msg_id, likes_count, followers_count) VALUES ({seed.Outsider}, SYSUTCDATETIME(), 1, 0, 0);
SET @outsiderMessage = SCOPE_IDENTITY();
INSERT INTO dbo.yammer_messages (sender_id, created, yammer_msg_id, reply_to_yammer_msg_id, likes_count, followers_count, parent_msg_id)
VALUES ({seed.Pilot}, SYSUTCDATETIME(), 2, 1, 0, 0, @outsiderMessage);
SET @pilotMessage = SCOPE_IDENTITY();
INSERT INTO dbo.yammer_messages (sender_id, created, yammer_msg_id, reply_to_yammer_msg_id, likes_count, followers_count, parent_msg_id)
VALUES ({seed.Outsider}, SYSUTCDATETIME(), 3, 1, 0, 0, @outsiderMessage);
INSERT INTO dbo.yammer_msg_to_stream (message_id, stream_id) VALUES (@outsiderMessage, 1), (@pilotMessage, 1);
INSERT INTO dbo.teams_addons_user_installed_log (date, addon_id, user_id) VALUES (SYSUTCDATETIME(), 1, {seed.Outsider}), (SYSUTCDATETIME(), 1, {seed.Pilot});");

                var jobId = await StartJobWithCandidatesAsync(seed.Outsider);
                var log = new CapturingLogger();
                var engine = new UserScopePurgeEngine(Database, State, log)
                {
                    RetryDelayOverride = TimeSpan.Zero,
                    StateCheckInterval = TimeSpan.Zero,
                    CandidatesOverride = _candidates,
                };
                var outcome = await engine.RunAsync(jobId, null);
                var job = await State.GetAsync(jobId);
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, outcome, $"{job.ErrorCode}: {job.ErrorDetail}{Environment.NewLine}{log}");
                Assert.AreEqual(1, job.UsersDeleted, "The outsider is removed, not kept for ever by what a retired feature left.");
                Assert.AreEqual(0, job.UsersSkipped);
                await AssertPurgedAsync(seed);

                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.audit_event_exchange_props"), "Only the pilot's Exchange property stays.");
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.audit_event_azure_ad_props"));
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.event_meta_stream"));
                Assert.AreEqual(0, await CountAsync("SELECT COUNT(*) FROM dbo.yammer_messages WHERE sender_id = @id", ("@id", seed.Outsider)));
                Assert.AreEqual(DBNull.Value, await ScalarAsync("SELECT parent_msg_id FROM dbo.yammer_messages WHERE sender_id = @id", ("@id", seed.Pilot)),
                    "The pilot's reply stays, no longer linked to the removed message.");
                Assert.AreEqual(1, await CountAsync("SELECT COUNT(*) FROM dbo.yammer_msg_to_stream"), "Only the pilot's message keeps its link.");
                Assert.AreEqual(seed.Pilot.ToString(), await ScalarAsync("SELECT STRING_AGG(user_id, ',') FROM dbo.teams_addons_user_installed_log"),
                    "Only the pilot's Teams add-on install stays.");
                Assert.AreEqual(2L, job.RowsAffected["yammer_messages.parent_msg_id"],
                    "The pilot's reply and the outsider's own are unlinked before the outsider's messages go.");
            }
            finally
            {
                await DropRetainedTablesAsync();
                await CleanupAsync(seed);
            }
        }

        private static Task DropRetainedTablesAsync() => ExecuteAsync(@"
IF OBJECT_ID(N'dbo.yammer_msg_to_stream', N'U') IS NOT NULL DROP TABLE dbo.yammer_msg_to_stream;
IF OBJECT_ID(N'dbo.yammer_messages', N'U') IS NOT NULL DROP TABLE dbo.yammer_messages;
IF OBJECT_ID(N'dbo.event_meta_stream', N'U') IS NOT NULL DROP TABLE dbo.event_meta_stream;
IF OBJECT_ID(N'dbo.audit_event_exchange_props', N'U') IS NOT NULL DROP TABLE dbo.audit_event_exchange_props;
IF OBJECT_ID(N'dbo.audit_event_azure_ad_props', N'U') IS NOT NULL DROP TABLE dbo.audit_event_azure_ad_props;
IF OBJECT_ID(N'dbo.teams_addons_user_installed_log', N'U') IS NOT NULL DROP TABLE dbo.teams_addons_user_installed_log;");

        /// <summary>
        /// A lookup row is only deleted once nothing refers to it, so every column that refers to it has to be checked. A
        /// new table referring to search text, prompt phrases or email addresses that nobody added here fails this.
        /// </summary>
        [TestMethod]
        public async Task Lookups_AreCheckedAgainstEveryColumnThatRefersToThem()
        {
            var checkedReferences = UserScopePurgePlan.Steps.Where(s => s.Lookup != null)
                .GroupBy(s => s.Lookup.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.SelectMany(s => s.Lookup.References).ToList(), StringComparer.OrdinalIgnoreCase);
            var addresses = UserScopePurgePlan.Steps.Single(s => s.Table == "email_addresses");
            CollectionAssert.AreEquivalent(new[] { "keywords", "search_terms" }, checkedReferences.Keys.ToList());

            var problems = new List<string>();
            foreach (var (child, column, parent) in await ForeignKeysAsync(new[] { "keywords", "search_terms", "email_addresses" }))
            {
                var reference = $"{child}.{column}";
                if (parent == "email_addresses")
                {
                    if (!addresses.Match.Contains($"FROM dbo.{child} x WITH (READCOMMITTEDLOCK) WHERE x.{column} = t.id"))
                        problems.Add($"{reference} is not checked before an email address is deleted");
                }
                else if (!checkedReferences[parent].Contains(reference, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{reference} is not checked before a {parent} row is deleted");
                }
            }
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static async Task<List<(string Child, string Column, string Parent)>> ForeignKeysAsync(IEnumerable<string> parents)
        {
            var found = new List<(string, string, string)>();
            var wanted = new HashSet<string>(parents, StringComparer.OrdinalIgnoreCase);
            using (var connection = new SqlConnection(ConnectionString))
            using (var command = new SqlCommand(@"
SELECT OBJECT_NAME(fk.parent_object_id), c.name, OBJECT_NAME(fk.referenced_object_id)
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id;", connection))
            {
                await connection.OpenAsync();
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        if (wanted.Contains(reader.GetString(2)))
                        {
                            found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                        }
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Columns that name a person but that a purge deliberately leaves alone, each with the reason. Everything else that
        /// names a person must be taken care of by a step of the plan
        /// (<see cref="EveryTableNamingAPerson_IsPurgedOrDeliberatelyExempt"/>). Adding to this list means the portal's purge
        /// description has to say what stays.
        /// </summary>
        private static readonly Dictionary<string, string> PurgeExemptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // An in-scope sender's email is their activity, recipients included: an address stays while any email refers to it.
            ["sent_email_recipients.recipient_address_id"] = "the recipients of someone else's email",
            // Exchange's own id for the audited item - a mailbox path or message id - not a Microsoft Entra object id. The
            // person's own events go with them; someone else's audit record of their mailbox is that person's record.
            ["event_meta_exchange.object_id"] = "the item an Exchange audit event is about",
            // Who last saved a portal-wide filter, who installed the solution, who excluded someone from reclaim
            // recommendations and who started a user-organisation import: administrators' records of what they did, not
            // analytics about them. An exclusion itself goes with the person it excludes.
            ["portal_global_filters.modified_by"] = "an administrator's change to a portal setting",
            ["sys_configs.installed_by_username"] = "who ran the installer",
            ["copilot_adoption_reclaim_exclusions.excluded_by"] = "the administrator who excluded someone from reclaim recommendations",
            ["user_org_import_jobs.started_by"] = "the administrator who started a user-organisation import",
            // A count of the rows in an import whose user principal name matched nobody - not a name.
            ["user_org_import_jobs.rows_unknown_upn"] = "a count",
            // A user-organisation CSV import: its rows wait in staging until it is applied, and what it changed waits in
            // user_org_import_changes only until the change log (Azure Table storage) has it. That log is the record of who
            // an import changed, and is meant to outlive the people it names.
            ["user_org_import_staging.upn"] = "a user-organisation file waiting to be applied",
            ["user_org_import_changes.upn"] = "what a user-organisation import changed, until it reaches the change log",
            ["user_org_import_changes.user_id"] = "what a user-organisation import changed, until it reaches the change log",
        };

        /// <summary>
        /// Every column that refers to a user row or names a person - by foreign key, or by holding a user id, user
        /// principal name, email address or Microsoft Entra object id - is either taken care of by a step of the plan, or
        /// listed in <see cref="PurgeExemptions"/> with the reason it stays. A new per-person table that nobody added to the
        /// plan fails here, whether or not it has a foreign key to users.
        /// </summary>
        [TestMethod]
        public async Task EveryTableNamingAPerson_IsPurgedOrDeliberatelyExempt()
        {
            var handled = new HashSet<string>(UserScopePurgePlan.Steps.SelectMany(s => s.Handles), StringComparer.OrdinalIgnoreCase);
            var personColumn = new System.Text.RegularExpressions.Regex(
                "^(user_id|.+_user_id|user_name|.+_username|upn|.+_upn|mail|email|.+_email|address|.+_address_id|azure_ad_id|entra_object_id|object_id|" +
                "sender_id|owner_id|organizer_id|manager_id|.+_by)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var connection = new SqlConnection(ConnectionString))
            using (var command = new SqlCommand(@"
SELECT s.name, t.name, c.name,
       CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc
                         WHERE fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
                           AND fkc.referenced_object_id = OBJECT_ID(N'dbo.users')) THEN 1 ELSE 0 END
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
WHERE t.is_ms_shipped = 0 AND t.name <> N'__MigrationHistory';", connection))
            {
                await connection.OpenAsync();
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var schema = reader.GetString(0);
                        var table = reader.GetString(1);
                        var column = reader.GetString(2);
                        var refersToUsers = reader.GetInt32(3) == 1;
                        if (refersToUsers || personColumn.IsMatch(column))
                        {
                            found.Add(schema == "dbo" ? $"{table}.{column}" : $"{schema}.{table}.{column}");
                        }
                    }
                }
            }

            var problems = found.Where(c => !handled.Contains(c) && !PurgeExemptions.ContainsKey(c))
                .Select(c => $"{c} names a person, but no purge step takes care of it and it is not in PurgeExemptions").ToList();
            problems.AddRange(PurgeExemptions.Keys.Where(c => !found.Contains(c))
                .Select(c => $"PurgeExemptions lists {c}, which is not a person column in this database any more"));
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            Assert.IsTrue(found.Count > 30, $"The schema scan found only {found.Count} person column(s); it is not reading the analytics database.");
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
                // The email-address step is not a delete of rows that something may still point at: it only deletes addresses
                // no email refers to, which Lookups_AreCheckedAgainstEveryColumnThatRefersToThem checks.
                if (steps[i].Kind == UserScopePurgeStepKind.Delete || steps[i].Kind == UserScopePurgeStepKind.Users
                    || steps[i].Kind == UserScopePurgeStepKind.DeleteByObject)
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
        /// type): a window is a range of its values. A step that walks the purge's own list of people or object ids instead
        /// needs its table's object id within the first two columns of one of its indexes, which its joins seek. Tables
        /// that only an upgraded database keeps are tested where they are created.
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
                    using (var exists = new SqlCommand("SELECT OBJECT_ID(@table, N'U');", connection))
                    {
                        exists.Parameters.AddWithValue("@table", "dbo." + step.Table);
                        if (await exists.ExecuteScalarAsync() is DBNull)
                        {
                            Assert.IsTrue(step.Phase == UserScopePurgePhases.RetiredFeatures || step.Phase == UserScopePurgePhases.AuditEvents,
                                $"{step}: dbo.{step.Table} is not in the database, but is not a table only a retired feature kept.");
                            continue;
                        }
                    }

                    if (step.Kind == UserScopePurgeStepKind.DeleteByObject)
                    {
                        using (var command = new SqlCommand(@"
SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE ic.object_id = OBJECT_ID(@table) AND ic.key_ordinal BETWEEN 1 AND 2 AND c.name = N'object_id';", connection))
                        {
                            command.Parameters.AddWithValue("@table", "dbo." + step.Table);
                            if ((int)await command.ExecuteScalarAsync() == 0)
                            {
                                problems.Add($"{step}: no index has object_id among its first two key columns");
                            }
                        }
                        continue;
                    }
                    if (step.Kind == UserScopePurgeStepKind.DeleteByCandidate)
                    {
                        continue;
                    }

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
            foreach (var step in UserScopePurgePlan.Steps.Where(s => s.Kind != UserScopePurgeStepKind.Calls && !UserScopePurgeEngine.IsWalkOfTheCandidates(s)))
            {
                foreach (var hasAfter in new[] { false, true })
                {
                    StringAssert.Contains(UserScopePurgeEngine.BuildWindowSql(step, "int", hasAfter), $"FROM dbo.[{step.Table}] t WITH (INDEX(1)) WHERE", step.ToString());
                }
            }
            StringAssert.Contains(UserScopePurgeEngine.BuildCallsWindowSql("int", hasAfter: true), "FROM dbo.call_records r WITH (INDEX(1))", "The calls window's INSERT INTO #calls.");

            // A step that walks the purge's own list seeks every table it joins from the window's keys.
            foreach (var step in UserScopePurgePlan.Steps.Where(UserScopePurgeEngine.IsWalkOfTheCandidates))
            {
                var sql = UserScopePurgeEngine.BuildCandidateWindowSql(step, hasAfter: true);
                StringAssert.Contains(sql, step.Kind == UserScopePurgeStepKind.DeleteByObject ? "FROM #purge_objects o INNER LOOP JOIN" : "FROM #purge_candidates c INNER LOOP JOIN", step.ToString());
                var joins = System.Text.RegularExpressions.Regex.Matches(step.Match, @"\bJOIN\b").Count;
                Assert.AreEqual(joins, System.Text.RegularExpressions.Regex.Matches(step.Match, @"\bLOOP JOIN\b").Count, $"{step}: every join carries LOOP.");
            }
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
