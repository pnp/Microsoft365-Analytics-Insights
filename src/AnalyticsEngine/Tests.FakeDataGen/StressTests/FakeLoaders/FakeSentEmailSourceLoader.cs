using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.StressTesting.FakeLoaders
{
    /// <summary>
    /// Fake <see cref="ISentEmailSourceLoader"/> that synthesises large numbers of
    /// <see cref="GraphSentMessage"/> objects per user, without calling Microsoft Graph.
    /// Used to stress test the <c>SentEmailImporter</c> pipeline (deduplication, address
    /// resolution, sentiment scoring path, EF batching, SQL persistence).
    /// </summary>
    public class FakeSentEmailSourceLoader : ISentEmailSourceLoader
    {
        private static readonly string[] DomainPool =
        {
            "contoso.com", "fabrikam.com", "northwindtraders.com",
            "tailspintoys.com", "adventure-works.com", "wingtiptoys.com"
        };

        private static readonly string[] SubjectTemplates =
        {
            "Project status update for {0}",
            "RE: Q{0} planning",
            "Meeting follow-up - {0}",
            "FW: {0} review",
            "Action required: {0}",
            "Weekly digest for {0}",
            "Approval needed - {0}"
        };

        private static readonly string[] BodySnippets =
        {
            "Hi team, please find the latest updates below.",
            "Following up on our discussion from yesterday.",
            "Heads up - we need this completed before EOD.",
            "Thanks everyone for the great progress this week.",
            "Please review and let me know your feedback.",
            "Reminder: action items are tracked in the planner.",
            "Sharing the deck attached for your reference."
        };

        private readonly int _messagesPerUser;
        private readonly int _maxRecipientsPerEmail;
        private readonly int _distinctRecipientPoolSize;
        private readonly bool _hasMailReadAccess;
        private readonly int _seed;
        private int _userCounter;

        public FakeSentEmailSourceLoader(
            int messagesPerUser,
            int maxRecipientsPerEmail,
            int distinctRecipientPoolSize,
            bool hasMailReadAccess = true,
            int seed = 2024)
        {
            if (messagesPerUser < 0) throw new ArgumentOutOfRangeException(nameof(messagesPerUser));
            if (maxRecipientsPerEmail < 1) throw new ArgumentOutOfRangeException(nameof(maxRecipientsPerEmail));
            if (distinctRecipientPoolSize < 1) throw new ArgumentOutOfRangeException(nameof(distinctRecipientPoolSize));

            _messagesPerUser = messagesPerUser;
            _maxRecipientsPerEmail = maxRecipientsPerEmail;
            _distinctRecipientPoolSize = distinctRecipientPoolSize;
            _hasMailReadAccess = hasMailReadAccess;
            _seed = seed;
        }

        public Task<bool> HasMailReadAccessAsync() => Task.FromResult(_hasMailReadAccess);

        public Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(Common.Entities.User user, bool includeBody)
        {
            // Each user gets a deterministic per-user random based on the global seed and
            // the order in which users are processed, so reruns produce identical key shapes.
            int userIndex = Interlocked.Increment(ref _userCounter);
            var random = new Random(unchecked(_seed * 397 + userIndex));

            var messages = new List<GraphSentMessage>(_messagesPerUser);
            var fromAddress = !string.IsNullOrEmpty(user?.Mail) ? user.Mail : $"stress-{userIndex}@stress.local";
            var fromName = user?.UserPrincipalName ?? fromAddress;

            for (int i = 0; i < _messagesPerUser; i++)
            {
                var msg = new GraphSentMessage
                {
                    // Stable id: user index + sequence => unique per user, reproducible across runs.
                    Id = $"stress-msg-{userIndex:D6}-{i:D6}",
                    Subject = string.Format(SubjectTemplates[random.Next(SubjectTemplates.Length)], i),
                    SentDateTime = DateTime.UtcNow.AddMinutes(-random.Next(0, 60 * 24 * 30)),
                    From = new GraphEmailRecipient
                    {
                        EmailAddress = new GraphEmailAddress { Name = fromName, Address = fromAddress }
                    },
                    ToRecipients = BuildRecipients(random)
                };

                if (includeBody)
                {
                    msg.Body = new GraphEmailBody
                    {
                        ContentType = (i % 3 == 0) ? "html" : "text",
                        Content = (i % 3 == 0)
                            ? $"<html><body><p>{BodySnippets[random.Next(BodySnippets.Length)]}</p></body></html>"
                            : BodySnippets[random.Next(BodySnippets.Length)]
                    };
                }

                messages.Add(msg);
            }

            // Simulate a single delta-token round-trip per user (read on entry, write on exit).
            return Task.FromResult(new SentEmailLoadResult
            {
                Messages = messages,
                DeltaTokenReads = 1,
                DeltaTokenWrites = 1
            });
        }

        private List<GraphEmailRecipient> BuildRecipients(Random random)
        {
            int count = 1 + random.Next(_maxRecipientsPerEmail);
            var list = new List<GraphEmailRecipient>(count);
            for (int i = 0; i < count; i++)
            {
                int slot = random.Next(_distinctRecipientPoolSize);
                var domain = DomainPool[slot % DomainPool.Length];
                var address = $"recipient-{slot:D6}@{domain}";
                list.Add(new GraphEmailRecipient
                {
                    EmailAddress = new GraphEmailAddress
                    {
                        Name = $"Recipient {slot}",
                        Address = address
                    }
                });
            }
            return list;
        }
    }
}
