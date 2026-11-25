using Azure;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace CloudInstallEngine.Azure
{
    internal static class AppServiceResourceLookup
    {
        internal static WebSiteResource FindSite(ResourceGroupResource group, string name, CancellationToken token) =>
            FindByName(group.GetWebSites().GetAll(cancellationToken: token), s => s.Data.Name, name, "App Services", token);

        internal static AppServicePlanResource FindPlan(ResourceGroupResource group, string name, CancellationToken token) =>
            FindByName(group.GetAppServicePlans().GetAll(cancellationToken: token), p => p.Data.Name, name, "App Service plans", token);

        private static T FindByName<T>(Pageable<T> resources, Func<T, string> getName, string name, string collection, CancellationToken token) where T : class
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
                        if (string.IsNullOrWhiteSpace(listedName)) throw new InvalidDataException();
                        if (!string.Equals(listedName, name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (match != null) throw new InvalidDataException();
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

        internal static InstallException ReadFailure(string operation, Exception error, string advice)
        {
            var reason = error is RequestFailedException response && response.Status >= 100 && response.Status <= 599
                ? $"HTTP {response.Status}" : "no usable response";
            return new InstallException($"Could not {operation} ({reason}). {advice}");
        }
    }
}
