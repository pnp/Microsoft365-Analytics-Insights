using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>What <see cref="CallWebhook.CreateOrUpdateWebhook"/> changed.</summary>
    public enum CallWebhookChange
    {
        /// <summary>The subscription was in place with plenty of life left: nothing was asked of Graph but the list.</summary>
        None,

        /// <summary>There was none, and the importer had no record of one: a first create.</summary>
        Created,

        /// <summary>The subscription the importer created had lapsed (or was deleted) and was created again.</summary>
        Recreated,

        Renewed,

        /// <summary>A subscription with an older clientState was replaced by one with the current clientState.</summary>
        Replaced
    }

    /// <summary>The outcome of one <see cref="CallWebhook.CreateOrUpdateWebhook"/> call.</summary>
    public sealed class CallWebhookMaintenanceResult
    {
        public CallWebhookMaintenanceResult(CallWebhookChange change, string subscriptionId, DateTime? expiresUtc)
        {
            Change = change;
            SubscriptionId = subscriptionId;
            ExpiresUtc = expiresUtc;
        }

        public CallWebhookChange Change { get; }

        /// <summary>The subscription now carrying this webhook's notifications.</summary>
        public string SubscriptionId { get; }

        public DateTime? ExpiresUtc { get; }
    }

    /// <summary>
    /// Makes the webhook answer one request before Graph sends its validation request, so a web app that is starting up
    /// (after a client-secret rotation restarts it, or with Always On off) has warmed up by the time Graph asks. Graph
    /// gives up on validation after about 10 seconds; a cold App Service can take longer than that to answer at all.
    /// </summary>
    public interface ICallWebhookWarmer
    {
        /// <summary>Never throws: a failure is reported in the result.</summary>
        Task<CallWebhookWarmUpResult> WarmUpAsync(Uri notificationUrl, CancellationToken cancellationToken);
    }

    public sealed class CallWebhookWarmUpResult
    {
        public bool Succeeded { get; set; }

        public long ElapsedMs { get; set; }

        /// <summary>Why it failed - an HTTP status, a timeout or an exception type. Never a response body.</summary>
        public string Failure { get; set; }
    }

    /// <summary>
    /// Sends the webhook the same harmless request Graph's validation does - a POST with a <c>validationToken</c> - and
    /// checks the token is echoed. The web app answers it without touching a notification, Service Bus or storage.
    /// </summary>
    public sealed class HttpCallWebhookWarmer : ICallWebhookWarmer
    {
        /// <summary>Long enough for an App Service cold start; the importer waits for it only before a create or renewal.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

        // One per process, as HttpClient is meant to be used.
        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout };

        public async Task<CallWebhookWarmUpResult> WarmUpAsync(Uri notificationUrl, CancellationToken cancellationToken)
        {
            var token = "warm-up " + Guid.NewGuid().ToString("N");
            var url = notificationUrl + (string.IsNullOrEmpty(notificationUrl.Query) ? "?" : "&") + "validationToken=" + Uri.EscapeDataString(token);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"))
                using (var response = await Client.PostAsync(url, content, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK && string.Equals(body, token, StringComparison.Ordinal))
                    {
                        return new CallWebhookWarmUpResult { Succeeded = true, ElapsedMs = timer.ElapsedMilliseconds };
                    }

                    return new CallWebhookWarmUpResult
                    {
                        ElapsedMs = timer.ElapsedMilliseconds,
                        Failure = response.StatusCode == HttpStatusCode.OK
                            ? "HTTP 200 without the validation token echoed back"
                            : $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                    };
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CallWebhookWarmUpResult { ElapsedMs = timer.ElapsedMilliseconds, Failure = $"no answer within {Timeout.TotalSeconds:0} s" };
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                var inner = ex.GetBaseException();
                return new CallWebhookWarmUpResult { ElapsedMs = timer.ElapsedMilliseconds, Failure = $"{ex.GetType().Name}: {inner.Message}" };
            }
        }
    }
}
