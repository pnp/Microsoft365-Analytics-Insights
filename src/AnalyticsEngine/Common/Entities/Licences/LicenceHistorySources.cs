using System;

namespace Common.Entities.Licences
{
    /// <summary>
    /// Values of <c>dbo.user_license_history.from_source</c>: who observed a licence holding. Every value is an
    /// observation, not Entra's assignment time. Readers special-case <see cref="SeededWhenTrackingBegan"/> only.
    /// </summary>
    public static class LicenceHistorySources
    {
        /// <summary>The person already held the licence when history tracking began.</summary>
        public const byte SeededWhenTrackingBegan = 0;

        /// <summary>A tenant-wide licence refresh saw the change; its previous run bounds when it happened.</summary>
        public const byte TenantRefresh = 1;

        /// <summary>
        /// An administrator's one-person refresh saw the change. It is not a tenant-wide run, so it never bounds
        /// another person's history and records no earlier bound of its own: when the change happened before it is
        /// unknown.
        /// </summary>
        public const byte UserRefresh = 2;

        /// <summary>
        /// Whether a history row stands for "already held when tracking began". The same rule as the reports
        /// (<c>from_source = 0 AND valid_from_utc &lt;= @historyStart</c>): a source-0 row opened later - the tenant
        /// refresh repairing a lookup that had no history - is an ordinary observation from its start date.
        /// </summary>
        public static bool HeldBeforeTrackingBegan(byte? fromSource, DateTime? validFromUtc, DateTime? trackingBeganUtc)
            => fromSource == SeededWhenTrackingBegan
               && validFromUtc.HasValue && trackingBeganUtc.HasValue
               && validFromUtc.Value <= trackingBeganUtc.Value;
    }
}
