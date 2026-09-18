using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.StatsUploader;

namespace WebJob.Office365ActivityImporter.Engine.CopilotAdoption
{
    public static class CopilotAdoptionDigestStatus
    {
        public const string NotConfigured = "notConfigured";
        public const string NotMeasurable = "notMeasurable";
        public const string NotDue = "notDue";
        public const string Sent = "sent";
        public const string AlreadyHandled = "alreadyHandled";
        public const string Failed = "failed";
    }

    public class CopilotAdoptionDigestResult
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTime? PeriodEnd { get; set; }
    }

    public interface ICopilotAdoptionDigestAnalysisSource
    {
        Task<CopilotAdoptionSummary> BuildDigestSummaryAsync(DateTime nowUtc, CancellationToken cancellationToken);
    }

    public interface ICopilotAdoptionDigestSender
    {
        Task SendAsync(string senderUserId, IReadOnlyList<string> recipients, CopilotAdoptionDigestMessage message, CancellationToken cancellationToken);
    }

    public class CopilotAdoptionDigestSendClaim
    {
        public int Id { get; set; }
        public bool Claimed { get; set; }
        public string Status { get; set; }
    }

    public interface ICopilotAdoptionDigestRunStore
    {
        Task<DateTime?> GetLatestSentUtcAsync(CancellationToken cancellationToken);
        Task<CopilotAdoptionDigestSendClaim> TryClaimSendAsync(DateTime periodEndUtc, int periodDays, string recipientsHash, string subject, string portalUrl, DateTime nowUtc, CancellationToken cancellationToken);
        Task MarkSentAsync(int id, DateTime nowUtc, CancellationToken cancellationToken);
        Task MarkFailedAsync(int? id, DateTime? periodEndUtc, int? periodDays, string recipientsHash, string subject, string portalUrl, string phase, string error, DateTime nowUtc, CancellationToken cancellationToken);
    }

    public class CopilotAdoptionDigestPhase
    {
        private readonly AppConfig _config;
        private readonly ICopilotAdoptionDigestAnalysisSource _source;
        private readonly ICopilotAdoptionDigestSender _sender;
        private readonly ICopilotAdoptionDigestRunStore _store;
        private readonly IClock _clock;
        private readonly ILogger _logger;

        public CopilotAdoptionDigestPhase(AppConfig config, ManualGraphCallClient graphClient, ILogger logger)
            : this(config, new PublishedPeriodDigestAnalysisSource(), new GraphCopilotAdoptionDigestSender(graphClient), new SqlCopilotAdoptionDigestRunStore(), SystemClock.Instance, logger)
        {
        }

        internal CopilotAdoptionDigestPhase(AppConfig config, ICopilotAdoptionDigestAnalysisSource source, ICopilotAdoptionDigestSender sender, ICopilotAdoptionDigestRunStore store, IClock clock, ILogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? SystemClock.Instance;
            _logger = logger;
        }

        public async Task<CopilotAdoptionDigestResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!_config.CopilotAdoptionDigestConfigured)
            {
                _logger?.LogInformation("Copilot Adoption digest is not configured; no recipients/sender means no mail is sent.");
                return Result(CopilotAdoptionDigestStatus.NotConfigured, "Copilot Adoption digest is not configured.", null);
            }

            if (!AdoptionStatsCollector.CanMeasureAdoption(_config.ImportJobSettings))
            {
                _logger?.LogInformation("Copilot Adoption digest needs user metadata plus a Copilot signal import; skipping.");
                return Result(CopilotAdoptionDigestStatus.NotMeasurable, "Copilot Adoption cannot be measured from the enabled imports.", null);
            }

            var now = _clock.UtcNow;
            CopilotAdoptionSummary summary = null;
            var recipientsHash = HashRecipients(_config.CopilotAdoptionDigestRecipients);
            string subject = null;
            string portalUrl = PortalUrl(_config.WebAppURL);
            int? claimId = null;

            try
            {
                var latestSent = await _store.GetLatestSentUtcAsync(cancellationToken).ConfigureAwait(false);
                if (!ImportCadenceGate.ShouldRun(latestSent, _config.CopilotAdoptionDigestIntervalHours, force: false, nowUtc: now))
                {
                    return Result(CopilotAdoptionDigestStatus.NotDue, $"The last Copilot Adoption digest was sent at {latestSent:O}; the next send is due after {_config.CopilotAdoptionDigestIntervalHours} hours.", null);
                }

                summary = await _source.BuildDigestSummaryAsync(now, cancellationToken).ConfigureAwait(false);
                if (summary == null)
                {
                    return Result(CopilotAdoptionDigestStatus.NotDue, "No closed Copilot Adoption period is ready for a digest.", null);
                }

                var message = CopilotAdoptionDigestRenderer.Render(summary, _config.WebAppURL);
                subject = message.Subject;
                var claim = await _store.TryClaimSendAsync(summary.ToUtc.Date, summary.WindowDays, recipientsHash, subject, portalUrl, now, cancellationToken).ConfigureAwait(false);
                if (!claim.Claimed)
                {
                    return Result(CopilotAdoptionDigestStatus.AlreadyHandled, "This Copilot Adoption digest period has already been claimed or sent.", summary.ToUtc.Date);
                }
                claimId = claim.Id;

                await _sender.SendAsync(_config.CopilotAdoptionDigestSenderUserId, _config.CopilotAdoptionDigestRecipients, message, cancellationToken).ConfigureAwait(false);
                await _store.MarkSentAsync(claim.Id, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation($"Copilot Adoption digest sent for period ending {summary.ToUtc:yyyy-MM-dd} to {_config.CopilotAdoptionDigestRecipients.Count} configured recipient(s). Body is aggregate-only and has no attachments.");
                return Result(CopilotAdoptionDigestStatus.Sent, "Copilot Adoption digest sent.", summary.ToUtc.Date);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Copilot Adoption digest failed: {ex.Message}");
                try
                {
                    await _store.MarkFailedAsync(claimId, summary?.ToUtc.Date, summary?.WindowDays, recipientsHash, subject, portalUrl, summary == null ? "generate" : "send", ex.Message, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception logEx)
                {
                    _logger?.LogWarning($"Could not persist Copilot Adoption digest failure for Health: {logEx.Message}");
                }
                return Result(CopilotAdoptionDigestStatus.Failed, ex.Message, summary?.ToUtc.Date);
            }
        }

        private static CopilotAdoptionDigestResult Result(string status, string message, DateTime? periodEnd)
            => new CopilotAdoptionDigestResult { Status = status, Message = message, PeriodEnd = periodEnd };

        internal static string HashRecipients(IReadOnlyList<string> recipients)
        {
            var material = string.Join(";", (recipients ?? new List<string>()).Select(r => (r ?? string.Empty).Trim().ToUpperInvariant()).OrderBy(r => r, StringComparer.Ordinal));
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(material))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        internal static string PortalUrl(string webAppUrl)
        {
            var root = (webAppUrl ?? string.Empty).Trim();
            if (root.Length > 0 && !root.EndsWith("/", StringComparison.Ordinal)) root += "/";
            return root + "#/insights/copilot-adoption";
        }
    }

    public class PublishedPeriodDigestAnalysisSource : ICopilotAdoptionDigestAnalysisSource
    {
        public async Task<CopilotAdoptionSummary> BuildDigestSummaryAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            var service = new CopilotAdoptionService(CopilotAdoptionOptions.Default, contextFactory: null, maxConcurrentSteps: 1);
            var publish = await service.PublishClosedPeriodAsync(nowUtc.Date.AddDays(-1), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (publish?.Run == null) return null;

            var period = await service.ReadPublishedPeriodAsync(publish.Run.PeriodEnd, publish.Run.PeriodDays, cancellationToken).ConfigureAwait(false);
            if (period == null) return null;

            await service.EnrichProgressAsync(period.Analysis, CopilotAdoptionComparisonModes.PreviousPeriod, cancellationToken).ConfigureAwait(false);
            return period.Analysis.Summary;
        }
    }

    public class GraphCopilotAdoptionDigestSender : ICopilotAdoptionDigestSender
    {
        private readonly ManualGraphCallClient _client;

        public GraphCopilotAdoptionDigestSender(ManualGraphCallClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task SendAsync(string senderUserId, IReadOnlyList<string> recipients, CopilotAdoptionDigestMessage message, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(senderUserId)) throw new ArgumentException("A digest sender user id is required.", nameof(senderUserId));
            if (recipients == null || recipients.Count == 0) throw new ArgumentException("At least one digest recipient is required.", nameof(recipients));
            if (message == null) throw new ArgumentNullException(nameof(message));

            var body = new
            {
                message = new
                {
                    subject = message.Subject,
                    body = new { contentType = "HTML", content = message.HtmlBody },
                    toRecipients = recipients.Select(r => new { emailAddress = new { address = r } }).ToArray(),
                },
                saveToSentItems = false,
            };
            var json = JsonConvert.SerializeObject(body);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await _client.PostAsync("https://graph.microsoft.com/v1.0/users/" + Uri.EscapeDataString(senderUserId.Trim()) + "/sendMail", content, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }
        }
    }

    public class SqlCopilotAdoptionDigestRunStore : ICopilotAdoptionDigestRunStore
    {
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public SqlCopilotAdoptionDigestRunStore() : this(DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        public SqlCopilotAdoptionDigestRunStore(IAnalyticsDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task<DateTime?> GetLatestSentUtcAsync(CancellationToken cancellationToken)
        {
            const string sql = "IF OBJECT_ID(N'dbo.copilot_adoption_digest_run', N'U') IS NULL SELECT CAST(NULL AS datetime2) ELSE EXEC sp_executesql N'SELECT MAX(sent_utc) FROM dbo.copilot_adoption_digest_run WHERE status = N''Sent'''";
            using (var db = _contextFactory.Create())
            {
                return (await db.Database.SqlQuery<DateTime?>(sql).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            }
        }

        public async Task<CopilotAdoptionDigestSendClaim> TryClaimSendAsync(DateTime periodEndUtc, int periodDays, string recipientsHash, string subject, string portalUrl, DateTime nowUtc, CancellationToken cancellationToken)
        {
            const string sql = @"SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @id int;
DECLARE @status nvarchar(32);
SELECT @id = id, @status = status
FROM dbo.copilot_adoption_digest_run WITH (UPDLOCK, HOLDLOCK)
WHERE period_end = @periodEnd AND period_days = @periodDays AND recipients_hash = @recipientsHash;
IF @id IS NULL
BEGIN
    INSERT INTO dbo.copilot_adoption_digest_run (period_end, period_days, recipients_hash, subject, portal_url, status, claimed_utc, created_utc, updated_utc)
    VALUES (@periodEnd, @periodDays, @recipientsHash, @subject, @portalUrl, N'Sending', @nowUtc, @nowUtc, @nowUtc);
    SELECT @id = SCOPE_IDENTITY(), @status = N'Sending';
END
COMMIT TRANSACTION;
SELECT @id AS Id, CASE WHEN @status = N'Sending' AND @id = SCOPE_IDENTITY() THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Claimed, @status AS Status;";

            using (var db = _contextFactory.Create())
            {
                var rows = await db.Database.SqlQuery<CopilotAdoptionDigestSendClaim>(
                    sql,
                    new SqlParameter("@periodEnd", periodEndUtc.Date),
                    new SqlParameter("@periodDays", periodDays),
                    new SqlParameter("@recipientsHash", recipientsHash ?? string.Empty),
                    new SqlParameter("@subject", (object)subject ?? DBNull.Value),
                    new SqlParameter("@portalUrl", (object)portalUrl ?? DBNull.Value),
                    new SqlParameter("@nowUtc", nowUtc)).ToListAsync(cancellationToken).ConfigureAwait(false);
                return rows.First();
            }
        }

        public async Task MarkSentAsync(int id, DateTime nowUtc, CancellationToken cancellationToken)
        {
            const string sql = "UPDATE dbo.copilot_adoption_digest_run SET status = N'Sent', sent_utc = @nowUtc, completed_utc = @nowUtc, updated_utc = @nowUtc, error = NULL WHERE id = @id";
            using (var db = _contextFactory.Create())
            {
                await db.Database.ExecuteSqlCommandAsync(sql, cancellationToken, new SqlParameter("@id", id), new SqlParameter("@nowUtc", nowUtc)).ConfigureAwait(false);
            }
        }

        public async Task MarkFailedAsync(int? id, DateTime? periodEndUtc, int? periodDays, string recipientsHash, string subject, string portalUrl, string phase, string error, DateTime nowUtc, CancellationToken cancellationToken)
        {
            const string update = "UPDATE dbo.copilot_adoption_digest_run SET status = N'Failed', phase = @phase, error = @error, completed_utc = @nowUtc, updated_utc = @nowUtc WHERE id = @id";
            const string insert = @"INSERT INTO dbo.copilot_adoption_digest_run (period_end, period_days, recipients_hash, subject, portal_url, status, phase, error, completed_utc, created_utc, updated_utc)
VALUES (@periodEnd, @periodDays, @recipientsHash, @subject, @portalUrl, N'Failed', @phase, @error, @nowUtc, @nowUtc, @nowUtc)";
            using (var db = _contextFactory.Create())
            {
                if (id.HasValue)
                {
                    await db.Database.ExecuteSqlCommandAsync(update, cancellationToken,
                        new SqlParameter("@id", id.Value), new SqlParameter("@phase", (object)phase ?? DBNull.Value), new SqlParameter("@error", (object)error ?? DBNull.Value), new SqlParameter("@nowUtc", nowUtc)).ConfigureAwait(false);
                }
                else
                {
                    await db.Database.ExecuteSqlCommandAsync(insert, cancellationToken,
                        new SqlParameter("@periodEnd", (object)periodEndUtc?.Date ?? DBNull.Value),
                        new SqlParameter("@periodDays", (object)periodDays ?? DBNull.Value),
                        new SqlParameter("@recipientsHash", (object)recipientsHash ?? DBNull.Value),
                        new SqlParameter("@subject", (object)subject ?? DBNull.Value),
                        new SqlParameter("@portalUrl", (object)portalUrl ?? DBNull.Value),
                        new SqlParameter("@phase", (object)phase ?? DBNull.Value),
                        new SqlParameter("@error", (object)error ?? DBNull.Value),
                        new SqlParameter("@nowUtc", nowUtc)).ConfigureAwait(false);
                }
            }
        }
    }
}
