using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Email;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the new <see cref="SentEmailImporter"/> pipeline introduced with the
    /// normalized recipient model and the multi-row raw-SQL persistence path. Tests cover:
    ///   - <c>BuildCandidates</c> (dedup, recipient normalization, distinct-address harvest, skip rules);
    ///   - <c>BuildSentEmailRow</c> (subject truncation, sentiment hydration, FK wiring);
    ///   - <c>BuildSentEmailsInsertSql</c> / <c>BuildSentEmailRecipientsInsertSql</c>
    ///     (parameter shapes, OUTPUT clause, multi-row VALUES, parameter-count guard rails);
    ///   - <c>NullSentEmailSentimentScorer</c> (no-op behavior);
    ///   - <c>FakeSentEmailSourceLoader</c> isn't referenced here to keep the unit-test
    ///     project independent of <c>Tests.FakeDataGen</c>; instead we hand-roll messages.
    /// </summary>
    [TestClass]
    public class SentEmailImporterPipelineTests
    {
        [TestMethod]
        public void BuildDeltaUrl_EscapesMailboxPathWithoutReencodingStoredDeltaToken()
        {
            foreach (var upn in new[] { "alex#sales@contoso.com", "alex'ops@contoso.com", "alex!ops@contoso.com", "alex@contoso.com" })
            {
                var user = new User { UserPrincipalName = upn };
                const string token = "opaque%2Btoken%2Fvalue%3D";
                var url = GraphSentEmailSourceLoader.BuildDeltaUrl(user, token, includeBody: true);
                var uri = new Uri(url);

                Assert.AreEqual(string.Empty, uri.Fragment);
                StringAssert.Contains(uri.AbsolutePath, "/users/" + Uri.EscapeDataString(upn) + "/mailFolders/sentitems/messages/delta");
                StringAssert.Contains(uri.Query, "&$deltatoken=" + token);
                StringAssert.Contains(uri.Query, "sentDateTime,body");
            }
        }

        #region BuildCandidates

        [TestMethod]
        public void BuildCandidates_EmptyInput_ProducesEmptyResultAndNoAddresses()
        {
            var candidates = SentEmailImporter.BuildCandidates(
                new List<GraphSentMessage>(), out var distinct);

            Assert.AreEqual(0, candidates.Count);
            Assert.AreEqual(0, distinct.Count);
        }

        [TestMethod]
        public void BuildCandidates_SkipsMessageWithoutId()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: null, from: "a@x.com", to: new[] { "b@x.com" }),
                Msg(id: "m1", from: "a@x.com", to: new[] { "b@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual("m1", candidates[0].GraphMessageId);
        }

        [TestMethod]
        public void BuildCandidates_SkipsMessageWithoutFromAddress()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: "m1", from: null, to: new[] { "b@x.com" }),
                Msg(id: "m2", from: "a@x.com", to: new[] { "b@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual("m2", candidates[0].GraphMessageId);
        }

        [TestMethod]
        public void BuildCandidates_SkipsMessageWithoutRecipients()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: "m1", from: "a@x.com", to: Array.Empty<string>()),
                Msg(id: "m2", from: "a@x.com", to: null),
                Msg(id: "m3", from: "a@x.com", to: new[] { "b@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual("m3", candidates[0].GraphMessageId);
        }

        [TestMethod]
        public void BuildCandidates_SkipsRecipientsWithoutAddress_AndKeepsRest()
        {
            var msgs = new List<GraphSentMessage>
            {
                new GraphSentMessage
                {
                    Id = "m1",
                    From = Recipient("alice@x.com"),
                    SentDateTime = DateTime.UtcNow,
                    ToRecipients = new List<GraphEmailRecipient>
                    {
                        new GraphEmailRecipient { EmailAddress = new GraphEmailAddress { Address = null } },
                        new GraphEmailRecipient { EmailAddress = new GraphEmailAddress { Address = "" } },
                        Recipient("bob@x.com"),
                    },
                },
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual(1, candidates.Count);
            CollectionAssert.AreEqual(new[] { "bob@x.com" }, candidates[0].RecipientAddresses);
        }

        [TestMethod]
        public void BuildCandidates_DeduplicatesRecipientsCaseInsensitively()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: "m1", from: "a@x.com",
                    to: new[] { "Bob@x.com", "bob@X.com", "carol@x.com", "BOB@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual(1, candidates.Count);
            // Recipients are stored lowercase and deduped, preserving first occurrence order.
            CollectionAssert.AreEqual(
                new[] { "bob@x.com", "carol@x.com" },
                candidates[0].RecipientAddresses);
        }

        [TestMethod]
        public void BuildCandidates_DeduplicatesByMessageIdAcrossInput()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: "m1", from: "a@x.com", to: new[] { "b@x.com" }),
                Msg(id: "m1", from: "a@x.com", to: new[] { "c@x.com" }),
                Msg(id: "M1", from: "a@x.com", to: new[] { "d@x.com" }), // case-insensitive id match
                Msg(id: "m2", from: "a@x.com", to: new[] { "b@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual(2, candidates.Count);
            CollectionAssert.AreEquivalent(
                new[] { "m1", "m2" },
                candidates.Select(c => c.GraphMessageId).ToArray());
        }

        [TestMethod]
        public void BuildCandidates_DistinctAddresses_IncludeFromAndAllRecipientsLowercased()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: "m1", from: "Alice@X.com", to: new[] { "Bob@X.com", "carol@x.com" }),
                Msg(id: "m2", from: "alice@x.com", to: new[] { "dave@x.com", "BOB@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out var distinct);

            Assert.AreEqual(2, candidates.Count);
            CollectionAssert.AreEquivalent(
                new[] { "alice@x.com", "bob@x.com", "carol@x.com", "dave@x.com" },
                distinct.ToArray());
        }

        [TestMethod]
        public void BuildCandidates_LowercasesFromAddressOnCandidate()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg(id: "m1", from: "AlIcE@X.COM", to: new[] { "b@x.com" }),
            };

            var candidates = SentEmailImporter.BuildCandidates(msgs, out _);
            Assert.AreEqual("alice@x.com", candidates[0].FromAddress);
        }

        #endregion

        #region BuildSentEmailRow

        [TestMethod]
        public void BuildSentEmailRow_MapsCoreFields_AndKeepsRecipientsEmpty()
        {
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["sender@x.com"] = 11,
                ["a@x.com"] = 22,
                ["b@x.com"] = 33,
            };
            var sent = new DateTime(2024, 5, 1, 8, 0, 0, DateTimeKind.Utc);
            var msg = new GraphSentMessage
            {
                Id = "m1",
                Subject = "Hello",
                SentDateTime = sent,
                From = Recipient("sender@x.com"),
                ToRecipients = new List<GraphEmailRecipient> { Recipient("a@x.com"), Recipient("b@x.com") },
            };
            var candidates = SentEmailImporter.BuildCandidates(
                new List<GraphSentMessage> { msg }, out _);

            var row = SentEmailImporter.BuildSentEmailRow(
                new User { ID = 7 }, candidates[0], addressIds, sentimentByMessageId: null);

            Assert.AreEqual("m1", row.GraphMessageId);
            Assert.AreEqual("Hello", row.Subject);
            Assert.AreEqual(sent, row.SentDate);
            Assert.AreEqual(11, row.FromAddressID);
            Assert.AreEqual(7, row.UserID);
            Assert.IsNull(row.CognitiveScore);
            // Recipients are persisted in phase B via explicit FKs - row.Recipients is NOT populated here.
            Assert.AreEqual(0, row.Recipients.Count);
        }

        [TestMethod]
        public void BuildSentEmailRow_TruncatesLongSubjectTo1000Chars()
        {
            var longSubject = new string('x', 1500);
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["a@x.com"] = 1,
                ["b@x.com"] = 2,
            };
            var msg = new GraphSentMessage
            {
                Id = "m1",
                Subject = longSubject,
                SentDateTime = DateTime.UtcNow,
                From = Recipient("a@x.com"),
                ToRecipients = new List<GraphEmailRecipient> { Recipient("b@x.com") },
            };
            var candidate = SentEmailImporter.BuildCandidates(
                new List<GraphSentMessage> { msg }, out _).Single();

            var row = SentEmailImporter.BuildSentEmailRow(
                new User { ID = 1 }, candidate, addressIds, sentimentByMessageId: null);

            Assert.AreEqual(1000, row.Subject.Length);
            Assert.IsTrue(row.Subject.All(ch => ch == 'x'));
        }

        [TestMethod]
        public void BuildSentEmailRow_NullSubject_RemainsNull()
        {
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["a@x.com"] = 1,
                ["b@x.com"] = 2,
            };
            var candidate = SentEmailImporter.BuildCandidates(
                new List<GraphSentMessage>
                {
                    new GraphSentMessage
                    {
                        Id = "m1",
                        Subject = null,
                        SentDateTime = DateTime.UtcNow,
                        From = Recipient("a@x.com"),
                        ToRecipients = new List<GraphEmailRecipient> { Recipient("b@x.com") },
                    },
                }, out _).Single();

            var row = SentEmailImporter.BuildSentEmailRow(
                new User { ID = 1 }, candidate, addressIds, sentimentByMessageId: null);

            Assert.IsNull(row.Subject);
        }

        /// <summary>
        /// <c>sent_date</c> is a SQL <c>datetime</c>, which starts in 1753. A message with no sentDateTime used to be stored as
        /// <see cref="DateTime.MinValue"/>, which overflows it - failing that mailbox's save on every cycle, for good, and (with the
        /// rethrow) every Graph section after the sent emails with it. No date is invented now: the message is skipped and counted.
        /// </summary>
        [TestMethod]
        public void BuildCandidates_SkipsMessagesWithoutAStorableSentDate_AndCountsThem()
        {
            var good = Msg("m-good", "a@x.com", new[] { "b@x.com" });
            var noDate = Msg("m-none", "a@x.com", new[] { "b@x.com" });
            noDate.SentDateTime = null;
            var tooOld = Msg("m-old", "a@x.com", new[] { "b@x.com" });
            tooOld.SentDateTime = DateTime.MinValue;
            var tooNew = Msg("m-new", "a@x.com", new[] { "b@x.com" });
            tooNew.SentDateTime = DateTime.MaxValue;
            var oldButStorable = Msg("m-1753", "a@x.com", new[] { "b@x.com" });
            oldButStorable.SentDateTime = new DateTime(1753, 1, 1);

            var candidates = SentEmailImporter.BuildCandidates(new[] { good, noDate, tooOld, tooNew, oldButStorable }, out _, out var skipped);

            CollectionAssert.AreEqual(new[] { "m-good", "m-1753" }, candidates.Select(c => c.GraphMessageId).ToArray());
            Assert.AreEqual(3, skipped);
        }

        [TestMethod]
        public async Task ImportSentEmailsForUser_AMessageWithNoSentDate_DoesNotFailTheMailbox()
        {
            var user = await CreateSavedUserAsync("no-sent-date");
            var suffix = Guid.NewGuid().ToString("N");
            var undated = Msg("no-date-" + suffix, user.Mail, new[] { "recipient-nd@contoso.com" }, "Καλημέρα κόσμε");
            undated.SentDateTime = null;
            var dated = Msg("dated-" + suffix, user.Mail, new[] { "recipient-nd@contoso.com" }, "Καλημέρα κόσμε");
            var source = new RecordingSentEmailSourceLoader((user, undated), (user, dated));
            var committer = new RecordingDeltaTokenCommitter();
            using (var captured = new CapturedAnalyticsLogger())
            {
                await NewImporter(source, committer, logger: captured.Logger).ImportSentEmailsForUser(user);

                Assert.AreEqual(1, await CountSentEmailsAsync("dated-" + suffix), "The message that can be stored is stored.");
                Assert.AreEqual(0, await CountSentEmailsAsync("no-date-" + suffix));
                Assert.IsTrue(committer.TryGetToken(user, out _), "The mailbox's checkpoint still moves: it does not fail every cycle.");
                Assert.IsTrue(captured.Messages(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Warning)
                    .Any(m => m.StartsWith("SentEmailChunk:") && m.Contains("skippedNoSentDate=1")), "The skip is counted in the chunk's line.");
            }
        }

        [TestMethod]
        public void BuildSentEmailRow_HydratesSentimentWhenPresent()
        {
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["a@x.com"] = 1,
                ["b@x.com"] = 2,
            };
            var candidate = SentEmailImporter.BuildCandidates(
                new List<GraphSentMessage>
                {
                    Msg("m1", "a@x.com", new[] { "b@x.com" }),
                }, out _).Single();

            var sentiment = new Dictionary<string, double?> { ["m1"] = 0.83 };

            var row = SentEmailImporter.BuildSentEmailRow(
                new User { ID = 1 }, candidate, addressIds, sentiment);

            Assert.AreEqual(0.83, row.CognitiveScore);
        }

        [TestMethod]
        public void BuildSentEmailRow_LeavesSentimentNullWhenNotInDictionary()
        {
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["a@x.com"] = 1,
                ["b@x.com"] = 2,
            };
            var candidate = SentEmailImporter.BuildCandidates(
                new List<GraphSentMessage>
                {
                    Msg("m1", "a@x.com", new[] { "b@x.com" }),
                }, out _).Single();

            var sentiment = new Dictionary<string, double?> { ["other"] = 0.5 };

            var row = SentEmailImporter.BuildSentEmailRow(
                new User { ID = 1 }, candidate, addressIds, sentiment);

            Assert.IsNull(row.CognitiveScore);
        }

        #endregion

        #region BuildSentEmailsInsertSql

        [TestMethod]
        public void BuildSentEmailsInsertSql_SingleRow_GeneratesExpectedShape()
        {
            var sql = SentEmailImporter.BuildSentEmailsInsertSql(1);
            StringAssert.Contains(sql,
                "INSERT INTO sent_emails (subject, sent_date, graph_message_id, " +
                "cognitive_score, from_address_id, user_id) " +
                "OUTPUT INSERTED.id, INSERTED.graph_message_id VALUES (@p0,@p1,@p2,@p3,@p4,@p5)");
        }

        [TestMethod]
        public void BuildSentEmailsInsertSql_MultipleRows_GeneratesIncrementingParamGroups()
        {
            var sql = SentEmailImporter.BuildSentEmailsInsertSql(3);

            StringAssert.Contains(sql, "(@p0,@p1,@p2,@p3,@p4,@p5)");
            StringAssert.Contains(sql, "(@p6,@p7,@p8,@p9,@p10,@p11)");
            StringAssert.Contains(sql, "(@p12,@p13,@p14,@p15,@p16,@p17)");

            // 3 row groups => exactly two commas between groups (commas inside groups are not VALUES separators)
            int valuesIndex = sql.IndexOf("VALUES ", StringComparison.Ordinal);
            Assert.IsTrue(valuesIndex > 0);
            string valuesSection = sql.Substring(valuesIndex + "VALUES ".Length);
            int groupSeparators = Regex.Matches(valuesSection, @"\),\(").Count;
            Assert.AreEqual(2, groupSeparators);
        }

        [TestMethod]
        public void BuildSentEmailsInsertSql_AlwaysIncludesOutputClause()
        {
            for (int n = 1; n <= 5; n++)
            {
                var sql = SentEmailImporter.BuildSentEmailsInsertSql(n);
                StringAssert.Contains(sql, "OUTPUT INSERTED.id, INSERTED.graph_message_id");
            }
        }

        [TestMethod]
        public void BuildSentEmailsInsertSql_UsesSixParamsPerRow()
        {
            // 50 rows * 6 params = 300 distinct @pN tokens.
            var sql = SentEmailImporter.BuildSentEmailsInsertSql(50);
            int tokenCount = Regex.Matches(sql, @"@p\d+").Count;
            Assert.AreEqual(300, tokenCount);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        public void BuildSentEmailsInsertSql_ZeroRows_Throws()
        {
            SentEmailImporter.BuildSentEmailsInsertSql(0);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        public void BuildSentEmailsInsertSql_NegativeRows_Throws()
        {
            SentEmailImporter.BuildSentEmailsInsertSql(-1);
        }

        [TestMethod]
        public void BuildSentEmailsInsertSql_StaysUnderSqlServerParameterLimit_AtChosenBatchSize()
        {
            // SQL Server allows 2100 params per command; the importer uses 300 parents/batch
            // (= 1800 params). This is a regression guard.
            var sql = SentEmailImporter.BuildSentEmailsInsertSql(300);
            int tokenCount = Regex.Matches(sql, @"@p\d+").Count;
            Assert.IsTrue(tokenCount <= 2100,
                $"Sent_emails batch must stay <= 2100 params; got {tokenCount}.");
            Assert.AreEqual(1800, tokenCount);
        }

        #endregion

        #region BuildSentEmailRecipientsInsertSql

        [TestMethod]
        public void BuildSentEmailRecipientsInsertSql_SingleRow_GeneratesExpectedShape()
        {
            var sql = SentEmailImporter.BuildSentEmailRecipientsInsertSql(1);
            StringAssert.Contains(sql,
                "INSERT INTO sent_email_recipients (sent_email_id, recipient_address_id) " +
                "VALUES (@p0,@p1)");
        }

        [TestMethod]
        public void BuildSentEmailRecipientsInsertSql_MultiRow_GeneratesIncrementingPairs()
        {
            var sql = SentEmailImporter.BuildSentEmailRecipientsInsertSql(4);
            StringAssert.Contains(sql, "(@p0,@p1)");
            StringAssert.Contains(sql, "(@p2,@p3)");
            StringAssert.Contains(sql, "(@p4,@p5)");
            StringAssert.Contains(sql, "(@p6,@p7)");
        }

        [TestMethod]
        public void BuildSentEmailRecipientsInsertSql_UsesTwoParamsPerRow()
        {
            var sql = SentEmailImporter.BuildSentEmailRecipientsInsertSql(50);
            int tokenCount = Regex.Matches(sql, @"@p\d+").Count;
            Assert.AreEqual(100, tokenCount);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        public void BuildSentEmailRecipientsInsertSql_ZeroRows_Throws()
        {
            SentEmailImporter.BuildSentEmailRecipientsInsertSql(0);
        }

        [TestMethod]
        public void BuildSentEmailRecipientsInsertSql_StaysUnderSqlServerParameterLimit_AtChosenBatchSize()
        {
            // The importer uses 1000 recipients/batch => 2000 params, safely under 2100.
            var sql = SentEmailImporter.BuildSentEmailRecipientsInsertSql(1000);
            int tokenCount = Regex.Matches(sql, @"@p\d+").Count;
            Assert.IsTrue(tokenCount <= 2100,
                $"Sent_email_recipients batch must stay <= 2100 params; got {tokenCount}.");
            Assert.AreEqual(2000, tokenCount);
        }

        #endregion

        #region NullSentEmailSentimentScorer

        [TestMethod]
        public void NullSentimentScorer_IsDisabled()
        {
            Assert.IsFalse(NullSentEmailSentimentScorer.Instance.IsEnabled);
        }

        [TestMethod]
        public async Task NullSentimentScorer_ReturnsNullForAnyInput()
        {
            var msgs = new List<GraphSentMessage>
            {
                Msg("m1", "a@x.com", new[] { "b@x.com" }),
            };
            var result = await NullSentEmailSentimentScorer.Instance.ScoreAsync(msgs);
            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task NullSentimentScorer_HandlesEmptyAndNullInput()
        {
            Assert.IsNull(await NullSentEmailSentimentScorer.Instance.ScoreAsync(
                new List<GraphSentMessage>()));
            Assert.IsNull(await NullSentEmailSentimentScorer.Instance.ScoreAsync(null));
        }

        #endregion

        #region SentEmailLoadResult

        [TestMethod]
        public void SentEmailLoadResult_Empty_HasZeroDeltaTokenCounts()
        {
            Assert.IsNotNull(SentEmailLoadResult.Empty.Messages);
            Assert.AreEqual(0, SentEmailLoadResult.Empty.Messages.Count);
            Assert.AreEqual(0, SentEmailLoadResult.Empty.DeltaTokenReads);
            Assert.AreEqual(0, SentEmailLoadResult.Empty.DeltaTokenWrites);
            Assert.IsNull(SentEmailLoadResult.Empty.NextDeltaToken);
        }

        #endregion

        #region Delta-token commit gate

        [TestMethod]
        public async Task ImportSentEmailsForUser_PhaseAFailure_DoesNotCommitDeltaToken_AndReplaySavesMail()
        {
            var user = await CreateSavedUserAsync("phase-a");
            var messageId = "i618-phase-a-" + Guid.NewGuid().ToString("N");
            var source = new RecordingSentEmailSourceLoader(user, Msg(messageId, user.Mail, new[] { "recipient-a@contoso.com" }, "Καλημέρα κόσμε"));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);
            importer.FailureInjectionPhase = SentEmailPersistenceFailurePhase.PhaseA;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportSentEmailsForUser(user));

            Assert.IsFalse(committer.TryGetToken(user, out _), "A failed parent save must not advance the user's delta token.");
            Assert.AreEqual(0, await CountSentEmailsAsync(messageId), "The failed phase-A transaction should leave no parent row behind.");

            importer.FailureInjectionPhase = SentEmailPersistenceFailurePhase.None;
            await importer.ImportSentEmailsForUser(user);

            Assert.AreEqual(2, source.LoadCount, "The unsaved message must be loaded again on the next run.");
            Assert.IsTrue(committer.TryGetToken(user, out var token));
            Assert.AreEqual(source.NextDeltaToken, token);
            Assert.AreEqual(1, await CountSentEmailsAsync(messageId));
            Assert.AreEqual(1, await CountRecipientsAsync(messageId));
        }

        [TestMethod]
        public async Task ImportSentEmailsForUser_BoundedRead_CommitsItsContinuationOnlyAfterThePagesAreSaved()
        {
            // A read that stopped at its per-cycle bound continues from its nextLink next cycle - but only once what
            // it read is durable, or a failed save would skip those messages for good.
            var user = await CreateSavedUserAsync("bounded");
            var messageId = "c8-bounded-" + Guid.NewGuid().ToString("N");
            const string nextPage = "https://graph.microsoft.com/v1.0/users/synthetic/mailFolders('sentitems')/messages/delta?$skiptoken=page-2";
            var source = new RecordingSentEmailSourceLoader(user, Msg(messageId, user.Mail, new[] { "recipient-c8@contoso.com" }, "Καλημέρα κόσμε"))
            {
                NextPageLink = nextPage,
            };
            var committer = new RecordingDeltaTokenCommitter(async (committedUser, checkpoint) =>
                Assert.AreEqual(1, await CountRecipientsAsync(messageId), "The checkpoint may only move once the page's rows are durable."));
            var importer = NewImporter(source, committer);
            importer.FailureInjectionPhase = SentEmailPersistenceFailurePhase.PhaseA;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportSentEmailsForUser(user));
            Assert.IsFalse(committer.TryGetContinuation(user, out _), "A failed save must not move the read past the unsaved page.");

            importer.FailureInjectionPhase = SentEmailPersistenceFailurePhase.None;
            await importer.ImportSentEmailsForUser(user);

            Assert.IsTrue(committer.TryGetContinuation(user, out var continuation));
            Assert.AreEqual(nextPage, continuation);
            Assert.IsFalse(committer.TryGetToken(user, out _), "The round is not finished, so no delta token is committed.");
            Assert.AreEqual(1, await CountSentEmailsAsync(messageId));
        }

        [TestMethod]
        public async Task ImportSentEmailsForUser_PhaseBFailure_RollsBackParent_AndReplayLeavesNoOrphan()
        {
            var user = await CreateSavedUserAsync("phase-b");
            var messageId = "i618-phase-b-" + Guid.NewGuid().ToString("N");
            var source = new RecordingSentEmailSourceLoader(user, Msg(messageId, user.Mail, new[] { "recipient-b@contoso.com" }, "Καλημέρα κόσμε"));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);
            importer.FailureInjectionPhase = SentEmailPersistenceFailurePhase.PhaseB;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportSentEmailsForUser(user));

            Assert.IsFalse(committer.TryGetToken(user, out _), "A failed recipient save must not advance the user's delta token.");
            Assert.AreEqual(0, await CountSentEmailsAsync(messageId), "The transaction must roll back the parent row when phase B fails.");

            importer.FailureInjectionPhase = SentEmailPersistenceFailurePhase.None;
            await importer.ImportSentEmailsForUser(user);

            Assert.AreEqual(2, source.LoadCount, "The rolled-back message must be loaded again on the next run.");
            Assert.AreEqual(1, await CountSentEmailsAsync(messageId));
            Assert.AreEqual(1, await CountRecipientsAsync(messageId), "Replay should persist parent and recipients together.");
        }

        [TestMethod]
        public async Task ImportSentEmailsForUsers_HappyPath_CommitsEachDeltaTokenOnceAfterRowsAreSaved()
        {
            var alice = await CreateSavedUserAsync("happy-alice");
            var bob = await CreateSavedUserAsync("happy-bob");
            var aliceMessageId = "i618-happy-a-" + Guid.NewGuid().ToString("N");
            var bobMessageId = "i618-happy-b-" + Guid.NewGuid().ToString("N");
            var source = new RecordingSentEmailSourceLoader(
                (alice, Msg(aliceMessageId, alice.Mail, new[] { "recipient-ha@contoso.com" }, "Καλημέρα κόσμε")),
                (bob, Msg(bobMessageId, bob.Mail, new[] { "recipient-hb@contoso.com" }, "Καλημέρα κόσμε")));
            var committer = new RecordingDeltaTokenCommitter(async (user, token) =>
            {
                var expectedMessageId = user.ID == alice.ID ? aliceMessageId : bobMessageId;
                Assert.AreEqual(1, await CountRecipientsAsync(expectedMessageId),
                    "Delta tokens must be committed only after the user's parent and recipient rows are durable.");
            });
            var importer = NewImporter(source, committer, userChunkSize: 25);

            await importer.ImportSentEmailsForUser(alice);
            await importer.ImportSentEmailsForUser(bob);

            Assert.AreEqual(2, committer.WriteCount);
            Assert.IsTrue(committer.TryGetToken(alice, out var aliceToken));
            Assert.IsTrue(committer.TryGetToken(bob, out var bobToken));
            Assert.AreEqual(source.NextDeltaToken, aliceToken);
            Assert.AreEqual(source.NextDeltaToken, bobToken);
        }

        [TestMethod]
        public async Task ImportSentEmailsForUser_ExistingParentWithoutRecipients_IsRepairedAndTokenCommitted()
        {
            var user = await CreateSavedUserAsync("repair");
            var messageId = "i618-repair-" + Guid.NewGuid().ToString("N");
            var recipient = "recipient-repair-" + Guid.NewGuid().ToString("N") + "@contoso.com";
            await SeedOrphanParentAsync(user, messageId);

            var source = new RecordingSentEmailSourceLoader(user, Msg(messageId, user.Mail, new[] { recipient }, "Καλημέρα κόσμε"));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);

            await importer.ImportSentEmailsForUser(user);

            Assert.AreEqual(1, await CountSentEmailsAsync(messageId), "Repair must reuse the existing parent rather than insert a duplicate.");
            Assert.AreEqual(1, await CountRecipientsAsync(messageId), "A historical parent with no recipients should be repaired when Graph replays it.");
            Assert.IsTrue(committer.TryGetToken(user, out var token));
            Assert.AreEqual(source.NextDeltaToken, token);
        }

        /// <summary>
        /// One recipient address that SQL refuses must not strand the other new addresses resolved in the same
        /// batch. Here it is a stored address repeated with a trailing blank: distinct in memory, but equal under
        /// SQL's comparison, so the unique index rejects it - and SaveChanges is all-or-nothing, so the sender's
        /// brand-new address was rolled back with it. The message then failed its chunk on the unresolved
        /// sender, and because the delta token now waits for the chunk to save (#629), it failed again on every
        /// cycle, taking every later chunk and Graph section with it.
        /// </summary>
        [TestMethod]
        public async Task ImportSentEmailsForUser_OneRefusedRecipientAddress_DoesNotStrandTheRestOfItsBatch()
        {
            var user = await CreateSavedUserAsync("refused-address");
            var messageId = "i618-refused-address-" + Guid.NewGuid().ToString("N");
            var storedRecipient = ("stored-recipient-" + Guid.NewGuid().ToString("N") + "@contoso.com").ToLowerInvariant();
            var newRecipient = ("new-recipient-" + Guid.NewGuid().ToString("N") + "@contoso.com").ToLowerInvariant();
            using (var db = new AnalyticsEntitiesContext())
            {
                db.EmailAddresses.Add(new EmailAddress { Address = storedRecipient });
                await db.SaveChangesAsync();
            }

            var source = new RecordingSentEmailSourceLoader(user,
                Msg(messageId, user.Mail, new[] { newRecipient, storedRecipient + " " }, "Καλημέρα κόσμε"));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);

            await importer.ImportSentEmailsForUser(user);

            Assert.AreEqual(1, await CountSentEmailsAsync(messageId), "The message must be saved despite the refused recipient address.");
            Assert.AreEqual(2, await CountRecipientsAsync(messageId),
                "Both recipients resolve: the new one inserted on its own, the trailing-blank twin onto the stored row.");
            Assert.IsTrue(committer.TryGetToken(user, out var token), "The chunk saved, so its delta token must be committed.");
            Assert.AreEqual(source.NextDeltaToken, token);
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(1, await db.EmailAddresses.CountAsync(e => e.Address == storedRecipient),
                    "The refused twin must map onto the stored address, not add a second row.");
            }
        }

        /// <summary>
        /// The other side of the fallback above: only refusals that are permanent may leave an address
        /// unresolved. Any other failure inserting a recipient's address must fail the chunk and keep its delta
        /// token, not save the message without that recipient and commit the token, which would lose the
        /// recipient for good because the message is never read again.
        /// </summary>
        /// <remarks>
        /// Failures EF's execution strategy recognises as transient are retried inside SaveChanges and surface
        /// as <c>RetryLimitExceededException</c> before the fallback is ever reached. What does reach it is a
        /// <c>DbUpdateException</c> for anything the strategy does not classify, so that is what this injects:
        /// an error on exactly that INSERT that is neither a duplicate nor a value too long for the column.
        /// </remarks>
        [TestMethod]
        public async Task ImportSentEmailsForUser_TransientFailureInsertingARecipientAddress_KeepsTheTokenForARetry()
        {
            var user = await CreateSavedUserAsync("transient-address");
            var messageId = "i618-transient-address-" + Guid.NewGuid().ToString("N");
            var steadyRecipient = ("steady-recipient-" + Guid.NewGuid().ToString("N") + "@contoso.com").ToLowerInvariant();
            var flakyRecipient = ("flaky-recipient-" + Guid.NewGuid().ToString("N") + "@contoso.com").ToLowerInvariant();

            var source = new RecordingSentEmailSourceLoader(user,
                Msg(messageId, user.Mail, new[] { steadyRecipient, flakyRecipient }, "Καλημέρα κόσμε"));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);

            var failFlakyInsert = new FailAddressInsertInterceptor(flakyRecipient);
            System.Data.Entity.Infrastructure.Interception.DbInterception.Add(failFlakyInsert);
            try
            {
                await Assert.ThrowsExceptionAsync<System.Data.Entity.Infrastructure.DbUpdateException>(
                    () => importer.ImportSentEmailsForUser(user));
            }
            finally
            {
                System.Data.Entity.Infrastructure.Interception.DbInterception.Remove(failFlakyInsert);
            }

            Assert.IsTrue(failFlakyInsert.Failures >= 2, "Both the batch insert and the one-by-one retry must have hit the failure.");
            Assert.IsFalse(committer.TryGetToken(user, out _), "A transient failure must not advance the user's delta token.");
            Assert.AreEqual(0, await CountSentEmailsAsync(messageId), "Nothing is saved for a chunk that failed.");

            await importer.ImportSentEmailsForUser(user);

            Assert.AreEqual(2, source.LoadCount, "The message must be read again on the next run.");
            Assert.AreEqual(1, await CountSentEmailsAsync(messageId));
            Assert.AreEqual(2, await CountRecipientsAsync(messageId), "The retry must save the message with both recipients.");
            Assert.IsTrue(committer.TryGetToken(user, out var token));
            Assert.AreEqual(source.NextDeltaToken, token);
        }

        /// <summary>
        /// Fails EF's INSERT of one email address with an error that is neither a duplicate nor a truncation, and
        /// that EF's execution strategy does not retry - as a failure it cannot classify would. Everything else,
        /// including the lookup that follows a refusal, runs normally.
        /// </summary>
        private sealed class FailAddressInsertInterceptor : System.Data.Entity.Infrastructure.Interception.DbCommandInterceptor
        {
            private readonly string _address;

            public FailAddressInsertInterceptor(string address)
            {
                _address = address;
            }

            public int Failures { get; private set; }

            public override void ReaderExecuting(System.Data.Common.DbCommand command,
                System.Data.Entity.Infrastructure.Interception.DbCommandInterceptionContext<System.Data.Common.DbDataReader> interceptionContext)
            {
                if (IsInsertOfTheAddress(command))
                {
                    Failures++;
                    interceptionContext.Exception = new InvalidOperationException("Simulated unclassified SQL failure.");
                }
            }

            public override void NonQueryExecuting(System.Data.Common.DbCommand command,
                System.Data.Entity.Infrastructure.Interception.DbCommandInterceptionContext<int> interceptionContext)
            {
                if (IsInsertOfTheAddress(command))
                {
                    Failures++;
                    interceptionContext.Exception = new InvalidOperationException("Simulated unclassified SQL failure.");
                }
            }

            private bool IsInsertOfTheAddress(System.Data.Common.DbCommand command)
            {
                return command.CommandText.IndexOf("INSERT", StringComparison.OrdinalIgnoreCase) >= 0
                    && command.CommandText.IndexOf("email_addresses", StringComparison.OrdinalIgnoreCase) >= 0
                    && command.Parameters.Cast<System.Data.Common.DbParameter>().Any(p => string.Equals(p.Value as string, _address, StringComparison.Ordinal));
            }
        }

        [TestMethod]
        public async Task ImportSentEmailsForUsers_DuplicateOrphanRepairInChunk_IsDedupedAndCommitsTokens()
        {
            var alice = await CreateSavedUserAsync("repair-alice");
            var bob = await CreateSavedUserAsync("repair-bob");
            var charlie = await CreateSavedUserAsync("repair-charlie");
            var orphanMessageId = "i618-repair-dupe-" + Guid.NewGuid().ToString("N");
            var nextMessageId = "i618-repair-next-" + Guid.NewGuid().ToString("N");
            var recipient = "recipient-repair-dupe-" + Guid.NewGuid().ToString("N") + "@contoso.com";
            await SeedOrphanParentAsync(alice, orphanMessageId);

            var source = new RecordingSentEmailSourceLoader(
                (alice, Msg(orphanMessageId, alice.Mail, new[] { recipient }, "Καλημέρα κόσμε")),
                (bob, Msg(orphanMessageId, bob.Mail, new[] { recipient }, "Καλημέρα κόσμε")),
                (charlie, Msg(nextMessageId, charlie.Mail, new[] { "recipient-next@contoso.com" }, "Καλημέρα κόσμε")));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);

            await importer.ImportSentEmailsForUsersForTest(new List<User> { alice, bob, charlie });

            Assert.AreEqual(1, await CountSentEmailsAsync(orphanMessageId), "The existing orphan parent should be reused.");
            Assert.AreEqual(1, await CountRecipientsAsync(orphanMessageId), "Cross-user orphan repair must insert the shared recipient pair once.");
            Assert.AreEqual(1, await CountSentEmailsAsync(nextMessageId), "Later work in the chunk should still run.");
            Assert.AreEqual(1, await CountRecipientsAsync(nextMessageId), "Later work in the chunk should still persist recipients.");
            Assert.AreEqual(3, committer.WriteCount);
            Assert.IsTrue(committer.TryGetToken(alice, out _));
            Assert.IsTrue(committer.TryGetToken(bob, out _));
            Assert.IsTrue(committer.TryGetToken(charlie, out _));
        }

        [TestMethod]
        public async Task ImportSentEmailsForUsers_FailedOrphanOwner_DoesNotRepairWithAnotherMailboxesRecipients()
        {
            var owner = await CreateSavedUserAsync("orphan-owner");
            var other = await CreateSavedUserAsync("orphan-other");
            var suffix = Guid.NewGuid().ToString("N");
            var orphanId = "orphan-owned-" + suffix;
            var failedId = "orphan-fail-" + suffix;
            await SeedOrphanParentAsync(owner, orphanId);
            var source = new RecordingSentEmailSourceLoader(
                (owner, Msg(orphanId, owner.Mail, new[] { "correct@contoso.com" })),
                (owner, Msg(failedId, owner.Mail, new[] { "correct@contoso.com" })),
                (other, Msg(orphanId, other.Mail, new[] { "wrong@contoso.com" })));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer);
            importer.BeforeSentEmailBatchInsertAsync = (connection, id) => id == failedId
                ? throw new InvalidOperationException("Synthetic owner transaction failure.")
                : Task.CompletedTask;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                importer.ImportSentEmailsForUsersForTest(new List<User> { owner, other }));
            Assert.AreEqual(0, await CountRecipientsAsync(orphanId), "Only the parent mailbox may repair its orphan.");
            Assert.IsFalse(committer.TryGetToken(owner, out _));
            Assert.IsTrue(committer.TryGetToken(other, out _));

            importer.BeforeSentEmailBatchInsertAsync = null;
            await importer.ImportSentEmailsForUser(owner);
            using (var db = new AnalyticsEntitiesContext())
            {
                var addresses = await db.SentEmailRecipients.Where(r => r.SentEmail.GraphMessageId == orphanId)
                    .Select(r => r.RecipientAddress.Address).ToListAsync();
                CollectionAssert.AreEqual(new[] { "correct@contoso.com" }, addresses);
            }
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(25)]
        public async Task ImportSentEmailsForUsers_OneMailboxFails_OtherMailboxesCommitAndFailureReplays(int chunkSize)
        {
            var failed = await CreateSavedUserAsync("isolated-failed");
            var healthy = await CreateSavedUserAsync("isolated-healthy");
            var empty = await CreateSavedUserAsync("isolated-empty");
            var suffix = Guid.NewGuid().ToString("N");
            var failedMessageId = "isolated-failed-" + suffix;
            var healthyMessageId = "isolated-healthy-" + suffix;
            var source = new RecordingSentEmailSourceLoader(
                (failed, Msg(failedMessageId, failed.Mail, new[] { "recipient@contoso.com" })),
                (healthy, Msg(healthyMessageId, healthy.Mail, new[] { "recipient@contoso.com" })));
            var committer = new RecordingDeltaTokenCommitter();
            var importer = NewImporter(source, committer, userChunkSize: chunkSize);
            importer.BeforeSentEmailBatchInsertAsync = (connection, id) =>
            {
                if (id == failedMessageId)
                    throw new InvalidOperationException("Synthetic mailbox persistence failure.");
                return Task.CompletedTask;
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                importer.ImportSentEmailsForUsersForTest(new List<User> { failed, healthy, empty }));

            Assert.AreEqual(0, await CountSentEmailsAsync(failedMessageId));
            Assert.IsFalse(committer.TryGetToken(failed, out _));
            Assert.AreEqual(1, await CountSentEmailsAsync(healthyMessageId));
            Assert.AreEqual(1, await CountRecipientsAsync(healthyMessageId));
            Assert.IsTrue(committer.TryGetToken(healthy, out _));
            Assert.IsTrue(committer.TryGetToken(empty, out _));

            importer.BeforeSentEmailBatchInsertAsync = null;
            await importer.ImportSentEmailsForUsersForTest(new List<User> { failed, healthy, empty });
            Assert.AreEqual(1, await CountSentEmailsAsync(failedMessageId));
            Assert.AreEqual(1, await CountRecipientsAsync(failedMessageId));
            Assert.AreEqual(1, await CountSentEmailsAsync(healthyMessageId));
            Assert.IsTrue(committer.TryGetToken(failed, out _));
        }

        [TestMethod]
        public async Task ImportSentEmailsForUsers_CheckpointFailure_DoesNotBlockLaterMailbox()
        {
            var failed = await CreateSavedUserAsync("checkpoint-failed");
            var healthy = await CreateSavedUserAsync("checkpoint-healthy");
            var suffix = Guid.NewGuid().ToString("N");
            var source = new RecordingSentEmailSourceLoader(
                (failed, Msg("checkpoint-failed-" + suffix, failed.Mail, new[] { "recipient@contoso.com" })),
                (healthy, Msg("checkpoint-healthy-" + suffix, healthy.Mail, new[] { "recipient@contoso.com" })));
            var committer = new RecordingDeltaTokenCommitter((user, token) =>
                user.ID == failed.ID ? Task.FromException(new InvalidOperationException("Synthetic checkpoint outage.")) : Task.CompletedTask);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                NewImporter(source, committer).ImportSentEmailsForUsersForTest(new List<User> { failed, healthy }));

            Assert.AreEqual(1, await CountRecipientsAsync("checkpoint-failed-" + suffix),
                "SQL remains committed when its subsequent checkpoint fails, so replay must deduplicate it.");
            Assert.IsFalse(committer.TryGetToken(failed, out _));
            Assert.IsTrue(committer.TryGetToken(healthy, out _));
            Assert.AreEqual(1, await CountRecipientsAsync("checkpoint-healthy-" + suffix));
        }

        #region One bad mailbox must not wedge the section for ever (F-8), and checkpoints are batched (F-7), logging (F-5)

        private static async Task<(User Failed, User Healthy, RecordingSentEmailSourceLoader Source, string FailedMessageId, string HealthyMessageId)> FailedAndHealthyMailboxesAsync()
        {
            var failed = await CreateSavedUserAsync("poison");
            var healthy = await CreateSavedUserAsync("fine");
            var suffix = Guid.NewGuid().ToString("N");
            var failedId = "poison-" + suffix;
            var healthyId = "fine-" + suffix;
            var source = new RecordingSentEmailSourceLoader(
                (failed, Msg(failedId, failed.Mail, new[] { "recipient@contoso.com" })),
                (healthy, Msg(healthyId, healthy.Mail, new[] { "recipient@contoso.com" })));
            return (failed, healthy, source, failedId, healthyId);
        }

        [TestMethod]
        public async Task APersistentlyFailingMailbox_IsQuarantinedAfterThreeCycles_AndNoLongerFailsTheSection()
        {
            var (failed, healthy, source, failedId, healthyId) = await FailedAndHealthyMailboxesAsync();
            var committer = new RecordingDeltaTokenCommitter();
            using (var captured = new CapturedAnalyticsLogger())
            {
                var quarantine = new SentEmailMailboxQuarantine(new Common.Entities.State.InMemoryKeyValueStore(), captured.Logger);
                var importer = NewImporter(source, committer, logger: captured.Logger, quarantine: quarantine);
                importer.BeforeSentEmailBatchInsertAsync = (connection, id) =>
                    id == failedId ? throw new InvalidOperationException("Synthetic poisoned mailbox.") : Task.CompletedTask;
                var both = new List<User> { failed, healthy };

                for (var cycle = 1; cycle < SentEmailMailboxQuarantine.QuarantineAfterConsecutiveFailures; cycle++)
                {
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportSentEmailsForUsersForTest(both));
                    Assert.IsFalse(quarantine.IsQuarantined(failed.ID), "Cycle " + cycle + ": not yet.");
                }

                await importer.ImportSentEmailsForUsersForTest(both);

                Assert.IsTrue(quarantine.IsQuarantined(failed.ID));
                Assert.IsFalse(quarantine.IsQuarantined(healthy.ID));
                Assert.AreEqual(1, await CountSentEmailsAsync(healthyId), "Every other mailbox carried on.");
                Assert.IsTrue(committer.TryGetToken(healthy, out _));
                Assert.IsFalse(committer.TryGetToken(failed, out _), "The quarantined mailbox's checkpoint is left exactly as it was.");
                Assert.AreEqual(0, await CountSentEmailsAsync(failedId));
                var error = captured.Messages(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Error)
                    .Single(m => m.StartsWith("SentEmailMailboxQuarantined:"));
                StringAssert.Contains(error, "userId=" + failed.ID + ";");
                StringAssert.Contains(error, "consecutiveFailures=3");
                StringAssert.Contains(error, "failure=InvalidOperationException");
                Assert.IsFalse(error.Contains("@contoso.com"), "A user id, never a UPN.");
            }
        }

        [TestMethod]
        public async Task EveryMailboxInAChunkFailing_IsTheDatabaseNotTheMailboxes_SoNobodyIsQuarantined()
        {
            var (failed, healthy, source, failedId, healthyId) = await FailedAndHealthyMailboxesAsync();
            using (var captured = new CapturedAnalyticsLogger())
            {
                var quarantine = new SentEmailMailboxQuarantine(new Common.Entities.State.InMemoryKeyValueStore(), captured.Logger);
                var importer = NewImporter(source, new RecordingDeltaTokenCommitter(), logger: captured.Logger, quarantine: quarantine);
                importer.BeforeSentEmailBatchInsertAsync = (connection, id) => throw new InvalidOperationException("Synthetic database outage.");

                for (var cycle = 0; cycle < SentEmailMailboxQuarantine.QuarantineAfterConsecutiveFailures + 2; cycle++)
                {
                    await Assert.ThrowsExceptionAsync<AggregateException>(() => importer.ImportSentEmailsForUsersForTest(new List<User> { failed, healthy }));
                }

                Assert.IsFalse(quarantine.IsQuarantined(failed.ID) || quarantine.IsQuarantined(healthy.ID));
            }
        }

        [TestMethod]
        public async Task AMailboxThatRecovers_StartsCountingFromZeroAgain()
        {
            var quarantine = new SentEmailMailboxQuarantine(new Common.Entities.State.InMemoryKeyValueStore(), AnalyticsLogger.ConsoleOnlyTracer());
            await quarantine.LoadAsync();

            Assert.AreEqual(1, quarantine.RecordFailure(7, new InvalidOperationException("synthetic")).ConsecutiveFailures);
            Assert.AreEqual(2, quarantine.RecordFailure(7, new InvalidOperationException("synthetic")).ConsecutiveFailures);
            quarantine.RecordSuccess(7);

            // One failure after a success is the first of a new run, not the third of the old one: it still fails the section.
            var decision = quarantine.RecordFailure(7, new InvalidOperationException("synthetic"));
            Assert.AreEqual(1, decision.ConsecutiveFailures);
            Assert.IsFalse(decision.Quarantined);
        }

        [TestMethod]
        public async Task Quarantine_IsKeptByUserIdInOneRow_EndsAfterADay_AndSurvivesARestart()
        {
            var now = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            var state = new Common.Entities.State.InMemoryKeyValueStore();
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var quarantine = new SentEmailMailboxQuarantine(state, logger, () => now);
            await quarantine.LoadAsync();

            for (var i = 0; i < SentEmailMailboxQuarantine.QuarantineAfterConsecutiveFailures; i++)
            {
                quarantine.RecordFailure(42, new InvalidOperationException("synthetic"));
            }
            await quarantine.SaveAsync();

            var restarted = new SentEmailMailboxQuarantine(state, logger, () => now.AddHours(1));
            await restarted.LoadAsync();
            Assert.IsTrue(restarted.IsQuarantined(42), "A restart does not release it.");
            Assert.IsFalse(restarted.IsQuarantined(43));
            Assert.AreEqual(1, restarted.QuarantinedCount());
            var row = await state.GetStringAsync("SentEmailMailboxQuarantine");
            Assert.IsFalse(row.Contains("@"), "User ids only: " + row);

            var later = new SentEmailMailboxQuarantine(state, logger, () => now.AddHours(25));
            await later.LoadAsync();
            Assert.IsFalse(later.IsQuarantined(42), "A day on, the mailbox is tried again.");
            Assert.IsTrue(later.RecordFailure(42, new InvalidOperationException("synthetic")).Quarantined,
                "...and if it fails once more it goes straight back: it has been failing for 4 cycles.");
        }

        [TestMethod]
        public async Task AStateStoreThatCannotBeRead_CostsOnlyTheQuarantine()
        {
            var quarantine = new SentEmailMailboxQuarantine(new FailingStateStore(), AnalyticsLogger.ConsoleOnlyTracer());
            await quarantine.LoadAsync();

            Assert.IsFalse(quarantine.IsQuarantined(1));
            Assert.IsFalse(quarantine.RecordFailure(1, new InvalidOperationException("synthetic")).Quarantined);
            await quarantine.SaveAsync();
        }

        private sealed class FailingStateStore : Common.Entities.State.IKeyValueStore
        {
            public string Description => "unreachable";
            public Task<string> GetStringAsync(string key, System.Threading.CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, System.Threading.CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
            public Task<bool> DeleteAsync(string key, System.Threading.CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
            public Task<bool> ExistsAsync(string key, System.Threading.CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic outage");
        }

        [TestMethod]
        public async Task ACommitterThatCanBatch_GetsTheWholeChunksCheckpointsInOneCall_ExceptTheFailedMailbox()
        {
            var (failed, healthy, source, failedId, healthyId) = await FailedAndHealthyMailboxesAsync();
            var third = await CreateSavedUserAsync("fine-too");
            var committer = new BatchRecordingCommitter();
            var importer = NewImporter(source, committer);
            importer.BeforeSentEmailBatchInsertAsync = (connection, id) =>
                id == failedId ? throw new InvalidOperationException("Synthetic poisoned mailbox.") : Task.CompletedTask;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportSentEmailsForUsersForTest(new List<User> { failed, healthy, third }));

            Assert.AreEqual(1, committer.Batches.Count, "One storage round trip for the chunk, not one per mailbox.");
            CollectionAssert.AreEquivalent(new[] { healthy.ID, third.ID }, committer.Batches[0].ToArray(),
                "Only mailboxes whose messages are saved get a checkpoint; the failed one keeps its previous one.");
            Assert.AreEqual(0, committer.SingleCommits.Count);
        }

        [TestMethod]
        public async Task IfTheBatchFails_EachMailboxIsCommittedOnItsOwn_SoOneFailureNeverWithholdsAnothersCheckpoint()
        {
            var (first, second, source, firstId, secondId) = await FailedAndHealthyMailboxesAsync();
            var committer = new BatchRecordingCommitter { BatchFailure = new InvalidOperationException("Synthetic batch outage."), FailSingleFor = first.ID };
            using (var captured = new CapturedAnalyticsLogger())
            {
                var importer = NewImporter(source, committer, logger: captured.Logger);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => importer.ImportSentEmailsForUsersForTest(new List<User> { first, second }));

                CollectionAssert.AreEqual(new[] { second.ID }, committer.SingleCommits.ToArray(), "The mailbox that can be committed is.");
                Assert.IsTrue(captured.Messages(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Warning).Any(m =>
                    m.StartsWith("SentEmailCheckpointBatchFailed:") && m.Contains("mailboxes=2")));
            }
        }

        /// <summary>
        /// A chunk of mailboxes is one line, not about three per mailbox with no mailbox named: counts, and the time each phase took.
        /// </summary>
        [TestMethod]
        public async Task AChunkIsOneOutcomeLine_NotSeveralPerMailbox()
        {
            var (failed, healthy, source, failedId, healthyId) = await FailedAndHealthyMailboxesAsync();
            using (var captured = new CapturedAnalyticsLogger())
            {
                await NewImporter(source, new RecordingDeltaTokenCommitter(), logger: captured.Logger)
                    .ImportSentEmailsForUsersForTest(new List<User> { failed, healthy });

                var all = captured.Messages();
                Assert.AreEqual(0, all.Count(m => m.Contains("[persist]") || m.Contains("[chunk]")), "No per-phase, per-mailbox lines: " + string.Join(" | ", all));
                var line = all.Single(m => m.StartsWith("SentEmailChunk:"));
                StringAssert.Contains(line, "chunk=1/1; mailboxes=2; loaded=2; committed=2; failed=0; quarantined=0; messages=2; inserted=2; recipients=2;");
                foreach (var phase in new[] { "loadMs=", "addressMs=", "existingMs=", "scoreMs=", "persistMs=", "checkpointMs=", "totalMs=" })
                {
                    StringAssert.Contains(line, phase);
                }
            }
        }

        private sealed class BatchRecordingCommitter : ISentEmailDeltaTokenCommitter, ISentEmailSyncContinuationCommitter, ISentEmailBatchCheckpointCommitter
        {
            public List<List<int>> Batches { get; } = new List<List<int>>();
            public List<int> SingleCommits { get; } = new List<int>();
            public Exception BatchFailure { get; set; }
            public int FailSingleFor { get; set; } = -1;

            public Task CommitCheckpointsAsync(IReadOnlyList<SentEmailCheckpoint> checkpoints)
            {
                if (BatchFailure != null)
                    return Task.FromException(BatchFailure);

                Batches.Add(checkpoints.Select(c => c.User.ID).ToList());
                return Task.CompletedTask;
            }

            public Task CommitDeltaTokenAsync(User user, string deltaToken)
            {
                if (user.ID == FailSingleFor)
                    return Task.FromException(new InvalidOperationException("Synthetic checkpoint failure."));

                SingleCommits.Add(user.ID);
                return Task.CompletedTask;
            }

            public Task CommitContinuationAsync(User user, string nextPageLink) => CommitDeltaTokenAsync(user, nextPageLink);
        }

        #endregion

        [TestMethod]
        public void AddDistinctRecipientPair_SkipsDuplicatePairs()
        {
            var pairs = new List<(int SentEmailId, int RecipientAddressId)>();
            var seen = new HashSet<(int SentEmailId, int RecipientAddressId)>();

            Assert.IsTrue(SentEmailImporter.AddDistinctRecipientPair(pairs, seen, 10, 20));
            Assert.IsFalse(SentEmailImporter.AddDistinctRecipientPair(pairs, seen, 10, 20));
            Assert.IsTrue(SentEmailImporter.AddDistinctRecipientPair(pairs, seen, 10, 21));

            CollectionAssert.AreEqual(
                new[] { (SentEmailId: 10, RecipientAddressId: 20), (SentEmailId: 10, RecipientAddressId: 21) },
                pairs);
        }

        #endregion

        #region End-to-end pipeline (in-memory)

        [TestMethod]
        public void EndToEnd_DedupesAcrossUsersAndProducesExpectedRecipientPairs()
        {
            // Two users send overlapping messages; one duplicates a recipient inside its own
            // recipient list. Exercise the full pure-pipeline path (load -> dedupe -> build).
            var alice = new User { ID = 1, Mail = "alice@x.com", UserPrincipalName = "alice@x.com" };
            var bob = new User { ID = 2, Mail = "bob@x.com", UserPrincipalName = "bob@x.com" };

            var aliceMsgs = new List<GraphSentMessage>
            {
                Msg("a-1", "alice@x.com", new[] { "carol@x.com", "dan@x.com", "carol@x.com" }),
                Msg("a-2", "alice@x.com", new[] { "dan@x.com" }),
                Msg("a-1", "alice@x.com", new[] { "eve@x.com" }), // duplicate id
            };
            var bobMsgs = new List<GraphSentMessage>
            {
                Msg("b-1", "bob@x.com", new[] { "alice@x.com" }),
            };

            var aliceCandidates = SentEmailImporter.BuildCandidates(aliceMsgs, out var aliceAddrs);
            var bobCandidates = SentEmailImporter.BuildCandidates(bobMsgs, out var bobAddrs);

            // a-1 dedup -> 2 candidates for alice; 1 for bob.
            Assert.AreEqual(2, aliceCandidates.Count);
            Assert.AreEqual(1, bobCandidates.Count);

            // Carol appears twice in a-1 but should be in the recipient list only once.
            var aliceFirst = aliceCandidates.Single(c => c.GraphMessageId == "a-1");
            CollectionAssert.AreEquivalent(
                new[] { "carol@x.com", "dan@x.com" },
                aliceFirst.RecipientAddresses.ToArray());

            // Build a deterministic address-id map covering everything seen by both users.
            var allAddrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in aliceAddrs) allAddrs.Add(a);
            foreach (var a in bobAddrs) allAddrs.Add(a);

            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int next = 100;
            foreach (var a in allAddrs.OrderBy(x => x))
                addressIds[a] = next++;

            // Build parent rows like the importer does.
            var aliceRows = aliceCandidates
                .Select(c => SentEmailImporter.BuildSentEmailRow(alice, c, addressIds, null))
                .ToList();
            var bobRows = bobCandidates
                .Select(c => SentEmailImporter.BuildSentEmailRow(bob, c, addressIds, null))
                .ToList();

            // Simulate phase A by assigning IDs (in production the SQL OUTPUT clause does this).
            int parentId = 1;
            foreach (var row in aliceRows.Concat(bobRows))
                row.ID = parentId++;

            // Build the recipient pair list the way the importer does in phase B.
            var pairs = new List<(int SentEmailId, int RecipientAddressId)>();
            void Emit(SentEmail row, SentEmailImporter.Candidate cand)
            {
                foreach (var addr in cand.RecipientAddresses)
                    pairs.Add((row.ID, addressIds[addr]));
            }
            for (int i = 0; i < aliceCandidates.Count; i++) Emit(aliceRows[i], aliceCandidates[i]);
            for (int i = 0; i < bobCandidates.Count; i++) Emit(bobRows[i], bobCandidates[i]);

            // a-1: 2 recipients, a-2: 1 recipient, b-1: 1 recipient -> 4 pairs total.
            Assert.AreEqual(4, pairs.Count);

            // No duplicates of (parent, recipient).
            var distinctPairs = pairs.Distinct().ToList();
            Assert.AreEqual(pairs.Count, distinctPairs.Count, "Recipient pairs should be unique.");

            // Every pair points at a real address id and a real parent row id.
            var validParentIds = new HashSet<int>(aliceRows.Concat(bobRows).Select(r => r.ID));
            var validAddrIds = new HashSet<int>(addressIds.Values);
            foreach (var pair in pairs)
            {
                Assert.IsTrue(validParentIds.Contains(pair.SentEmailId));
                Assert.IsTrue(validAddrIds.Contains(pair.RecipientAddressId));
            }

            // FK on parent rows is the lowercased sender address.
            Assert.AreEqual(addressIds["alice@x.com"], aliceRows[0].FromAddressID);
            Assert.AreEqual(addressIds["bob@x.com"], bobRows[0].FromAddressID);
        }

        #endregion

        #region Test helpers

        private static GraphEmailRecipient Recipient(string address) =>
            new GraphEmailRecipient { EmailAddress = new GraphEmailAddress { Address = address } };

        private static GraphSentMessage Msg(string id, string from, IEnumerable<string> to)
        {
            var msg = new GraphSentMessage
            {
                Id = id,
                Subject = "Test",
                SentDateTime = DateTime.UtcNow,
                From = from == null ? null : Recipient(from),
                ToRecipients = to == null ? null : to.Select(Recipient).ToList(),
            };
            return msg;
        }

        private static GraphSentMessage Msg(string id, string from, IEnumerable<string> to, string subject)
        {
            var msg = Msg(id, from, to);
            msg.Subject = subject;
            return msg;
        }

        private static SentEmailImporter NewImporter(
            RecordingSentEmailSourceLoader source,
            ISentEmailDeltaTokenCommitter committer,
            int userChunkSize = 25,
            AnalyticsLogger logger = null,
            SentEmailMailboxQuarantine quarantine = null)
            => new SentEmailImporter(
                logger ?? AnalyticsLogger.ConsoleOnlyTracer(),
                new AppConfig(),
                source,
                NullSentEmailSentimentScorer.Instance,
                userChunkSize: userChunkSize,
                graphLoadParallelism: 1,
                deltaTokenCommitter: committer,
                mailboxQuarantine: quarantine);

        private static async Task<User> CreateSavedUserAsync(string label)
        {
            var unique = Guid.NewGuid().ToString("N");
            var user = new User
            {
                UserPrincipalName = $"{label}-{unique}@contoso.com",
                Mail = $"{label}-{unique}@contoso.com",
                AzureAdId = "00000000-0000-0000-0000-000000000000"
            };

            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(user);
                await db.SaveChangesAsync();
            }

            return user;
        }

        private static async Task<int> CountSentEmailsAsync(string graphMessageId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.SentEmails.CountAsync(s => s.GraphMessageId == graphMessageId);
            }
        }

        private static async Task<int> CountRecipientsAsync(string graphMessageId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.SentEmailRecipients
                    .CountAsync(r => r.SentEmail.GraphMessageId == graphMessageId);
            }
        }

        private static async Task SeedOrphanParentAsync(User user, string graphMessageId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var sender = new EmailAddress { Address = ("sender-" + Guid.NewGuid().ToString("N") + "@contoso.com").ToLowerInvariant() };
                db.EmailAddresses.Add(sender);
                await db.SaveChangesAsync();

                db.SentEmails.Add(new SentEmail
                {
                    Subject = "Καλημέρα κόσμε",
                    SentDate = DateTime.UtcNow,
                    GraphMessageId = graphMessageId,
                    FromAddressID = sender.ID,
                    UserID = user.ID
                });
                await db.SaveChangesAsync();
            }
        }

        private sealed class RecordingSentEmailSourceLoader : ISentEmailSourceLoader
        {
            private readonly Dictionary<int, List<GraphSentMessage>> _messagesByUserId;

            public RecordingSentEmailSourceLoader(User user, GraphSentMessage message)
                : this((user, message))
            {
            }

            public RecordingSentEmailSourceLoader(params (User User, GraphSentMessage Message)[] messages)
            {
                _messagesByUserId = messages.GroupBy(m => m.User.ID).ToDictionary(g => g.Key, g => g.Select(m => m.Message).ToList());
            }

            public string NextDeltaToken { get; } = "delta-after-success";

            /// <summary>When set, every load reports a read that stopped at its bound, with this link to continue from.</summary>
            public string NextPageLink { get; set; }

            public int LoadCount { get; private set; }

            public Task<bool> HasMailReadAccessAsync() => Task.FromResult(true);

            public Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(User user, bool includeBody)
            {
                LoadCount++;
                return Task.FromResult(new SentEmailLoadResult
                {
                    Messages = _messagesByUserId.TryGetValue(user.ID, out var messages)
                        ? new List<GraphSentMessage>(messages)
                        : new List<GraphSentMessage>(),
                    DeltaTokenReads = 1,
                    DeltaTokenWrites = 0,
                    NextDeltaToken = NextPageLink == null ? NextDeltaToken : null,
                    NextPageLink = NextPageLink
                });
            }
        }

        private sealed class RecordingDeltaTokenCommitter : ISentEmailDeltaTokenCommitter, ISentEmailSyncContinuationCommitter
        {
            private readonly Dictionary<int, string> _tokensByUserId = new Dictionary<int, string>();
            private readonly Dictionary<int, string> _continuationsByUserId = new Dictionary<int, string>();
            private readonly Func<User, string, Task> _onCommit;

            public RecordingDeltaTokenCommitter(Func<User, string, Task> onCommit = null)
            {
                _onCommit = onCommit;
            }

            public int WriteCount { get; private set; }

            public async Task CommitDeltaTokenAsync(User user, string deltaToken)
            {
                if (_onCommit != null)
                    await _onCommit(user, deltaToken);

                _tokensByUserId[user.ID] = deltaToken;
                WriteCount++;
            }

            public async Task CommitContinuationAsync(User user, string nextPageLink)
            {
                if (_onCommit != null)
                    await _onCommit(user, nextPageLink);

                _continuationsByUserId[user.ID] = nextPageLink;
                WriteCount++;
            }

            public bool TryGetToken(User user, out string token)
                => _tokensByUserId.TryGetValue(user.ID, out token);

            public bool TryGetContinuation(User user, out string nextPageLink)
                => _continuationsByUserId.TryGetValue(user.ID, out nextPageLink);
        }

        #endregion
    }
}
