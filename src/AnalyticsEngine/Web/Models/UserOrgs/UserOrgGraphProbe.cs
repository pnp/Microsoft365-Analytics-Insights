using Azure.Core;
using Azure.Identity;
using Common.Entities.Config;
using Common.Entities.UserOrgs;
using DataUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>What a live attribute probe found.</summary>
    public sealed class UserOrgProbeOutcome
    {
        public bool Succeeded { get; set; }

        /// <summary>The raw value Graph returned, or <c>null</c> when the user has no value for it.</summary>
        public string RawValue { get; set; }

        /// <summary>True when the request worked but the user simply has no value.</summary>
        public bool HasNoValue { get; set; }

        /// <summary>
        /// True only when Graph rejected the <b>property</b> itself, as opposed to failing for a reason
        /// that says nothing about the attribute (user not found, throttled, no permission, offline).
        /// </summary>
        /// <remarks>
        /// This is the distinction that decides whether a configuration may be saved. Refusing a save
        /// because a tenant was briefly throttled would be both wrong and impossible for an
        /// administrator to act on; allowing one for a property Graph will not accept breaks the user
        /// import.
        /// </remarks>
        public bool RejectedTheProperty { get; set; }

        /// <summary>An operator-facing explanation when it failed.</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// Asks Microsoft Graph what an attribute actually resolves to, so an administrator can validate a
    /// configuration before saving it.
    /// </summary>
    /// <remarks>
    /// This is a safety feature, not a convenience. Graph answers an unknown <c>$select</c> property
    /// with a 400 that fails the whole request, so an attribute that does not exist would - once saved -
    /// break the next user import entirely until the importer's fallback kicked in. Probing before
    /// saving is what stops a typo ever reaching the importer.
    /// </remarks>
    public interface IUserOrgGraphProbe
    {
        /// <summary>Resolves one attribute for one user.</summary>
        Task<UserOrgProbeOutcome> ResolveAsync(EntraOrgAttributeSpec spec, string upn, CancellationToken cancellationToken);

        /// <summary>
        /// Lists the directory extensions registered in the tenant. Best-effort: the result carries a
        /// warning rather than an exception when discovery is unavailable.
        /// </summary>
        Task<UserOrgAttributeCatalogueModel> DiscoverAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Microsoft Graph implementation of <see cref="IUserOrgGraphProbe"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately a raw HTTP call with Newtonsoft rather than the Graph SDK. The importer reads these
    /// attributes out of raw JSON through <see cref="UserOrgRules.ExtractRawValue"/>, and a test that
    /// went through the SDK's typed model - where unknown properties become Kiota untyped nodes - would
    /// be testing a different code path from the one that actually runs. Matching the importer exactly
    /// is the entire point of the feature.
    /// </remarks>
    public sealed class UserOrgGraphProbe : IUserOrgGraphProbe
    {
        private const string GraphScope = "https://graph.microsoft.com/.default";
        private const string GraphRoot = "https://graph.microsoft.com/v1.0";

        // One shared HttpClient: a new one per request exhausts sockets under load, and this is called
        // from an interactive admin page where several probes in a row are normal.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly AppConfig _config;

        /// <summary>
        /// The app's Entra credential, reused across probes.
        /// </summary>
        /// <remarks>
        /// Azure Identity caches an acquired token on the credential <b>instance</b>, so building a
        /// fresh one per call meant a token-endpoint round trip for every attribute test and every
        /// catalogue load - on an admin page where several probes in a row are the normal way to use
        /// it. One instance lets it reuse the roughly hour-long token and serialise concurrent
        /// acquisition itself.
        /// </remarks>
        private static readonly object CredentialGate = new object();
        private static TokenCredential _cachedCredential;
        private static string _cachedCredentialKey;

        public UserOrgGraphProbe(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<UserOrgProbeOutcome> ResolveAsync(EntraOrgAttributeSpec spec, string upn, CancellationToken cancellationToken)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            var normalisedUpn = UserOrgRules.NormaliseUpn(upn);
            if (normalisedUpn == null)
            {
                return new UserOrgProbeOutcome { Message = "A user principal name is required." };
            }

            string token;
            try
            {
                token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new UserOrgProbeOutcome
                {
                    Message = "Could not authenticate to Microsoft Graph: " + ex.Message,
                };
            }

            var url = $"{GraphRoot}/users/{Uri.EscapeDataString(normalisedUpn)}?$select={Uri.EscapeDataString(spec.SelectFragment)}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        return new UserOrgProbeOutcome
                        {
                            Message = DescribeFailure(response.StatusCode, body, spec),

                            // Graph validates $select before it resolves the user, so a 400 means the
                            // property is unusable while a 404 only means this particular person does
                            // not exist. Only the former may block a configuration from being saved.
                            RejectedTheProperty = response.StatusCode == HttpStatusCode.BadRequest,
                        };
                    }

                    IDictionary<string, JToken> properties;
                    try
                    {
                        properties = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(body)
                                     ?? new Dictionary<string, JToken>();
                    }
                    catch (JsonException)
                    {
                        return new UserOrgProbeOutcome { Message = "Microsoft Graph returned a response that could not be read." };
                    }

                    // Exactly the extraction the importer performs, so what is shown here is what would
                    // be stored - including the absent-key and explicit-null cases.
                    var raw = UserOrgRules.ExtractRawValue(properties, spec);

                    return new UserOrgProbeOutcome
                    {
                        Succeeded = true,
                        RawValue = raw,
                        HasNoValue = raw == null,
                    };
                }
            }
        }

        public async Task<UserOrgAttributeCatalogueModel> DiscoverAsync(CancellationToken cancellationToken)
        {
            var catalogue = new UserOrgAttributeCatalogueModel
            {
                ExtensionAttributes = EntraOrgAttributeSpec.OnPremisesExtensionAttributeNames().ToList(),
                BuiltInProperties = EntraOrgAttributeSpec.BuiltInPropertyNames.ToList(),
                EmployeeOrgDataProperties = EntraOrgAttributeSpec.EmployeeOrgDataProperties
                    .Select(p => "employeeOrgData." + p).ToList(),
            };

            string token;
            try
            {
                token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                catalogue.DiscoveryWarning =
                    "Could not authenticate to Microsoft Graph to look for directory extensions (" + ex.Message
                    + "). You can still type a directory extension name in full and test it.";
                return catalogue;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, GraphRoot + "/directoryObjects/getAvailableExtensionProperties"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                try
                {
                    using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            catalogue.DiscoveryWarning =
                                "This app registration cannot list directory extensions - that specific call needs the "
                                + "Directory.Read.All application permission, which is more than the rest of this product "
                                + "requires. You can still type a directory extension name in full and test it.";
                            return catalogue;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            catalogue.DiscoveryWarning =
                                "Microsoft Graph could not list directory extensions (HTTP " + (int)response.StatusCode
                                + "). You can still type a directory extension name in full and test it.";
                            return catalogue;
                        }

                        var parsed = JObject.Parse(body);
                        var values = parsed["value"] as JArray;

                        if (values != null)
                        {
                            foreach (var item in values.OfType<JObject>())
                            {
                                var name = item.Value<string>("name");
                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    continue;
                                }

                                // Only user-targeted extensions are usable here.
                                var targets = item["targetObjects"] as JArray;
                                if (targets != null && targets.Count > 0
                                    && !targets.Values<string>().Any(t => string.Equals(t, "User", StringComparison.OrdinalIgnoreCase)))
                                {
                                    continue;
                                }

                                catalogue.DirectoryExtensions.Add(new UserOrgDiscoveredAttributeModel
                                {
                                    Name = name,
                                    DataType = item.Value<string>("dataType"),
                                    IsSyncedFromOnPremises = item.Value<bool?>("isSyncedFromOnPremises") ?? false,
                                });
                            }
                        }

                        if (catalogue.DirectoryExtensions.Count == 0)
                        {
                            // Microsoft documents this call as returning an empty response on tenants
                            // with more than 1,000 service principals - which many real tenants exceed,
                            // Microsoft's own first-party apps alone contributing a large number. An
                            // empty list must therefore never be presented as "there are none".
                            catalogue.DiscoveryWarning =
                                "No directory extensions were returned. Note that Microsoft Graph's discovery call is "
                                + "documented as returning nothing on tenants with more than 1,000 service principals, so "
                                + "this does not necessarily mean the tenant has none. You can type a directory extension "
                                + "name in full and test it.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    catalogue.DiscoveryWarning =
                        "Could not reach Microsoft Graph to list directory extensions (" + ex.Message
                        + "). You can still type a directory extension name in full and test it.";
                }
            }

            return catalogue;
        }

        /// <summary>
        /// Turns a Graph failure into something an administrator can act on.
        /// </summary>
        /// <remarks>
        /// The 400 case is the one that matters. It is what an admin sees when the attribute does not
        /// exist in the tenant, and it is exactly the failure that would otherwise break the user
        /// import - so it gets a specific explanation rather than a status code.
        /// </remarks>
        internal static string DescribeFailure(HttpStatusCode statusCode, string body, EntraOrgAttributeSpec spec)
        {
            switch (statusCode)
            {
                case HttpStatusCode.NotFound:
                    return "That user was not found in this tenant. Check the user principal name.";

                case HttpStatusCode.BadRequest:
                    return
                        $"Microsoft Graph does not recognise the property '{spec.SelectFragment}' on a user. Check the "
                        + "attribute name - for a directory extension it must be the full "
                        + "extension_{applicationId}_{name} form. This attribute cannot be used until Graph accepts it: "
                        + "saving it would make every user import fail.";

                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return
                        "This app registration is not allowed to read that user or that property. Reading users needs the "
                        + "User.Read.All application permission, granted with admin consent.";

                case (HttpStatusCode)429:
                    return "Microsoft Graph is throttling this tenant right now. Wait a moment and try again.";

                default:
                    return $"Microsoft Graph returned HTTP {(int)statusCode}. " + Summarise(body);
            }
        }

        /// <summary>
        /// Pulls just the message out of a Graph error body.
        /// </summary>
        /// <remarks>
        /// Never returns the raw body. A Graph error can echo back the request, and this text is shown
        /// in a portal and may be pasted into a support ticket.
        /// </remarks>
        private static string Summarise(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            try
            {
                var message = JObject.Parse(body)["error"]?["message"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message.Length > 300 ? message.Substring(0, 300) : message;
                }
            }
            catch (JsonException)
            {
                // Not a Graph error envelope; say nothing rather than leaking the body.
            }

            return string.Empty;
        }

        private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            var credential = await BuildCredentialAsync().ConfigureAwait(false);
            var token = await credential
                .GetTokenAsync(new TokenRequestContext(new[] { GraphScope }), cancellationToken)
                .ConfigureAwait(false);
            return token.Token;
        }

        /// <summary>
        /// Builds the app's own Entra credential, honouring certificate auth. Mirrors
        /// <c>HealthService.BuildCredential</c>.
        /// </summary>
        private async Task<TokenCredential> BuildCredentialAsync()
        {
            // Keyed on the identity it was built for, so a configuration change is picked up rather
            // than being masked by the cache for the life of the process.
            var key = _config.TenantGUID + "|" + _config.ClientID + "|" + (_config.UseClientCertificate ? "cert" : "secret");

            lock (CredentialGate)
            {
                if (_cachedCredential != null && _cachedCredentialKey == key)
                {
                    return _cachedCredential;
                }
            }

            TokenCredential built;
            if (_config.UseClientCertificate)
            {
                var cert = await AuthHelper
                    .RetrieveKeyVaultCertificate(AuthHelper.CertificateName, _config.KeyVaultUrl, AnalyticsLogger.ConsoleOnlyTracer())
                    .ConfigureAwait(false);
                built = new ClientCertificateCredential(_config.TenantGUID.ToString(), _config.ClientID, cert);
            }
            else
            {
                built = new ClientSecretCredential(_config.TenantGUID.ToString(), _config.ClientID, _config.ClientSecret);
            }

            lock (CredentialGate)
            {
                // A race here only means two credentials were built and one is discarded, which is
                // harmless - far cheaper than holding the lock across a Key Vault round trip.
                _cachedCredential = built;
                _cachedCredentialKey = key;
            }

            return built;
        }
    }
}
