using System;
using System.Collections.Generic;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql.Models;

namespace WebJob.AppInsightsImporter.Engine.Sql.Rules
{
    /// <summary>
    /// What the click rules decided to stage, and what they rejected. The reject count was previously
    /// only ever written to the log, so it could not be asserted. See issue #369.
    /// </summary>
    internal class ClickStagingPlan
    {
        public List<ClickTempEntity> RowsToStage { get; } = new List<ClickTempEntity>();

        /// <summary>Click events that did not carry enough data to be staged.</summary>
        public int InvalidClicks { get; set; }
    }

    /// <summary>
    /// Pure rules deciding which click events can be staged - no SQL, no ADO.NET, no logging.
    /// </summary>
    internal static class ClickEventRules
    {
        /// <summary>
        /// Whether a click event can be turned into a staging row.
        ///
        /// This deliberately does NOT simply defer to <see cref="ClickEventAppInsightsQueryResult.IsValid"/>,
        /// which has a null-lifting hole: <c>CustomProperties?.PageRequestId</c> is a <c>Guid?</c>, so when
        /// the id is null the comparison <c>null != Guid.Empty</c> evaluates to TRUE and the event passes.
        /// The <see cref="ClickTempEntity"/> constructor then requires <c>PageRequestId.HasValue</c> and
        /// throws <c>ArgumentNullException</c>, which <c>SaveSectionSafe</c> catches - discarding every
        /// click in that cycle rather than just the malformed one. Requiring HasValue here keeps a single
        /// bad event from costing the whole batch.
        /// </summary>
        public static bool CanStage(ClickEventAppInsightsQueryResult click)
        {
            var props = click?.CustomProperties;
            if (props == null)
            {
                return false;
            }

            return props.PageRequestId.HasValue
                && props.PageRequestId.Value != Guid.Empty
                && !string.IsNullOrEmpty(props.LinkText)
                && click.Timestamp > DateTime.MinValue;
        }

        /// <summary>
        /// Select the click events out of a mixed custom-event collection and project the stageable ones.
        /// </summary>
        public static ClickStagingPlan Plan(IEnumerable<BaseCustomEventAppInsightsQueryResult> events)
        {
            var plan = new ClickStagingPlan();
            if (events == null)
            {
                return plan;
            }

            foreach (var e in events)
            {
                var click = e as ClickEventAppInsightsQueryResult;
                if (click == null)
                {
                    continue; // a different event type - not this section's business
                }

                if (CanStage(click))
                {
                    plan.RowsToStage.Add(new ClickTempEntity(click));
                }
                else
                {
                    plan.InvalidClicks++;
                }
            }

            return plan;
        }
    }
}
