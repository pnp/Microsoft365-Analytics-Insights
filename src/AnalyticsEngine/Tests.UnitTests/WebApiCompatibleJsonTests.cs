extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using Common.Entities.CopilotAdoption;
using Common.Entities.LicenceActivity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The web host writes JSON exactly as the Web API 2 build on <c>dev</c>/<c>main</c> does.
    /// </summary>
    /// <remarks>
    /// The portal is shared between the branches and reads fields by the names the models'
    /// Newtonsoft attributes give them. ASP.NET Core's default formatter, System.Text.Json, ignores
    /// those attributes: on this host it published the Copilot Adoption tables' <c>manager</c> column
    /// as <c>managerUserPrincipalName</c> (so the portal showed none), the Cowork credit position's
    /// <c>available_credits</c> as <c>availableCredits</c>, and every <c>[JsonIgnore]</c>d member -
    /// the licence activity read-model key among them. These pin the formatter and the contract.
    /// </remarks>
    [TestClass]
    public class WebApiCompatibleJsonTests
    {
        private static IServiceProvider ConfiguredServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers().AddWebApiCompatibleJson();
            return services.BuildServiceProvider();
        }

        private static string Serialise(object value)
        {
            var settings = ConfiguredServices()
                .GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>().Value.SerializerSettings;
            return JsonConvert.SerializeObject(value, settings);
        }

        [TestMethod]
        public void Host_ReadsAndWritesJsonWithNewtonsoftOnly()
        {
            var mvc = ConfiguredServices().GetRequiredService<IOptions<MvcOptions>>().Value;

            Assert.IsTrue(mvc.OutputFormatters.OfType<NewtonsoftJsonOutputFormatter>().Any(),
                "The Newtonsoft output formatter must be registered.");
            Assert.IsFalse(mvc.OutputFormatters.OfType<SystemTextJsonOutputFormatter>().Any(),
                "System.Text.Json ignores the models' [JsonProperty]/[JsonIgnore] and must not write any response.");
            Assert.IsTrue(mvc.InputFormatters.OfType<NewtonsoftJsonInputFormatter>().Any(),
                "Request bodies must bind as they did on Web API 2.");
            Assert.IsFalse(mvc.InputFormatters.OfType<SystemTextJsonInputFormatter>().Any());
        }

        [TestMethod]
        public void ExplicitlyNamedFields_GoOutUnderTheirAttributeNames()
        {
            var row = Serialise(new LicensedUserAdoptionRow { ManagerUserPrincipalName = "manager@contoso.com" });
            StringAssert.Contains(row, "\"manager\":\"manager@contoso.com\"");
            Assert.IsFalse(row.Contains("managerUserPrincipalName", StringComparison.OrdinalIgnoreCase),
                "The portal reads 'manager'; the C# property name must not reach the wire.");

            var credits = Serialise(new CoworkCreditPosition { AvailableCredits = 58000m });
            StringAssert.Contains(credits, "\"available_credits\":58000");
        }

        [TestMethod]
        public void IgnoredMembers_StayOffTheWire()
        {
            var overview = Serialise(new LicenceActivityOverview
            {
                ReadModelId = "internal-synthetic-key",
                SourceExpiresUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            Assert.IsFalse(overview.Contains("internal-synthetic-key"), "The read-model key is internal.");
            Assert.IsFalse(overview.Contains("readModelId", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(overview.Contains("sourceExpiresUtc", StringComparison.OrdinalIgnoreCase));

            var row = Serialise(new LicensedUserAdoptionRow());
            Assert.IsFalse(row.Contains("seatLicenceTypeIds", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void UnannotatedProperties_KeepTheirDeclaredCase()
        {
            // Web API 2's formatter has no naming strategy, and neither may this host: a model that
            // relies on that (rather than [JsonObject(NamingStrategyType = ...)]) must read the same
            // on both branches.
            StringAssert.Contains(Serialise(new UnannotatedPayload { DeclaredName = 1 }), "\"DeclaredName\":1");
        }

        [TestMethod]
        public void NamingStrategyAttributes_AreStillHonoured()
        {
            // LicenceActivityOverview carries [JsonObject(NamingStrategyType = CamelCase)].
            StringAssert.Contains(Serialise(new LicenceActivityOverview()), "\"distinctAssignedUsers\":0");
        }

        [TestMethod]
        public void AccountabilityRows_CarryTheEmptySegmentKeyThePortalTranslates()
        {
            // The portal translates a product-authored empty bucket such as "(no manager)" from this key and
            // renders every other segment verbatim, as tenant data. Under any other name the key is simply
            // absent and the English label shows on a Spanish page - with nothing failing.
            var summary = new CopilotAdoptionSummary();
            summary.AccountabilityRollup.Add(new AccountabilityRollupRow { Segment = "(no manager)", EmptySegmentKey = "noManager" });
            summary.AccountabilityRollup.Add(new AccountabilityRollupRow { Segment = "Finance" });

            var json = Newtonsoft.Json.Linq.JObject.Parse(Serialise(summary));
            var rows = (Newtonsoft.Json.Linq.JArray)json["accountabilityRollup"];

            Assert.IsNotNull(rows, "The summary must publish the roll-up as 'accountabilityRollup'.");
            Assert.AreEqual("(no manager)", (string)rows[0]["segment"]);
            Assert.AreEqual("noManager", (string)rows[0]["emptySegmentKey"],
                "The key must go out as exactly 'emptySegmentKey'.");
            Assert.AreEqual("Finance", (string)rows[1]["segment"]);
            Assert.IsTrue(((Newtonsoft.Json.Linq.JObject)rows[1]).ContainsKey("emptySegmentKey"),
                "As on Web API 2, a tenant segment still carries the property - as null.");
            Assert.AreEqual(Newtonsoft.Json.Linq.JTokenType.Null, rows[1]["emptySegmentKey"].Type);
        }

        public sealed class UnannotatedPayload
        {
            public int DeclaredName { get; set; }
        }
    }
}
