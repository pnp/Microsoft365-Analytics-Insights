using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Common.Entities.LicenceActivity
{
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityAvailability
    {
        public bool Available { get; set; }
        public int MinimumDays { get; set; } = LicenceActivityQuery.MinimumDays;
        public int MaximumDays { get; set; } = LicenceActivityQuery.MaximumDays;
        public List<string> Messages { get; set; } = new List<string>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public abstract class LicenceActivitySnapshot
    {
        public string SnapshotId { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }

        [JsonIgnore]
        public DateTime? SourceExpiresUtc { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityOverview : LicenceActivitySnapshot
    {
        [JsonIgnore]
        public string ReadModelId { get; set; }

        public LicenceActivityQuery Query { get; set; }
        public int DistinctAssignedUsers { get; set; }
        public List<LicenceActivitySku> Licences { get; set; } = new List<LicenceActivitySku>();
        public List<LicenceActivityCoverage> Coverage { get; set; } = new List<LicenceActivityCoverage>();
        public List<LicenceActivityDemographic> Departments { get; set; } = new List<LicenceActivityDemographic>();
        public List<LicenceActivityDemographic> Countries { get; set; } = new List<LicenceActivityDemographic>();
        public bool DemographicsTruncated { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivitySku
    {
        public int LicenceTypeId { get; set; }
        public string Name { get; set; }
        public string SkuId { get; set; }
        public int AssignedUsers { get; set; }
        public List<LicenceActivityDistribution> Workloads { get; set; } = new List<LicenceActivityDistribution>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityDistribution
    {
        public string Workload { get; set; }
        public int High { get; set; }
        public int Moderate { get; set; }
        public int Low { get; set; }
        public int Zero { get; set; }
        public int Unknown { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityDemographic
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int AssignedUsers { get; set; }
        public List<LicenceActivityDistribution> Workloads { get; set; } = new List<LicenceActivityDistribution>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityCoverage
    {
        public string Workload { get; set; }
        public string Status { get; set; }
        public string Source { get; set; }
        public string Measure { get; set; }
        /// <summary>Stable key the portal translates <see cref="Measure"/> by; <c>null</c> for an unrecognised measure.</summary>
        public string MeasureKey => LicenceActivityDisplayKeys.MeasureKeyFor(Measure);
        public string Granularity { get; set; }
        public string Message { get; set; }
        /// <summary>Stable key the portal translates <see cref="Message"/> by; <c>null</c> for an unrecognised message.</summary>
        public string MessageKey => LicenceActivityDisplayKeys.MessageKeyFor(Message);
        public DateTime? EffectiveFromUtc { get; set; }
        public DateTime? EffectiveToUtc { get; set; }
        public DateTime? LatestImportUtc { get; set; }
        public int LagDays { get; set; }
        public int? ReportPeriodDays { get; set; }
        public int ExpectedSamples { get; set; }
        public int ObservedSamples { get; set; }
        public int UnmatchedUsers { get; set; }
        public List<DateTime> SnapshotDates { get; set; } = new List<DateTime>();
    }

    /// <summary>
    /// Stable keys for the coverage measures and messages the licence activity SQL writes in English, so the
    /// portal can show them in the reader's language (the server's English stays the fallback for a key the
    /// portal does not know).
    /// </summary>
    /// <remarks>
    /// The keys are computed from the English text rather than stamped on at load time, so every way a
    /// coverage or evidence object is made - read from SQL, cloned by the read model, or built in C# -
    /// carries them. <c>serverAuthoredText.test.ts</c> reads <c>LicenceActivitySql.cs</c>,
    /// <c>SqlLicenceActivityStore.cs</c> and this switch, and fails when a sentence has no key, a key has no
    /// sentence, or the portal and the server disagree about the keys.
    /// </remarks>
    public static class LicenceActivityDisplayKeys
    {
        public static string MeasureKeyFor(string measure)
        {
            switch (measure)
            {
                case "Teams messages and meetings counted by Microsoft, averaged across the readings": return "m365.teams";
                case "emails sent and read counted by Microsoft, averaged across the readings": return "m365.outlook";
                case "files viewed or edited counted by Microsoft, averaged across the readings": return "m365.files";
                case "counts published by Microsoft": return "m365.published";
                case "Copilot prompts counted by Microsoft, averaged across the readings": return "copilot.microsoftReportPrompts";
                case "Copilot prompts and days used, from one rolling report": return "copilot.singleRollingReport";
                case "recorded Copilot activity only": return "copilot.recordedActivity";
                case "Copilot use counted per active week": return "copilot.auditActiveWeeks";
                case "Copilot activity counted per active week": return "copilot.interactionActiveWeeks";
                default: return null;
            }
        }

        public static string MessageKeyFor(string message)
        {
            switch (message)
            {
                case "The Microsoft 365 usage-report import is switched off, so nothing can be measured for this service. That is not the same as nobody using it.": return "m365.disabled";
                case "Every day of every week in the period was imported from Microsoft's published reports. Someone counts as active in a week if Microsoft recorded activity for them in that week. Because those reports list only the people who were active, a fully measured week with nothing recorded for someone means they did nothing - not that they could not be measured. The published counts are averaged across the weeks, never added up and never presented as a daily total.": return "m365.available";
                case "At least one week in the period is missing a day of Microsoft's reports. Those weeks cannot prove either activity or inactivity, so activity levels stay Unknown and nobody is listed as least active for this service.": return "m365.partial";
                case "Collection is switched on for this service, but no report has arrived yet.": return "m365.notImported";
                case "No week in the dates you selected was imported in full.": return "m365.missingCoverage";
                case "Microsoft's most recent 7-day Copilot report was read once per week; where those reports overlap the counts are averaged, never added up. People Microsoft did not list, and older reports that predate the current counters, stay Unknown. Microsoft only reports on people who hold a Copilot licence.": return "copilotReport.available";
                case "At least one week in the period has no Copilot reading on its end date. Earlier readings are still shown as evidence, but activity levels stay Unknown and nobody is listed as least active for Copilot.": return "copilotReport.partial";
                case "Microsoft's Copilot usage report hid every person's identity, so its activity cannot be tied back to the people holding the licence. To fix this, turn off 'Display concealed user, group and site names in all reports' in the Microsoft 365 admin centre (Settings > Org settings > Reports).": return "copilotReport.unmatchableIdentity";
                case "Microsoft's per-person Copilot usage report has never been collected on this deployment.": return "copilotReport.notImported";
                case "The last attempt to collect Microsoft's per-person Copilot usage report failed, so Copilot activity is unknown rather than zero.": return "copilotReport.failed";
                case "None of Microsoft's Copilot usage reports fits entirely inside the dates you selected.": return "copilotReport.missingCoverage";
                case "Microsoft's Copilot report hid every person's identity, so Copilot audit records are used instead. They prove who DID use Copilot, but cannot prove that anybody else did not.": return "copilotAudit.unmatchableIdentity";
                case "Copilot audit records prove who DID use Copilot, but nothing confirms that every Copilot event was captured, so anyone absent stays Unknown rather than inactive.": return "copilotAudit.partial";
                case "Microsoft's Copilot report hid every person's identity, so Copilot chat history is used instead. It proves who DID use Copilot, but cannot prove that anybody else did not.": return "copilotInteractions.unmatchableIdentity";
                case "Copilot chat history proves who DID use Copilot, but nothing confirms the history is complete for everybody, so anyone absent stays Unknown rather than inactive.": return "copilotInteractions.partial";
                case "Copilot audit records exist, but none fall inside the dates you selected. That is not the same as nobody using Copilot.": return "copilotAudit.missingCoverage";
                case "Copilot chat history exists, but none of it falls inside the dates you selected. That is not the same as nobody using Copilot.": return "copilotInteractions.missingCoverage";
                case "The dates you selected match one of Microsoft's rolling Copilot reports exactly. People Microsoft did not list, and reports that do not record days used, stay Unknown. Microsoft only reports on people who hold a Copilot licence.": return "copilotReport.singleWindowAvailable";
                case "Microsoft only published a longer rolling report inside the dates you selected. It is shown here with the dates it really covers, but activity levels stay Unknown for your custom range.": return "copilotReport.singleWindowLonger";
                // LicenceActivitySql's Copilot fallback row, written from C# (initialMessage) into N'{2}'.
                case "Collection is switched on for Copilot, but no Copilot activity has arrived for these dates yet.": return "copilot.notImported";
                case "Copilot collection is switched off on this deployment, so nothing can be measured. That is not the same as nobody using Copilot.": return "copilot.disabled";
                default: return null;
            }
        }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityUsers : LicenceActivitySnapshot
    {
        public string OverviewId { get; set; }
        public LicenceActivityQuery Query { get; set; }
        public int TotalUsers { get; set; }
        public int RankedUsers { get; set; }
        public List<LicenceActivityUser> MostActive { get; set; } = new List<LicenceActivityUser>();
        public List<LicenceActivityUser> LeastActive { get; set; } = new List<LicenceActivityUser>();
        public List<LicenceActivityUser> Users { get; set; } = new List<LicenceActivityUser>();
        public List<string> Messages { get; set; } = new List<string>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityUser
    {
        public int UserId { get; set; }
        public string UserPrincipalName { get; set; }
        public string Department { get; set; }
        public string Country { get; set; }
        public bool? AccountEnabled { get; set; }
        public List<LicenceActivityEvidence> Workloads { get; set; } = new List<LicenceActivityEvidence>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityEvidence
    {
        public string Workload { get; set; }
        public string Status { get; set; }
        public string Band { get; set; }
        public string Source { get; set; }
        public string Measure { get; set; }
        /// <summary>Stable key the portal translates <see cref="Measure"/> by; <c>null</c> for an unrecognised measure.</summary>
        public string MeasureKey => LicenceActivityDisplayKeys.MeasureKeyFor(Measure);
        public int ActiveSamples { get; set; }
        public int ObservedSamples { get; set; }
        public int ExpectedSamples { get; set; }
        public double? AverageActions { get; set; }
        public DateTime? LastActivityUtc { get; set; }
    }
}
