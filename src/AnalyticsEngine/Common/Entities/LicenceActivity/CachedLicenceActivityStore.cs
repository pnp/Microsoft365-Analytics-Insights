using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LicenceActivity
{
    public interface ILicenceActivitySnapshotValidator
    {
        bool IsCurrent(LicenceActivityOverview overview, LicenceActivitySources sources);
    }

    public sealed class CachedLicenceActivityStore : ILicenceActivityStore, ILicenceActivitySnapshotValidator
    {
        private readonly ILicenceActivityReadModelLoader _loader;
        private readonly LicenceActivityReadModelCache _cache;
        private readonly string _scope;

        public CachedLicenceActivityStore(
            ILicenceActivityReadModelLoader loader, LicenceActivityReadModelCache cache, string scope)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("A cache scope is required.", nameof(scope));
            _scope = scope;
        }

        public async Task<LicenceActivityOverview> LoadOverviewAsync(
            LicenceActivityQuery query, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            Validate(query, sources);
            diagnostics = diagnostics ?? NullLicenceActivityDiagnostics.Instance;
            var range = LicenceActivityQuery.Create(query.From, query.To, sources.NowUtc);
            var capturedSources = Capture(sources);
            var lease = await _cache.GetAsync(
                Scope(capturedSources), range.From + "\n" + range.To,
                token => _loader.LoadReadModelAsync(range, capturedSources, diagnostics, token),
                cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var overview = lease.Model.BuildOverview(query, cancellationToken);
            overview.ReadModelId = lease.Model.Id;
            overview.SourceExpiresUtc = lease.ExpiresUtc;
            diagnostics.Stage("ProjectionCompleted", watch.ElapsedMilliseconds);
            return overview;
        }

        public Task<LicenceActivityUsers> LoadUsersAsync(
            LicenceActivityOverview overview, LicenceActivityQuery query, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            Validate(query, sources);
            if (overview == null) throw new ArgumentNullException(nameof(overview));
            cancellationToken.ThrowIfCancellationRequested();
            var lease = _cache.Find(Scope(sources), overview.ReadModelId);
            var watch = Stopwatch.StartNew();
            var users = lease.Model.BuildUsers(overview, query, cancellationToken);
            users.SourceExpiresUtc = lease.ExpiresUtc;
            (diagnostics ?? NullLicenceActivityDiagnostics.Instance).Stage("ProjectionCompleted", watch.ElapsedMilliseconds);
            return Task.FromResult(users);
        }

        private string Scope(LicenceActivitySources sources) => _scope + "\n" + sources.CacheKey;

        public bool IsCurrent(LicenceActivityOverview overview, LicenceActivitySources sources) =>
            _cache.Contains(Scope(sources), overview.ReadModelId);

        private static void Validate(LicenceActivityQuery query, LicenceActivitySources sources)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (!sources.UserMetadata)
                throw new InvalidOperationException("Licence activity requires the user metadata import.");
        }

        private static LicenceActivitySources Capture(LicenceActivitySources sources) =>
            new LicenceActivitySources
            {
                UserMetadata = sources.UserMetadata,
                UsageReports = sources.UsageReports,
                CopilotUsageReports = sources.CopilotUsageReports,
                CopilotAudit = sources.CopilotAudit,
                CopilotInteractions = sources.CopilotInteractions,
                UsageReportsGroupFiltered = sources.UsageReportsGroupFiltered,
                NowUtc = sources.NowUtc
            };
    }
}
