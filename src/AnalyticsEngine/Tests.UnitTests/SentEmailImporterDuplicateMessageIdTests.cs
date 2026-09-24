using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Email;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.UnitTests
{
    [TestClass]
    public class SentEmailImporterDuplicateMessageIdTests
    {
        private string _token;

        [TestInitialize]
        public async Task Initialize()
        {
            _token = "i617-" + Guid.NewGuid().ToString("N");
            await CleanupAsync(_token);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (!string.IsNullOrEmpty(_token))
                await CleanupAsync(_token);
        }

        [TestMethod]
        public async Task ImportSentEmails_DuplicateGraphMessageIdWithinChunk_IsSkippedAndSectionCompletes()
        {
            var sharedMessageId = _token + "-shared-message";
            var sameChunkMessageId = _token + "-same-chunk";
            var laterChunkMessageId = _token + "-later-chunk";

            var users = await SeedUsersAsync(
                _token + "-alice@contoso.com",
                _token + "-duplicate-alice@contoso.com",
                _token + "-later@contoso.com",
                _token + "-no-mailbox@contoso.com");

            var source = new FakeSentEmailSourceLoader(new Dictionary<string, IReadOnlyList<GraphSentMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                [users[0].UserPrincipalName] = new[]
                {
                    Message(sharedMessageId, users[0].Mail, _token + "-recipient-one@contoso.com"),
                },
                [users[1].UserPrincipalName] = new[]
                {
                    Message(sharedMessageId.ToUpperInvariant(), users[1].Mail, _token + "-recipient-two@contoso.com"),
                    Message(sameChunkMessageId, users[1].Mail, _token + "-recipient-three@contoso.com"),
                },
                [users[2].UserPrincipalName] = new[]
                {
                    Message(laterChunkMessageId, users[2].Mail, _token + "-recipient-four@contoso.com"),
                },
            }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { users[3].UserPrincipalName });
            var skipList = new RecordingMailboxSkipList();

            var console = new StringWriter();
            var originalOut = Console.Out;
            try
            {
                Console.SetOut(console);
                var importer = new SentEmailImporter(
                    AnalyticsLogger.ConsoleOnlyTracer(),
                    new AppConfig(),
                    source,
                    NullSentEmailSentimentScorer.Instance,
                    userChunkSize: 10000,
                    mailboxSkipList: skipList,
                    noMailboxRetryHours: 1);

                await importer.ImportSentEmails();
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(1, await db.SentEmails.CountAsync(e => e.GraphMessageId == sharedMessageId));
                Assert.AreEqual(1, await db.SentEmails.CountAsync(e => e.GraphMessageId == sameChunkMessageId));
                Assert.AreEqual(1, await db.SentEmails.CountAsync(e => e.GraphMessageId == laterChunkMessageId));

                var insertedIds = await db.SentEmails
                    .Where(e => e.GraphMessageId == sharedMessageId
                        || e.GraphMessageId == sameChunkMessageId
                        || e.GraphMessageId == laterChunkMessageId)
                    .Select(e => e.ID)
                    .ToListAsync();
                Assert.AreEqual(3, insertedIds.Count);
                Assert.AreEqual(3, await db.SentEmailRecipients.CountAsync(r => insertedIds.Contains(r.SentEmailID)));
            }

            Assert.IsNotNull(skipList.LastSaved, "The no-mailbox skip list should still be saved after the duplicate is skipped.");
            CollectionAssert.Contains(skipList.LastSaved.Upns, users[3].UserPrincipalName);

            var duplicateLog = console.ToString()
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None)
                .Single(line => line.Contains("duplicate graph_message_id found within one user chunk"));
            StringAssert.Contains(duplicateLog, $"user IDs {users[0].ID} and {users[1].ID}");
            Assert.IsFalse(duplicateLog.Contains(users[0].UserPrincipalName), "Duplicate-user diagnostics must use database user IDs, not UPNs.");
            Assert.IsFalse(duplicateLog.Contains(users[1].Mail), "Duplicate-user diagnostics must use database user IDs, not mail addresses.");
            StringAssert.Contains(console.ToString(), "duplicates skipped in chunk: 1");
        }

        [TestMethod]
        public async Task ImportSentEmails_ConcurrentDuplicateInBatch_RetriesBatchRowByRowAndSkipsOnlyDuplicate()
        {
            var duplicateMessageId = _token + "-concurrent-duplicate";
            var keptMessageId = _token + "-kept-after-retry";
            var sender = _token + "-sender@contoso.com";

            var users = await SeedUsersAsync(sender);
            var source = new FakeSentEmailSourceLoader(new Dictionary<string, IReadOnlyList<GraphSentMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                [users[0].UserPrincipalName] = new[]
                {
                    Message(duplicateMessageId, sender, _token + "-recipient-five@contoso.com"),
                    Message(keptMessageId, sender, _token + "-recipient-six@contoso.com", _token + "-recipient-seven@contoso.com"),
                },
            });

            var insertedConcurrentDuplicate = false;
            var importer = new SentEmailImporter(
                AnalyticsLogger.ConsoleOnlyTracer(),
                new AppConfig(),
                source,
                NullSentEmailSentimentScorer.Instance,
                userChunkSize: 25);
            importer.BeforeSentEmailBatchInsertAsync = async (conn, firstGraphMessageId) =>
            {
                if (insertedConcurrentDuplicate || !string.Equals(firstGraphMessageId, duplicateMessageId, StringComparison.OrdinalIgnoreCase))
                    return;

                insertedConcurrentDuplicate = true;
                using (var db = new AnalyticsEntitiesContext())
                {
                    var fromAddressId = await db.EmailAddresses
                        .Where(a => a.Address == sender)
                        .Select(a => a.ID)
                        .SingleAsync();
                    db.SentEmails.Add(new SentEmail
                    {
                        GraphMessageId = duplicateMessageId,
                        Subject = "Concurrent synthetic duplicate Καλημέρα",
                        SentDate = DateTime.UtcNow,
                        FromAddressID = fromAddressId,
                        UserID = users[0].ID,
                    });
                    await db.SaveChangesAsync();
                }
            };

            await importer.ImportSentEmails();

            using (var db = new AnalyticsEntitiesContext())
            {
                var duplicate = await db.SentEmails.SingleAsync(e => e.GraphMessageId == duplicateMessageId);
                var kept = await db.SentEmails.SingleAsync(e => e.GraphMessageId == keptMessageId);

                Assert.AreEqual(0, await db.SentEmailRecipients.CountAsync(r => r.SentEmailID == duplicate.ID),
                    "The retry-skipped row must not receive phase-B recipient rows.");
                Assert.AreEqual(2, await db.SentEmailRecipients.CountAsync(r => r.SentEmailID == kept.ID),
                    "The non-duplicate row in the failed batch should still be persisted with recipients.");
            }
        }

        private static async Task<List<User>> SeedUsersAsync(params string[] upns)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var users = upns.Select(upn => new User
                {
                    UserPrincipalName = upn,
                    Mail = upn,
                    AzureAdId = "00000000-0000-0000-0000-000000000000",
                }).ToList();
                db.users.AddRange(users);
                await db.SaveChangesAsync();
                return users;
            }
        }

        private static GraphSentMessage Message(string id, string from, params string[] to)
        {
            return new GraphSentMessage
            {
                Id = id,
                Subject = "Καλημέρα κόσμε",
                SentDateTime = DateTime.UtcNow,
                From = Recipient("Synthetic Sender", from),
                ToRecipients = to.Select(address => Recipient("Synthetic Recipient", address)).ToList(),
            };
        }

        private static GraphEmailRecipient Recipient(string displayName, string address)
        {
            return new GraphEmailRecipient
            {
                EmailAddress = new GraphEmailAddress
                {
                    Name = displayName + " Καλημέρα",
                    Address = address,
                },
            };
        }

        private static async Task CleanupAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    @"DELETE r
                        FROM dbo.sent_email_recipients r
                        INNER JOIN dbo.sent_emails e ON e.id = r.sent_email_id
                       WHERE e.graph_message_id LIKE @p0;
                      DELETE FROM dbo.sent_emails WHERE graph_message_id LIKE @p0;
                      DELETE FROM dbo.users WHERE user_name LIKE @p0;
                      DELETE FROM dbo.email_addresses WHERE address LIKE @p0;",
                    token + "%");
            }
        }

        private sealed class FakeSentEmailSourceLoader : ISentEmailSourceLoader
        {
            private readonly Dictionary<string, IReadOnlyList<GraphSentMessage>> _messagesByUpn;
            private readonly HashSet<string> _notFoundUpns;

            public FakeSentEmailSourceLoader(
                Dictionary<string, IReadOnlyList<GraphSentMessage>> messagesByUpn,
                HashSet<string> notFoundUpns = null)
            {
                _messagesByUpn = messagesByUpn;
                _notFoundUpns = notFoundUpns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            public Task<bool> HasMailReadAccessAsync() => Task.FromResult(true);

            public Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(User user, bool includeBody)
            {
                if (_notFoundUpns.Contains(user.UserPrincipalName))
                    throw new GraphResourceNotFoundException(
                        "https://graph.microsoft.com/v1.0/users/00000000-0000-0000-0000-000000000000/messages",
                        "{\"error\":{\"code\":\"MailboxNotEnabledForRESTAPI\"}}",
                        null);

                _messagesByUpn.TryGetValue(user.UserPrincipalName, out var messages);
                return Task.FromResult(new SentEmailLoadResult
                {
                    Messages = messages ?? Array.Empty<GraphSentMessage>(),
                    DeltaTokenReads = 1,
                    DeltaTokenWrites = 0,
                });
            }
        }

        private sealed class RecordingMailboxSkipList : ISentEmailMailboxSkipList
        {
            public MailboxSkipList LastSaved { get; private set; }

            public Task<MailboxSkipList> LoadAsync() => Task.FromResult(MailboxSkipList.Empty());

            public Task SaveAsync(MailboxSkipList skipList)
            {
                LastSaved = skipList;
                return Task.CompletedTask;
            }
        }
    }
}
