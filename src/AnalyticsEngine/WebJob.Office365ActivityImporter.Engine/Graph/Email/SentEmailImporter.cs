using Azure.AI.TextAnalytics;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Email;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Imports sent emails from user mailboxes via Graph API
    /// </summary>
    public class SentEmailImporter : AbstractApiLoader
    {
        private readonly ManualGraphCallClient _httpClient;
        private readonly IDeltaTokenStore _deltaTokenStore;

        public SentEmailImporter(AnalyticsLogger telemetry, AppConfig settings, ManualGraphCallClient httpClient, IDeltaTokenStore deltaTokenStore)
            : base(telemetry, settings)
        {
            _httpClient = httpClient;
            _deltaTokenStore = deltaTokenStore;
        }

        public async Task ImportSentEmails()
        {
            _telemetry.LogInformation("Starting sent emails import...");

            // Load all users from DB
            List<Common.Entities.User> users;
            using (var db = new AnalyticsEntitiesContext())
            {
                users = await db.users.Where(u => u.Mail != null && u.Mail != "").ToListAsync();
            }

            if (users == null || users.Count == 0)
            {
                _telemetry.LogWarning("No users found with email addresses to scan for sent items.");
                return;
            }

            _telemetry.LogInformation($"Found {users.Count} users with email addresses to scan for sent items.");

            TextAnalyticsClient cognitiveClient = null;
            if (_settings.IsValidCognitiveConfig)
            {
                cognitiveClient = new TextAnalyticsClient(
                    new Uri(_settings.CognitiveEndpoint),
                    new Azure.AzureKeyCredential(_settings.CognitiveKey));
            }

            foreach (var user in users)
            {
                try
                {
                    await ImportSentEmailsForUser(user, cognitiveClient);
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    // User mailbox may not be accessible - log and continue
                    _telemetry.LogWarning($"Could not access sent items for user '{user.UserPrincipalName}': {ex.Message}");
                }
                catch (Exception ex)
                {
                    _telemetry.LogError(ex, $"Error importing sent emails for user '{user.UserPrincipalName}': {ex.Message}");
                }
            }

            _telemetry.LogInformation("Finished sent emails import.");
        }

        internal async Task ImportSentEmailsForUser(Common.Entities.User user, TextAnalyticsClient cognitiveClient)
        {
            var deltaKey = $"SentEmails-{user.UserPrincipalName}";
            var deltaToken = await _deltaTokenStore.GetDeltaToken(deltaKey);

            var url = $"https://graph.microsoft.com/v1.0/users/{user.UserPrincipalName}/mailFolders/sentitems/messages/delta" +
                      "?$select=id,subject,from,toRecipients,sentDateTime,body";

            if (!string.IsNullOrEmpty(deltaToken))
            {
                url += $"&$deltatoken={deltaToken}";
            }

            var messages = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphSentMessage>(url, _telemetry,
                async (deltaLink) =>
                {
                    var thisPageDelta = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    await _deltaTokenStore.SetDeltaToken(deltaKey, thisPageDelta);
                });

            if (messages.Count == 0)
            {
                return;
            }

            _telemetry.LogInformation($"Found {messages.Count} sent messages for user '{user.UserPrincipalName}'.");

            using (var db = new AnalyticsEntitiesContext())
            {
                var emailAddressCache = new EmailAddressCache(db);

                foreach (var msg in messages)
                {
                    if (string.IsNullOrEmpty(msg.Id))
                        continue;

                    // Check if already imported
                    var existing = await db.SentEmails.AnyAsync(s => s.GraphMessageId == msg.Id);
                    if (existing)
                        continue;

                    var fromAddr = msg.From?.EmailAddress?.Address;
                    if (string.IsNullOrEmpty(fromAddr))
                        continue;

                    var fromEmailEntity = await emailAddressCache.GetOrCreateEmailAddress(fromAddr.ToLower());

                    // Process each recipient
                    var toRecipients = msg.ToRecipients ?? new List<GraphEmailRecipient>();
                    foreach (var recipient in toRecipients)
                    {
                        var toAddr = recipient?.EmailAddress?.Address;
                        if (string.IsNullOrEmpty(toAddr))
                            continue;

                        var toEmailEntity = await emailAddressCache.GetOrCreateEmailAddress(toAddr.ToLower());

                        var sentEmail = new SentEmail
                        {
                            GraphMessageId = msg.Id + "_" + toAddr.ToLower(),
                            Subject = msg.Subject?.Length > 1000 ? msg.Subject.Substring(0, 1000) : msg.Subject,
                            SentDate = msg.SentDateTime ?? DateTime.MinValue,
                            FromAddressID = fromEmailEntity.ID,
                            ToAddressID = toEmailEntity.ID,
                            UserID = user.ID
                        };

                        // Cognitive score if configured
                        if (cognitiveClient != null && !string.IsNullOrEmpty(msg.Body?.Content))
                        {
                            try
                            {
                                var plainText = StripHtml(msg.Body.Content);
                                if (!string.IsNullOrWhiteSpace(plainText))
                                {
                                    var sentiment = await cognitiveClient.AnalyzeSentimentAsync(plainText);
                                    sentEmail.CognitiveScore = sentiment.Value.ConfidenceScores.Positive;
                                }
                            }
                            catch (Exception ex)
                            {
                                _telemetry.LogWarning($"Cognitive analysis failed for message {msg.Id}: {ex.Message}");
                            }
                        }

                        db.SentEmails.Add(sentEmail);
                    }
                }

                await db.SaveChangesAsync();
            }
        }

        internal static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            // Simple HTML tag removal
            var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            return text.Trim();
        }
    }
}

