using Azure.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// The two identifiers of a system-assigned managed identity.
    /// </summary>
    public class SystemAssignedIdentityIds
    {
        public SystemAssignedIdentityIds(Guid principalId, Guid clientId)
        {
            PrincipalId = principalId;
            ClientId = clientId;
        }

        /// <summary>The service principal's object ID - the only one a resource's own <c>identity</c> block carries.</summary>
        public Guid PrincipalId { get; }

        /// <summary>The application (client) ID - the one Azure SQL matches a service principal's sign-in against.</summary>
        public Guid ClientId { get; }
    }

    /// <summary>
    /// Reads the identifiers of an Azure resource's system-assigned managed identity.
    /// </summary>
    /// <remarks>
    /// An interface so the database grant that depends on it is unit testable without Azure.
    /// </remarks>
    public interface IManagedIdentityApplicationIdSource
    {
        /// <summary>
        /// Returns the system-assigned identity of the resource, or null when it has none. Throws when the
        /// identity could not be read.
        /// </summary>
        Task<SystemAssignedIdentityIds> GetSystemAssignedIdentityAsync(string resourceId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Reads a system-assigned managed identity's application (client) ID from Azure Resource Manager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resource's <c>identity</c> block reports only the object ID, but the identity's own extension
    /// resource - <c>{resourceId}/providers/Microsoft.ManagedIdentity/identities/default</c>, the "System
    /// Assigned Identities - Get By Scope" operation - also carries the client ID. Reading it needs nothing
    /// beyond read access to the resource, which the installer has because it manages the resource.
    /// </para>
    /// <para>
    /// Both other ways of learning the client ID need a tenant-wide grant: Microsoft Graph needs
    /// <c>Application.Read.All</c> on one of the installer's app registrations, and <c>CREATE USER ... FROM
    /// EXTERNAL PROVIDER</c> run by a service principal needs the SQL Server to have an identity of its own
    /// holding the Microsoft Entra Directory Readers role. A tenant with neither got no database user for the
    /// App Service's identity, and every web-job start then failed with
    /// <c>Login failed for user '&lt;token-identified principal&gt;'</c>.
    /// </para>
    /// </remarks>
    public class ArmManagedIdentityApplicationIdSource : IManagedIdentityApplicationIdSource
    {
        /// <summary>A GA api-version of "System Assigned Identities - Get By Scope".</summary>
        public const string ApiVersion = "2023-01-31";

        public const string ArmScope = "https://management.azure.com/.default";

        private const int MaxErrorBodyLength = 500;

        private readonly TokenCredential _credential;
        private readonly HttpMessageHandler _handler;

        /// <param name="credential">The installer's Azure Resource Manager credential.</param>
        /// <param name="handler">Test hook. Null uses the default handler, which honours the installer's proxy settings.</param>
        public ArmManagedIdentityApplicationIdSource(TokenCredential credential, HttpMessageHandler handler = null)
        {
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _handler = handler;
        }

        public async Task<SystemAssignedIdentityIds> GetSystemAssignedIdentityAsync(string resourceId, CancellationToken cancellationToken)
        {
            var url = BuildRequestUrl(resourceId);
            var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { ArmScope }), cancellationToken).ConfigureAwait(false);

            using (var http = _handler == null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    // What ARM returns for a resource that has no system-assigned identity.
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;

                    var body = response.Content == null
                        ? string.Empty
                        : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"Azure Resource Manager returned {(int)response.StatusCode} ({response.ReasonPhrase}): {Truncate(body)}");
                    }

                    return ParseResponse(body);
                }
            }
        }

        /// <summary>
        /// Builds the request URL for a resource ID such as
        /// <c>/subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{name}</c>.
        /// </summary>
        public static string BuildRequestUrl(string resourceId)
        {
            var scope = resourceId?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(scope) || !scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{resourceId}' is not an Azure resource ID.", nameof(resourceId));
            }

            return $"https://management.azure.com{scope}/providers/Microsoft.ManagedIdentity/identities/default?api-version={ApiVersion}";
        }

        /// <summary>
        /// Reads the principal and client IDs out of a response body. Throws when either is missing, rather
        /// than returning a half-populated result a caller could mistake for a usable SID.
        /// </summary>
        public static SystemAssignedIdentityIds ParseResponse(string json)
        {
            var properties = string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json)["properties"] as JObject;

            Guid principalId, clientId;
            if (!TryReadGuid(properties?["principalId"], out principalId) || !TryReadGuid(properties?["clientId"], out clientId))
            {
                throw new InvalidOperationException("Azure Resource Manager did not return the managed identity's principal and client IDs.");
            }

            return new SystemAssignedIdentityIds(principalId, clientId);
        }

        static bool TryReadGuid(JToken token, out Guid value)
        {
            value = Guid.Empty;
            var text = (token as JValue)?.Value;
            return text != null
                && Guid.TryParse(Convert.ToString(text, CultureInfo.InvariantCulture), out value)
                && value != Guid.Empty;
        }

        static string Truncate(string body)
        {
            if (string.IsNullOrEmpty(body)) return "(no response body)";
            return body.Length <= MaxErrorBodyLength ? body : body.Substring(0, MaxErrorBodyLength) + "...";
        }
    }
}
