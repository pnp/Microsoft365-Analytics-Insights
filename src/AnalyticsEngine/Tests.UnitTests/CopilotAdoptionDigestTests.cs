using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.CopilotAdoption;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotAdoptionDigestTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void RenderedDigest_IsAggregateOnlyEvenWhenAnalysisContainsPerUserData()
        {
            var summary = BuildSuppressedSummary();
            summary.Targets.Add(new CopilotAdoptionTarget
            {
                Metric = CopilotAdoptionTargetMetricCodes.AdoptionRatePct,
                Label = "Adoption rate",
                ScopeType = "tenant",
                TargetValue = 80,
                CurrentValue = 60,
                ProgressPct = 50,
                Comparable = true,
                Owner = "Adele Vance"
            });

            var message = CopilotAdoptionDigestRenderer.Render(summary, "https://contoso.example/");

            var body = message.HtmlBody;
            AssertNoIndividualData(body);
            StringAssert.Contains(body, "Open the Copilot Adoption portal");
            StringAssert.Contains(body, "No intervention cohort outcome summary is available");
            Assert.IsFalse(body.Contains("Attachment"), "The digest body must link to the portal, not advertise an attachment.");
        }

        [TestMethod]
        public void RenderedDigest_UsesExistingMinSeatsSuppressionForNamedSegments()
        {
            var summary = BuildSuppressedSummary();
            var body = CopilotAdoptionDigestRenderer.Render(summary, "https://contoso.example/").HtmlBody;

            StringAssert.Contains(body, "Enterprise Adoption");
            Assert.IsFalse(body.Contains("Tiny Research"), "Segments below MinSeatsPerSegment must not be named in an emailed digest.");
        }

        [TestMethod]
        public async Task UnconfiguredDeployment_SendsNothing()
        {
            var source = new FakeDigestSource { Summary = BuildSuppressedSummary() };
            var sender = new FakeDigestSender();
            var store = new FakeDigestRunStore();
            var result = await Phase(Config(recipients: null, senderUser: null), source, sender, store).RunAsync();

            Assert.AreEqual(CopilotAdoptionDigestStatus.NotConfigured, result.Status);
            Assert.AreEqual(0, sender.SendCount);
            Assert.AreEqual(0, store.Claims.Count);
        }

        [TestMethod]
        public async Task RetryAfterSuccessfulSend_DoesNotDoubleSend()
        {
            var source = new FakeDigestSource { Summary = BuildSuppressedSummary() };
            var sender = new FakeDigestSender();
            var store = new FakeDigestRunStore();
            var phase = Phase(Config(new[] { "execs@contoso.example" }, "digest-sender@contoso.example"), source, sender, store);

            var first = await phase.RunAsync();
            var second = await phase.RunAsync();

            Assert.AreEqual(CopilotAdoptionDigestStatus.Sent, first.Status);
            Assert.AreEqual(CopilotAdoptionDigestStatus.NotDue, second.Status);
            Assert.AreEqual(1, sender.SendCount, "A retry for the same closed period and recipient set must not double-send.");
        }

        [TestMethod]
        public async Task SqlRunStore_ClaimsAndStampsOneSendPerPeriodRecipientSet()
        {
            using (var db = ScratchDatabase.Create("CopilotDigestStore"))
            {
                db.Execute(Common.Entities.Migrations.CopilotAdoptionDigest.Up_Sql);
                var store = new SqlCopilotAdoptionDigestRunStore(new TestContextFactory(db.ConnectionString));
                var hash = CopilotAdoptionDigestPhase.HashRecipients(new[] { "execs@contoso.example" });

                var first = await store.TryClaimSendAsync(Now.Date, 28, hash, "Subject", "https://contoso.example/#/insights/copilot-adoption", Now, CancellationToken.None);
                await store.MarkSentAsync(first.Id, Now.AddMinutes(1), CancellationToken.None);
                var second = await store.TryClaimSendAsync(Now.Date, 28, hash, "Subject", "https://contoso.example/#/insights/copilot-adoption", Now.AddMinutes(2), CancellationToken.None);
                var latest = await store.GetLatestSentUtcAsync(CancellationToken.None);

                Assert.IsTrue(first.Claimed);
                Assert.IsFalse(second.Claimed);
                Assert.AreEqual("Sent", second.Status);
                Assert.AreEqual(Now.AddMinutes(1), latest);
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.copilot_adoption_digest_run")));
            }
        }

        [TestMethod]
        public async Task GenerationFailure_IsRecordedForHealth()
        {
            var source = new FakeDigestSource { Exception = new InvalidOperationException("synthetic generation failure") };
            var store = new FakeDigestRunStore();
            var result = await Phase(Config(new[] { "execs@contoso.example" }, "digest-sender@contoso.example"), source, new FakeDigestSender(), store).RunAsync();

            Assert.AreEqual(CopilotAdoptionDigestStatus.Failed, result.Status);
            Assert.AreEqual(1, store.Failures.Count);
            Assert.AreEqual("generate", store.Failures.Single().Phase);
            StringAssert.Contains(store.Failures.Single().Error, "synthetic generation failure");
        }

        private static CopilotAdoptionDigestPhase Phase(AppConfig config, FakeDigestSource source, FakeDigestSender sender, FakeDigestRunStore store)
            => new CopilotAdoptionDigestPhase(config, source, sender, store, new FixedClock(Now), logger: null);

        private static AppConfig Config(IEnumerable<string> recipients = null, string senderUser = null)
        {
            ConfigurationManager.AppSettings.Set("TenantGUID", "00000000-0000-0000-0000-000000000000");
            return new TestsAppConfig
            {
                ImportJobSettings = new ImportTaskSettings { GraphUsersMetadata = true, Copilot = true },
                WebAppURL = "https://contoso.example/",
                CopilotAdoptionDigestRecipients = recipients?.ToList() ?? new List<string>(),
                CopilotAdoptionDigestSenderUserId = senderUser ?? string.Empty,
            };
        }

        private static CopilotAdoptionSummary BuildSuppressedSummary()
        {
            var options = new CopilotAdoptionOptions { MinSeatsPerSegment = 5 };
            var analysis = new CopilotAdoptionAnalysis
            {
                Summary =
                {
                    GeneratedUtc = Now,
                    WindowDays = options.WindowDays,
                    FromUtc = Now.Date.AddDays(-(options.WindowDays - 1)),
                    ToUtc = Now.Date,
                    Options = options,
                    LicensedUsers = 9,
                }
            };

            for (var i = 0; i < 5; i++)
            {
                analysis.LicensedUsers.Add(User(i + 1, $"enterprise-{i}@contoso.example", "Enterprise Adoption", active: i < 3));
            }
            for (var i = 0; i < 4; i++)
            {
                analysis.LicensedUsers.Add(User(i + 100, $"tiny-{i}@contoso.example", "Tiny Research", active: false));
            }

            var service = new CopilotAdoptionService(options, maxConcurrentSteps: 1);
            service.FinaliseSummary(analysis);
            analysis.Summary.PeriodMovement = new CopilotAdoptionPeriodMovement
            {
                Comparable = true,
                ComparisonLabel = "previous closed period",
                Deltas = new List<CopilotAdoptionMetricDelta>
                {
                    new CopilotAdoptionMetricDelta { Metric = CopilotAdoptionTargetMetricCodes.AdoptionRatePct, Change = 4.2 },
                    new CopilotAdoptionMetricDelta { Metric = CopilotAdoptionTargetMetricCodes.HabitRatePct, Change = 1.1 },
                    new CopilotAdoptionMetricDelta { Metric = CopilotAdoptionTargetMetricCodes.ReclaimableSeats, Change = -2 },
                    new CopilotAdoptionMetricDelta { Metric = CopilotAdoptionTargetMetricCodes.NeverUsedUsers, Change = -1 },
                    new CopilotAdoptionMetricDelta { Metric = CopilotAdoptionTargetMetricCodes.DormantUsers, Change = 0 },
                    new CopilotAdoptionMetricDelta { Metric = CopilotAdoptionTargetMetricCodes.RecommendedForLicence, Change = 3 },
                }
            };
            return analysis.Summary;
        }

        private static LicensedUserAdoptionRow User(int id, string upn, string department, bool active)
        {
            return new LicensedUserAdoptionRow
            {
                UserId = id,
                UserPrincipalName = upn,
                Mail = upn,
                Department = department,
                Band = active ? AdoptionBand.Established : AdoptionBand.NeverUsed,
                ActiveDays = active ? 10 : 0,
                Interactions = active ? 40 : 0,
                AppsUsed = active ? 2 : 0,
                AdoptionScore = active ? 60 : 0,
                RecommendedActionCode = active ? CopilotAdoptionScoring.AdoptionActionCodes.Sustain : CopilotAdoptionScoring.AdoptionActionCodes.Reclaim,
            };
        }

        private static void AssertNoIndividualData(string body)
        {
            foreach (var forbidden in new[]
            {
                "enterprise-0@contoso.example",
                "tiny-0@contoso.example",
                "userPrincipalName",
                "mail",
                "Adele Vance"
            })
            {
                Assert.IsFalse(body.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0, $"Digest leaked individual data token '{forbidden}'.");
            }
        }

        private class FakeDigestSource : ICopilotAdoptionDigestAnalysisSource
        {
            public CopilotAdoptionSummary Summary { get; set; }
            public Exception Exception { get; set; }

            public Task<CopilotAdoptionSummary> BuildDigestSummaryAsync(DateTime nowUtc, CancellationToken cancellationToken)
            {
                if (Exception != null) throw Exception;
                return Task.FromResult(Summary);
            }
        }

        private class FakeDigestSender : ICopilotAdoptionDigestSender
        {
            public int SendCount { get; private set; }
            public Task SendAsync(string senderUserId, IReadOnlyList<string> recipients, CopilotAdoptionDigestMessage message, CancellationToken cancellationToken)
            {
                SendCount++;
                AssertNoIndividualData(message.HtmlBody);
                return Task.CompletedTask;
            }
        }

        private class FakeDigestRunStore : ICopilotAdoptionDigestRunStore
        {
            private readonly HashSet<string> _sent = new HashSet<string>(StringComparer.Ordinal);
            public List<string> Claims { get; } = new List<string>();
            public List<Failure> Failures { get; } = new List<Failure>();
            public DateTime? LatestSentUtc { get; set; }

            public Task<DateTime?> GetLatestSentUtcAsync(CancellationToken cancellationToken) => Task.FromResult(LatestSentUtc);

            public Task<CopilotAdoptionDigestSendClaim> TryClaimSendAsync(DateTime periodEndUtc, int periodDays, string recipientsHash, string subject, string portalUrl, DateTime nowUtc, CancellationToken cancellationToken)
            {
                var key = periodEndUtc.ToString("yyyy-MM-dd") + ":" + periodDays + ":" + recipientsHash;
                Claims.Add(key);
                if (_sent.Contains(key)) return Task.FromResult(new CopilotAdoptionDigestSendClaim { Id = 1, Claimed = false, Status = "Sent" });
                _sent.Add(key);
                return Task.FromResult(new CopilotAdoptionDigestSendClaim { Id = 1, Claimed = true, Status = "Sending" });
            }

            public Task MarkSentAsync(int id, DateTime nowUtc, CancellationToken cancellationToken)
            {
                LatestSentUtc = nowUtc;
                return Task.CompletedTask;
            }

            public Task MarkFailedAsync(int? id, DateTime? periodEndUtc, int? periodDays, string recipientsHash, string subject, string portalUrl, string phase, string error, DateTime nowUtc, CancellationToken cancellationToken)
            {
                Failures.Add(new Failure { Phase = phase, Error = error });
                return Task.CompletedTask;
            }
        }

        private class Failure
        {
            public string Phase { get; set; }
            public string Error { get; set; }
        }

        private sealed class TestContextFactory : IAnalyticsDbContextFactory
        {
            private readonly string _connectionString;
            public TestContextFactory(string connectionString) { _connectionString = connectionString; }
            public AnalyticsEntitiesContext Create()
            {
                Database.SetInitializer<AnalyticsEntitiesContext>(null);
                return new AnalyticsEntitiesContext(new SqlConnection(_connectionString));
            }
        }
    }
}
