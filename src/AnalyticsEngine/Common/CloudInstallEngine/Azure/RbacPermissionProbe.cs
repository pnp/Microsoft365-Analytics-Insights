using Azure.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    /// <summary>Outcome of an installer-account RBAC role-assignment permission probe.</summary>
    public enum RbacAssignmentProbeStatus
    {
        /// <summary>The account is allowed to create role assignments (Owner / User Access Administrator / RBAC Administrator / equivalent).</summary>
        CanAssignRoles,

        /// <summary>The account is NOT allowed to create role assignments - the install will fail at the RBAC step.</summary>
        CannotAssignRoles,

        /// <summary>Azure Resource Manager could not be reached at all (DNS / network / firewall transport failure).</summary>
        TransportFailure,

        /// <summary>Some other, unexpected error occurred and the permission could not be determined.</summary>
        OtherError
    }

    /// <summary>Result of <see cref="RbacPermissionProbe.CanAssignRolesAsync"/>.</summary>
    public class RbacAssignmentProbeResult
    {
        public RbacAssignmentProbeResult(RbacAssignmentProbeStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public RbacAssignmentProbeStatus Status { get; }
        public string Message { get; }
    }

    /// <summary>
    /// A single <c>Microsoft.Authorization/permissions</c> entry: the control-plane actions a role grants
    /// (<see cref="Actions"/>) or explicitly excludes (<see cref="NotActions"/>) for the caller at a scope.
    /// </summary>
    public class RbacPermissionEntry
    {
        [JsonProperty("actions")]
        public List<string> Actions { get; set; } = new List<string>();

        [JsonProperty("notActions")]
        public List<string> NotActions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Pre-flight check that the installer service principal is actually allowed to create Azure RBAC role
    /// assignments (<c>Microsoft.Authorization/roleAssignments/write</c>) at the deployment scope. This pre-empts
    /// the mid-install failure where <see cref="InstallTasks.RoleAssignmentTask"/> aborts with
    /// "...does not have authorization to perform action 'Microsoft.Authorization/roleAssignments/write' over
    /// scope '/subscriptions/.../resourceGroups/...'", which happens when the installer account only has e.g.
    /// Contributor instead of Owner / User Access Administrator.
    /// <para>
    /// It asks Azure Resource Manager for the caller's effective permissions at the scope
    /// (<c>GET .../{scope}/providers/Microsoft.Authorization/permissions</c>) and evaluates them with Azure RBAC
    /// wildcard semantics, so an Owner ("*"), User Access Administrator ("Microsoft.Authorization/*") or custom
    /// role all pass, while Contributor (which excludes role-assignment writes via <c>notActions</c>) and Reader fail.
    /// </para>
    /// </summary>
    public static class RbacPermissionProbe
    {
        /// <summary>The control-plane action the installer needs in order to create RBAC role assignments.</summary>
        public const string RoleAssignmentWriteAction = "Microsoft.Authorization/roleAssignments/write";

        /// <summary>GA api-version for the Microsoft.Authorization "List permissions" operation.</summary>
        private const string PermissionsApiVersion = "2022-04-01";

        private static readonly string[] ArmScopes = new[] { "https://management.azure.com/.default" };

        /// <summary>
        /// Determine whether the supplied credential can create role assignments at <paramref name="scopeId"/>.
        /// <paramref name="scopeId"/> is an ARM scope path beginning with '/', e.g.
        /// <c>/subscriptions/{subId}/resourceGroups/{rg}</c> or <c>/subscriptions/{subId}</c>. Never throws.
        /// </summary>
        public static async Task<RbacAssignmentProbeResult> CanAssignRolesAsync(string scopeId, TokenCredential credential, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scopeId)) throw new ArgumentException($"'{nameof(scopeId)}' cannot be null or empty.", nameof(scopeId));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            try
            {
                var permissions = await GetEffectivePermissionsAsync(scopeId.Trim(), credential, cancellationToken);
                if (ActionIsAllowed(RoleAssignmentWriteAction, permissions))
                {
                    return new RbacAssignmentProbeResult(RbacAssignmentProbeStatus.CanAssignRoles,
                        "Installer account can create role assignments at the deployment scope.");
                }

                return new RbacAssignmentProbeResult(RbacAssignmentProbeStatus.CannotAssignRoles,
                    $"Installer account is not granted '{RoleAssignmentWriteAction}' at the deployment scope.");
            }
            catch (Exception ex) when (TransportFailureDetector.IsTransportOrDnsFailure(ex, out var leaf))
            {
                return new RbacAssignmentProbeResult(RbacAssignmentProbeStatus.TransportFailure, leaf);
            }
            catch (Exception ex)
            {
                return new RbacAssignmentProbeResult(RbacAssignmentProbeStatus.OtherError, ex.Message);
            }
        }

        /// <summary>
        /// Call the ARM "List permissions" endpoint for the caller at the given scope, following paging.
        /// Uses the same ARM-REST + bearer-token pattern as the rest of the install engine.
        /// </summary>
        private static async Task<List<RbacPermissionEntry>> GetEffectivePermissionsAsync(string scopeId, TokenCredential credential, CancellationToken cancellationToken)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(ArmScopes), cancellationToken);

            var all = new List<RbacPermissionEntry>();
            var url = $"https://management.azure.com{scopeId}/providers/Microsoft.Authorization/permissions?api-version={PermissionsApiVersion}";

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

                while (!string.IsNullOrEmpty(url))
                {
                    using (var response = await http.GetAsync(url, cancellationToken))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException(
                                $"Azure Resource Manager returned {(int)response.StatusCode} ({response.ReasonPhrase}) listing permissions for scope '{scopeId}': {body}");
                        }

                        var page = JsonConvert.DeserializeObject<PermissionsListResponse>(body);
                        if (page?.Value != null)
                        {
                            all.AddRange(page.Value);
                        }
                        url = page?.NextLink;
                    }
                }
            }

            return all;
        }

        /// <summary>
        /// True when <paramref name="action"/> is permitted by any of the supplied permission entries, applying
        /// Azure RBAC semantics: an entry grants the action when one of its <see cref="RbacPermissionEntry.Actions"/>
        /// matches AND none of its <see cref="RbacPermissionEntry.NotActions"/> matches.
        /// </summary>
        public static bool ActionIsAllowed(string action, IEnumerable<RbacPermissionEntry> permissions)
        {
            if (string.IsNullOrEmpty(action) || permissions == null) return false;

            foreach (var permission in permissions)
            {
                if (permission == null) continue;

                var granted = permission.Actions != null && permission.Actions.Any(pattern => WildcardMatches(action, pattern));
                if (!granted) continue;

                var excluded = permission.NotActions != null && permission.NotActions.Any(pattern => WildcardMatches(action, pattern));
                if (!excluded) return true;
            }

            return false;
        }

        /// <summary>
        /// Azure RBAC wildcard match (case-insensitive). A '*' in <paramref name="pattern"/> matches any sequence
        /// of characters, including '/', so e.g. "*", "Microsoft.Authorization/*" and "Microsoft.Authorization/*/Write"
        /// all match "Microsoft.Authorization/roleAssignments/write".
        /// </summary>
        public static bool WildcardMatches(string action, string pattern)
        {
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(pattern)) return false;

            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(action, regex, RegexOptions.IgnoreCase);
        }

        private class PermissionsListResponse
        {
            [JsonProperty("value")]
            public List<RbacPermissionEntry> Value { get; set; }

            [JsonProperty("nextLink")]
            public string NextLink { get; set; }
        }
    }
}
