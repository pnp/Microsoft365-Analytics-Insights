using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppServiceSkuDescription = Azure.ResourceManager.AppService.Models.AppServiceSkuDescription;

namespace CloudInstallEngine.Azure
{
    public static class AppServiceResourceLookup
    {
        /// <summary>
        /// Reads the hosting state before provisioning or network teardown. A null group is a confirmed new group;
        /// otherwise, a list miss and a not-found GET on the exact resource must agree before treating it as new.
        /// An existing site uses its own serverFarmId, site configuration and FTP policy, never the configured plan as a fallback.
        /// </summary>
        public static async Task<AppServiceHostingDiscovery> DiscoverHostingAsync(ResourceGroupResource group, string webAppName,
            string planName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (group == null) return new AppServiceHostingDiscovery(null, false);
            var hosting = await DiscoverExistingSiteAsync(group, webAppName, token);
            return hosting ?? await DiscoverConfiguredPlanAsync(group, planName, webAppName, token);
        }

        internal static async Task<AppServiceHostingDiscovery> DiscoverExistingSiteAsync(ResourceGroupResource group, string webAppName, CancellationToken token)
        {
            var site = string.IsNullOrWhiteSpace(webAppName) ? null : await FindSiteForDeploymentAsync(group, webAppName, token);
            if (site == null) return null;

            // An existing site keeps its actual plan, including a shared plan outside the deployment group.
            var id = site.Data.AppServicePlanId;
            if (id == null || !ResourceIdentifier.TryParse(id.ToString(), out var planId) ||
                planId.ResourceType != AppServicePlanResource.ResourceType || planId.Parent?.ResourceType != ResourceGroupResource.ResourceType)
                throw new InstallException("Azure did not return a valid hosting plan ID (serverFarmId) for the existing App Service. " +
                    "Verify the site's App Service plan association and retry.");
            var plan = await ReadPlanAsync(group, planId, "the existing App Service's hosting plan", false, token);
            var config = await ReadSiteConfigAsync(site, token);
            var ftpAllowed = await ReadFtpAllowedAsync(site, token);
            return new AppServiceHostingDiscovery(site, plan, config, ftpAllowed);
        }

