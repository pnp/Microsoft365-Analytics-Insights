using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Web.AnalyticsWeb.Models.Dlp
{
    /// <summary>
    /// The DLP page's governance section (#648): the prompt-safety and grounding signals Microsoft records on
    /// every Copilot interaction, for one reporting window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not reported is not clean.</b> The jailbreak and XPIA flags are NULL whenever Microsoft's payload
    /// left them out, and on every row imported before the import read them (#570). A NULL is therefore
    /// "unknown", so each rate is taken over the interactions on which the flag WAS reported, and that
    /// denominator travels with it. Counting a NULL as "no attack" would dilute the rate with interactions
    /// nobody checked.
    /// </para>
    /// <para>
    /// <b>Aggregates only.</b> Nothing here names a person or carries prompt content, so every signed-in
    /// reader may see it. Model and plugin names are Microsoft's identifiers, shown as reported.
    /// </para>
    /// </remarks>
    public class DlpGovernanceSummary
    {
        [JsonProperty("fromUtc")]
        public DateTime FromUtc { get; set; }

        [JsonProperty("toUtc")]
        public DateTime ToUtc { get; set; }

        /// <summary>Copilot interactions in the window: the population every figure below is drawn from.</summary>
        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>Interactions with a prompt Microsoft flagged as a jailbreak attempt (<c>copilot_event_messages.jailbreak_detected</c>).</summary>
        [JsonProperty("jailbreak")]
        public DlpGovernanceRate Jailbreak { get; set; } = new DlpGovernanceRate();

        /// <summary>
        /// Interactions where Microsoft detected a cross-prompt injection attack (XPIA) in a resource Copilot used
        /// (<c>copilot_event_accessed_resources.xpia_detected</c>).
        /// </summary>
        [JsonProperty("xpia")]
        public DlpGovernanceRate Xpia { get; set; } = new DlpGovernanceRate();

        /// <summary>How much of the content Copilot used carried a sensitivity label.</summary>
        [JsonProperty("sensitivityLabels")]
        public DlpGovernanceLabelShare SensitivityLabels { get; set; } = new DlpGovernanceLabelShare();

        /// <summary>
        /// Interactions on which Microsoft named at least one AI model. Microsoft does not name the model for
        /// most Microsoft 365 Copilot interactions, so this is normally far below <see cref="Interactions"/>.
        /// </summary>
        [JsonProperty("interactionsWithModel")]
        public long InteractionsWithModel { get; set; }

        /// <summary>The models named, most used first, each as a share of <see cref="Interactions"/>.</summary>
        [JsonProperty("models")]
        public List<DlpGovernanceMixRow> Models { get; set; } = new List<DlpGovernanceMixRow>();

        /// <summary>Interactions on which Copilot invoked at least one AI system plugin, such as web search.</summary>
        [JsonProperty("interactionsWithPlugin")]
        public long InteractionsWithPlugin { get; set; }

        /// <summary>The plugins invoked, most used first, each as a share of <see cref="Interactions"/>.</summary>
        [JsonProperty("plugins")]
        public List<DlpGovernanceMixRow> Plugins { get; set; } = new List<DlpGovernanceMixRow>();
    }

    /// <summary>A per-interaction flag, as a rate per 10,000 of the interactions it was reported on.</summary>
    public class DlpGovernanceRate
    {
        /// <summary>The numerator: interactions on which the flag was reported TRUE at least once.</summary>
        [JsonProperty("flaggedInteractions")]
        public long FlaggedInteractions { get; set; }

        /// <summary>
        /// The denominator: interactions on which the flag was reported at all, true or false. An interaction
        /// whose flag is NULL everywhere is in neither figure.
        /// </summary>
        [JsonProperty("reportedInteractions")]
        public long ReportedInteractions { get; set; }

        /// <summary>
        /// Flagged interactions per 10,000 reported ones. Null - unknown, never 0 - when the flag was reported
        /// on no interaction at all.
        /// </summary>
        [JsonProperty("ratePer10000")]
        public double? RatePer10000 => DlpGovernanceMaths.PerTenThousand(FlaggedInteractions, ReportedInteractions);
    }

    /// <summary>The share of the content Copilot used that carried a sensitivity label.</summary>
    /// <remarks>
    /// A resource is counted once per interaction that used it, so a labelled document Copilot read in ten
    /// interactions counts ten times: this measures how often Copilot works with labelled content, not how
    /// much labelled content exists. An absent label means the content carried none - Microsoft includes
    /// <c>SensitivityLabelId</c> only when there is one - so every resource is in the denominator.
    /// </remarks>
    public class DlpGovernanceLabelShare
    {
        /// <summary>Resources Copilot used that carried a sensitivity label.</summary>
        [JsonProperty("labelledResources")]
        public long LabelledResources { get; set; }

        /// <summary>The denominator: every resource Copilot used, counted once per interaction.</summary>
        [JsonProperty("resources")]
        public long Resources { get; set; }

        /// <summary>Interactions in which Copilot used at least one resource.</summary>
        [JsonProperty("interactionsWithResources")]
        public long InteractionsWithResources { get; set; }

        /// <summary>Labelled resources as a fraction (0-1) of all resources. Null when Copilot used none.</summary>
        [JsonProperty("share")]
        public double? Share => DlpGovernanceMaths.Share(LabelledResources, Resources);
    }

    /// <summary>One AI model or system plugin, and how many of the window's interactions used it.</summary>
    public class DlpGovernanceMixRow
    {
        /// <summary>Microsoft's own name for the model or plugin (e.g. "DEEP_LEO", "BingWebSearch"). Never translated.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Interactions that used it, each counted once however many times it was invoked.</summary>
        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>
        /// <see cref="Interactions"/> as a fraction (0-1) of every interaction in the window. The rows do not add
        /// up to 1: one interaction can use several, and most use none that Microsoft names.
        /// </summary>
        [JsonProperty("share")]
        public double? Share { get; set; }
    }

    /// <summary>
    /// The governance section's arithmetic, apart from the SQL so the rules can be pinned without a database.
    /// </summary>
    public static class DlpGovernanceMaths
    {
        /// <summary>The prompt-safety rates are expressed as flagged interactions per this many.</summary>
        public const int RateBase = 10000;

        /// <summary>Models and plugins listed. The rest are rare enough not to change the picture.</summary>
        public const int TopMix = 10;

        /// <summary>
        /// <paramref name="flagged"/> per 10,000 of <paramref name="reported"/>, or null when nothing was reported:
        /// a rate with no denominator is unknown, and showing it as 0 would claim a clean bill of health.
        /// </summary>
        public static double? PerTenThousand(long flagged, long reported)
        {
            if (reported <= 0)
            {
                return null;
            }

            return flagged * (double)RateBase / reported;
        }

        /// <summary><paramref name="part"/> as a fraction of <paramref name="whole"/>, or null when there is no whole.</summary>
        public static double? Share(long part, long whole)
        {
            if (whole <= 0)
            {
                return null;
            }

            return (double)part / whole;
        }

        /// <summary>
        /// The models or plugins to list: most used first, ties in name order so the list is stable, at most
        /// <see cref="TopMix"/>, each as a share of every interaction in the window.
        /// </summary>
        /// <remarks>
        /// SQL groups by name, so a model reported under several versions - or a plugin under several names -
        /// arrives as one row; a name SQL could not supply arrives empty and is dropped, because a nameless row
        /// is nothing an admin can act on.
        /// </remarks>
        public static List<DlpGovernanceMixRow> Mix(IEnumerable<DlpGovernanceMixRow> rows, long interactions)
        {
            return (rows ?? Enumerable.Empty<DlpGovernanceMixRow>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name) && r.Interactions > 0)
                .OrderByDescending(r => r.Interactions)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .Take(TopMix)
                .Select(r => new DlpGovernanceMixRow
                {
                    Name = r.Name,
                    Interactions = r.Interactions,
                    Share = Share(r.Interactions, interactions),
                })
                .ToList();
        }
    }
}