#region Graph DTO classes

public class GraphSentMessage
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("subject")]
    public string Subject { get; set; }

    [JsonProperty("sentDateTime")]
    public DateTime? SentDateTime { get; set; }

    [JsonProperty("from")]
    public GraphEmailRecipient From { get; set; }

    [JsonProperty("toRecipients")]
    public List<GraphEmailRecipient> ToRecipients { get; set; }

    [JsonProperty("body")]
    public GraphEmailBody Body { get; set; }
}

public class GraphEmailRecipient
{
    [JsonProperty("emailAddress")]
    public GraphEmailAddress EmailAddress { get; set; }
}

public class GraphEmailAddress
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("address")]
    public string Address { get; set; }
}

public class GraphEmailBody
{
    [JsonProperty("contentType")]
    public string ContentType { get; set; }

    [JsonProperty("content")]
    public string Content { get; set; }
}

#endregion

#region Delta Token Store

/// <summary>
/// Interface for per-user delta token storage
/// </summary>
public interface IDeltaTokenStore
{
    Task<string> GetDeltaToken(string key);
    Task SetDeltaToken(string key, string deltaToken);
}

/// <summary>
/// In-memory delta token store
/// </summary>
public class InMemoryDeltaTokenStore : IDeltaTokenStore
{
    private readonly Dictionary<string, string> _tokens = new Dictionary<string, string>();

    public Task<string> GetDeltaToken(string key)
    {
        _tokens.TryGetValue(key, out var token);
        return Task.FromResult(token);
    }

    public Task SetDeltaToken(string key, string deltaToken)
    {
        _tokens[key] = deltaToken;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Redis-based delta token store for per-user keys
/// </summary>
public class RedisDeltaTokenStore : IDeltaTokenStore
{
    private readonly Common.Entities.Redis.CacheConnectionManager _cacheConnectionManager;

    public RedisDeltaTokenStore(string redisConnectionString)
    {
        _cacheConnectionManager = Common.Entities.Redis.CacheConnectionManager.GetConnectionManager(redisConnectionString);
    }

    public async Task<string> GetDeltaToken(string key)
    {
        return await _cacheConnectionManager.GetString(key);
    }

    public async Task SetDeltaToken(string key, string deltaToken)
    {
        await _cacheConnectionManager.SetString(key, deltaToken);
    }
}

    #endregion
}
