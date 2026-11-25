using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    internal static class AppServiceResourceLookup
    {
        internal static WebSiteResource FindSite(ResourceGroupResource group, string name, CancellationToken token) =>
            FindByName(group.GetWebSites().GetAll(cancellationToken: token), s => s.Data.Name,
                WebSiteResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, name), "App Services", token);

        internal static (AppServicePlanResource Plan, bool LegacyNameFound) FindPlans(ResourceGroupResource group,
            string planName, string legacyName, CancellationToken token)
        {
            var planId = AppServicePlanResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, planName);
            var legacyId = string.IsNullOrWhiteSpace(legacyName) || string.Equals(legacyName, planName, StringComparison.OrdinalIgnoreCase)
                ? null : AppServicePlanResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, legacyName);
            var legacyNameFound = false;
            var plan = FindByName(group.GetAppServicePlans().GetAll(cancellationToken: token), p => p.Data.Name, planId,
                "App Service plans", token, p => { if (legacyId?.Equals(p.Id) == true) legacyNameFound = true; });
            return (plan, legacyNameFound);
        }

        private static T FindByName<T>(Pageable<T> resources, Func<T, string> getName, ResourceIdentifier expectedId,
            string collection, CancellationToken token, Action<T> observe = null) where T : ArmResource
        {
            T match = null;
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var page in resources.AsPages())
                {
                    if (page.Values.Count == 0)
                    {
                        // The SDK also yields an empty page for {}. Only an explicit [] confirms there are no entries.
                        using (var body = JsonDocument.Parse(page.GetRawResponse().Content.ToMemory()))
                        {
                            if (body.RootElement.GetProperty("value").GetArrayLength() != 0) throw new InvalidDataException();
                        }
                    }
                    foreach (var resource in page.Values)
                    {
                        var listedName = getName(resource);
                        if (string.IsNullOrWhiteSpace(listedName) || resource.Id == null ||
                            resource.Id.ResourceType != expectedId.ResourceType ||
                            !string.Equals(resource.Id.Name, listedName, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException();
                        observe?.Invoke(resource);
                        if (!string.Equals(listedName, expectedId.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (match != null || !expectedId.Equals(resource.Id)) throw new InvalidDataException();
                        match = resource;
                    }
                }
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                throw ReadFailure($"list {collection} in the deployment resource group", ex,
                    "Verify the resource group and the installer's access to the complete list, then retry.");
            }
            token.ThrowIfCancellationRequested();
            return match;
        }

        // ARM list responses may omit identity or its userAssignedIdentities map. Never build a replacement
        // identity from that projection. A 404 after the site was listed is not permission to recreate it.
        internal static async Task<WebSiteResource> FindSiteForDeploymentAsync(ResourceGroupResource group, string name, CancellationToken token)
        {
            var listed = FindSite(group, name, token);
            var site = await ReadSiteAsync(group, listed?.Data.Name ?? name, token, allowMissing: listed == null);
            if (site == null) return null;
            if (listed == null || (listed.Data.AppServicePlanId != null && !listed.Data.AppServicePlanId.Equals(site.Data.AppServicePlanId)))
                throw new InstallException("The App Service list and the site read disagree about the existing site or its hosting plan. " +
                    "Verify the site and retry; the installer will not create a replacement.");

            var identity = site.Data.Identity;
            if (!ManagedIdentityPlanner.GetSystemAssignedPrincipal(identity).HasValue)
            {
                var userIds = identity?.UserAssignedIdentities;
                var knownType = identity == null || identity.ManagedServiceIdentityType == ManagedServiceIdentityType.None ||
                    identity.ManagedServiceIdentityType == ManagedServiceIdentityType.SystemAssigned || HasUserAssignedType(identity);
                var missingMap = (HasUserAssignedType(identity) || HasUserAssignedType(listed.Data.Identity)) && (userIds?.Count ?? 0) == 0;
                if (!knownType || missingMap ||
                    listed.Data.Identity?.UserAssignedIdentities?.Keys.Any(id => userIds?.ContainsKey(id) != true) == true)
                    throw new InstallException("Azure did not return the complete managed identity type and user-assigned resource IDs for the App Service. " +
                        "Verify the site's managed identities and retry; the installer will not replace an incomplete identity.");
            }
            return site;
        }

        private static bool HasUserAssignedType(ManagedServiceIdentity identity) =>
            identity?.ManagedServiceIdentityType == ManagedServiceIdentityType.UserAssigned ||
            identity?.ManagedServiceIdentityType == ManagedServiceIdentityType.SystemAssignedUserAssigned;

        internal static async Task<WebSiteResource> ReadSiteAsync(ResourceGroupResource group, string name, CancellationToken token, bool allowMissing = false)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = await group.GetWebSiteAsync(name, token);
                token.ThrowIfCancellationRequested();
                var site = response.Value;
                var expected = WebSiteResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, name);
                if (site?.HasData != true || !expected.Equals(site.Id) || !string.Equals(name, site.Data.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                using (var body = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory()))
                {
                    // An omitted/null identity on a complete site is valid; an empty identity object is not "None".
                    if (body.RootElement.GetProperty("properties").ValueKind != JsonValueKind.Object ||
                        (body.RootElement.TryGetProperty("identity", out var identity) && identity.ValueKind != JsonValueKind.Null &&
                         string.IsNullOrWhiteSpace(identity.GetProperty("type").GetString())))
                        throw new InvalidDataException();
                }
                return site;
            }
            catch (RequestFailedException ex) when (allowMissing && PrivateNetworkProvenance.IsMissing(ex))
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                throw ReadFailure("read the existing App Service", ex, "Verify the site and the installer's read access, then retry.");
            }
        }

        internal static InstallException ReadFailure(string operation, Exception error, string advice)
        {
            var reason = error is RequestFailedException response && response.Status >= 100 && response.Status <= 599
                ? $"HTTP {response.Status}" : "no usable response";
            return new InstallException($"Could not {operation} ({reason}). {advice}");
        }
    }
}
