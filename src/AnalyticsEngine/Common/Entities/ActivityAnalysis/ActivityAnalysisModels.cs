using Common.Entities.UserFilters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>
    /// <c>GET api/ActivityAnalysis/availability</c>: whether the page has figures, which weeks, and which metrics.
    /// </summary>
    /// <remarks>Facts and stable keys only - the portal writes every sentence (the API reports facts, the UI writes the words).</remarks>
    public sealed class ActivityAnalysisAvailability
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary><see cref="ActivityAnalysisReasons"/>, or <c>null</c>.</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("earliestWeek")]
        public string EarliestWeek { get; set; }

        [JsonProperty("latestWeek")]
        public string LatestWeek { get; set; }

        [JsonProperty("defaultFrom")]
        public string DefaultFrom { get; set; }

        [JsonProperty("defaultTo")]
        public string DefaultTo { get; set; }

        [JsonProperty("maximumWeeks")]
        public int MaximumWeeks { get; set; } = ActivityAnalysisPeriod.MaximumWeeks;

        [JsonProperty("categories")]
        public List<string> Categories { get; set; } = new List<string>();

        [JsonProperty("metrics")]
        public List<ActivityAnalysisMetricModel> Metrics { get; set; } = new List<ActivityAnalysisMetricModel>();

        public static ActivityAnalysisAvailability From(ActivityAnalysisSchema schema)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            return new ActivityAnalysisAvailability
            {
                Available = schema.Reason == null,
                Reason = schema.Reason,
                EarliestWeek = ActivityAnalysisWeeks.Format(schema.EarliestWeek),
                LatestWeek = ActivityAnalysisWeeks.Format(schema.LatestWeek),
                DefaultFrom = ActivityAnalysisWeeks.Format(schema.DefaultFrom),
                DefaultTo = ActivityAnalysisWeeks.Format(schema.DefaultTo),
                Categories = ActivityAnalysisCategories.All.ToList(),
                Metrics = ActivityAnalysisMetricCatalogue.All.Select(m => new ActivityAnalysisMetricModel
                {
                    Key = m.Key,
                    Category = m.Category,
                    Unit = m.Unit,
                    Core = m.Core,
                    Available = schema.IsAvailable(m),
                    Label = m.Label,
                }).ToList(),
            };
        }
    }

    public sealed class ActivityAnalysisMetricModel
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("core")]
        public bool Core { get; set; }

        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>English fallback only, for a key the portal's catalogue does not know.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }
    }

    /// <summary><c>GET api/ActivityAnalysis/report</c>: every figure on the page for one period, metric selection and filter.</summary>
    public sealed class ActivityAnalysisReport
    {
        /// <summary>When the figures were read from the database.</summary>
        [JsonProperty("generatedUtc")]
        public DateTime GeneratedUtc { get; set; }

        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("to")]
        public string To { get; set; }

        /// <summary>Every Monday in the period; each series array lines up with it.</summary>
        [JsonProperty("weekStarts")]
        public List<string> WeekStarts { get; set; } = new List<string>();

        /// <summary>The selected metrics, in the order requested.</summary>
        [JsonProperty("metrics")]
        public List<string> Metrics { get; set; } = new List<string>();

        /// <summary>People with any recorded activity in the period whom the administrator's filter admits.</summary>
        [JsonProperty("populationPeople")]
        public int PopulationPeople { get; set; }

        /// <summary>Of those, the people the reader's filters - user filter, licences, activity ranges - also admit.</summary>
        [JsonProperty("matchingPeople")]
        public int MatchingPeople { get; set; }

        /// <summary>Of those, the people with activity in at least one selected metric. 0 when suppressed.</summary>
        [JsonProperty("activePeople")]
        public int ActivePeople { get; set; }

        /// <summary>
        /// True when so few people match - 1 to 4 - that the figures would be a handful of individuals' records, and the
        /// reader may not see individuals. Every figure is then empty; only the counts of people remain.
        /// </summary>
        [JsonProperty("suppressed")]
        public bool Suppressed { get; set; }

        [JsonProperty("series")]
        public List<ActivityAnalysisSeries> Series { get; set; } = new List<ActivityAnalysisSeries>();

        [JsonProperty("byCompany")]
        public ActivityAnalysisGroupChart ByCompany { get; set; } = new ActivityAnalysisGroupChart();

        [JsonProperty("byDepartment")]
        public ActivityAnalysisGroupChart ByDepartment { get; set; } = new ActivityAnalysisGroupChart();

        [JsonProperty("departments")]
        public List<ActivityAnalysisDepartmentRow> Departments { get; set; } = new List<ActivityAnalysisDepartmentRow>();

        /// <summary>How many departments were folded into the single "other" row.</summary>
        [JsonProperty("otherDepartments")]
        public int OtherDepartments { get; set; }

        [JsonProperty("total")]
        public ActivityAnalysisTotal Total { get; set; } = new ActivityAnalysisTotal();

        /// <summary>Every licence held by somebody in the population, for the filter picker.</summary>
        [JsonProperty("licences")]
        public List<ActivityAnalysisLicenceModel> Licences { get; set; } = new List<ActivityAnalysisLicenceModel>();

        /// <summary>The highest per-person total of every available metric over the population - the range sliders' bounds.</summary>
        [JsonProperty("rangeMaxima")]
        public List<ActivityAnalysisRangeMaximum> RangeMaxima { get; set; } = new List<ActivityAnalysisRangeMaximum>();

        /// <summary>The reader's filter as applied, or <c>null</c> when they set none.</summary>
        [JsonProperty("userFilter")]
        public UserFilterEcho UserFilter { get; set; }
    }

    public sealed class ActivityAnalysisSeries
    {
        [JsonProperty("metric")]
        public string Metric { get; set; }

        /// <summary>The metric's total per week, aligned with <see cref="ActivityAnalysisReport.WeekStarts"/>.</summary>
        [JsonProperty("sum")]
        public long[] Sum { get; set; }

        /// <summary>How many of the matching people had a value above zero each week.</summary>
        [JsonProperty("activePeople")]
        public int[] ActivePeople { get; set; }
    }

    public sealed class ActivityAnalysisGroupChart
    {
        [JsonProperty("rows")]
        public List<ActivityAnalysisGroupRow> Rows { get; set; } = new List<ActivityAnalysisGroupRow>();

        /// <summary>How many groups were folded into the single <c>other</c> row.</summary>
        [JsonProperty("otherGroups")]
        public int OtherGroups { get; set; }
    }

    public sealed class ActivityAnalysisGroupRow
    {
        /// <summary>
        /// The company or department as the directory holds it - tenant data, never translated. <c>null</c> for "not set"
        /// (<see cref="Other"/> false) and for the folded row (<see cref="Other"/> true).
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("other")]
        public bool Other { get; set; }

        [JsonProperty("activePeople")]
        public int ActivePeople { get; set; }
    }

    public sealed class ActivityAnalysisDepartmentRow
    {
        /// <summary>Tenant data, never translated. <c>null</c> for "not set" and for the folded row.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("other")]
        public bool Other { get; set; }

        /// <summary>Matching people in the department.</summary>
        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("values")]
        public List<ActivityAnalysisMetricValue> Values { get; set; } = new List<ActivityAnalysisMetricValue>();
    }

    public sealed class ActivityAnalysisMetricValue
    {
        [JsonProperty("metric")]
        public string Metric { get; set; }

        /// <summary>The metric summed over the people and the weeks.</summary>
        [JsonProperty("sum")]
        public long Sum { get; set; }

        /// <summary>How many of the people have a total above zero.</summary>
        [JsonProperty("unique")]
        public int Unique { get; set; }
    }

    public sealed class ActivityAnalysisTotal
    {
        [JsonProperty("people")]
        public int People { get; set; }

        [JsonProperty("values")]
        public List<ActivityAnalysisMetricValue> Values { get; set; } = new List<ActivityAnalysisMetricValue>();
    }

    public sealed class ActivityAnalysisLicenceModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>Tenant data, never translated.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuId")]
        public string SkuId { get; set; }

        /// <summary>Holders among the population.</summary>
        [JsonProperty("people")]
        public int People { get; set; }
    }

    public sealed class ActivityAnalysisRangeMaximum
    {
        [JsonProperty("metric")]
        public string Metric { get; set; }

        [JsonProperty("max")]
        public long Max { get; set; }
    }

    /// <summary><c>GET api/ActivityAnalysis/people</c>: the matching people themselves. Needs See PII.</summary>
    public sealed class ActivityAnalysisPeople
    {
        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("noDepartment")]
        public bool NoDepartment { get; set; }

        /// <summary>Matching people in the department (or everywhere), however many are returned.</summary>
        [JsonProperty("totalPeople")]
        public int TotalPeople { get; set; }

        [JsonProperty("truncated")]
        public bool Truncated { get; set; }

        [JsonProperty("people")]
        public List<ActivityAnalysisPerson> People { get; set; } = new List<ActivityAnalysisPerson>();
    }

    public sealed class ActivityAnalysisPerson
    {
        /// <summary>From the directory snapshot; <c>null</c> for somebody imported since it was read.</summary>
        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("values")]
        public List<ActivityAnalysisPersonValue> Values { get; set; } = new List<ActivityAnalysisPersonValue>();
    }

    public sealed class ActivityAnalysisPersonValue
    {
        [JsonProperty("metric")]
        public string Metric { get; set; }

        /// <summary>The person's total over the period.</summary>
        [JsonProperty("sum")]
        public long Sum { get; set; }
    }
}
