using App.ControlPanel.Engine.InstallerTasks;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure;
using DataUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    public static class PrivateNetworkReviewCommand
    {
        public const string Operation = "--review-networking";
        public const string PasswordEnvironmentVariable = "M365_ANALYTICS_CONFIG_PASSWORD";

        public static async Task Run(string[] args, string password, CancellationToken ct,
            Func<SolutionInstallConfig, CancellationToken, Task<PrivateNetworkReview>> review = null)
        {
            if (args == null || args.Length != 3 || args[0] != Operation ||
                args.Skip(1).Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Usage: AnalyticsInstaller.exe --review-networking <saved-config.json> <new-private-report.json>. " +
                    "Set M365_ANALYTICS_CONFIG_PASSWORD in this process environment; never put the password on the command line. " +
                    "This GET-only operation does not install, checkpoint, adopt or delete resources.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Set " + PasswordEnvironmentVariable + " before reviewing the encrypted installer configuration.");
            ct.ThrowIfCancellationRequested();
            var configPath = Path.GetFullPath(args[1]);
            var reportPath = PrivateArtifactPath.Validate(args[2]);
            if (string.Equals(configPath, reportPath, StringComparison.OrdinalIgnoreCase) || File.Exists(reportPath))
                throw new IOException("The private review report must be a new file, separate from the saved installer configuration.");
            // The interactive file loader can open a Retry dialog on I/O errors and replaces invalid
            // JSON with a new config. Neither behaviour is appropriate for a non-interactive review.
            var json = JObject.Parse(File.ReadAllText(configPath));
            var loaded = SolutionInstallConfig.LoadFromJson(json.ToString(Formatting.None), password);
            if (loaded.Config == null || !loaded.DecryptedOk)
                throw new InvalidOperationException("Could not load the saved installer configuration for read-only networking review.");
            var report = await (review ?? Read)(loaded.Config, ct);
            ct.ThrowIfCancellationRequested();
            if (report == null) throw new InvalidOperationException("Networking inventory did not return a report.");
            var contents = JsonConvert.SerializeObject(report, Formatting.Indented);
            PrivateArtifactPath.Validate(reportPath);
            // CreateNew also guards against another process creating the destination during discovery.
            using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(contents);
        }

        private static async Task<PrivateNetworkReview> Read(SolutionInstallConfig config, CancellationToken ct)
        {
            if (config.Subscription == null || string.IsNullOrWhiteSpace(config.Subscription.SubId) ||
                string.IsNullOrWhiteSpace(config.ResourceGroupName))
                throw new InvalidOperationException("Select the exact subscription and resource group in a saved config before review.");
            var subscription = BaseAnalyticsSolutionInstallJob.FromConfig(config);
            var group = (await subscription.GetResourceGroupAsync(config.ResourceGroupName, ct)).Value;
            var manifest = config.PrivateNetworking ?? new PrivateNetworkManifest();
            if (manifest.ResourceGroupId != null && !PrivateNetworkProvenance.Same(manifest.ResourceGroupId, group.Id.ToString()))
                throw new InvalidOperationException("The saved networking manifest belongs to a different resource group.");
            var targets = AzurePaaSInstallJob.NetworkingTargets(config, group.Id.ToString());
            var client = new PrivateNetworkCleanupClient(group, manifest,
                targets.Select(t => t.Item1), targets.Select(t => t.Item2));
            return await client.Review(ct);
        }
    }
}
