extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ReportAreasModel = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportAreasModel;
using ReportChart = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportChart;
using ReportMatrix = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportMatrix;
using ReportMatrixCell = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportMatrixCell;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards the Office apps report area's wire contract and the shape of the SQL it generates.
    /// </summary>
    /// <remarks>
    /// Two classes of defect are caught here, both of which compile cleanly and fail only in a
    /// browser or only against a real database:
    /// <list type="number">
    /// <item>A model property with no <c>[JsonProperty]</c>. This web application configures no
    /// camelCase contract resolver, so such a property serialises in PascalCase and the SPA reads
    /// <c>undefined</c> - the exact defect <see cref="DlpApiContractTests"/> exists for, repeated
    /// here for the new matrix models.</item>
    /// <item>SQL that silently stops covering an app or a platform. Every query in the area is
    /// generated from a single catalogue, so a column renamed in one place and not another would
    /// otherwise produce a chart that is simply missing a row, with no error anywhere.</item>
    /// </list>
    /// </remarks>
    [TestClass]
    public class OfficeAppsReportContractTests
    {
        /// <summary>
        /// The apps the area reports on. Hard-coded rather than read from the catalogue so that
        /// dropping an app from the catalogue fails a test instead of quietly shrinking every chart.
        /// </summary>
        private static readonly string[] ExpectedApps =
            { "Outlook", "Teams", "Word", "Excel", "PowerPoint", "OneNote" };

        private static readonly string[] ExpectedPlatforms = { "Windows", "Mac", "Mobile", "Web" };

        #region Wire contract

        [TestMethod]
        public void EveryReportModelPropertyDeclaresACamelCaseWireName()
        {
            foreach (var type in new[] { typeof(ReportAreasModel), typeof(ReportChart), typeof(ReportMatrix), typeof(ReportMatrixCell) })
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var attribute = property.GetCustomAttribute<JsonPropertyAttribute>();

                    Assert.IsNotNull(attribute,
                        $"{type.Name}.{property.Name} has no [JsonProperty]. This app has no camelCase contract "
                        + "resolver, so it would serialise in PascalCase and the SPA would read undefined.");

                    Assert.IsFalse(string.IsNullOrWhiteSpace(attribute.PropertyName),
                        $"{type.Name}.{property.Name} has an empty [JsonProperty] name.");

                    Assert.IsTrue(char.IsLower(attribute.PropertyName[0]),
                        $"{type.Name}.{property.Name} serialises as '{attribute.PropertyName}', which is not camelCase.");
                }
            }
        }

        /// <summary>
        /// <c>api/Reports/areas</c> must offer the new area, or the tab never appears however well the
        /// endpoint behind it works.
        /// </summary>
        [TestMethod]
        public void AreasResponseCarriesTheOfficeAppsFlag()
        {
            var areas = JObject.Parse(JsonConvert.SerializeObject(new ReportAreasModel { OfficeApps = true }));

            Assert.IsNotNull(areas["officeApps"], "api/Reports/areas must expose 'officeApps'.");
            Assert.IsTrue((bool)areas["officeApps"]);
        }

        /// <summary>
        /// The matrix collections are rendered with <c>.map()</c>, so they must serialise as arrays on
        /// a default-constructed instance rather than as null.
        /// </summary>
        [TestMethod]
        public void MatrixSerialisesItsCollectionsAsArraysEvenWhenEmpty()
        {
            var matrix = JObject.Parse(JsonConvert.SerializeObject(new ReportMatrix()));

            foreach (var field in new[] { "rows", "columns", "cells" })
            {
                Assert.IsNotNull(matrix[field], $"A matrix chart must expose '{field}'.");
                Assert.AreEqual(JTokenType.Array, matrix[field].Type,
                    $"'{field}' is rendered with .map() and must always be an array.");
            }

            foreach (var field in new[] { "rowLabel", "columnLabel", "shadeByRow" })
            {
                Assert.IsTrue(matrix.ContainsKey(field), $"A matrix chart must expose '{field}'.");
            }
        }

        [TestMethod]
        public void MatrixCellSerialisesTheFieldsTheGridReads()
        {
            var cell = JObject.Parse(JsonConvert.SerializeObject(
                new ReportMatrixCell { Row = "Excel", Column = "Finance", Value = 42 }));

            Assert.AreEqual("Excel", (string)cell["row"]);
            Assert.AreEqual("Finance", (string)cell["column"]);
            Assert.AreEqual(42d, (double)cell["value"]);
        }

        /// <summary>
        /// The SPA's <c>types/reports.ts</c> is the other half of the contract, so it is read here - a
        /// field renamed on one side without the other fails the build rather than the page.
        /// </summary>
        [TestMethod]
        public void TheTypeScriptTypesDeclareTheSameFieldNames()
        {
            var typings = ReportsTypeScriptSource();

            var expected = new[] { typeof(ReportAreasModel), typeof(ReportChart), typeof(ReportMatrix), typeof(ReportMatrixCell) }
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Select(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct();

            foreach (var field in expected)
            {
                StringAssert.Contains(typings, field + ":",
                    $"types/reports.ts does not declare '{field}', so the SPA cannot read it.");
            }

            StringAssert.Contains(typings, "'matrix'",
                "types/reports.ts must include 'matrix' in ReportChartType.");
            StringAssert.Contains(typings, "'office-apps'",
                "types/reports.ts must include 'office-apps' in ReportAreaKey or the route cannot be requested.");
        }

        /// <summary>
        /// The page renders the new area, and does so through the matrix component.
        /// </summary>
        /// <remarks>
        /// A route the API serves but the page never lists is invisible, which is precisely how a
        /// completed endpoint can look like a missing feature.
        /// </remarks>
        [TestMethod]
        public void TheReportsPageDeclaresTheAreaAndRendersMatrixCharts()
        {
            var page = PortalSource(Path.Combine("pages", "ReportsPage.tsx"));

            StringAssert.Contains(page, "'office-apps'",
                "ReportsPage.tsx must list the office-apps area or its tab never appears.");
            StringAssert.Contains(page, "officeApps",
                "ReportsPage.tsx must gate the tab on the officeApps availability flag.");
            StringAssert.Contains(page, "MatrixChart",
                "ReportsPage.tsx must render matrix charts or the department and domain grids show nothing.");
        }

        #endregion

        #region Generated SQL

        /// <summary>Every query the area can run, so a new one cannot escape these checks.</summary>
        private static IEnumerable<KeyValuePair<string, string>> AllQueries()
        {
            yield return Query("AppPopularity", ReportsAPIController.AppPopularityQuery());
            yield return Query("PlatformPopularity", ReportsAPIController.PlatformPopularityQuery());
            yield return Query("AppWeekly", ReportsAPIController.AppWeeklyQuery());
            yield return Query("PlatformWeekly", ReportsAPIController.PlatformWeeklyQuery());
            yield return Query("AppBreadth", ReportsAPIController.AppBreadthQuery());
            yield return Query("AppPlatformMatrix", ReportsAPIController.AppPlatformMatrixQuery());
            yield return Query("AppByDepartment", ReportsAPIController.AppByDepartmentQuery());
            yield return Query("DepartmentAdoptionRate", ReportsAPIController.DepartmentAdoptionRateQuery());
            yield return Query("AppByDomain", ReportsAPIController.AppByDomainQuery());
            yield return Query("WebOnly", ReportsAPIController.WebOnlyQuery());
            yield return Query("CopilotAttachRate", ReportsAPIController.CopilotAttachRateQuery());
            yield return Query("CopilotDataPresence", ReportsAPIController.CopilotDataPresenceQuery());
        }

        private static KeyValuePair<string, string> Query(string name, string sql) =>
            new KeyValuePair<string, string>(name, sql);

        /// <summary>
        /// Every query is bounded by the requested window.
        /// </summary>
        /// <remarks>
        /// An unbounded query against <c>platform_user_activity_log</c> is not a slow query, it is an
        /// outage: the table gains a row per user per day, so at the ~200k-user tenant this product is
        /// designed against it reaches tens of millions of rows within a year and a missing predicate
        /// scans all of it on every page load.
        /// </remarks>
        [TestMethod]
        public void EveryQueryIsBoundedByTheWindowParameter()
        {
            foreach (var query in AllQueries())
            {
                StringAssert.Contains(query.Value, "@from",
                    $"{query.Key} does not reference @from, so it would read the whole table.");
                StringAssert.Contains(query.Value, ">= @from",
                    $"{query.Key} must bound its scan with '>= @from'.");
            }
        }

        /// <summary>
        /// The window is a real cost lever only if the plan is chosen for the window actually asked
        /// for, so every query recompiles rather than reusing a plan built for a different one.
        /// </summary>
        [TestMethod]
        public void EveryQueryRecompilesForTheWindowItWasGiven()
        {
            foreach (var query in AllQueries())
            {
                StringAssert.Contains(query.Value, "OPTION (RECOMPILE)",
                    $"{query.Key} should recompile so a 1-month window does not inherit a 6-month plan.");
            }
        }

        /// <summary>
        /// Nothing here may count an activity row as a unit of work, and every query must collapse a
        /// person's rows before it fans out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The source is one boolean per user per report date - it records THAT someone used an app,
        /// never how much. Counting activity rows would report "number of daily report rows", which
        /// rises purely with how long the importer has been running and reads convincingly like a
        /// usage figure.
        /// </para>
        /// <para>
        /// The collapse is also the difference between the area working and timing out. Every query
        /// aggregates to one row per person (or per person per week) with <c>MAX(1 * bit)</c> BEFORE
        /// the <c>CROSS APPLY (VALUES ...)</c> fan-out. Measured on an 18m-row synthetic table, the
        /// app-on-platform matrix took 91s fanning out first and 17s collapsing first, for identical
        /// output - and the per-chart timeout is 25s. A future edit that reorders those two steps
        /// would produce correct numbers and an unusable page, which is exactly the kind of regression
        /// no other test would catch.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void ActivityQueriesCollapsePerPersonBeforeFanningOut()
        {
            foreach (var query in AllQueries())
            {
                if (query.Key == "DepartmentAdoptionRate")
                {
                    // This one never fans out: it asks a single "any app" question per person, so it
                    // collapses with SELECT DISTINCT rather than MAX(1 * bit).
                    StringAssert.Contains(query.Value, "SELECT DISTINCT a.user_id",
                        "The adoption rate must reduce activity to one row per person.");
                    continue;
                }

                if (query.Key == "CopilotDataPresence") continue; // A TOP(1) existence probe; nothing to collapse.

                StringAssert.Contains(query.Value, "GROUP BY a.user_id",
                    $"{query.Key} must group by user_id so the collapse happens before any fan-out.");

                Assert.IsFalse(query.Value.Contains("COUNT(*) AS Value"),
                    $"{query.Key} must not count rows as a value.");

                // EVERY fan-out, not just the first, and each one checked against ITS OWN source.
                //
                // The Copilot attach rate has two applies - one over the activity table and one over
                // the Copilot report - and an earlier revision collapsed only the first. A weaker
                // version of this test (any earlier "GROUP BY ... user_id" anywhere in the query)
                // passed that revision, because the activity-side collapse near the top of the query
                // "covered" the Copilot-side apply hundreds of characters later. So the check is
                // bound to the rowset each apply actually reads.
                foreach (var apply in IndexesOf(query.Value, "CROSS APPLY"))
                {
                    var source = SourceFeeding(query.Value, apply);

                    Assert.IsFalse(source.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase),
                        $"{query.Key} fans out directly over base table {source}. Fanning out raw rows and "
                        + "deduplicating afterwards is the shape measured at 91s against a 25s per-chart "
                        + "timeout - the apply must read an already-collapsed CTE.");

                    var body = CteBody(query.Value, source);
                    Assert.IsNotNull(body, $"{query.Key}: could not find the CTE '{source}' feeding a CROSS APPLY.");
                    Assert.IsTrue(IsOneRowPerPerson(query.Value, source, new HashSet<string>(StringComparer.Ordinal)),
                        $"{query.Key}: the CTE '{source}' feeding a CROSS APPLY is not one row per person. "
                        + "Fanning out before collapsing is the shape measured at 91s against a 25s timeout.");
                }
            }
        }

        /// <summary>
        /// The collapse check must reject a fan-out over a base table even when an unrelated
        /// per-person collapse appears earlier in the same query.
        /// </summary>
        /// <remarks>
        /// This pins the check itself, because a weaker version of it silently did not work. That
        /// version asked only whether ANY <c>GROUP BY ... user_id</c> appeared earlier in the string,
        /// so the activity-side collapse near the top of the Copilot attach query "covered" the
        /// Copilot-side fan-out hundreds of characters later - and the revision that genuinely fanned
        /// out every usage-report snapshot row six ways passed. The SQL below is that revision's
        /// shape, reduced to the part that matters.
        /// </remarks>
        [TestMethod]
        public void TheCollapseCheckRejectsAFanOutOverABaseTable()
        {
            const string halfCollapsed =
                "WITH PerUser AS (\r\n" +
                "    SELECT a.user_id, MAX(1 * a.word) AS Word\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                "    GROUP BY a.user_id\r\n" +
                "),\r\n" +
                "AppUsers AS (\r\n" +
                "    SELECT app.AppName, p.user_id\r\n" +
                "    FROM PerUser AS p\r\n" +
                "    CROSS APPLY (VALUES (N'Word', p.Word)) AS app(AppName, Used)\r\n" +
                "    WHERE app.Used = 1\r\n" +
                "),\r\n" +
                "CopilotUsers AS (\r\n" +
                "    SELECT copilot.AppName, r.user_id\r\n" +
                "    FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
                "    CROSS APPLY (VALUES (N'Word', r.word_last_activity_date)) AS copilot(AppName, LastActivity)\r\n" +
                "    WHERE r.[date] >= @from\r\n" +
                "    GROUP BY copilot.AppName, r.user_id\r\n" +
                ")\r\n" +
                "SELECT 1;";

            var fanOutSources = IndexesOf(halfCollapsed, "CROSS APPLY")
                .Select(i => SourceFeeding(halfCollapsed, i))
                .ToList();

            CollectionAssert.AreEqual(
                new[] { "PerUser", "dbo.copilot_usage_user_activity_log" }, fanOutSources.ToArray(),
                "Each apply must be attributed to the rowset it actually reads.");

            Assert.IsTrue(
                fanOutSources.Any(s => s.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)),
                "The half-collapsed shape must be rejected, or the check does not deliver the guarantee "
                + "it is written for.");

            // ...and the shipped queries must all pass the same check, which the test above asserts.
            foreach (var source in IndexesOf(ReportsAPIController.CopilotAttachRateQuery(), "CROSS APPLY")
                         .Select(i => SourceFeeding(ReportsAPIController.CopilotAttachRateQuery(), i)))
            {
                Assert.IsFalse(source.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase),
                    $"The shipped Copilot attach query still fans out over {source}.");
            }
        }

        /// <summary>
        /// Whether a CTE yields one row per person: either it collapses with
        /// <c>GROUP BY ... user_id</c>, or it is a row-preserving projection over one that does.
        /// </summary>
        /// <remarks>
        /// The chain has to be followed rather than checked one level deep. The department and domain
        /// matrices fan out over <c>PerUserDimension</c>, which contains no <c>GROUP BY</c> of its
        /// own - it simply joins the already-collapsed <c>PerUser</c> to the directory, one row in,
        /// one row out. Requiring the immediate CTE to collapse would reject that correct shape.
        /// </remarks>
        private static bool IsOneRowPerPerson(string sql, string cte, HashSet<string> visited)
        {
            if (!visited.Add(cte)) return false;

            var body = CteBody(sql, cte);
            if (body == null) return false;

            if (body.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) >= 0 && body.Contains("user_id"))
            {
                return true;
            }

            // Not a collapse itself, so it only qualifies if everything it reads does.
            var sources = IndexesOf(body, "FROM ").Select(i => SourceFeeding(body, i + 5)).ToList();
            return sources.Count > 0
                   && sources.All(s => !s.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)
                                       && IsOneRowPerPerson(sql, s, visited));
        }

        private static IEnumerable<int> IndexesOf(string haystack, string needle)
        {
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                yield return i;
            }
        }

        /// <summary>The table or CTE named by the nearest <c>FROM</c> before <paramref name="position"/>.</summary>
        private static string SourceFeeding(string sql, int position)
        {
            var from = sql.LastIndexOf("FROM ", position, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(from >= 0, "A CROSS APPLY with no preceding FROM is not valid SQL.");
            return sql.Substring(from + 5).Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        /// <summary>
        /// The body of the named CTE, found by matching parentheses from its <c>&lt;name&gt; AS (</c>.
        /// </summary>
        private static string CteBody(string sql, string name)
        {
            var header = sql.IndexOf(name + " AS (", StringComparison.Ordinal);
            if (header < 0) return null;

            var open = sql.IndexOf('(', header);
            var depth = 0;
            for (var i = open; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')' && --depth == 0) return sql.Substring(open + 1, i - open - 1);
            }
            return null;
        }

        /// <summary>Each app appears in every chart that claims to cover the suite.</summary>
        [TestMethod]
        public void AppQueriesCoverEveryAppInTheCatalogue()
        {
            CollectionAssert.AreEqual(ExpectedApps,
                ReportsAPIController.OfficeAppCatalogue.Select(a => a.Name).ToArray(),
                "The catalogue drives every chart in the area; changing it changes all of them.");

            foreach (var query in new[]
            {
                Query("AppPopularity", ReportsAPIController.AppPopularityQuery()),
                Query("AppWeekly", ReportsAPIController.AppWeeklyQuery()),
                Query("AppPlatformMatrix", ReportsAPIController.AppPlatformMatrixQuery()),
                Query("AppByDepartment", ReportsAPIController.AppByDepartmentQuery()),
                Query("AppByDomain", ReportsAPIController.AppByDomainQuery()),
                Query("WebOnly", ReportsAPIController.WebOnlyQuery()),
                Query("CopilotAttachRate", ReportsAPIController.CopilotAttachRateQuery()),
            })
            {
                foreach (var app in ExpectedApps)
                {
                    StringAssert.Contains(query.Value, "N'" + app + "'",
                        $"{query.Key} is missing {app}, so that row/bar would silently disappear.");
                }
            }
        }

        /// <summary>
        /// The matrices choose their columns by DISTINCT active headcount, not by the sum of the
        /// per-app counts.
        /// </summary>
        /// <remarks>
        /// Summing the per-app counts counts a person once per app they use, so a 30-person department
        /// where everyone uses six apps would outrank a 100-person department where everyone uses one -
        /// pushing the larger department off a grid that promises "the departments with the most
        /// active people". Every cell would still be a correct headcount, which is what makes this the
        /// kind of defect that survives review: only the choice of which columns to show is wrong.
        /// </remarks>
        [TestMethod]
        public void MatrixColumnsAreChosenByDistinctHeadcount()
        {
            foreach (var query in new[]
            {
                Query("AppByDepartment", ReportsAPIController.AppByDepartmentQuery()),
                Query("AppByDomain", ReportsAPIController.AppByDomainQuery()),
            })
            {
                StringAssert.Contains(query.Value, "(Any app)",
                    $"{query.Key} must carry the distinct-headcount sentinel to rank on.");
                StringAssert.Contains(query.Value, "DENSE_RANK() OVER (ORDER BY ActivePeople DESC",
                    $"{query.Key} must rank columns on the distinct headcount, not on summed per-app counts.");
                StringAssert.Contains(query.Value, "AppName <> N'(Any app)'",
                    $"{query.Key} must filter the sentinel out of its results, or it becomes a fake app row.");
            }
        }

        /// <summary>
        /// The adoption ranking is deterministic, including where departments tie.
        /// </summary>
        /// <remarks>
        /// Ordering on the ROUNDED percentage alone leaves the cutoff between equal-looking
        /// departments to the query plan, so which 25 appear could change between runs for no visible
        /// reason - and "which departments are worst" is exactly the output someone acts on.
        /// </remarks>
        [TestMethod]
        public void DepartmentAdoptionRankingIsDeterministic()
        {
            var sql = ReportsAPIController.DepartmentAdoptionRateQuery();

            StringAssert.Contains(sql, "ORDER BY (100.0 * ActivePeople / PeopleInDepartment) ASC",
                "The ranking must use the unrounded ratio, not the rounded display value.");
            StringAssert.Contains(sql, "DepartmentName ASC",
                "A final name tiebreak is what makes the TOP(N) cutoff stable across runs.");
        }

        [TestMethod]
        public void PlatformQueriesCoverEveryPlatform()
        {
            CollectionAssert.AreEqual(ExpectedPlatforms,
                ReportsAPIController.OfficePlatformCatalogue.Select(p => p.Key).ToArray());

            foreach (var platform in ExpectedPlatforms)
            {
                StringAssert.Contains(ReportsAPIController.PlatformPopularityQuery(), "N'" + platform + "'");
                StringAssert.Contains(ReportsAPIController.PlatformWeeklyQuery(), "N'" + platform + "'");
                StringAssert.Contains(ReportsAPIController.AppPlatformMatrixQuery(), "N'" + platform + "'");
            }
        }

        /// <summary>
        /// The app-on-platform matrix reads the 24 dedicated cross columns, not the 6 app columns
        /// ANDed with the 4 platform columns.
        /// </summary>
        /// <remarks>
        /// The difference is real and would not show up as an error. "Used Word" AND "used a Mac" is
        /// true for someone who uses Word on Windows and Teams on a Mac; only <c>word_mac</c> says
        /// they used Word on the Mac.
        /// </remarks>
        [TestMethod]
        public void AppPlatformMatrixUsesTheDedicatedCrossColumns()
        {
            var sql = ReportsAPIController.AppPlatformMatrixQuery();

            foreach (var app in ReportsAPIController.OfficeAppCatalogue)
            {
                foreach (var platform in ReportsAPIController.OfficePlatformCatalogue)
                {
                    StringAssert.Contains(sql, $"a.{app.Column}_{platform.Value}",
                        $"The matrix must read the {app.Column}_{platform.Value} column.");
                }
            }
        }

        /// <summary>
        /// The Copilot attach rate keeps non-Copilot people in its denominator.
        /// </summary>
        /// <remarks>
        /// This is the single most corruptible figure in the area. An <c>INNER JOIN</c> would restrict
        /// the calculation to people who already have a Copilot usage row and report something near
        /// 100% for every app - a flattering number that answers a question nobody asked. The test
        /// pins the join type because the mistake is one character wide and the output still looks
        /// plausible.
        /// </remarks>
        [TestMethod]
        public void CopilotAttachRateLeftJoinsSoAppUsersWithoutCopilotStillCount()
        {
            var sql = ReportsAPIController.CopilotAttachRateQuery();

            StringAssert.Contains(sql, "LEFT JOIN CopilotUsers",
                "An INNER JOIN here would report Copilot take-up among Copilot users, which is ~100% by construction.");
            StringAssert.Contains(sql, "COUNT(cu.user_id) / COUNT(*)",
                "The numerator must be matched Copilot rows and the denominator every app user.");
        }

        /// <summary>
        /// The department rate divides by the whole department, not by the people already known to be
        /// active - which would return 100% everywhere.
        /// </summary>
        [TestMethod]
        public void DepartmentAdoptionRateDividesByTheWholeDirectoryDepartment()
        {
            var sql = ReportsAPIController.DepartmentAdoptionRateQuery();

            StringAssert.Contains(sql, "FROM dbo.users AS u",
                "The denominator must come from the directory, not from the activity table.");
            StringAssert.Contains(sql, "LEFT JOIN ActiveUsers",
                "Inactive people must stay in the denominator.");
            StringAssert.Contains(sql, "COUNT(act.user_id)", "The numerator counts matched active people.");
        }

        /// <summary>
        /// The domain split takes the part after the LAST '@', matching the importer's own
        /// <c>CopilotUsageReportPolicy.DomainOf</c>, and never produces a NULL column heading.
        /// </summary>
        [TestMethod]
        public void DomainQueryMatchesTheImporterSplitAndLabelsMissingDomains()
        {
            var sql = ReportsAPIController.AppByDomainQuery();

            StringAssert.Contains(sql, "REVERSE(u.user_name)",
                "The domain must be taken after the last '@', as CopilotUsageReportPolicy.DomainOf does.");
            StringAssert.Contains(sql, "(No domain)",
                "An address with no '@' must get a label rather than becoming a NULL column heading.");
        }

        /// <summary>People with no department are labelled rather than dropped by the join.</summary>
        [TestMethod]
        public void DepartmentQueriesLabelPeopleWithNoDepartment()
        {
            foreach (var sql in new[]
            {
                ReportsAPIController.AppByDepartmentQuery(),
                ReportsAPIController.DepartmentAdoptionRateQuery(),
            })
            {
                StringAssert.Contains(sql, "LEFT JOIN dbo.user_departments",
                    "An INNER JOIN would silently drop everyone whose directory record has no department.");
                StringAssert.Contains(sql, "(No department)");
            }
        }

        /// <summary>
        /// Web-only is decided after collapsing a person's whole window, not row by row.
        /// </summary>
        /// <remarks>
        /// Tested per row, someone who used Excel in the browser in March and on Windows in May would
        /// be counted as browser-only for every March row - overstating the figure that is supposed to
        /// identify people with no desktop app at all.
        /// </remarks>
        [TestMethod]
        public void WebOnlyCollapsesEachPersonBeforeDecidingTheyAreWebOnly()
        {
            var sql = ReportsAPIController.WebOnlyQuery();

            StringAssert.Contains(sql, "GROUP BY a.user_id",
                "Web-only must be decided over the person's whole window.");
            StringAssert.Contains(sql, "MAX(1 * a.excel_web)");
            StringAssert.Contains(sql, "p.Excel_Windows = 0 AND p.Excel_Mac = 0 AND p.Excel_Mobile = 0",
                "Every non-web platform must be excluded, or 'web only' is not what is being counted.");
        }

        #endregion

        private static string ReportsTypeScriptSource() =>
            PortalSource(Path.Combine("types", "reports.ts"));

        private static string PortalSource(string relativePath)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Could not locate the solution directory from the test output folder.");

            var path = Path.Combine(directory.FullName, "Web", "Scripts", "portal", "src", relativePath);
            Assert.IsTrue(File.Exists(path), "Expected portal source at " + path);
            return File.ReadAllText(path);
        }
    }
}
