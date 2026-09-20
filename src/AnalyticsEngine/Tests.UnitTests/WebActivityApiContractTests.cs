using Common.Entities.SpoWebActivity;
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
    /// Pins the JSON contract of every SharePoint web activity model that reaches the portal.
    /// </summary>
    /// <remarks>
    /// The web application configures NO camelCase contract resolver, so every model has to opt in
    /// individually with <c>[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]</c>.
    /// A model that misses it serialises PascalCase, the SPA reads <c>undefined</c>, and the tab
    /// renders blank or throws - and nothing in C# can see it, because the SQL, the store, the
    /// controller and the model are each correct in isolation and every other test passes. The same
    /// defect has already shipped twice in this repository, once in Teams Explorer and once in DLP.
    ///
    /// Two shapes of that mistake are easy to make and invisible on inspection: forgetting the
    /// attribute entirely, and writing it on the same line as the closing <c>&lt;/summary&gt;</c> so
    /// the compiler swallows it into the XML comment. The second produces a class that LOOKS
    /// attributed. Both are caught here by asking the runtime what the attribute actually is, over
    /// the whole reachable object graph, rather than by reading the source.
    /// </remarks>
    [TestClass]
    public class WebActivityApiContractTests
    {
        /// <summary>The payload roots - one per <c>api/WebActivity/*</c> endpoint.</summary>
        private static readonly Type[] PayloadRoots =
        {
            typeof(WebActivityAvailability),
            typeof(WebActivityOverview),
            typeof(WebActivityVisits),
            typeof(WebActivityPages),
            typeof(WebActivityJourneys),
            typeof(WebActivityGeography),
            typeof(WebActivitySearch),
            typeof(WebActivityTechnology),
        };

        [TestMethod]
        public void EveryModelReachableFromAWebActivityResponseSerialisesCamelCase()
        {
            var failures = new List<string>();

            foreach (var type in ReachableModels())
            {
                // Inherited: true, because the section classes legitimately inherit the strategy
                // from WebActivitySection. Asking the runtime is the point - a [JsonObject] that
                // was accidentally written inside a /// comment does not exist as far as this is
                // concerned, however much it looks like it does in the file.
                var naming = type.GetCustomAttribute<JsonObjectAttribute>(inherit: true)?.NamingStrategyType;
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
        /// Each section class carries the naming strategy in its own right, not only by inheritance.
        /// </summary>
        /// <remarks>
        /// Inheriting it works and the test above allows it, but relying on inheritance alone means
        /// moving or removing the attribute on <c>WebActivitySection</c> silently breaks every tab
        /// at once. This asserts the declaration is really on each class, which is what stops the
        /// "attribute swallowed by the doc comment" mistake from sitting there looking correct.
        /// </remarks>
        [TestMethod]
        public void EverySectionDeclaresTheNamingStrategyItself()
        {
            foreach (var root in PayloadRoots)
            {
                var declared = root.GetCustomAttribute<JsonObjectAttribute>(inherit: false)?.NamingStrategyType;

                Assert.AreEqual(
                    typeof(CamelCaseNamingStrategy),
                    declared,
                    root.Name + " does not declare the camelCase naming strategy on the class itself. If the "
                    + "attribute looks present in the source, check it is not on the same line as the closing "
                    + "</summary> - the compiler treats that as part of the comment.");
            }
        }

        /// <summary>
        /// The fields the Overview tab actually renders, spelled the way the TypeScript spells them.
        /// </summary>
        [TestMethod]
        public void SerialisedOverviewCarriesTheFieldsTheTabRenders()
        {
            var overview = new WebActivityOverview
            {
                Window = WebActivityWindow.From(WebActivityQuery.Create(28, DateTime.UtcNow)),
            };
            overview.Judgements.Add(new WebActivityJudgement
            {
                Key = "bounce",
                Tone = "good",
                Headline = "Headline sentence.",
                Detail = "Detail sentence.",
            });

            var payload = JObject.Parse(JsonConvert.SerializeObject(overview));

            var judgement = payload["judgements"].Single();
            Assert.AreEqual("bounce", (string)judgement["key"]);
            Assert.AreEqual("good", (string)judgement["tone"]);
            Assert.AreEqual("Headline sentence.", (string)judgement["headline"],
                "The Overview tab reads judgement.headline. PascalCase here renders a blank card.");
            Assert.AreEqual("Detail sentence.", (string)judgement["detail"]);
            Assert.IsNull(judgement["Headline"], "PascalCase must not be emitted alongside the camelCase name.");

            // The window drives captions and the suppression thresholds the panels read.
            var window = payload["window"];
            Assert.AreEqual(28, (int)window["days"]);
            Assert.AreEqual(
                WebActivityScoring.MinimumPagesForDecile,
                (int)window["minimumPagesForDecile"],
                "PagesPanel reads window.minimumPagesForDecile to decide whether a decile is expressible.");
            Assert.IsNotNull(window["minimumViews"]);
            Assert.IsNotNull(window["segmentsFullyReachable"]);
        }

        /// <summary>
        /// Availability drives the banner shown when a tab is empty, so its fields must survive too.
        /// </summary>
        [TestMethod]
        public void SerialisedAvailabilityCarriesTheFieldsTheBannerBranchesOn()
        {
            var model = WebActivityAvailability.Build(
                new WebActivitySources { Readable = true, WebTraffic = true, AppInsightsConfigured = true },
                new WebActivityCollectionStatus { Readable = true, LastHitUtc = DateTime.UtcNow },
                true,
                true,
                DateTime.UtcNow);

            var payload = JObject.Parse(JsonConvert.SerializeObject(model));

            Assert.IsNotNull(payload["available"], "The page branches on availability.available.");
            Assert.IsNotNull(payload["collectionStatusKnown"],
                "The banner distinguishes 'switched off' from 'could not tell' on this field.");
            Assert.IsNotNull(payload["reasons"]);
            Assert.IsNull(payload["Available"]);
        }

        /// <summary>
        /// Walks the payload roots, collecting every web activity model that ends up on the wire.
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
                        if (IsWebActivityModel(candidate)) pending.Enqueue(candidate);
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

        private static bool IsWebActivityModel(Type type)
        {
            return type.IsClass
                && type != typeof(string)
                && type.Namespace == typeof(WebActivityOverview).Namespace;
        }
    }
}
