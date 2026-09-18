using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UsageReporting
{
    /// <summary>
    /// A maintainer's note about which real organisation is behind an anonymous client id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONLY place in the system where a reporting client is tied to a real identity, and
    /// it only ever happens because a customer asked for it: <see cref="AnonUsageStatsModel.AnonClientId"/>
    /// is random, so nobody - including the project team - can work out who a client is from the
    /// telemetry alone. A customer who wants their reports recognised finds the id in their own
    /// importer logs and tells us; a maintainer then records it here.
    /// </para>
    /// <para>
    /// Nothing about this changes what a client uploads. The payload stays anonymous on the wire.
    /// </para>
    /// <para>
    /// It must live in its OWN Cosmos container. <see cref="CosmosTelemetrySaveAdaptor.SaveOrUpdate"/>
    /// upserts the whole <see cref="AnonUsageStatsModel"/> into the "current" container on every
    /// report, so an annotation written onto that document would be silently destroyed by the client's
    /// next upload.
    /// </para>
    /// </remarks>
    public class ClientAnnotation : StatsCosmosDoc
    {
        /// <summary>The anonymous client id this note describes. Also the Cosmos id / partition key.</summary>
        public override string id { get; set; }

        /// <summary>
        /// What to show instead of the bare id, e.g. the organisation's name. Maintainer-entered, never
        /// reported by the client.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>Free-text context - who asked, when, what they are trialling, and so on.</summary>
        public string Notes { get; set; }

        /// <summary>When the note was last changed.</summary>
        public DateTime UpdatedUtc { get; set; }

        /// <summary>Which maintainer last changed it, for accountability.</summary>
        public string UpdatedBy { get; set; }

        /// <summary>Longest accepted <see cref="DisplayName"/>.</summary>
        public const int MaxDisplayNameLength = 200;

        /// <summary>Longest accepted <see cref="Notes"/>.</summary>
        public const int MaxNotesLength = 2000;

        /// <summary>True when there is nothing worth storing, so the caller can delete instead.</summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(DisplayName) && string.IsNullOrWhiteSpace(Notes);

        public override string ToString() => $"{id}: {DisplayName}";
    }

    /// <summary>
    /// Reads and writes <see cref="ClientAnnotation"/> records.
    /// </summary>
    public interface IClientAnnotationStore
    {
        /// <summary>Every annotation. Small by nature - one per client the team has actually spoken to.</summary>
        Task<IReadOnlyList<ClientAnnotation>> LoadAllAsync();

        Task<ClientAnnotation> GetAsync(string anonClientId);

        Task SaveAsync(ClientAnnotation annotation);

        Task DeleteAsync(string anonClientId);
    }
}
