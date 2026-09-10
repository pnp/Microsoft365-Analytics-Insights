using Azure;
using Azure.ResourceManager.AppService.Models;
using System;

namespace CloudInstallEngine.Azure
{
    public static class AppServicePlanCapabilities
    {
        public static bool SupportsAlwaysOn(AppServiceSkuDescription sku)
        {
            return !IsFreeOrShared(sku);
        }

        public static bool Supports64BitWorkerProcess(AppServiceSkuDescription sku)
        {
            return !IsFreeOrShared(sku);
        }

        public static bool IsFreeOrShared(AppServiceSkuDescription sku)
        {
            if (sku == null) return false;

            if (IsTierKnownUnsupported(sku.Tier)) return true;

            // ARM normally supplies Tier ("Free"/"Shared"), which is the stable capability boundary.
            // Some partially populated test/ARM objects only carry Name or Size, so fall back to exact F1/D1.
            return IsFreeOrSharedName(sku.Name) || IsFreeOrSharedName(sku.Size);
        }

        public static string GetDisplayTier(AppServiceSkuDescription sku)
        {
            if (sku == null) return "unknown";

            var tier = string.IsNullOrWhiteSpace(sku.Tier) ? "unknown" : sku.Tier.Trim();
            var name = string.IsNullOrWhiteSpace(sku.Name) ? sku.Size : sku.Name;
            return string.IsNullOrWhiteSpace(name) ? tier : $"{tier} ({name.Trim()})";
        }

        public static string BuildAlwaysOnUnsupportedWarning(string planName, AppServiceSkuDescription sku)
        {
            var safePlanName = string.IsNullOrWhiteSpace(planName) ? "<unknown>" : planName.Trim();
            return $"App Service plan '{safePlanName}' is tier '{GetDisplayTier(sku)}', which does not support Always On or 64-bit workers. " +
                "The site will be unloaded when idle and the web-jobs will not run reliably. " +
                "Scale the plan to B1 or higher and re-run the installer.";
        }

        public static bool IsPlanCapabilityConflict(RequestFailedException ex)
        {
            return ex != null && ex.Status == 409;
        }

        private static bool IsTierKnownUnsupported(string tier)
        {
            return string.Equals(tier, "Free", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tier, "Shared", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFreeOrSharedName(string nameOrSize)
        {
            return string.Equals(nameOrSize, "F1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nameOrSize, "D1", StringComparison.OrdinalIgnoreCase);
        }
    }
}
