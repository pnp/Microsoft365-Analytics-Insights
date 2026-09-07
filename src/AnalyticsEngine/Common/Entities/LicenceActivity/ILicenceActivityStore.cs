using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LicenceActivity
{
    public interface ILicenceActivityStore
    {
        Task<LicenceActivityOverview> LoadOverviewAsync(
            LicenceActivityQuery query, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken);

        Task<LicenceActivityUsers> LoadUsersAsync(
            LicenceActivityOverview overview, LicenceActivityQuery query, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken);
    }

    public interface ILicenceActivityDiagnostics
    {
        void Stage(string stage, long elapsedMs = 0);
    }

    public sealed class NullLicenceActivityDiagnostics : ILicenceActivityDiagnostics
    {
        public static readonly NullLicenceActivityDiagnostics Instance = new NullLicenceActivityDiagnostics();
        private NullLicenceActivityDiagnostics() { }
        public void Stage(string stage, long elapsedMs = 0) { }
    }

    public sealed class LicenceActivitySources
    {
        public bool UserMetadata { get; set; }
        public bool UsageReports { get; set; }
        public bool CopilotUsageReports { get; set; }
        public bool CopilotAudit { get; set; }
        public bool CopilotInteractions { get; set; }
        public DateTime NowUtc { get; set; }

        public string CacheKey => string.Join(":", UserMetadata, UsageReports, CopilotUsageReports,
            CopilotAudit, CopilotInteractions, NowUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static class LicenceActivityRules
    {
        public const string Method =
            "Activity bands describe the share of observed reporting samples with activity: zero = none, "
            + "low = below 25%, moderate = 25% to below 75%, high = at least 75%. "
            + "Incomplete evidence is unknown, not zero. Counts are workload-specific snapshot averages, not daily events.";

        public const string AssignmentCaveat =
            "Historical activity is shown against currently imported licence assignments, not historical ownership. "
            + "A user can hold several SKUs; summed assignments are not a unique-user total. Assigned seats are not purchased capacity.";

        public const string InterpretationCaveat =
            "Activity by a licence holder does not prove that this SKU enabled it. These estimates do not measure productivity, "
            + "ROI or compliance and are not sufficient evidence to remove a licence.";

        public static string Band(int activeSamples, int observedSamples, int expectedSamples)
        {
            if (expectedSamples <= 0 || observedSamples != expectedSamples || activeSamples < 0 || activeSamples > observedSamples)
                return "unknown";
            if (activeSamples == 0) return "zero";
            if ((long)activeSamples * 4 < expectedSamples) return "low";
            return (long)activeSamples * 4 < (long)expectedSamples * 3 ? "moderate" : "high";
        }
    }
}