        private static async Task<AppServiceHostingDiscovery> DiscoverConfiguredPlanAsync(ResourceGroupResource group,
            string planName, string legacyName, CancellationToken token)
        {
            var planId = AppServicePlanResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, planName);
            var legacyId = string.IsNullOrWhiteSpace(legacyName) || string.Equals(legacyName, planName, StringComparison.OrdinalIgnoreCase)
                ? null : AppServicePlanResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, legacyName);
            var legacyNameFound = false;
            var listed = FindByName(group.GetAppServicePlans().GetAll(cancellationToken: token), p => p.Data, planId,
                "App Service plans", token, p => { if (legacyId?.Equals(p.Id) == true) legacyNameFound = true; });
            // A PUT is create-or-update: treating an omitted paid plan as new would resize it to Basic.
            var plan = await ReadPlanAsync(group, planId, "the configured App Service plan", listed == null, token);
            if (plan != null && (listed == null || SkuDisagrees(listed.Data.Sku, plan.Data.Sku)))
                throw new InstallException("The App Service plan list and the exact plan read disagree. " +
                    "Verify the configured plan and its SKU in the deployment resource group, then retry; the installer will not replace it.");
            return new AppServiceHostingDiscovery(plan, legacyNameFound);
        }

        private static bool SkuDisagrees(AppServiceSkuDescription listed, AppServiceSkuDescription actual)
        {
            bool Differs(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return Differs(listed?.Tier, actual.Tier) || Differs(listed?.Name, actual.Name) || Differs(listed?.Size, actual.Size);
        }

        private static async Task<AppServicePlanResource> ReadPlanAsync(ResourceGroupResource group, ResourceIdentifier id,
            string description, bool allowMissing, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var arm = group.GetCachedClient(client => client);
                var response = await arm.GetAppServicePlanResource(id).GetAsync(token);
                token.ThrowIfCancellationRequested();
                var plan = response.Value;
                RequireIdentity(plan?.Data, id);
                using (var body = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory()))
                    RequireProperties(body.RootElement, id);
                var sku = plan.Data.Sku;
                if (string.IsNullOrWhiteSpace(sku?.Name) && string.IsNullOrWhiteSpace(sku?.Size) && string.IsNullOrWhiteSpace(sku?.Tier))
                    throw new InvalidDataException();
                return plan;
            }
            catch (RequestFailedException ex) when (allowMissing && IsResourceNotFound(ex, allowEmptyBody: group.HasData))
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                throw ReadFailure($"read {description}", ex,
                    "Verify the plan's identity and SKU and the installer's read access in the plan's resource group, then retry.");
            }
        }

        // Both exact GETs document a bodyless 404. Accept it only after a list miss in a verified group.
        private static bool IsResourceNotFound(RequestFailedException error, bool allowEmptyBody)
        {
            if (error.Status != 404) return false;
            bool MissingCode(string code) => string.Equals(code, "ResourceNotFound", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "NotFound", StringComparison.OrdinalIgnoreCase);
            var response = error.GetRawResponse();
            if (response == null || (response.Headers.TryGetValue("x-ms-error-code", out var header) && !MissingCode(header)))
                return false;
            var content = response.Content.ToMemory();
            if (content.IsEmpty) return allowEmptyBody && (string.IsNullOrEmpty(error.ErrorCode) || MissingCode(error.ErrorCode));
            if (!MissingCode(error.ErrorCode)) return false;
            using (var body = JsonDocument.Parse(content))
            {
                var root = body.RootElement;
                return root.TryGetProperty("error", out var details) && details.TryGetProperty("code", out var code) && MissingCode(code.GetString());
            }
        }

        private static void RequireIdentity(ResourceData data, ResourceIdentifier expected)
        {
            if (data?.Id == null || !expected.Equals(data.Id) || data.ResourceType != expected.ResourceType ||
                string.IsNullOrWhiteSpace(data.Name) || !string.Equals(data.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
        }

        private static T FindByName<T>(Pageable<T> resources, Func<T, ResourceData> getData, ResourceIdentifier expectedId,
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
                        if (resource.Id == null || resource.Id.ResourceType != expectedId.ResourceType)
                            throw new InvalidDataException();
                        RequireIdentity(getData(resource), resource.Id);
                        observe?.Invoke(resource);
                        if (!string.Equals(resource.Id.Name, expectedId.Name, StringComparison.OrdinalIgnoreCase)) continue;
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
            var listed = FindByName(group.GetWebSites().GetAll(cancellationToken: token), s => s.Data,
                WebSiteResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, name), "App Services", token);
            var site = await ReadSiteAsync(group, listed?.Data.Name ?? name, token, allowMissing: listed == null, allowEmptyNotFound: group.HasData);
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

        /// <summary>Rechecks the exact site previously confirmed absent by hosting discovery before network cleanup.</summary>
        public static async Task ConfirmSiteStillMissingAsync(ResourceGroupResource group, string name, CancellationToken token)
        {
            if (await ReadSiteAsync(group, name, token, allowMissing: true, allowEmptyNotFound: group.HasData) != null)
                throw new InstallException("The App Service now exists after hosting discovery reported it missing. Verify its settings and retry.");
        }

        internal static async Task<WebSiteResource> ReadSiteAsync(ResourceGroupResource group, string name, CancellationToken token,
            bool allowMissing = false, bool allowEmptyNotFound = false)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = await group.GetWebSiteAsync(name, token);
                token.ThrowIfCancellationRequested();
                var site = response.Value;
                var expected = WebSiteResource.CreateResourceIdentifier(group.Id.SubscriptionId, group.Id.Name, name);
                RequireIdentity(site?.Data, expected);
                using (var body = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory()))
                {
                    // An omitted/null identity on a complete site is valid; an empty identity object is not "None".
                    RequireProperties(body.RootElement, expected);
                    if (body.RootElement.TryGetProperty("identity", out var identity) && identity.ValueKind != JsonValueKind.Null &&
                        string.IsNullOrWhiteSpace(identity.GetProperty("type").GetString()))
                        throw new InvalidDataException();
                }
                return site;
            }
            catch (RequestFailedException ex) when (allowMissing && IsResourceNotFound(ex, allowEmptyNotFound))
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

        public static Task<SiteConfigData> ReadSiteConfigAsync(WebSiteResource site, CancellationToken token)
        {
            var config = site.GetWebSiteConfig();
            return ReadChildAsync(config, config.GetAsync, (resource, _) =>
            {
                var data = resource.Data;
                var tls = data.MinTlsVersion?.ToString();
                // SiteConfig properties are optional. An empty projection is not evidence of an unset TLS minimum.
                if (string.IsNullOrWhiteSpace(tls) && string.IsNullOrWhiteSpace(data.FtpsState?.ToString()) &&
                    !data.IsAlwaysOn.HasValue && !data.IsVnetRouteAllEnabled.HasValue)
                    throw new InvalidDataException();
                var siteTls = site.HasData ? site.Data.SiteConfig?.MinTlsVersion?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(siteTls) && !string.Equals(siteTls, tls, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                return data;
            }, "App Service site configuration (config/web)", token, alternateName: site.Id.Name);
        }

        internal static Task<bool> ReadFtpAllowedAsync(WebSiteResource site, CancellationToken token)
        {
            var policy = site.GetWebSiteFtpPublishingCredentialsPolicy();
            return ReadChildAsync(policy, policy.GetAsync, (_, properties) => properties.GetProperty("allow").GetBoolean(),
                "App Service FTP basic publishing policy", token);
        }

        private static async Task<T> ReadChildAsync<TResource, T>(TResource expected, Func<CancellationToken, Task<Response<TResource>>> get,
            Func<TResource, JsonElement, T> select, string description, CancellationToken token, string alternateName = null) where TResource : ArmResource
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = await get(token);
                token.ThrowIfCancellationRequested();
                using (var body = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory()))
                {
                    // FTP can omit both labels; config/web can use its parent site's name with the exact child ID.
                    var root = body.RootElement;
                    if ((root.TryGetProperty("type", out var type) && !string.Equals(type.GetString(), expected.Id.ResourceType.ToString(), StringComparison.OrdinalIgnoreCase)) ||
                        (root.TryGetProperty("name", out var name) && !string.Equals(name.GetString(), expected.Id.Name, StringComparison.OrdinalIgnoreCase) &&
                         (alternateName == null || !string.Equals(name.GetString(), alternateName, StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidDataException();
                    return select(response.Value, RequireProperties(root, expected.Id));
                }
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                var message = ReadFailureMessage($"read the {description}", ex, "Verify the site's settings and the installer's read access, then retry.");
                // Preserve the HTTP status for installer diagnostics, never the response body.
                if (ex is RequestFailedException http) throw new RequestFailedException(http.Status, message);
                throw new InstallException(message);
            }
        }

        private static JsonElement RequireProperties(JsonElement resource, ResourceIdentifier expectedId)
        {
            // HasData also holds for metadata-only success bodies. Only a real properties object can describe an unset setting.
            if (!string.Equals(resource.GetProperty("id").GetString(), expectedId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !resource.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
            return properties;
        }

        private static InstallException ReadFailure(string operation, Exception error, string advice) =>
            new InstallException(ReadFailureMessage(operation, error, advice));

        private static string ReadFailureMessage(string operation, Exception error, string advice)
        {
            var reason = error is RequestFailedException response && response.Status >= 100 && response.Status <= 599
                ? $"HTTP {response.Status}" : "no usable response";
            return $"Could not {operation} ({reason}). {advice}";
        }
    }
}
