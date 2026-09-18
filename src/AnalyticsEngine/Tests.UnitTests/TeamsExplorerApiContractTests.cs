using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins the JSON contract of every Teams Explorer model that reaches the portal.
    /// </summary>
    /// <remarks>
    /// This exists because of a real, shipped defect. The web application configures NO camelCase
    /// contract resolver, so each Teams Explorer model opts in individually with
    /// <c>[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]</c>.
    /// <see cref="TeamsJudgement"/> lives in <c>TeamsExplorerScoring.cs</c> rather than next to the
    /// other models and was the one type that never got the attribute, so it serialised as
    /// <c>Headline</c> / <c>Detail</c> / <c>Tone</c>. The Overview tab read <c>judgement.headline</c>,
    /// got <c>undefined</c>, and rendered a row of correctly-counted, completely blank cards.
    ///
    /// Nothing in C# can see that failure: the scoring, the controller and the model were each
    /// correct in isolation and every unit test passed. Only loading the page in a browser showed it.
    /// So the contract is asserted here, over the whole reachable object graph, rather than type by
    /// type - a new model added without the attribute fails the build instead of the page.
    /// </remarks>
    [TestClass]
    public class TeamsExplorerApiContractTests
    {
        /// <summary>The payload roots - one per <c>api/TeamsExplorer/*</c> endpoint.</summary>
        private static readonly Type[] PayloadRoots =
        {
            typeof(TeamsExplorerAvailability),
            typeof(TeamsOverview),
            typeof(TeamsAdoption),
            typeof(TeamsMeetings),
            typeof(TeamsCollaboration),
            typeof(TeamsConversations),
            typeof(TeamsPeople),
        };

        /// <summary>
        /// Every model reachable from a Teams Explorer response must serialise camelCase, either by
        /// carrying the naming strategy or by naming each property explicitly.
        /// </summary>
        [TestMethod]
        public void EveryModelReachableFromATeamsExplorerResponseSerialisesCamelCase()
        {
            var failures = new List<string>();

            foreach (var type in ReachableModels())
            {
                var naming = type.GetCustomAttribute<JsonObjectAttribute>()?.NamingStrategyType;
                if (naming == typeof(CamelCaseNamingStrategy)) continue;

                var unnamed = type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                    .Where(p =>
                    {
                        var name = p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName;
                        return string.IsNullOrEmpty(name) || !char.IsLower(name[0]);
                    })
                    .Select(p => p.Name)
                    .ToList();

                if (unnamed.Count > 0)
                {
                    failures.Add(
                        $"{type.Name} is serialised to the portal but neither declares "
                        + "[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))] nor gives a camelCase "
                        + "[JsonProperty] name to: " + string.Join(", ", unnamed));
                }
            }

            Assert.AreEqual(
                0,
                failures.Count,
                "This app has no camelCase contract resolver, so these would serialise in PascalCase and the "
                + "portal would read undefined:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// The exact defect, asserted end to end: a serialised overview carries the judgement fields
        /// the Overview tab renders, spelled the way the TypeScript spells them.
        /// </summary>
        [TestMethod]
        public void SerialisedJudgementsCarryTheFieldsTheOverviewTabRenders()
        {
            var overview = new TeamsOverview();
            overview.Judgements.Add(new TeamsJudgement("reach", "good", "Headline sentence.", "Detail sentence."));

            var judgement = JObject.Parse(JsonConvert.SerializeObject(overview))["judgements"].Single();

            Assert.AreEqual("reach", (string)judgement["key"]);
            Assert.AreEqual("good", (string)judgement["tone"]);
            Assert.AreEqual("Headline sentence.", (string)judgement["headline"],
                "The Overview tab reads judgement.headline. PascalCase here renders a blank card.");
            Assert.AreEqual("Detail sentence.", (string)judgement["detail"]);

            Assert.IsNull(judgement["Headline"], "PascalCase must not be emitted alongside the camelCase name.");
        }

        /// <summary>
        /// The judgements the scoring actually produces all survive serialisation with a non-empty
        /// headline, so an empty card can only mean "no finding", never "wrong field name".
        /// </summary>
        [TestMethod]
        public void EveryProducedJudgementSerialisesWithContent()
        {
            var findings = TeamsExplorerScoring.Judgements(new TeamsJudgementInputs
            {
                UsageReportsAvailable = true,
                CallsAvailable = true,
                TeamsAnalyticsAvailable = true,
                ActiveUsers = 859,
                KnownUsers = 955,
                OpenCollaborationPct = 50.4,
                MeetingsPerActiveUser = 19.5,
                AfterHoursPct = 4,
                OrganiserConcentrationPct = 30,
                ActiveTeams = 13,
                TotalTeams = 13,
            });

            Assert.IsTrue(findings.Count > 0, "This input should produce headline findings.");

            foreach (var serialised in JArray.Parse(JsonConvert.SerializeObject(findings)))
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace((string)serialised["headline"]),
                    "A judgement serialised without a headline renders as an empty card: " + serialised);
                Assert.IsFalse(string.IsNullOrWhiteSpace((string)serialised["detail"]), serialised.ToString());
                Assert.IsFalse(string.IsNullOrWhiteSpace((string)serialised["tone"]), serialised.ToString());
            }
        }

        /// <summary>
        /// Walks the payload roots, collecting every Teams Explorer model type that ends up on the
        /// wire. Property types are unwrapped through nullables and collections.
        /// </summary>
        private static IEnumerable<Type> ReachableModels()
        {
            var seen = new HashSet<Type>();
            var pending = new Queue<Type>(PayloadRoots);

            while (pending.Count > 0)
            {
                var type = pending.Dequeue();
                if (!seen.Add(type)) continue;

                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0);

                foreach (var property in properties)
                {
                    foreach (var candidate in Unwrap(property.PropertyType))
                    {
                        if (IsTeamsExplorerModel(candidate)) pending.Enqueue(candidate);
                    }
                }
            }

            return seen;
        }

        /// <summary>Strips nullables and collection wrappers down to the types actually serialised.</summary>
        private static IEnumerable<Type> Unwrap(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying.IsArray)
            {
                return Unwrap(underlying.GetElementType());
            }

            if (underlying.IsGenericType && typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                return underlying.GetGenericArguments().SelectMany(Unwrap);
            }

            return new[] { underlying };
        }

        private static bool IsTeamsExplorerModel(Type type)
        {
            return type.IsClass
                && type != typeof(string)
                && type.Namespace == typeof(TeamsOverview).Namespace;
        }
    }
}
