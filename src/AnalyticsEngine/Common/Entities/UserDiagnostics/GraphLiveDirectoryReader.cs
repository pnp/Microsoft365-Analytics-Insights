using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserDiagnostics
{
    /// <summary>The outcome of one live directory read: an observation, or why there is none.</summary>
    public sealed class LiveDirectoryReadResult
    {
        public LiveDirectoryObservation Observation { get; private set; }

        /// <summary>The user object exactly as Graph returned it, for a caller that maps more than licences.</summary>
        public JObject UserJson { get; private set; }

        /// <summary>One of <see cref="LiveComparisonReasons"/> when <see cref="Observation"/> is null.</summary>
        public string FailureReason { get; private set; }

        public int? HttpStatus { get; private set; }

        public static LiveDirectoryReadResult Ok(LiveDirectoryObservation observation, JObject userJson = null) =>
            new LiveDirectoryReadResult { Observation = observation, UserJson = userJson };

        public static LiveDirectoryReadResult Failed(string reason, int? httpStatus = null) =>
            new LiveDirectoryReadResult { FailureReason = reason, HttpStatus = httpStatus };
    }

    /// <summary>Reads one user's current licence assignments from the directory. Read-only.</summary>
    public interface ILiveDirectoryReader
    {
        /// <param name="objectIdOrUserPrincipalName">The stored object id when there is one; it survives a rename.</param>
        /// <param name="profileSelect">
        /// Extra properties to read in the same request (the user import's own selection, for a profile refresh), or
        /// null for licences only. The manager's id is expanded when this is set.
        /// </param>
        Task<LiveDirectoryReadResult> ReadAsync(string objectIdOrUserPrincipalName, CancellationToken cancellationToken, string profileSelect = null);
    }

    /// <summary>
    /// Microsoft Graph implementation of <see cref="ILiveDirectoryReader"/>, with the runtime app registration: two
    /// requests for one user (<c>/users/{id}</c> and <c>/users/{id}/licenseDetails</c>), never a directory scan.
    /// Needs <c>User.Read.All</c>, which the user import already requires.
    /// </summary>
    public sealed class GraphLiveDirectoryReader : ILiveDirectoryReader
    {
        private const string GraphRoot = "https://graph.microsoft.com/v1.0";

        /// <summary>A diagnostic page must answer: give up rather than wait out a long throttle.</summary>
        public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);

        private readonly Func<AutoThrottleHttpClient> _createClient;
        private readonly ILogger _logger;

        public GraphLiveDirectoryReader(Func<AutoThrottleHttpClient> createClient, ILogger logger)
        {
            _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>The reader for this deployment's runtime app registration.</summary>
        public static GraphLiveDirectoryReader ForConfig(AppConfig config, ILogger logger)
        {
            return new GraphLiveDirectoryReader(() =>
            {
                var auth = new GraphDirectoryOAuthContext(logger ?? NullLogger.Instance, config.ClientID, config.TenantGUID.ToString(),
                    config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);
                return new ConfidentialClientApplicationThrottledHttpClient(auth, false, logger ?? NullLogger.Instance);
            }, logger);
        }

        public async Task<LiveDirectoryReadResult> ReadAsync(string objectIdOrUserPrincipalName, CancellationToken cancellationToken, string profileSelect = null)
        {
            if (string.IsNullOrWhiteSpace(objectIdOrUserPrincipalName)) throw new ArgumentException("An object id or UPN is required.", nameof(objectIdOrUserPrincipalName));

            AutoThrottleHttpClient client;
            try
            {
                client = _createClient();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User diagnostics - couldn't create a Graph client ({ex.GetType().Name}).");
                return LiveDirectoryReadResult.Failed(LiveComparisonReasons.CredentialsUnavailable);
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ReadTimeout);
                try
                {
                    var key = Uri.EscapeDataString(objectIdOrUserPrincipalName.Trim());
                    var select = "id,userPrincipalName,accountEnabled,assignedLicenses,licenseAssignmentStates";
                    if (!string.IsNullOrWhiteSpace(profileSelect))
                    {
                        select = string.Join(",", select.Split(',').Concat(profileSelect.Split(',')).Select(s => s.Trim())
                            .Where(s => s.Length > 0 && !string.Equals(s, "manager", StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                    }
                    var userUrl = $"{GraphRoot}/users/{key}?$select={select}"
                        + (string.IsNullOrWhiteSpace(profileSelect) ? string.Empty : "&$expand=manager($select=id)");
                    JObject user;
                    using (var response = await client.GetAsyncWithThrottleRetries(userUrl, _logger, timeout.Token))
                    {
                        var failure = Classify(response.StatusCode);
                        if (failure != null) return LiveDirectoryReadResult.Failed(failure, (int)response.StatusCode);
                        user = JObject.Parse(await response.Content.ReadAsStringAsync());
                    }

                    var observation = ParseUser(user, DateTime.UtcNow);

                    // Optional: part numbers and service-plan states. Without them the comparison falls back to this
                    // build's catalogue for part numbers and reports the Copilot plans as unknown - it does not fail.
                    var detailsUrl = $"{GraphRoot}/users/{Uri.EscapeDataString(observation.ObjectId ?? key)}/licenseDetails?$select=skuId,skuPartNumber,servicePlans";
                    using (var details = await client.GetAsyncWithThrottleRetries(detailsUrl, _logger, timeout.Token))
                    {
                        if (details.IsSuccessStatusCode)
                        {
                            observation.LicenceDetails = ParseLicenceDetails(JObject.Parse(await details.Content.ReadAsStringAsync()));
                        }
                        else
                        {
                            _logger.LogInformation($"User diagnostics - licenseDetails returned HTTP {(int)details.StatusCode}; using the catalogue for part numbers.");
                        }
                    }

                    return LiveDirectoryReadResult.Ok(observation, user);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return LiveDirectoryReadResult.Failed(LiveComparisonReasons.Timeout);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.LogWarning($"User diagnostics - live directory read failed ({ex.GetType().Name}).");
                    return LiveDirectoryReadResult.Failed(LiveComparisonReasons.GraphUnavailable);
                }
            }
        }

        internal static string Classify(HttpStatusCode status)
        {
            if ((int)status >= 200 && (int)status < 300) return null;
            if (status == HttpStatusCode.NotFound) return LiveComparisonReasons.UserNotFoundInDirectory;
            if (status == HttpStatusCode.Forbidden || status == HttpStatusCode.Unauthorized) return LiveComparisonReasons.PermissionDenied;
            return LiveComparisonReasons.GraphUnavailable;
        }

        /// <summary>Parses a Graph user. Public for tests: the "property present" rule is the contract that matters.</summary>
        public static LiveDirectoryObservation ParseUser(JObject user, DateTime observedAtUtc)
        {
            var observation = new LiveDirectoryObservation
            {
                ObjectId = (string)user["id"],
                UserPrincipalName = (string)user["userPrincipalName"],
                AccountEnabled = (bool?)user["accountEnabled"],
                ObservedAtUtc = observedAtUtc,
            };

            if (user["assignedLicenses"] is JArray assigned)
            {
                observation.AssignedLicencesPresent = true;
                foreach (var licence in assigned)
                {
                    if (Guid.TryParse((string)licence["skuId"], out var skuId))
                    {
                        observation.AssignedSkuIds.Add(skuId);
                    }
                    else
                    {
                        // An entry we cannot identify makes the list incomplete as far as comparison is concerned.
                        observation.AssignedLicencesPresent = false;
                    }
                }
            }

            if (user["licenseAssignmentStates"] is JArray states)
            {
                foreach (var state in states)
                {
                    if (!Guid.TryParse((string)state["skuId"], out var skuId)) continue;
                    observation.AssignmentStates.Add(new LiveAssignmentState
                    {
                        SkuId = skuId,
                        AssignedByGroup = (string)state["assignedByGroup"],
                        State = (string)state["state"],
                        Error = (string)state["error"],
                    });
                }
            }

            return observation;
        }

        public static List<LiveLicenceDetail> ParseLicenceDetails(JObject page)
        {
            var result = new List<LiveLicenceDetail>();
            if (!(page["value"] is JArray values)) return null;

            foreach (var value in values)
            {
                if (!Guid.TryParse((string)value["skuId"], out var skuId)) continue;
                var detail = new LiveLicenceDetail { SkuId = skuId, SkuPartNumber = (string)value["skuPartNumber"] };
                if (value["servicePlans"] is JArray plans)
                {
                    foreach (var plan in plans)
                    {
                        var name = (string)plan["servicePlanName"];
                        if (!string.IsNullOrWhiteSpace(name) && !detail.ServicePlanStatus.ContainsKey(name))
                        {
                            detail.ServicePlanStatus.Add(name, (string)plan["provisioningStatus"]);
                        }
                    }
                }
                result.Add(detail);
            }
            return result;
        }
    }
}
