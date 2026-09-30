using System;
using System.Collections.Concurrent;
using UsageReporting;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// Decides whether a SKU part number is one Microsoft publishes, and is therefore safe to name in
    /// anonymous telemetry.
    /// </summary>
    /// <remarks>
    /// SKU part numbers like <c>ENTERPRISEPACK</c> are Microsoft-global identifiers, not tenant data.
    /// An UNRECOGNISED one is a different matter: it can be a reseller- or partner-specific string,
    /// which narrows down who the tenant buys from. So anything off Microsoft's published list is
    /// reported as <see cref="AnonStatsPrivacy.UnlistedSku"/> rather than sent verbatim.
    /// </remarks>
    public interface ISkuAllowList
    {
        /// <summary>True when Microsoft publishes this SKU part number.</summary>
        bool IsPublished(string skuPartNumber);
    }

    /// <summary>
    /// <see cref="ISkuAllowList"/> backed by the same embedded Microsoft CSV the user import already
    /// uses to resolve licence display names.
    /// </summary>
    public class EmbeddedCsvSkuAllowList : ISkuAllowList
    {
        // Both positive AND negative answers are memoised, so each distinct part number reaches the
        // resolver once however often it is asked about. The embedded resolver is a dictionary lookup,
        // but an injected one need not be cheap.
        private readonly ConcurrentDictionary<string, bool> _answers =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly IOfficeLicenseNameResolver _resolver;

        public EmbeddedCsvSkuAllowList() : this(new OfficeLicenseNameResolver())
        {
        }

        /// <param name="resolver">
        /// Injected so tests can supply a fixed list rather than parsing the shipped CSV.
        /// </param>
        public EmbeddedCsvSkuAllowList(IOfficeLicenseNameResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public bool IsPublished(string skuPartNumber)
        {
            // Guarded here because ConcurrentDictionary rejects a null key, and a blank part number is
            // never a published SKU.
            if (string.IsNullOrWhiteSpace(skuPartNumber)) return false;

            return _answers.GetOrAdd(skuPartNumber, sku => _resolver.GetDisplayNameFor(sku) != null);
        }
    }
}
