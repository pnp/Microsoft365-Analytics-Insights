using Common.Entities.CopilotAdoption;
using System;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// The calendar days a report window covers, for the sources kept by day: preview credit balances (one snapshot a
    /// day) and the Copilot Studio ledger (billed by day). A rolling window runs to now, so its last day is today.
    /// Explicit dates end on the chosen day, and their exclusive bound is already the next midnight: adding a day to
    /// that would read, and label, one day too many.
    /// </summary>
    public static class CopilotInvestmentWindow
    {
        /// <summary>The last calendar day the window covers: the chosen end date, or today for a rolling window.</summary>
        public static DateTime LastDay(CopilotAdoptionDateRange range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            return (range.UsesExplicitDates ? range.ToInclusiveUtc : range.ToExclusiveUtc).Date;
        }

        /// <summary>The midnight after <see cref="LastDay"/>: the exclusive bound for a source kept by day.</summary>
        public static DateTime DayAfter(CopilotAdoptionDateRange range) => LastDay(range).AddDays(1);

        /// <summary>The end the report shows: the chosen end date, or now for a rolling window.</summary>
        public static DateTime End(CopilotAdoptionDateRange range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            return range.UsesExplicitDates ? range.ToInclusiveUtc : range.ToExclusiveUtc;
        }
    }
}
