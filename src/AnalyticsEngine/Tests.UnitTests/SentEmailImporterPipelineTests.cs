using Common.Entities;
using Common.Entities.Entities.Email;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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

        [TestMethod]
        public void BuildSentEmailRow_MissingSentDateTime_DefaultsToMinValue()
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
                        From = Recipient("a@x.com"),
                        ToRecipients = new List<GraphEmailRecipient> { Recipient("b@x.com") },
                    },
                }, out _).Single();

            var row = SentEmailImporter.BuildSentEmailRow(
                new User { ID = 1 }, candidate, addressIds, sentimentByMessageId: null);

            Assert.AreEqual(DateTime.MinValue, row.SentDate);
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

        #endregion
    }
}
