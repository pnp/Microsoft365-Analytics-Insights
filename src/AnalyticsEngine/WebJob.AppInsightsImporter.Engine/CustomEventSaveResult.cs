using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// What saving a batch of custom events did. A section that fails is isolated - the sections after it still
    /// run - but the batch is then incomplete, and the day it belongs to must not count as saved: it is retried,
    /// within the limit <see cref="AppInsightsDayFailureTracker"/> sets. Sections swallowing their failures used to
    /// let a failed section of any day but the last be lost silently.
    ///
    /// A failure inside a section that the section handles itself does not count: the page-update section skips
    /// a page whose metadata fails to apply and logs it, and the tracker sends a page's metadata again on a later
    /// visit.
    /// </summary>
    public sealed class CustomEventSaveResult
    {
        public CustomEventSaveResult(IEnumerable<string> failedSections)
        {
            FailedSections = (failedSections ?? Enumerable.Empty<string>()).ToList();
        }

        /// <summary>The sections that failed, by the name they are logged under.</summary>
        public IReadOnlyList<string> FailedSections { get; }

        public bool IsComplete => FailedSections.Count == 0;

        /// <summary>Throws when a section failed, so the importer treats the batch's day as not saved.</summary>
        public void ThrowIfIncomplete()
        {
            if (!IsComplete)
            {
                throw new CustomEventSaveIncompleteException(FailedSections);
            }
        }
    }

    /// <summary>One or more custom-event sections failed; the others were saved. See <see cref="CustomEventSaveResult"/>.</summary>
    public sealed class CustomEventSaveIncompleteException : Exception
    {
        public CustomEventSaveIncompleteException(IReadOnlyList<string> failedSections)
            : base($"Saving custom events failed in: {string.Join(", ", failedSections)} (logged above). The other sections were saved.")
        {
            FailedSections = failedSections;
        }

        public IReadOnlyList<string> FailedSections { get; }
    }
}
