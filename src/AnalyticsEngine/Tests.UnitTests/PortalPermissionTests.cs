extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.State;
using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Routing;
using System.Xml.Linq;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;

namespace Tests.UnitTests
{
    /// <summary>
    /// The portal's two permissions - Administration (#660) and See PII (#661) - and the rule that every
    /// Web API endpoint has decided which, if either, it needs.
    /// </summary>
    /// <remarks>
    /// All identities and data here are synthetic. The HTTP tests run the real controllers in memory with
    /// the enforcement policy pinned, so they do not depend on what the test App.config happens to say.
    /// </remarks>
    [TestClass]
    public class PortalPermissionTests
    {
        internal const string Any = "any signed-in user";
        internal const string Public = "anonymous (not signed in)";
        private const string Admin = "Administration";
        private const string Pii = "See PII";
        private const string AdminAndPii = "Administration + See PII";

        /// <summary>
        /// Who may call every Web API action the portal ships. Adding an endpoint without deciding fails
        /// <see cref="EveryWebApiAction_HasDecidedWhoMayCallIt"/>.
        /// </summary>
        /// <remarks>
        /// "Any" does not mean "sees everything": the actions marked <c>// trims</c> serve a reader without
        /// a permission a copy with the restricted parts removed, and have their own tests below.
        /// </remarks>
        private static readonly Dictionary<string, string> ExpectedAccess = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ActivityAnalysisAPIController.Availability"] = Any,
            ["ActivityAnalysisAPIController.Report"] = Any,            // everyone in the period, small groups folded; any user filter, licence or range needs See PII
            ["ActivityAnalysisAPIController.People"] = Pii,

            ["AgentCostsAPIController.Availability"] = Any,
            ["AgentCostsAPIController.Summary"] = Any,
            ["AgentCostsAPIController.Trend"] = Any,
            ["AgentCostsAPIController.Breakdown"] = Any,
            ["AgentCostsAPIController.Detail"] = Any,
            ["AgentCostsAPIController.Azure"] = Any,
            ["AgentCostsAPIController.Filters"] = Any,
            ["AgentCostsAPIController.Users"] = Pii,
            ["AgentCostConnectionAPIController.Status"] = Admin,
            ["AgentCostConnectionAPIController.Begin"] = Admin,
            ["AgentCostConnectionAPIController.Disconnect"] = Admin,

            ["Agent365PackageCatalogAPIController.Get"] = Admin,

            // Microsoft Graph's change-notification webhook: Graph cannot sign in, so it checks clientState instead.
            ["CallRecordWebhookController.Post"] = Public,

            ["CopilotAdoptionAPIController.Availability"] = Any,
            ["CopilotAdoptionAPIController.Summary"] = Any,            // trims the manager roll-up; an email domain, user filter or seat-licence override needs See PII
            ["CopilotAdoptionAPIController.Rebuild"] = Any,            // same scope checks as Summary; a generation token cannot grant access
            ["CopilotAdoptionAPIController.LicenceTypes"] = Any,       // a seat-licence override needs See PII
            ["CopilotAdoptionAPIController.Sql"] = Any,                // a seat-licence override needs See PII
            ["CopilotAdoptionAPIController.Filters"] = Any,            // a seat-licence override needs See PII
            ["CopilotAdoptionAPIController.LicensedUsers"] = Pii,
            ["CopilotAdoptionAPIController.ExportLicensedUsers"] = Pii,
            ["CopilotAdoptionAPIController.Opportunities"] = Pii,
            ["CopilotAdoptionAPIController.ExportOpportunities"] = Pii,
            ["CopilotAdoptionAPIController.Cowork"] = Pii,
            ["CopilotAdoptionAPIController.ExportCowork"] = Pii,
            ["CopilotAdoptionAPIController.ExportWorkbook"] = Any,     // trims the per-person sheets; narrowing as for Summary needs See PII

            ["CopilotCostConnectionAPIController.Status"] = Admin,
            ["CopilotCostConnectionAPIController.Collection"] = Admin,
            ["CopilotCostConnectionAPIController.Connect"] = Admin,
            ["CopilotCostConnectionAPIController.Disconnect"] = Admin,
            ["CopilotCostConnectionAPIController.Probe"] = Admin,
            ["CopilotCostConnectionAPIController.ResetProbe"] = Admin,
            ["CopilotCostHealthAPIController.Get"] = Any,              // connection outcomes and timestamps only; no account or tenant identifiers

            ["CopilotInvestmentAPIController.Availability"] = Any,
            ["CopilotInvestmentAPIController.Summary"] = Any,          // trims the people and any figure of fewer than 5 people; a user filter or manager grouping needs See PII
            ["CopilotInvestmentAPIController.Trend"] = Any,            // balance totals only, a reading of fewer than 5 people withheld; a user filter needs See PII
            ["CopilotInvestmentAPIController.People"] = Pii,
            ["CopilotInvestmentAPIController.Person"] = Pii,
            ["CopilotInvestmentAPIController.ExportWorkbook"] = Any,   // trims the people sheet, and withholds what the summary does
            ["CopilotInvestmentSettingsAPIController.Get"] = Admin,    // the history names who changed what
            ["CopilotInvestmentSettingsAPIController.Save"] = Admin,

            ["CopilotSpendAPIController.Summary"] = Any,               // trims the people; a user filter or manager grouping needs See PII
            ["CopilotSpendAPIController.Trend"] = Any,                 // balance totals only, a reading of fewer than 5 people withheld; a user filter needs See PII
            ["CopilotSpendAPIController.People"] = Pii,
            ["CopilotSpendAPIController.Person"] = Pii,
            ["CopilotSpendAPIController.ExportWorkbook"] = Any,        // trims the people sheet

            ["DlpAPIController.Availability"] = Any,
            ["DlpAPIController.Summary"] = Any,                        // trims the top-users table; drops agent, policy and label rows of fewer than 5 people without See PII
            ["DlpAPIController.Governance"] = Any,                     // tenant-level rates and model/plugin names only

            // Every reader is shown the administrator's filter that narrows their reports. Reading, previewing
            // or changing the definition needs See PII as well as Administration: its value picker lists
            // people, and a filter that selects one person turns every report into that person's record (#680).
            ["GlobalFilterAPIController.Effective"] = Any,
            ["GlobalFilterAPIController.Get"] = AdminAndPii,
            ["GlobalFilterAPIController.Save"] = AdminAndPii,
            ["GlobalFilterAPIController.Preview"] = AdminAndPii,

            ["HealthAPIController.Summary"] = Admin,
            ["HealthAPIController.Data"] = Admin,
            ["HealthAPIController.Liveness"] = Admin,
            ["HealthAPIController.Exceptions"] = Admin,
            ["HealthAPIController.Components"] = Admin,
            ["HealthAPIController.Config"] = Admin,

            // The AITracker on SharePoint pages: anonymous by design, CORS- and instrumentation-key-checked.
            ["ImportConfigController.Post"] = Public,

            ["InstallLogAPIController.Get"] = Admin,

            ["LicenceActivityAPIController.Availability"] = Any,
            ["LicenceActivityAPIController.Overview"] = Any,           // department/country narrowing needs See PII; groups of fewer than 5 withheld without it
            ["LicenceActivityAPIController.Users"] = Pii,
            ["LicenceActivityAPIController.Export"] = Any,             // refuses usersId without See PII; the same withheld overview as the screen

            ["PortalAccessAPIController.Get"] = Any,
            ["PromptCategoriesAPIController.Get"] = Admin,
            ["PromptCategoriesAPIController.Save"] = Admin,
            ["PromptCategoriesAPIController.Reset"] = Admin,
            ["PromptCategoriesAPIController.Runs"] = Admin,
            ["PromptCategoriesAPIController.TestPrompt"] = Admin,
            ["PromptCategoriesAPIController.Report"] = Any,

            ["ProfilingStatusAPIController.Get"] = Admin,
            ["ProfilingStatusAPIController.TraceLogs"] = Admin,

            ["ReportsAPIController.Areas"] = Any,
            ["ReportsAPIController.Copilot"] = Any,                    // drops prompt phrases and languages of fewer than 5 people without See PII
            ["ReportsAPIController.CopilotAgents"] = Any,
            ["ReportsAPIController.Usage"] = Any,
            ["ReportsAPIController.SpoAudit"] = Any,
            ["ReportsAPIController.WebTraffic"] = Any,
            ["ReportsAPIController.Calls"] = Any,
            ["ReportsAPIController.Emails"] = Any,
            ["ReportsAPIController.OfficeApps"] = Any,                 // drops department and domain columns of fewer than 5 people, withholds cells of 1-4, without See PII

            ["SiteTokenAPIController.Post"] = Admin,

            ["SystemStatusAPIController.Get"] = Any,                   // trims the admin-only fields

            ["TeamsAuthAPIController.Post"] = Admin,
            ["TeamsAuthAPIController.Put"] = Admin,

            ["TeamsExplorerAPIController.Availability"] = Any,
            ["TeamsExplorerAPIController.Overview"] = Any,             // reads the breakdown's population without See PII; withholds weeks, segments and KPIs of 1-4 people
            ["TeamsExplorerAPIController.Adoption"] = Any,             // folds breakdown groups of fewer than 5 people without See PII, reads its population, withholds rhythm, lifecycle, segments and platforms of 1-4
            ["TeamsExplorerAPIController.Meetings"] = Any,             // trims the named leaderboards; withholds weeks of fewer than 5 attendees with the quietest week shown, distributions and modalities of fewer than 5 people
            ["TeamsExplorerAPIController.Collaboration"] = Any,        // drops teams and channels of teams with fewer than 5 members, withholds reactions of 1-4 people and the tab list, folds rare reactions
            ["TeamsExplorerAPIController.Conversations"] = Any,        // withholds key phrases and languages; drops the sentiment of teams with fewer than 5 members
            ["TeamsExplorerAPIController.People"] = Any,               // trims the champion and dormant lists; folds departments of fewer than 5 power users
            ["TeamsExplorerAPIController.Export"] = Any,               // refuses the people sections without See PII; folds the adoption breakdown and floors the team and channel leaderboards as on screen

            ["UpdateCheckAPIController.Get"] = Admin,

            ["UserDataLookupAPIController.Summary"] = AdminAndPii,
            ["UserDataLookupAPIController.Detail"] = AdminAndPii,
            // Per-person licence and coverage diagnostics, and the live directory comparison (which is also
            // refused for anyone outside the import scope before any Graph call).
            ["UserDataLookupAPIController.Diagnostics"] = AdminAndPii,
            ["UserDataLookupAPIController.LiveLicences"] = AdminAndPii,
            ["UserDataLookupAPIController.ReportVerdict"] = AdminAndPii,
            ["UserDataLookupAPIController.Coverage"] = AdminAndPii,
            // One-person refresh: queues an importer re-read of one named person, and reports its result.
            ["UserDataLookupAPIController.Refresh"] = AdminAndPii,
            ["UserDataLookupAPIController.RefreshStatus"] = AdminAndPii,

            // Aggregate dimensions and value counts drive the report-wide filter for every insights reader.
            ["UserFilterAPIController.Dimensions"] = Pii,
            ["UserFilterAPIController.Values"] = Pii,

            ["UserImportCheckpointAPIController.Get"] = Admin,
            ["UserImportCheckpointAPIController.Clear"] = Admin,

            ["UserOrgAPIController.GetTypes"] = Admin,
            ["UserOrgAPIController.CreateType"] = Admin,
            ["UserOrgAPIController.UpdateType"] = Admin,
            ["UserOrgAPIController.DeleteType"] = Admin,
            ["UserOrgAPIController.TestEntra"] = Admin,
            ["UserOrgAPIController.GetAttributes"] = Admin,
            ["UserOrgAPIController.PreviewCsv"] = Admin,
            ["UserOrgAPIController.ImportCsv"] = Admin,
            ["UserOrgAPIController.GetJob"] = Admin,
            ["UserOrgAPIController.GetImports"] = Admin,
            ["UserOrgAPIController.GetChanges"] = Admin,
            ["UserOrgAPIController.GetValues"] = Admin,
            ["UserOrgAPIController.GetMembers"] = Admin,

            ["UserScopeAPIController.Get"] = Admin,
            ["UserScopeAPIController.Refresh"] = Admin,
            ["UserScopeAPIController.StartPurge"] = Admin,
            ["UserScopeAPIController.GetPurge"] = Admin,
            ["UserScopeAPIController.CancelPurge"] = Admin,

            ["CopilotAuditBackfillAPIController.Get"] = Admin,
            ["CopilotAuditBackfillAPIController.Start"] = Admin,
            ["CopilotAuditBackfillAPIController.Cancel"] = Admin,
            ["LeadershipCohortAPIController.Get"] = Admin,
            ["LeadershipCohortAPIController.Save"] = Admin,
            ["LeadershipCohortAPIController.Refresh"] = Admin,
            ["CopilotAdoptionSettingsAPIController.Get"] = Admin,
            ["CopilotAdoptionSettingsAPIController.Save"] = Admin,
            ["CopilotAdoptionSettingsAPIController.Reset"] = Admin,

            // The Teams calls dead-letter queue: reprocessing re-imports calls and clearing deletes them for good. Counts and codes only.
            ["CallDeadLettersAPIController.Status"] = Admin,
            ["CallDeadLettersAPIController.Submit"] = Admin,

            ["WebActivityAPIController.Availability"] = Any,
            ["WebActivityAPIController.Overview"] = Any,
            ["WebActivityAPIController.Visits"] = Any,
            ["WebActivityAPIController.Pages"] = Any,
            ["WebActivityAPIController.Journeys"] = Any,
            ["WebActivityAPIController.Geography"] = Any,
            ["WebActivityAPIController.Search"] = Any,
            ["WebActivityAPIController.Technology"] = Any,
            ["WebActivityAPIController.Export"] = Any,
        };

        #region Every endpoint has decided

        [TestMethod]
        public void EveryWebApiAction_HasDecidedWhoMayCallIt()
        {
            var actual = WebApiActions().ToDictionary(a => a.Key, a => Classify(a.Controller, a.Method), StringComparer.Ordinal);

            var undecided = actual.Keys.Except(ExpectedAccess.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.AreEqual(0, undecided.Count,
                "New Web API action(s) with no recorded decision about who may call them. Add each to ExpectedAccess "
                + "- and to the SPA's route/permission handling - after deciding whether it is Administration, "
                + "per-person data (See PII) or open to every signed-in user: " + string.Join(", ", undecided));

            var gone = ExpectedAccess.Keys.Except(actual.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.AreEqual(0, gone.Count, "ExpectedAccess lists action(s) that no longer exist: " + string.Join(", ", gone));

            var wrong = actual.Where(a => a.Value != ExpectedAccess[a.Key])
                .Select(a => $"{a.Key}: expected '{ExpectedAccess[a.Key]}', attributes say '{a.Value}'")
                .ToList();
            Assert.AreEqual(0, wrong.Count, string.Join(Environment.NewLine, wrong));
        }

        [TestMethod]
        public void GetCachedUserAccessToken_IsNotAnAction()
        {
            // Web API makes every public method an action and infers GET from the name, so this answered
            // GET api/SiteTokenAPI with the caller's Graph refresh token until it was marked [NonAction].
            Assert.IsFalse(WebApiActions().Any(a => a.Method.Name == nameof(BaseAPIController.GetCachedUserAccessTokenAsync)));
        }

        [TestMethod]
        public async Task NoGetRequest_HandsTheCallersRefreshTokenToScript()
        {
            // The behaviour the reflection test above guards. The site keeps the Graph refresh token in its
            // encrypted, httpOnly auth cookie precisely so that script on the page never sees it; SiteTokenAPI
            // hands out short-lived access tokens instead. The inherited helper, exposed as a GET action on
            // both controllers, returned the refresh token itself as JSON to whoever was signed in.
            var identity = new ClaimsIdentity("synthetic-test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-portal-user"));
            identity.AddClaim(new Claim("roles", PortalRoles.Administration));
            identity.AddClaim(new Claim(GraphTokenClaims.RefreshToken, "synthetic-refresh-token"));

            using (var host = new PortalTestHost(PortalTestHost.WebControllers(), new ClaimsPrincipal(identity), PortalAccessPolicy.Enforcing))
            {
                foreach (var url in new[] { "api/SiteTokenAPI", "api/TeamsAuthAPI" })
                {
                    var response = await host.Client.GetAsync(url);
                    var body = await response.Content.ReadAsStringAsync();

                    Assert.IsFalse(body.Contains("synthetic-refresh-token"), $"GET {url} returned the caller's refresh token: {body}");
                    Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode, $"GET {url}: {body}");
                }
            }
        }

        /// <summary>Who the attributes let call an action. Shared with <see cref="PortalPrivacyFloorTests"/>.</summary>
        internal static string Classify(Type controller, MethodInfo method)
        {
            var authorized = controller.GetCustomAttributes<AuthorizeAttribute>(true).Any()
                             || method.GetCustomAttributes<AuthorizeAttribute>(true).Any();
            var anonymous = method.GetCustomAttributes<AllowAnonymousAttribute>(true).Any()
                            || controller.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
            if (!authorized || anonymous) return Public;

            var required = controller.GetCustomAttributes<RequirePortalPermissionAttribute>(true)
                .Concat(method.GetCustomAttributes<RequirePortalPermissionAttribute>(true))
                .Select(a => a.Permission)
                .Distinct()
                .ToList();

            var admin = required.Contains(PortalPermission.Administration);
            var pii = required.Contains(PortalPermission.SeePii);
            if (admin && pii) return AdminAndPii;
            if (admin) return Admin;
            if (pii) return Pii;
            return Any;
        }

        /// <summary>
        /// The actions Web API itself would find: see <c>ApiControllerActionSelector</c>. Shared with
        /// <see cref="PortalPrivacyFloorTests"/>.
        /// </summary>
        internal static IEnumerable<(string Key, Type Controller, MethodInfo Method)> WebApiActions()
        {
            foreach (var controller in PortalTestHost.WebControllers())
            {
                foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.IsSpecialName) continue;
                    if (method.GetBaseDefinition().DeclaringType.IsAssignableFrom(typeof(ApiController))) continue;
                    if (method.GetCustomAttribute<NonActionAttribute>() != null) continue;
                    yield return (controller.Name + "." + method.Name, controller, method);
                }
            }
        }

        #endregion

        #region Who holds what

        [DataTestMethod]
        [DataRow(null, true)]
        [DataRow("", true)]
        [DataRow("true", true)]
        [DataRow("True", true)]
        [DataRow("yes", true)]
        [DataRow("0", true)]
        [DataRow("off", true)]
        [DataRow("false", false)]
        [DataRow("False", false)]
        [DataRow(" FALSE ", false)]
        public void Enforcement_IsOnUnlessExplicitlyFalse(string value, bool enforced)
        {
            // A typo in a security switch must never widen access, so only an explicit "false" turns it off.
            var settings = new NameValueCollection();
            if (value != null) settings[PortalAccessPolicy.EnforcementSettingName] = value;

            Assert.AreEqual(enforced, PortalAccessPolicy.FromSettings(settings).Enforced);
            Assert.IsTrue(PortalAccessPolicy.FromSettings(null).Enforced);
        }

        [TestMethod]
        public void NoRoles_MeansAggregatesOnly()
        {
            var grant = PortalAccessPolicy.Enforcing.Evaluate(PortalTestHost.SignedIn());

            Assert.IsTrue(grant.Enforced);
            Assert.IsFalse(grant.Administration);
            Assert.IsFalse(grant.SeePii);
        }

        [TestMethod]
        public void TheTwoPermissions_AreIndependent()
        {
            var admin = PortalAccessPolicy.Enforcing.Evaluate(PortalTestHost.SignedIn(PortalRoles.Administration));
            Assert.IsTrue(admin.Administration);
            Assert.IsFalse(admin.SeePii, "Administration must not imply seeing individuals: an operator can run the service without it.");

            var pii = PortalAccessPolicy.Enforcing.Evaluate(PortalTestHost.SignedIn(PortalRoles.SeePii));
            Assert.IsTrue(pii.SeePii);
            Assert.IsFalse(pii.Administration);

            var both = PortalAccessPolicy.Enforcing.Evaluate(PortalTestHost.SignedIn(PortalRoles.SeePii, PortalRoles.Administration));
            Assert.IsTrue(both.Administration && both.SeePii);
        }

        [DataTestMethod]
        [DataRow("roles")]
        [DataRow("role")]
        [DataRow("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")]
        public void ARole_IsRecognisedHoweverTheTokenHandlerNamedTheClaim(string claimType)
        {
            var identity = new ClaimsIdentity("synthetic-test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-portal-user"));
            identity.AddClaim(new Claim(claimType, PortalRoles.SeePii));

            Assert.IsTrue(PortalAccessPolicy.Enforcing.Evaluate(new ClaimsPrincipal(identity)).SeePii);
        }

        [TestMethod]
        public void ARole_IsMatchedWithoutRegardToCase_AndGenericPrincipalsWork()
        {
            Assert.IsTrue(PortalAccessPolicy.Enforcing.Evaluate(PortalTestHost.SignedIn("portal.seepii")).SeePii);

            var generic = new GenericPrincipal(new GenericIdentity("synthetic-portal-user"), new[] { PortalRoles.Administration });
            Assert.IsTrue(PortalAccessPolicy.Enforcing.Evaluate(generic).Administration);
        }

        [DataTestMethod]
        [DataRow("Portal.Admin")]
        [DataRow("Portal.SeePII.Extra")]
        [DataRow("SeePII")]
        [DataRow("LicenceActivity.ReadUsers")]
        [DataRow("Telemetry.Dashboard.Read")]
        public void ALookalikeRole_GrantsNothing(string role)
        {
            var grant = PortalAccessPolicy.Enforcing.Evaluate(PortalTestHost.SignedIn(role));

            Assert.IsFalse(grant.Administration);
            Assert.IsFalse(grant.SeePii);
        }

        [TestMethod]
        public void EnforcementOff_GivesEverySignedInUserBoth_ButNeverAnAnonymousCaller()
        {
            var signedIn = PortalAccessPolicy.NotEnforcing.Evaluate(PortalTestHost.SignedIn());
            Assert.IsFalse(signedIn.Enforced);
            Assert.IsTrue(signedIn.Administration && signedIn.SeePii);

            var anonymous = PortalAccessPolicy.NotEnforcing.Evaluate(PortalTestHost.Anonymous());
            Assert.IsFalse(anonymous.Administration || anonymous.SeePii);
        }

        #endregion

        #region The filter

        [DataTestMethod]
        [DataRow("GET", "")]
        [DataRow("GET", "/collection")]
        [DataRow("POST", "/connect")]
        [DataRow("POST", "/disconnect")]
        [DataRow("POST", "/probe")]
        [DataRow("POST", "/resetProbe")]
        public async Task CopilotCostConnection_RequiresAdministrationBeforeOpeningRuntime(string method, string path)
        {
            using (var host = new PortalTestHost(new[] { typeof(CopilotCostConnectionAPIController) },
                PortalTestHost.SignedIn(PortalRoles.SeePii), PortalAccessPolicy.Enforcing))
            using (var request = new HttpRequestMessage(new HttpMethod(method), "api/CopilotCostConnection" + path))
            {
                var response = await host.Client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(PortalPermissionDeniedModel.ErrorCode, (string)body["code"]);
                Assert.AreEqual(PortalRoles.Administration, (string)body["role"]);
                Assert.IsTrue(response.Headers.CacheControl.NoStore);
            }
        }

        [TestMethod]
        public async Task Filter_RefusesWith403AndSaysWhichRoleToAskFor_WithoutRunningTheAction()
        {
            using (var host = ProbeHost(PortalTestHost.SignedIn()))
            {
                ProbeController.Runs = 0;
                var response = await host.Client.GetAsync("api/portal-permission-probe/pii");

                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(PortalPermissionDeniedModel.ErrorCode, (string)body["code"]);
                Assert.AreEqual("seePii", (string)body["permission"]);
                Assert.AreEqual(PortalRoles.SeePii, (string)body["role"]);
                StringAssert.Contains((string)body["message"], PortalRoles.SeePii);
                Assert.AreEqual(0, ProbeController.Runs, "A refused call must never reach the action.");
            }
        }

        [TestMethod]
        public async Task Filter_LeavesAnonymousCallersToAuthorize_SoTheyGet401AndCanSignIn()
        {
            // 403 would tell the SPA "you may not" when the truth is "you are not signed in", and signing in
            // again is the only thing that fixes it.
            using (var host = ProbeHost(PortalTestHost.Anonymous()))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/portal-permission-probe/admin")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/portal-permission-probe/open")).StatusCode);
            }
        }

        [TestMethod]
        public async Task Filter_LetsTheRightRoleThrough()
        {
            using (var host = ProbeHost(PortalTestHost.SignedIn(PortalRoles.Administration)))
            {
                Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/portal-permission-probe/admin")).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/portal-permission-probe/open")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.GetAsync("api/portal-permission-probe/pii")).StatusCode);
            }
        }

        [TestMethod]
        public async Task Filter_StacksSoAnEndpointNeedingBothNeedsBoth()
        {
            using (var host = ProbeHost(PortalTestHost.SignedIn(PortalRoles.Administration)))
            {
                var adminOnly = await host.Client.GetAsync("api/portal-permission-probe/both");
                Assert.AreEqual(HttpStatusCode.Forbidden, adminOnly.StatusCode);
                Assert.AreEqual("seePii", (string)JObject.Parse(await adminOnly.Content.ReadAsStringAsync())["permission"]);

                // A controller-level requirement must survive an action adding its own: Web API keeps only the
                // most specific instance of a filter type unless the filter allows multiples.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var piiOnly = await host.Client.GetAsync("api/portal-permission-probe-admin/pii");
                Assert.AreEqual(HttpStatusCode.Forbidden, piiOnly.StatusCode);
                Assert.AreEqual("administration", (string)JObject.Parse(await piiOnly.Content.ReadAsStringAsync())["permission"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii, PortalRoles.Administration);
                Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/portal-permission-probe/both")).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/portal-permission-probe-admin/pii")).StatusCode);
            }
        }

        [TestMethod]
        public async Task Filter_StepsAsideWhenEnforcementIsSwitchedOff()
        {
            using (var host = ProbeHost(PortalTestHost.SignedIn(), PortalAccessPolicy.NotEnforcing))
            {
                Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/portal-permission-probe/both")).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/portal-permission-probe-admin/pii")).StatusCode);
            }
        }

        [DataTestMethod]
        [DataRow(nameof(AccountController.ConnectTeams))]
        [DataRow(nameof(AccountController.ConnectAgentCosts))]
        public void DelegatedConnection_MvcActionRequiresAdministration(string actionName)
        {
            var action = typeof(AccountController).GetMethod(actionName);
            var filter = action.GetCustomAttributes(typeof(RequirePortalMvcPermissionAttribute), true)
                .Cast<RequirePortalMvcPermissionAttribute>()
                .Single();
            Assert.AreEqual(PortalPermission.Administration, filter.Permission);

            var denied = ConnectTeamsAuthorization(PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing, actionName);
            filter.OnAuthorization(denied);
            Assert.AreEqual((int)HttpStatusCode.Forbidden, denied.HttpContext.Response.StatusCode);
            Assert.IsInstanceOfType(denied.Result, typeof(System.Web.Mvc.ContentResult));
            StringAssert.Contains(((System.Web.Mvc.ContentResult)denied.Result).Content, PortalRoles.Administration);

            var allowed = ConnectTeamsAuthorization(
                PortalTestHost.SignedIn(PortalRoles.Administration),
                PortalAccessPolicy.Enforcing, actionName);
            filter.OnAuthorization(allowed);
            Assert.IsNull(allowed.Result, "An administrator must reach the OIDC challenge.");

            var compatibilityMode = ConnectTeamsAuthorization(
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.NotEnforcing, actionName);
            filter.OnAuthorization(compatibilityMode);
            Assert.IsNull(compatibilityMode.Result, "EnforcePortalRoles=false must preserve the pre-role behaviour.");

            var anonymous = ConnectTeamsAuthorization(PortalTestHost.Anonymous(), PortalAccessPolicy.Enforcing, actionName);
            filter.OnAuthorization(anonymous);
            Assert.IsInstanceOfType(anonymous.Result, typeof(System.Web.Mvc.HttpUnauthorizedResult));
        }

        /// <summary>
        /// Sign-out and the Teams connection change state, and with consent already granted Entra ID asks nothing during
        /// the connection, so a page on another site must not be able to start either by linking to it or by posting a
        /// form (#853, R6-8): POST only, and only from this origin. Sign-out needs no role; the Teams connection keeps
        /// its Administration requirement (above).
        /// </summary>
        [DataTestMethod]
        [DataRow(nameof(AccountController.ConnectTeams))]
        [DataRow(nameof(AccountController.SignOut))]
        public void AccountStateChange_IsASameOriginPostOnly(string actionName)
        {
            var action = typeof(AccountController).GetMethod(actionName);
            Assert.IsTrue(action.IsDefined(typeof(System.Web.Mvc.HttpPostAttribute), true), "A GET must not reach " + actionName);
            Assert.IsFalse(action.IsDefined(typeof(System.Web.Mvc.HttpGetAttribute), true));
            var filter = action.GetCustomAttributes(typeof(RequireSameOriginFormPostAttribute), true)
                .Cast<RequireSameOriginFormPostAttribute>()
                .Single();

            Assert.IsNull(SameOriginAuthorization(filter, actionName, ("Sec-Fetch-Site", "same-origin")).Result, "The portal's own form.");
            Assert.IsNull(SameOriginAuthorization(filter, actionName, ("Origin", "https://contoso.invalid")).Result, "A browser without Sec-Fetch-Site.");
            Assert.IsNull(SameOriginAuthorization(filter, actionName, ("Referer", "https://contoso.invalid/#/admin/teams-permissions")).Result,
                "A browser that sends neither.");

            foreach (var refused in new[]
            {
                SameOriginAuthorization(filter, actionName, ("Sec-Fetch-Site", "cross-site"), ("Origin", "https://contoso.invalid")),
                SameOriginAuthorization(filter, actionName, ("Sec-Fetch-Site", "same-site")),
                SameOriginAuthorization(filter, actionName, ("Origin", "https://contoso.example")),
                SameOriginAuthorization(filter, actionName, ("Origin", "null")),
                SameOriginAuthorization(filter, actionName, ("Referer", "https://contoso.invalid.example/")),
                SameOriginAuthorization(filter, actionName),
            })
            {
                Assert.IsInstanceOfType(refused.Result, typeof(System.Web.Mvc.HttpStatusCodeResult));
                Assert.AreEqual((int)HttpStatusCode.Forbidden, ((System.Web.Mvc.HttpStatusCodeResult)refused.Result).StatusCode);
            }
        }

        private static System.Web.Mvc.AuthorizationContext SameOriginAuthorization(
            RequireSameOriginFormPostAttribute filter, string actionName, params (string Name, string Value)[] headers)
        {
            var request = new HeaderRequest(new Uri("https://contoso.invalid/Account/" + actionName), headers);
            var controllerDescriptor = new System.Web.Mvc.ReflectedControllerDescriptor(typeof(AccountController));
            var actionDescriptor = new System.Web.Mvc.ReflectedActionDescriptor(
                typeof(AccountController).GetMethod(actionName), actionName, controllerDescriptor);
            var context = new System.Web.Mvc.AuthorizationContext(
                new System.Web.Mvc.ControllerContext(new RequestOnlyHttpContext(request), new RouteData(), new AccountController()),
                actionDescriptor);
            filter.OnAuthorization(context);
            return context;
        }

        private sealed class HeaderRequest : HttpRequestBase
        {
            private readonly Uri _url;
            private readonly System.Collections.Specialized.NameValueCollection _headers = new System.Collections.Specialized.NameValueCollection();

            internal HeaderRequest(Uri url, IEnumerable<(string Name, string Value)> headers)
            {
                _url = url;
                foreach (var header in headers) _headers.Add(header.Name, header.Value);
            }

            public override Uri Url => _url;
            public override string HttpMethod => "POST";
            public override System.Collections.Specialized.NameValueCollection Headers => _headers;
        }

        private sealed class RequestOnlyHttpContext : HttpContextBase
        {
            private readonly HttpRequestBase _request;
            private readonly System.Collections.IDictionary _items = new System.Collections.Hashtable();

            internal RequestOnlyHttpContext(HttpRequestBase request)
            {
                _request = request;
            }

            public override HttpRequestBase Request => _request;
            public override System.Collections.IDictionary Items => _items;
        }

        private static System.Web.Mvc.AuthorizationContext ConnectTeamsAuthorization(
            IPrincipal principal,
            PortalAccessPolicy policy,
            string actionName)
        {
            var raw = new HttpContext(
                new HttpRequest("", "https://contoso.invalid/Account/ConnectTeams", ""),
                new HttpResponse(new StringWriter()))
            {
                User = principal,
            };
            raw.Items[typeof(PortalAccessPolicy)] = policy;

            var controller = new AccountController();
            var controllerDescriptor = new System.Web.Mvc.ReflectedControllerDescriptor(typeof(AccountController));
            var actionDescriptor = new System.Web.Mvc.ReflectedActionDescriptor(
                typeof(AccountController).GetMethod(actionName),
                actionName,
                controllerDescriptor);
            var controllerContext = new System.Web.Mvc.ControllerContext(
                new HttpContextWrapper(raw),
                new RouteData(),
                controller);
            return new System.Web.Mvc.AuthorizationContext(controllerContext, actionDescriptor);
        }

        private static PortalTestHost ProbeHost(IPrincipal principal, PortalAccessPolicy policy = null) =>
            new PortalTestHost(
                new[] { typeof(ProbeController), typeof(AdminProbeController) },
                principal,
                policy ?? PortalAccessPolicy.Enforcing);

        [Authorize]
        [RoutePrefix("api/portal-permission-probe")]
        internal sealed class ProbeController : ApiController
        {
            internal static int Runs;

            [HttpGet, Route("open")]
            public IHttpActionResult Open() { Interlocked.Increment(ref Runs); return Ok("open"); }

            [HttpGet, Route("admin"), RequirePortalPermission(PortalPermission.Administration)]
            public IHttpActionResult AdminOnly() { Interlocked.Increment(ref Runs); return Ok("admin"); }

            [HttpGet, Route("pii"), RequirePortalPermission(PortalPermission.SeePii)]
            public IHttpActionResult PiiOnly() { Interlocked.Increment(ref Runs); return Ok("pii"); }

            [HttpGet, Route("both")]
            [RequirePortalPermission(PortalPermission.Administration)]
            [RequirePortalPermission(PortalPermission.SeePii)]
            public IHttpActionResult Both() { Interlocked.Increment(ref Runs); return Ok("both"); }
        }

        [Authorize]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RoutePrefix("api/portal-permission-probe-admin")]
        internal sealed class AdminProbeController : ApiController
        {
            [HttpGet, Route("pii"), RequirePortalPermission(PortalPermission.SeePii)]
            public IHttpActionResult Pii() => Ok("admin and pii");
        }

        #endregion

        #region The real endpoints

        /// <summary>
        /// Every endpoint that must refuse a signed-in user with no roles, and the permission it names. The
        /// refusal happens before the action touches SQL, Redis or Graph, so this needs none of them.
        /// </summary>
        private static readonly (HttpMethod Method, string Url, string Permission)[] Refused =
        {
            (HttpMethod.Get, "api/Health", "administration"),
            (HttpMethod.Get, "api/Health/summary", "administration"),
            (HttpMethod.Get, "api/Health/data", "administration"),
            (HttpMethod.Get, "api/Health/liveness", "administration"),
            (HttpMethod.Get, "api/Health/exceptions", "administration"),
            (HttpMethod.Get, "api/Health/components", "administration"),
            (HttpMethod.Get, "api/Health/config", "administration"),
            (HttpMethod.Get, "api/InstallLog", "administration"),
            (HttpMethod.Get, "api/ProfilingStatus", "administration"),
            (HttpMethod.Get, "api/ProfilingStatus/tracelogs", "administration"),
            (HttpMethod.Get, "api/UpdateCheck", "administration"),
            (HttpMethod.Post, "api/SiteTokenAPI", "administration"),
            (HttpMethod.Post, "api/TeamsAuthAPI", "administration"),
            (HttpMethod.Put, "api/TeamsAuthAPI", "administration"),
            (HttpMethod.Get, "api/UserImportCheckpoint", "administration"),
            (HttpMethod.Post, "api/UserImportCheckpoint/clear", "administration"),
            (HttpMethod.Get, "api/UserOrg/types", "administration"),
            (HttpMethod.Post, "api/UserOrg/types", "administration"),
            (HttpMethod.Put, "api/UserOrg/types/1", "administration"),
            (HttpMethod.Delete, "api/UserOrg/types/1", "administration"),
            (HttpMethod.Post, "api/UserOrg/test-entra", "administration"),
            (HttpMethod.Get, "api/UserOrg/attributes", "administration"),
            (HttpMethod.Post, "api/UserOrg/preview-csv?orgTypeId=1", "administration"),
            (HttpMethod.Post, "api/UserOrg/import-csv?orgTypeId=1&mode=replace", "administration"),
            (HttpMethod.Get, "api/UserOrg/jobs/1", "administration"),
            (HttpMethod.Get, "api/UserOrg/types/1/imports", "administration"),
            (HttpMethod.Get, "api/UserOrg/jobs/1/changes", "administration"),
            (HttpMethod.Get, "api/UserOrg/types/1/values", "administration"),
            (HttpMethod.Get, "api/UserOrg/types/1/values/1/members", "administration"),
            (HttpMethod.Get, "api/UserScope", "administration"),
            (HttpMethod.Post, "api/UserScope/refresh", "administration"),
            (HttpMethod.Post, "api/UserScope/purge", "administration"),
            (HttpMethod.Get, "api/UserScope/purge/1", "administration"),
            (HttpMethod.Post, "api/UserScope/purge/1/cancel", "administration"),
            (HttpMethod.Get, "api/CopilotAdoptionSettings", "administration"),
            (HttpMethod.Post, "api/CopilotAdoptionSettings", "administration"),
            (HttpMethod.Post, "api/CopilotAdoptionSettings/reset", "administration"),
            (HttpMethod.Get, "api/CopilotInvestmentSettings", "administration"),
            (HttpMethod.Post, "api/CopilotInvestmentSettings", "administration"),
            (HttpMethod.Get, "api/CallDeadLetters", "administration"),
            (HttpMethod.Post, "api/CallDeadLetters/requests", "administration"),

            (HttpMethod.Get, "api/CopilotInvestment/people", "seePii"),
            (HttpMethod.Get, "api/CopilotInvestment/person/1", "seePii"),
            (HttpMethod.Get, "api/CopilotInvestment/summary?dimension=manager", "seePii"),
            (HttpMethod.Get, "api/CopilotInvestment/summary?userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D", "seePii"),
            (HttpMethod.Get, "api/CopilotInvestment/trend?userFilter=%5B%7B%22d%22%3A%22department%22%2C%22v%22%3A%5B%22Sales%22%5D%7D%5D", "seePii"),
            (HttpMethod.Get, "api/CopilotInvestment/export/workbook?userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D", "seePii"),
            (HttpMethod.Get, "api/CopilotInvestment/export/workbook?dimension=manager", "seePii"),
            (HttpMethod.Get, "api/CopilotSpend/summary?userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D", "seePii"),
            (HttpMethod.Get, "api/CopilotSpend/trend?userFilter=%5B%7B%22d%22%3A%22department%22%2C%22v%22%3A%5B%22Sales%22%5D%7D%5D", "seePii"),
            (HttpMethod.Get, "api/CopilotSpend/export/workbook?userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D", "seePii"),

            (HttpMethod.Get, "api/CopilotAdoption/licensed-users", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/licensed-users/export", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/opportunities", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/opportunities/export", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/cowork", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/cowork/export", "seePii"),
            (HttpMethod.Get, "api/LicenceActivity/users?overviewId=synthetic&licenceTypeId=1", "seePii"),
            (HttpMethod.Get, "api/LicenceActivity/export?overviewId=synthetic&usersId=synthetic", "seePii"),
            (HttpMethod.Get, "api/LicenceActivity/overview?departmentId=1", "seePii"),
            (HttpMethod.Get, "api/LicenceActivity/overview?countryId=0", "seePii"),
            (HttpMethod.Get, "api/AgentCosts/users", "seePii"),
            (HttpMethod.Get, "api/ActivityAnalysis/people", "seePii"),
            (HttpMethod.Get, "api/ActivityAnalysis/report?metrics=teams.calls&userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D", "seePii"),
            (HttpMethod.Get, "api/ActivityAnalysis/report?metrics=teams.calls&licences=1", "seePii"),
            (HttpMethod.Get, "api/ActivityAnalysis/report?metrics=teams.calls&ranges=teams.calls%3A5%3A", "seePii"),
            (HttpMethod.Get, "api/TeamsExplorer/export/people", "seePii"),
            (HttpMethod.Get, "api/TeamsExplorer/export/dormant", "seePii"),
            (HttpMethod.Get, "api/TeamsExplorer/export/PEOPLE", "seePii"),
        };

        [TestMethod]
        public async Task EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission()
        {
            using (var host = new PortalTestHost(PortalTestHost.WebControllers(), PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing))
            {
                var failures = new List<string>();
                foreach (var endpoint in Refused)
                {
                    var request = new HttpRequestMessage(endpoint.Method, endpoint.Url);
                    if (endpoint.Method != HttpMethod.Get)
                    {
                        request.Content = new StringContent("[]", Encoding.UTF8, "application/json");
                    }

                    var response = await host.Client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode != HttpStatusCode.Forbidden)
                    {
                        failures.Add($"{endpoint.Method} {endpoint.Url}: {(int)response.StatusCode} {body}");
                        continue;
                    }

                    var json = JObject.Parse(body);
                    if ((string)json["code"] != PortalPermissionDeniedModel.ErrorCode || (string)json["permission"] != endpoint.Permission)
                    {
                        failures.Add($"{endpoint.Method} {endpoint.Url}: wrong refusal {body}");
                    }
                }

                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
        }

        [TestMethod]
        public async Task ARefusal_IsJsonForAScript_WhateverItAsksFor()
        {
            // Left to content negotiation, a caller preferring XML - a browser navigation among them - would get
            // the body serialised as XML, because the XML formatter is still registered. The SPA's own download
            // calls ask for the file type, and must still get the JSON it parses into the permission message.
            var accepts = new[] { "application/xml", "text/csv", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };
            using (var host = new PortalTestHost(PortalTestHost.WebControllers(), PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing))
            {
                var failures = new List<string>();
                foreach (var accept in accepts)
                foreach (var endpoint in Refused.Where(e => e.Method == HttpMethod.Get))
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
                    request.Headers.Accept.ParseAdd(accept);

                    var response = await host.Client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode != HttpStatusCode.Forbidden
                        || response.Content.Headers.ContentType?.MediaType != "application/json"
                        || (string)JObject.Parse(body)["permission"] != endpoint.Permission)
                    {
                        failures.Add($"{accept} {endpoint.Url}: {(int)response.StatusCode} {response.Content.Headers.ContentType} {body}");
                    }
                }

                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
        }

        [TestMethod]
        public async Task ARefusedPageLoad_GetsAMessageAPersonCanRead()
        {
            // An export link opened from a bookmark, or after the permission was withdrawn, is a navigation: the
            // body lands in a browser tab, in front of the person who clicked.
            using (var host = new PortalTestHost(PortalTestHost.WebControllers(), PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing))
            {
                var failures = new List<string>();
                foreach (var endpoint in Refused.Where(e => e.Method == HttpMethod.Get))
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
                    request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    request.Headers.Add("Sec-Fetch-Mode", "navigate");
                    request.Headers.Add("Sec-Fetch-Dest", "document");

                    var response = await host.Client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    var permission = endpoint.Permission == PortalPermissionNames.Administration
                        ? PortalPermission.Administration
                        : PortalPermission.SeePii;
                    if (response.StatusCode != HttpStatusCode.Forbidden
                        || response.Content.Headers.ContentType?.MediaType != "text/plain"
                        || body != PortalPermissionDeniedModel.For(permission).Message)
                    {
                        failures.Add($"{endpoint.Url}: {(int)response.StatusCode} {response.Content.Headers.ContentType} {body}");
                    }
                }

                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
        }

        [TestMethod]
        public async Task UserDataLookup_NeedsBothPermissions()
        {
            using (var host = new PortalTestHost(PortalTestHost.WebControllers(), PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing))
            {
                var adminOnly = await host.Client.GetAsync("api/UserDataLookup/summary");
                Assert.AreEqual(HttpStatusCode.Forbidden, adminOnly.StatusCode);
                Assert.AreEqual("seePii", (string)JObject.Parse(await adminOnly.Content.ReadAsStringAsync())["permission"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var piiOnly = await host.Client.GetAsync("api/UserDataLookup/detail");
                Assert.AreEqual(HttpStatusCode.Forbidden, piiOnly.StatusCode);
                Assert.AreEqual("administration", (string)JObject.Parse(await piiOnly.Content.ReadAsStringAsync())["permission"]);

                // With both, the request reaches the action - which rejects the missing UPN before touching SQL.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii, PortalRoles.Administration);
                Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Client.GetAsync("api/UserDataLookup/summary")).StatusCode);
            }
        }

        [TestMethod]
        public async Task TeamsExplorerExport_OnlyThePeopleSectionsNeedSeePii()
        {
            using (var host = new PortalTestHost(
                new[] { typeof(TeamsExplorerAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing,
                _ => new TeamsExplorerAPIController(new FakeTeamsStore(), () => new TeamsExplorerSources())))
            {
                foreach (var section in TeamsExplorerExports.AggregateSections)
                {
                    var response = await host.Client.GetAsync("api/TeamsExplorer/export/" + section);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, section + " names nobody and must stay open.");
                }

                Assert.IsTrue(TeamsExplorerExports.NamesPeople("people"));
                Assert.IsTrue(TeamsExplorerExports.NamesPeople("dormant"));
                Assert.IsTrue(TeamsExplorerExports.NamesPeople("a-section-added-later"), "An unclassified section is personal until decided otherwise.");
                Assert.IsFalse(TeamsExplorerExports.NamesPeople("TEAMS"));
            }
        }

        #endregion

        #region api/PortalAccess

        [TestMethod]
        public async Task PortalAccess_TellsTheSpaWhatTheUserHolds()
        {
            using (var host = new PortalTestHost(new[] { typeof(PortalAccessAPIController) }, PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing))
            {
                var none = await host.Client.GetAsync("api/PortalAccess");
                Assert.AreEqual(HttpStatusCode.OK, none.StatusCode);
                Assert.IsTrue(none.Headers.CacheControl.NoStore, "A new session must never be answered from an old one's reply.");
                var json = JObject.Parse(await none.Content.ReadAsStringAsync());
                Assert.AreEqual(true, (bool)json["enforced"]);
                Assert.AreEqual(false, (bool)json["permissions"]["administration"]);
                Assert.AreEqual(false, (bool)json["permissions"]["seePii"]);
                Assert.AreEqual(PortalRoles.Administration, (string)json["roles"]["administration"]);
                Assert.AreEqual(PortalRoles.SeePii, (string)json["roles"]["seePii"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                json = JObject.Parse(await host.Client.GetStringAsync("api/PortalAccess"));
                Assert.AreEqual(false, (bool)json["permissions"]["administration"]);
                Assert.AreEqual(true, (bool)json["permissions"]["seePii"]);

                host.Principal = PortalTestHost.Anonymous();
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/PortalAccess")).StatusCode);
            }

            using (var host = new PortalTestHost(new[] { typeof(PortalAccessAPIController) }, PortalTestHost.SignedIn(), PortalAccessPolicy.NotEnforcing))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/PortalAccess"));
                Assert.AreEqual(false, (bool)json["enforced"]);
                Assert.AreEqual(true, (bool)json["permissions"]["administration"]);
                Assert.AreEqual(true, (bool)json["permissions"]["seePii"]);
            }
        }

        #endregion

        #region Trimmed, never edited

        [TestMethod]
        public void SystemStatus_ForAnInsightsReader_CarriesNoAdminFieldAtAll()
        {
            var json = JObject.FromObject(SystemStatusApiModel.ForInsights(
                "synthetic-build",
                new List<NamedCountModel> { new NamedCountModel("users", "Users", 3) },
                new List<string> { "User metadata" },
                importSettingsKnown: true));

            CollectionAssert.AreEquivalent(
                new[] { "buildLabel", "dataCounts", "enabledImports", "importSettingsKnown" },
                json.Properties().Select(p => p.Name).ToArray(),
                "Absent, not false: hasValidConfig=false would read as 'this deployment is misconfigured'.");
        }

        [TestMethod]
        public async Task TeamsMeetings_WithoutSeePii_LoseTheNamedLeaderboards_AndTheCachedSectionKeepsThem()
        {
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, 37);
            MemoryCache.Default.Remove(query.CacheKey("meetings"));
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/meetings?days=28&top=37"));
                    Assert.AreEqual(0, ((JArray)trimmed["topOrganisers"]).Count);
                    Assert.AreEqual(0, ((JArray)trimmed["topAttendees"]).Count);
                    Assert.AreEqual(42, (int)trimmed["workingDayStartHour"], "Everything that names nobody is still there.");
                    Assert.IsFalse(trimmed.ToString().Contains("@contoso.com"));

                    // The next reader holds the permission and is served from the same cache entry.
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/meetings?days=28&top=37"));
                    Assert.AreEqual("organiser@contoso.com", (string)full["topOrganisers"][0]["name"]);
                    Assert.AreEqual("attendee@contoso.com", (string)full["topAttendees"][0]["name"]);
                    Assert.AreEqual(1, store.MeetingsCalls, "The second reader must have been served from the cache.");
                }
            }
            finally
            {
                MemoryCache.Default.Remove(query.CacheKey("meetings"));
            }
        }

        [TestMethod]
        public async Task TeamsMeetings_WithoutSeePii_WithholdAWeekOfFewerThanFiveAttendees_AndFoldRareModalities()
        {
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, 38);
            MemoryCache.Default.Remove(query.CacheKey("meetings"));
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/meetings?days=28&top=38"));
                    Assert.AreEqual(5, (int)trimmed["minimumGroupSize"]);
                    Assert.AreEqual(20, (long)trimmed["trend"][0]["calls"], "A week of twelve attendees is shown.");
                    Assert.IsNull(trimmed["trend"][0]["withheld"]);
                    var small = trimmed["trend"][1];
                    Assert.IsTrue((bool)small["withheld"], "A week only two people met in is those two people's meetings.");
                    Assert.AreEqual(JTokenType.Null, small["calls"].Type, "Withheld, never 0.");
                    Assert.AreEqual(JTokenType.Null, small["minutes"].Type);
                    Assert.AreEqual(JTokenType.Null, small["attendees"].Type);
                    // The quietest week shown (seven attendees) goes with it, so the calls less the weeks shown are 25 - the
                    // two weeks together - and never the two people's 10.
                    Assert.IsTrue((bool)trimmed["trend"][2]["withheld"]);
                    Assert.AreEqual(25, 45 - (long)trimmed["trend"][0]["calls"]);

                    // Screen sharing was one person's: it is folded with the least-used modality shown, so it cannot be
                    // worked out as the difference either.
                    CollectionAssert.AreEqual(
                        new[] { "audio", null },
                        trimmed["modalityMix"].Select(row => (string)row["key"]).ToArray());
                    Assert.IsTrue((bool)trimmed["modalityMix"][1]["other"]);
                    Assert.AreEqual(22, (long)trimmed["modalityMix"][1]["count"], "20 video + 2 screen sharing.");
                    Assert.AreEqual(2, (int)trimmed["otherModalities"]);
                    Assert.IsFalse((bool)trimmed["distributionsWithheld"], "Twelve people attended, so the distributions stay.");
                    Assert.AreEqual(1, ((JArray)trimmed["sizeDistribution"]).Count);
                    Assert.IsNull(trimmed["modalityMix"][0]["people"], "The people count only decides; it is never sent.");

                    // A See PII reader is served the cached section as it was.
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/meetings?days=28&top=38"));
                    Assert.AreEqual(10, (long)full["trend"][1]["calls"]);
                    Assert.AreEqual(2, (int)full["trend"][1]["attendees"]);
                    Assert.AreEqual(15, (long)full["trend"][2]["calls"]);
                    Assert.AreEqual(3, ((JArray)full["modalityMix"]).Count);
                    Assert.AreEqual(JTokenType.Null, full["minimumGroupSize"].Type);
                    Assert.AreEqual(1, store.MeetingsCalls, "The second reader must have been served from the cache.");
                }
            }
            finally
            {
                MemoryCache.Default.Remove(query.CacheKey("meetings"));
            }
        }

        [TestMethod]
        public void TeamsMeetings_WithoutSmallGroups_WithholdsEveryDistribution_WhenFewerThanFivePeopleAttended()
        {
            var meetings = new TeamsMeetings { Kpis = new TeamsCallKpis { Calls = 6, Attendees = 4 } };
            meetings.SizeDistribution.Add(new TeamsBucketRow { Key = "3-5", Label = "3-5", Count = 6, SharePct = 100 });
            meetings.DurationDistribution.Add(new TeamsBucketRow { Key = "30-60", Label = "30-60 minutes", Count = 6, SharePct = 100 });
            meetings.Heatmap.Add(new TeamsHeatCell { DayOfWeek = 6, Hour = 3, Calls = 6 });
            meetings.PeriodOfDay.Add(new TeamsBucketRow { Key = "night", Label = "Night", Count = 6, SharePct = 100 });
            meetings.ModalityMix.Add(new TeamsBucketRow { Key = "audio", Label = "audio", Count = 6, People = 4 });
            meetings.Quality.FeedbackCount = 1;
            meetings.Quality.Ratings.Add(new TeamsBucketRow { Key = "poor", Label = "poor", Count = 1, SharePct = 100 });

            var trimmed = meetings.WithoutSmallGroups(5);

            Assert.IsTrue(trimmed.DistributionsWithheld);
            Assert.AreEqual(0, trimmed.SizeDistribution.Count + trimmed.DurationDistribution.Count + trimmed.Heatmap.Count
                + trimmed.PeriodOfDay.Count + trimmed.ModalityMix.Count + trimmed.Quality.Ratings.Count);
            Assert.AreEqual(0, trimmed.Quality.FeedbackCount);
            Assert.IsFalse(meetings.DistributionsWithheld, "The cached section is never edited.");
            Assert.AreEqual(1, meetings.Heatmap.Count);
            Assert.AreEqual(1, meetings.Quality.FeedbackCount);

            var five = new TeamsMeetings { Kpis = new TeamsCallKpis { Calls = 6, Attendees = 5 } };
            five.Heatmap.Add(new TeamsHeatCell { DayOfWeek = 6, Hour = 3, Calls = 6 });
            Assert.IsFalse(five.WithoutSmallGroups(5).DistributionsWithheld);
            Assert.AreEqual(1, five.WithoutSmallGroups(5).Heatmap.Count);
        }

        [TestMethod]
        public async Task TeamsCollaboration_WithoutSeePii_LeavesOutTeamsOfFewerThanFive_WithholdsFewReactors_OnScreenAndInTheExports()
        {
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, 39);
            // A reader without See PII is served from the section read with one row more than the longest list, whatever top
            // they asked for; a See PII reader from the one read at their own top.
            var keys = new[] { query.CacheKey("collaboration"), query.WithListProbe().CacheKey("collaboration") };
            foreach (var key in keys) MemoryCache.Default.Remove(key);
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/collaboration?days=28&top=39"));
                    CollectionAssert.AreEqual(
                        new[] { "Contoso Big Team", "Contoso One Reactor" },
                        trimmed["teams"].Select(row => (string)row["name"]).ToArray(),
                        "A team of two is those two people; the team of eight, the smallest shown, goes with it.");
                    Assert.AreEqual(2, (int)trimmed["withheldTeams"]);
                    Assert.AreEqual(120, (long)trimmed["teams"][0]["reactions"]);
                    Assert.IsTrue((bool)trimmed["teams"][1]["reactionsWithheld"], "One person made every reaction in the team.");
                    Assert.AreEqual(JTokenType.Null, trimmed["teams"][1]["reactions"].Type, "Withheld, never 0.");
                    Assert.IsNull(trimmed["teams"][0]["reactingUsers"], "The team's reacting people only decide; they are never sent.");

                    CollectionAssert.AreEqual(
                        new[] { "General", "Ideas" },
                        trimmed["channels"].Select(row => (string)row["name"]).ToArray(),
                        "The channel of a team of two is left out with its team, and the smallest team's channel shown with it.");
                    Assert.AreEqual(2, (int)trimmed["withheldChannels"]);
                    Assert.IsTrue((bool)trimmed["channels"][1]["reactionsWithheld"]);
                    Assert.AreEqual(JTokenType.Null, trimmed["channels"][1]["reactions"].Type);
                    Assert.AreEqual(JTokenType.Null, trimmed["channels"][1]["reactingUsers"].Type);
                    Assert.AreEqual(20, (int)trimmed["channels"][0]["reactingUsers"]);
                    Assert.IsNull(trimmed["channels"][0]["teamMembers"]);

                    // "angry" was one person's: folded with the least-used reaction shown, so the published total less the
                    // shown reactions is never that person's count.
                    CollectionAssert.AreEqual(new[] { "like", null }, trimmed["reactionMix"].Select(row => (string)row["key"]).ToArray());
                    Assert.AreEqual(46, (long)trimmed["reactionMix"][1]["count"]);
                    Assert.AreEqual(2, (int)trimmed["otherReactions"]);
                    Assert.AreEqual(0, ((JArray)trimmed["tabUsage"]).Count);
                    Assert.IsTrue((bool)trimmed["tabUsageWithheld"]);
                    Assert.IsFalse(trimmed.ToString().Contains("Contoso Pair") || trimmed.ToString().Contains("Contoso private notes"));
                    Assert.IsFalse(trimmed.ToString().Contains("Contoso Mid") || trimmed.ToString().Contains("Plans"));

                    // The exports are never less redacted than the page.
                    var teamsCsv = await host.Client.GetStringAsync("api/TeamsExplorer/export/teams?days=28");
                    StringAssert.Contains(teamsCsv, "Contoso One Reactor");
                    Assert.IsFalse(teamsCsv.Contains("Contoso Pair") || teamsCsv.Contains("Contoso Mid"));
                    Assert.IsFalse(teamsCsv.Contains(",37,"), "The withheld reactions are a blank cell, not their count.");
                    var channelsCsv = await host.Client.GetStringAsync("api/TeamsExplorer/export/channels?days=28");
                    StringAssert.Contains(channelsCsv, "Ideas");
                    Assert.IsFalse(channelsCsv.Contains("Πωλήσεις") || channelsCsv.Contains("Plans"));
                    Assert.IsFalse(channelsCsv.Contains(",37,"));

                    // A See PII reader is served every row, as before, from a section of their own - read once, then cached.
                    var built = store.CollaborationCalls;
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    for (var request = 0; request < 2; request++)
                    {
                        var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/collaboration?days=28&top=39"));
                        Assert.AreEqual(4, ((JArray)full["teams"]).Count);
                        Assert.AreEqual(37, (long)full["teams"].Single(t => (string)t["name"] == "Contoso One Reactor")["reactions"]);
                        Assert.AreEqual(4, ((JArray)full["channels"]).Count);
                        Assert.AreEqual(3, ((JArray)full["reactionMix"]).Count);
                        Assert.AreEqual("Contoso private notes", (string)full["tabUsage"][0]["name"]);
                    }
                    Assert.AreEqual(built + 1, store.CollaborationCalls, "Never the trimmed reader's section; then cached.");
                    StringAssert.Contains(await host.Client.GetStringAsync("api/TeamsExplorer/export/teams?days=28"), "Contoso Pair");
                    StringAssert.Contains(await host.Client.GetStringAsync("api/TeamsExplorer/export/channels?days=28"), "Πωλήσεις");
                }
            }
            finally
            {
                foreach (var key in keys) MemoryCache.Default.Remove(key);
            }
        }

        [TestMethod]
        public async Task TeamsConversations_WithoutSeePii_WithholdKeyPhrasesAndLanguages_AndTheSentimentOfTeamsOfFewerThanFive()
        {
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, 41);
            var keys = new[] { query.CacheKey("conversations"), query.WithListProbe().CacheKey("conversations") };
            foreach (var key in keys) MemoryCache.Default.Remove(key);
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/conversations?days=28&top=41"));
                    Assert.AreEqual(0, ((JArray)trimmed["keywords"]).Count, "Text people wrote, with no count of who wrote it.");
                    Assert.IsTrue((bool)trimmed["keywordsWithheld"]);
                    Assert.AreEqual(0, ((JArray)trimmed["languages"]).Count);
                    Assert.IsTrue((bool)trimmed["languagesWithheld"]);
                    CollectionAssert.AreEqual(
                        new[] { "Contoso Big Team" },
                        trimmed["sentimentByTeam"].Select(r => (string)r["name"]).ToArray(),
                        "The team of two is left out, and the team of eight with it: the week's messages less the rows shown are both.");
                    Assert.AreEqual(2, (int)trimmed["withheldSentimentTeams"]);
                    CollectionAssert.AreEqual(
                        new[] { "Contoso Big Team / General" },
                        trimmed["sentimentByChannel"].Select(r => (string)r["name"]).ToArray());
                    Assert.AreEqual(2, (int)trimmed["withheldSentimentChannels"]);
                    Assert.AreEqual(1, ((JArray)trimmed["sentimentTrend"]).Count, "The whole tenant's weekly sentiment stays.");
                    Assert.IsNull(trimmed["sentimentByTeam"][0]["members"]);
                    Assert.IsFalse(trimmed.ToString().Contains("contoso secret project") || trimmed.ToString().Contains("Contoso Pair"));
                    Assert.IsFalse(trimmed.ToString().Contains("Contoso Mid"));

                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/conversations?days=28&top=41"));
                    Assert.AreEqual("contoso secret project", (string)full["keywords"][0]["name"]);
                    Assert.AreEqual("Ελληνικά", (string)full["languages"][0]["name"]);
                    Assert.AreEqual(3, ((JArray)full["sentimentByTeam"]).Count);
                    Assert.AreEqual(3, ((JArray)full["sentimentByChannel"]).Count);
                    Assert.IsFalse((bool)full["keywordsWithheld"]);
                    Assert.AreEqual(2, store.ConversationsCalls, "A See PII reader is never served the trimmed reader's section.");
                }
            }
            finally
            {
                foreach (var key in keys) MemoryCache.Default.Remove(key);
            }
        }

        [TestMethod]
        public async Task TeamsPeople_WithoutSeePii_KeepTheDepartmentCounts_AndTheCachedSectionKeepsTheNames()
        {
            // Power users per department is a count, so every reader sees it; only the lists that name people go.
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, 37);
            // A reader without See PII is served from the section read at the maximum top with every department, over the
            // Power segment's population, whatever top they asked for; a See PII reader from one of their own.
            var max = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, TeamsExplorerQuery.MaximumTop);
            var keys = new[] { query.CacheKey("people"), max.CacheKey("people"), max.WithEveryGroup().WithDirectoryPopulation().CacheKey("people") };
            foreach (var key in keys) MemoryCache.Default.Remove(key);
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/people?days=28&top=37"));
                    Assert.AreEqual(0, ((JArray)trimmed["champions"]).Count);
                    Assert.AreEqual(0, ((JArray)trimmed["dormant"]).Count);
                    Assert.AreEqual("Πωλήσεις", (string)trimmed["championsByDepartment"][0]["name"]);
                    Assert.AreEqual(9, (int)trimmed["championsByDepartment"][0]["count"]);
                    Assert.IsFalse(trimmed.ToString().Contains("@contoso.com"));

                    // A department with one to four power users is folded: in a small department the count says who.
                    CollectionAssert.AreEqual(
                        new[] { "Πωλήσεις", "Contoso Seven", null },
                        trimmed["championsByDepartment"].Select(row => (string)row["name"]).ToArray());
                    var other = trimmed["championsByDepartment"].Last;
                    Assert.IsTrue((bool)other["other"]);
                    Assert.AreEqual(5, (int)other["count"], "4 + 1 power users.");
                    Assert.AreEqual(2, (int)trimmed["otherChampionDepartments"]);
                    Assert.AreEqual(5, (int)trimmed["minimumGroupSize"]);
                    Assert.IsFalse(trimmed.ToString().Contains("Contoso Four") || trimmed.ToString().Contains("Contoso One"));

                    // The next reader holds the permission and is served a section of their own, as before.
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/people?days=28&top=" + TeamsExplorerQuery.MaximumTop));
                    Assert.AreEqual("champion@contoso.com", (string)full["champions"][0]["userPrincipalName"]);
                    Assert.AreEqual("dormant@contoso.com", (string)full["dormant"][0]["userPrincipalName"]);
                    CollectionAssert.AreEqual(
                        new[] { "Πωλήσεις", "Contoso Seven", "Contoso Four", "Contoso One" },
                        full["championsByDepartment"].Select(row => (string)row["name"]).ToArray(),
                        "A See PII reader sees every department, as before.");
                    Assert.IsNull(full["championsByDepartment"][0]["other"], "The flag is only sent on the folded row.");
                    Assert.AreEqual(2, store.PeopleCalls, "A See PII reader is never served the trimmed reader's section.");
                }
            }
            finally
            {
                foreach (var key in keys) MemoryCache.Default.Remove(key);
            }
        }

        [TestMethod]
        public async Task TeamsAdoption_WithoutSeePii_FoldsGroupsOfFewerThanFive_OnScreenAndInTheExport_AndTheCachedSectionKeepsThem()
        {
            // A job title held by one person is that person: their messages per active user are their own message count.
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, "jobTitle", 37);
            // A reader without See PII is served from the breakdown read with every row, whatever top they asked for, over
            // the directory population.
            var shared = TeamsExplorerQuery.Create(28, DateTime.UtcNow, "jobTitle", 37).WithEveryRow().WithDirectoryPopulation();
            var small = new[] { "Contoso Chief Explorer", "Contoso Four Title", "Contoso Night Office" };
            MemoryCache.Default.Remove(query.CacheKey("adoption"));
            MemoryCache.Default.Remove(shared.CacheKey("adoption"));
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var trimmed = await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle&top=37");
                    var json = JObject.Parse(trimmed);
                    CollectionAssert.AreEqual(
                        new[] { "Contoso Analysts", "Contoso Five Title", null },
                        json["breakdown"].Select(row => (string)row["name"]).ToArray(),
                        "Five people keep their name; one and four, and two active people among thirty, do not.");
                    var other = json["breakdown"].Last;
                    Assert.IsTrue((bool)other["other"]);
                    Assert.AreEqual(35, (int)other["knownUsers"]);
                    Assert.AreEqual(7, (int)other["activeUsers"]);
                    Assert.AreEqual(3, (int)json["otherGroups"]);
                    Assert.AreEqual(5, (int)json["minimumGroupSize"]);
                    var csv = await host.Client.GetStringAsync("api/TeamsExplorer/export/adoption?days=28&groupBy=jobTitle&top=37");
                    StringAssert.Contains(csv, TeamsExplorerExports.OtherGroupsLabel);
                    StringAssert.Contains(csv, "Contoso Five Title");
                    foreach (var name in small)
                    {
                        Assert.IsFalse(trimmed.Contains(name), name + " on screen");
                        Assert.IsFalse(csv.Contains(name), name + " in the export");
                    }
                    Assert.IsFalse(trimmed.Contains("777") || csv.Contains("777"), "The one person's message count.");

                    // What is cached is the whole breakdown: the fold was applied to a copy for this reader.
                    var cached = ((System.Threading.Tasks.Task<TeamsAdoption>)MemoryCache.Default.Get(shared.CacheKey("adoption"))).Result;
                    Assert.AreEqual(5, cached.Breakdown.Count, "The cached section keeps every group, named.");
                    Assert.IsTrue(small.All(name => cached.Breakdown.Any(row => row.Name == name)));

                    // A reader with the permission sees every group.
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle&top=37"));
                    Assert.AreEqual(5, ((JArray)full["breakdown"]).Count);
                    Assert.AreEqual(777d, (double)full["breakdown"].Single(row => (string)row["name"] == "Contoso Chief Explorer")["messagesPerActiveUser"]);
                    Assert.AreEqual(0, (int)full["otherGroups"]);
                    Assert.AreEqual(JTokenType.Null, full["minimumGroupSize"].Type);
                    Assert.AreEqual(37, store.LastAdoptionTop, "A reader with See PII reads the top they asked for.");
                    var fullCsv = await host.Client.GetStringAsync("api/TeamsExplorer/export/adoption?days=28&groupBy=jobTitle&top=37");
                    foreach (var name in small) StringAssert.Contains(fullCsv, name);
                    Assert.IsFalse(fullCsv.Contains(TeamsExplorerExports.OtherGroupsLabel));
                }
            }
            finally
            {
                MemoryCache.Default.Remove(query.CacheKey("adoption"));
                MemoryCache.Default.Remove(shared.CacheKey("adoption"));
            }
        }

        [TestMethod]
        public async Task TeamsAdoptionAndPeople_WithoutSeePii_FoldTheSameGroupsWhateverTopIsAskedFor()
        {
            // The SQL cuts its rows to top before the fold. When the fold ran over only those rows, asking for one row more
            // folded a 4-person job title with the smallest shown one, and subtracting that shown row (from the request one
            // row shorter) recovered the job title's own figures. Folded over every group, the combined row is the same
            // whatever top a reader asks for, and only named groups of five or more come and go.
            var store = new FakeTeamsStore();
            var keys = new[] { 1, 2, 3, 37, TeamsExplorerQuery.MaximumTop }
                .SelectMany(top => new[]
                {
                    TeamsExplorerQuery.Create(28, DateTime.UtcNow, "jobTitle", top).CacheKey("adoption"),
                    TeamsExplorerQuery.Create(28, DateTime.UtcNow, "jobTitle", top).WithEveryRow().WithDirectoryPopulation().CacheKey("adoption"),
                    TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, top).CacheKey("people"),
                    TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, top).WithEveryGroup().WithDirectoryPopulation().CacheKey("people"),
                })
                .Distinct()
                .ToArray();
            foreach (var key in keys) MemoryCache.Default.Remove(key);
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    string otherRow = null;
                    foreach (var top in new[] { 1, 2, 3, 37 })
                    {
                        var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle&top=" + top));
                        Assert.AreEqual(int.MaxValue, store.LastAdoptionTop, "Read with every row, so the fold sees every group.");
                        var rows = json["breakdown"].ToArray();
                        var other = rows.Single(row => (bool?)row["other"] == true);
                        otherRow = otherRow ?? other.ToString(Formatting.None);
                        Assert.AreEqual(otherRow, other.ToString(Formatting.None), "top=" + top + ": the combined row does not depend on top.");
                        Assert.AreEqual(Math.Min(top, 2), rows.Count(row => (bool?)row["other"] != true), "top=" + top + ": named groups are cut to top.");
                        Assert.IsFalse(json.ToString().Contains("Contoso Four Title"), "top=" + top);

                        var csv = await host.Client.GetStringAsync("api/TeamsExplorer/export/adoption?days=28&groupBy=jobTitle&top=" + top);
                        Assert.AreEqual(int.MaxValue, store.LastAdoptionTop, "The export folds the same way.");
                        Assert.IsFalse(csv.Contains("Contoso Four Title"), "top=" + top + " export");
                    }

                    string otherDepartments = null;
                    foreach (var top in new[] { 1, 2, 3, 37 })
                    {
                        var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/people?days=28&top=" + top));
                        Assert.AreEqual(TeamsExplorerQuery.MaximumTop, store.LastPeopleTop);
                        var rows = json["championsByDepartment"].ToArray();
                        var other = rows.Single(row => (bool?)row["other"] == true);
                        otherDepartments = otherDepartments ?? other.ToString(Formatting.None);
                        Assert.AreEqual(otherDepartments, other.ToString(Formatting.None), "top=" + top + ": the combined departments do not depend on top.");
                        Assert.AreEqual(5, (int)other["count"], "Contoso Four's 4 and Contoso One's 1, never Contoso Seven's 7 as well.");
                        Assert.AreEqual(Math.Min(top, 2), rows.Count(row => (bool?)row["other"] != true));
                    }

                    // A reader with See PII is unaffected: the store is asked for the top they asked for.
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle&top=2"));
                    Assert.AreEqual(2, store.LastAdoptionTop);
                    CollectionAssert.AreEqual(new[] { "Contoso Analysts", "Contoso Five Title" },
                        full["breakdown"].Select(row => (string)row["name"]).ToArray());
                }
            }
            finally
            {
                foreach (var key in keys) MemoryCache.Default.Remove(key);
            }
        }

        [TestMethod]
        public async Task TeamsAdoption_WithoutSeePii_FoldsGroupsBeyondTheMaximumTop_SoNoneCanBeWorkedOutFromTheTotals()
        {
            // 500 job titles of five active people, and a 501st held by one. Read at the 500-row cap, the 501st never
            // reached the fold; the tab's tenant-wide totals less every row shown was then that one person's figures.
            var store = new ManyGroupsTeamsStore(500, tail: 1);
            var key = TeamsExplorerQuery.Create(28, DateTime.UtcNow, "jobTitle", 3).WithEveryRow().WithDirectoryPopulation().CacheKey("adoption");
            MemoryCache.Default.Remove(key);
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(TeamsExplorerAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources())))
                {
                    var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle&top=3"));

                    Assert.AreEqual(501, store.RowsServed, "Every group was read, not the first 500.");
                    Assert.IsFalse(json.ToString().Contains("Contoso Lone Title"));
                    // Read at the cap, no group among the 500 was small, so nothing was folded and there was no combined row:
                    // the lone person was simply missing, recoverable from the totals. Now they are folded, topped up with
                    // the smallest shown title so the combined row is not small either.
                    var other = json["breakdown"].Single(row => (bool?)row["other"] == true);
                    Assert.AreEqual(6, (int)other["knownUsers"], "The lone person and the five-person title that tops them up.");
                    Assert.AreEqual(6, (int)other["activeUsers"]);
                    Assert.AreEqual(2, (int)json["otherGroups"]);
                    Assert.AreEqual(3, json["breakdown"].Count(row => (bool?)row["other"] != true), "Named groups are still cut to top.");
                }
            }
            finally
            {
                MemoryCache.Default.Remove(key);
            }
        }

        /// <summary>A breakdown of <c>groups</c> job titles of five active people, then one of <c>tail</c> people, cut to top as the SQL is.</summary>
        private sealed class ManyGroupsTeamsStore : ITeamsExplorerStore
        {
            private readonly List<TeamsDemographicRow> _rows = new List<TeamsDemographicRow>();
            internal int RowsServed;

            public ManyGroupsTeamsStore(int groups, int tail)
            {
                for (var i = 0; i < groups; i++)
                    _rows.Add(new TeamsDemographicRow { Name = "Contoso Title " + i.ToString("D3"), KnownUsers = 5, ActiveUsers = 5, ReachPct = 100, MessagesPerActiveUser = 10 });
                _rows.Add(new TeamsDemographicRow { Name = "Contoso Lone Title", KnownUsers = tail, ActiveUsers = tail, ReachPct = 100, MessagesPerActiveUser = 4321 });
            }

            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query)
            {
                var adoption = new TeamsAdoption { GroupBy = query.GroupBy };
                adoption.Breakdown.AddRange(_rows.OrderByDescending(r => r.ActiveUsers).ThenByDescending(r => r.KnownUsers).Take(query.Top));
                RowsServed = adoption.Breakdown.Count;
                return Task.FromResult(adoption);
            }

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(1, 1));
            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources) => Task.FromResult(new TeamsOverview());
            public Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsMeetings());
            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsCollaboration());
            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable) => Task.FromResult(new TeamsConversations());
            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsPeople());
        }

        [TestMethod]
        public async Task CopilotAdoptionSummary_WithoutSeePii_LosesTheManagerRollup_AndTheCachedAnalysisKeepsIt()
        {
            var analysis = SmallAnalysis();
            Assert.IsTrue(analysis.Summary.AccountabilityRollup.Count > 0, "fixture: the roll-up must name the manager");

            using (var host = CopilotAdoptionHost(analysis, PortalTestHost.SignedIn()))
            {
                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"));
                Assert.AreEqual(0, ((JArray)trimmed["accountabilityRollup"]).Count);
                Assert.IsFalse(trimmed.ToString(Formatting.None).Contains("manager@contoso.com"));
                Assert.AreEqual(analysis.Summary.LicensedUsers, (int)trimmed["licensedUsers"], "Aggregates are untouched.");

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"));
                Assert.AreEqual("manager@contoso.com", (string)full["accountabilityRollup"][0]["segment"]);
                Assert.IsTrue(analysis.Summary.AccountabilityRollup.Count > 0, "The shared analysis must never be edited for one reader.");
            }
        }

        [TestMethod]
        public async Task CopilotAdoption_NarrowedAggregates_RequireSeePii()
        {
            using (var host = CopilotAdoptionHost(SmallAnalysis(), PortalTestHost.SignedIn()))
            {
                foreach (var url in new[]
                {
                    "api/CopilotAdoption/summary?emailDomain=contoso.com",
                    "api/CopilotAdoption/summary?userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D",
                    "api/CopilotAdoption/export/workbook?emailDomain=contoso.com",
                })
                {
                    var response = await host.Client.GetAsync(url);
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, url);
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual(PortalPermissionDeniedModel.ErrorCode, (string)body["code"], url);
                    Assert.AreEqual("seePii", (string)body["permission"], url);
                }

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                foreach (var url in new[]
                {
                    "api/CopilotAdoption/summary?emailDomain=contoso.com",
                    "api/CopilotAdoption/export/workbook?emailDomain=contoso.com",
                })
                {
                    Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync(url)).StatusCode, url);
                }
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionRebuild_NarrowedRequestsRequireSeePiiBeforeStartingWork()
        {
            var runner = new FixedRunner(SmallAnalysis());
            using (var host = CopilotAdoptionHost(runner, PortalTestHost.SignedIn()))
            {
                foreach (var query in new[]
                {
                    "?emailDomain=contoso.com",
                    "?seatLicenceTypeIds=1",
                    "?userFilter=%5B%7B%22d%22%3A%22userName%22%2C%22v%22%3A%5B%22person1%40contoso.com%22%5D%7D%5D",
                })
                {
                    var response = await host.Client.PostAsync("api/CopilotAdoption/rebuild" + query,
                        new StringContent("{\"expectedGenerationId\":\"00000000000000000000000000000001\"}", Encoding.UTF8, "application/json"));
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, query);
                    Assert.AreEqual("seePii", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["permission"]);
                }
                Assert.AreEqual(0, runner.Overrides.Count, "A refused request cannot start an analysis.");
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionRebuild_RejectsInvalidTokens_AndStaleTokensReturnOnlyGenerationMetadata()
        {
            var analysis = SmallAnalysis();
            using (var host = CopilotAdoptionHost(analysis, PortalTestHost.SignedIn()))
            {
                var invalid = await host.Client.PostAsync("api/CopilotAdoption/rebuild",
                    new StringContent("{\"expectedGenerationId\":\"invalid\"}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                await host.Client.GetAsync("api/CopilotAdoption/summary");
                var response = await host.Client.PostAsync("api/CopilotAdoption/rebuild",
                    new StringContent("{\"expectedGenerationId\":\"00000000000000000000000000000001\"}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(analysis.Summary.Diagnostics.GenerationId, (string)body["generationId"]);
                Assert.IsNull(body["accountabilityRollup"], "Rebuild responses contain no report data.");
                Assert.IsFalse(body.ToString().Contains("manager@contoso.com"));
                Assert.AreEqual(analysis.Summary.Diagnostics.GenerationId,
                    response.Headers.GetValues(CopilotAdoptionAPIController.GenerationIdHeader).Single());
            }
        }

        [TestMethod]
        public async Task CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii()
        {
            // The override chooses whose activity every licensed-user figure describes, from every licence type in
            // the tenant: one held by a single known person would make the licensed side that person's record.
            var runner = new FixedRunner(SmallAnalysis());
            var overridden = new[]
            {
                "api/CopilotAdoption/summary?seatLicenceTypeIds=1",
                "api/CopilotAdoption/licence-types?seatLicenceTypeIds=1",
                "api/CopilotAdoption/filters?seatLicenceTypeIds=1",
                "api/CopilotAdoption/sql?seatLicenceTypeIds=1",
                "api/CopilotAdoption/export/workbook?seatLicenceTypeIds=1",
            };

            using (var host = CopilotAdoptionHost(runner, PortalTestHost.SignedIn()))
            {
                // Administration does not imply See PII.
                foreach (var principal in new[] { PortalTestHost.SignedIn(), PortalTestHost.SignedIn(PortalRoles.Administration) })
                {
                    host.Principal = principal;
                    foreach (var url in overridden)
                    {
                        var response = await host.Client.GetAsync(url);
                        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, url);
                        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                        Assert.AreEqual(PortalPermissionDeniedModel.ErrorCode, (string)body["code"], url);
                        Assert.AreEqual("seePii", (string)body["permission"], url);
                    }
                }

                Assert.AreEqual(0, runner.Overrides.Count, "A refused override must not start an analysis either.");

                // Without an override - an empty one overrides nothing - the aggregates are everyone's, as before.
                host.Principal = PortalTestHost.SignedIn();
                foreach (var url in new[]
                {
                    "api/CopilotAdoption/summary",
                    "api/CopilotAdoption/summary?seatLicenceTypeIds=",
                    "api/CopilotAdoption/licence-types",
                    "api/CopilotAdoption/filters",
                    "api/CopilotAdoption/sql",
                    "api/CopilotAdoption/export/workbook",
                })
                {
                    Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync(url)).StatusCode, url);
                }

                Assert.IsTrue(runner.Overrides.All(ids => ids.Count == 0), "Only the automatic classification was analysed.");

                // A See PII holder keeps the override, and it still reaches the analysis.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                foreach (var url in overridden)
                {
                    Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync(url)).StatusCode, url);
                }

                CollectionAssert.AreEqual(new List<int> { 1 }, runner.Overrides.Single(ids => ids.Count > 0));
            }
        }

        [TestMethod]
        public async Task UserFilterCatalogue_RequiresSeePii()
        {
            using (var host = new PortalTestHost(
                new[] { typeof(UserFilterAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing))
            {
                foreach (var url in new[] { "api/UserFilter/dimensions", "api/UserFilter/values?dimension=userName" })
                {
                    var response = await host.Client.GetAsync(url);
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, url);
                }
            }
        }

        [TestMethod]
        public void CopilotAdoptionSummary_GroupedByAnythingButManager_IsNotTrimmed()
        {
            var summary = new CopilotAdoptionSummary { AccountabilityDimension = CopilotAdoptionAccountabilityDimensions.Department };
            summary.AccountabilityRollup.Add(new AccountabilityRollupRow { Segment = "Finance", LicensedUsers = 9 });

            Assert.AreSame(summary, summary.WithoutIndividualData());

            summary.AccountabilityDimension = CopilotAdoptionAccountabilityDimensions.DirectManager;
            var copy = summary.WithoutIndividualData();
            Assert.AreNotSame(summary, copy);
            Assert.AreEqual(0, copy.AccountabilityRollup.Count);
            Assert.AreEqual(1, summary.AccountabilityRollup.Count);
            Assert.AreSame(summary.EmailDomains, copy.EmailDomains, "Shallow on purpose: only the replaced collection differs.");
        }

        [TestMethod]
        public async Task CopilotAdoptionWorkbook_WithoutSeePii_NamesNobody_AndWithItStillDoes()
        {
            var analysis = SmallAnalysis();

            using (var host = CopilotAdoptionHost(analysis, PortalTestHost.SignedIn()))
            {
                var trimmed = await host.Client.GetAsync("api/CopilotAdoption/export/workbook");
                Assert.AreEqual(HttpStatusCode.OK, trimmed.StatusCode);
                var trimmedText = WorkbookText(await trimmed.Content.ReadAsByteArrayAsync());
                Assert.IsFalse(trimmedText.Contains("@contoso.com"), "An export must never be less redacted than the screen.");

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = await host.Client.GetAsync("api/CopilotAdoption/export/workbook");
                StringAssert.Contains(WorkbookText(await full.Content.ReadAsByteArrayAsync()), "person1@contoso.com");
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionSeatIdle_OfACopilotSeatHeldByOneToFourPeople_IsWithheldWithoutSeePii_OnEveryEndpoint()
        {
            // #844 G4: the summary, the licence-type list and the workbook all carry the SKU table.
            var analysis = SmallAnalysis();
            analysis.Summary.SeatLicenceTypes = new List<LicenceTypeClassification>
            {
                new LicenceTypeClassification { Id = 1, Name = "Microsoft 365 Copilot", SkuPartNumber = "Microsoft_365_Copilot", AssignedUsers = 40, AssignedIdleUsers = 7, IsCopilotSeat = true },
                new LicenceTypeClassification { Id = 2, Name = "Contoso Copilot pilot", SkuPartNumber = "CONTOSO_COPILOT_PILOT", AssignedUsers = 3, AssignedIdleUsers = 2, IsCopilotSeat = true },
                new LicenceTypeClassification { Id = 3, Name = "Microsoft 365 Copilot EDU", SkuPartNumber = "Microsoft_365_Copilot_EDU", AssignedUsers = 9, AssignedIdleUsers = 6, IsCopilotSeat = true },
            };
            JObject Sku(JToken licences, int id) => (JObject)((JArray)licences).Single(l => (int)l["id"] == id);

            using (var host = CopilotAdoptionHost(analysis, PortalTestHost.SignedIn()))
            {
                var summary = JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"));
                var licenceTypes = JArray.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/licence-types"));
                foreach (var licences in new JToken[] { summary["seatLicenceTypes"], licenceTypes })
                {
                    Assert.AreEqual(JTokenType.Null, Sku(licences, 2)["assignedIdleUsers"].Type, "Withheld, never 0.");
                    Assert.IsTrue((bool)Sku(licences, 2)["assignedIdleWithheld"]);
                    Assert.AreEqual("fewHolders", (string)Sku(licences, 2)["assignedIdleWithheldReason"]);
                    Assert.AreEqual(5, (int)Sku(licences, 2)["minimumHolders"]);
                    Assert.AreEqual(3, (int)Sku(licences, 2)["assignedUsers"]);
                    Assert.AreEqual(JTokenType.Null, Sku(licences, 3)["assignedIdleUsers"].Type);
                    Assert.AreEqual("revealsSmallerSeat", (string)Sku(licences, 3)["assignedIdleWithheldReason"]);
                    Assert.AreEqual(7, (int)Sku(licences, 1)["assignedIdleUsers"]);
                }

                var workbook = WorkbookText(await (await host.Client.GetAsync("api/CopilotAdoption/export/workbook")).Content.ReadAsByteArrayAsync());
                StringAssert.Contains(workbook, "Not shown: fewer than 5 people hold this licence");
                StringAssert.Contains(workbook, "Not shown: with the reclaimable total, it would reveal the figure for a licence fewer than 5 people hold");

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"));
                var fullTypes = JArray.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/licence-types"));
                foreach (var licences in new JToken[] { full["seatLicenceTypes"], fullTypes })
                {
                    Assert.AreEqual(2, (int)Sku(licences, 2)["assignedIdleUsers"]);
                    Assert.IsFalse((bool)Sku(licences, 2)["assignedIdleWithheld"]);
                    Assert.AreEqual(6, (int)Sku(licences, 3)["assignedIdleUsers"]);
                }
                Assert.IsFalse(WorkbookText(await (await host.Client.GetAsync("api/CopilotAdoption/export/workbook")).Content.ReadAsByteArrayAsync())
                    .Contains("Not shown: fewer than 5 people hold this licence"));
                Assert.AreEqual(2, analysis.Summary.SeatLicenceTypes[1].AssignedIdleUsers, "The shared analysis must never be edited for one reader.");
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionPastRange_RefusesNamedListsButKeepsSummaryCounts()
        {
            var analysis = SmallAnalysis();
            analysis.Summary.Options.UsesExplicitDates = true;
            analysis.Summary.Options.FromUtc = new DateTime(2026, 7, 1);
            analysis.Summary.Options.ToUtc = new DateTime(2026, 7, 31);
            analysis.Summary.Options.ToExclusiveUtc = new DateTime(2026, 8, 1);
            analysis.Summary.FromUtc = analysis.Summary.Options.FromUtc.Value;
            analysis.Summary.ToUtc = analysis.Summary.Options.ToUtc.Value;

            using (var host = CopilotAdoptionHost(analysis, PortalTestHost.SignedIn(PortalRoles.SeePii)))
            {
                var summary = JObject.Parse(await host.Client.GetStringAsync(
                    "api/CopilotAdoption/summary?from=2026-07-01&to=2026-07-31"));
                Assert.AreEqual(6, (int)summary["licensedUsers"]);

                foreach (var url in new[]
                {
                    "api/CopilotAdoption/licensed-users?from=2026-07-01&to=2026-07-31",
                    "api/CopilotAdoption/licensed-users/export?from=2026-07-01&to=2026-07-31",
                    "api/CopilotAdoption/opportunities?from=2026-07-01&to=2026-07-31",
                    "api/CopilotAdoption/opportunities/export?from=2026-07-01&to=2026-07-31",
                    "api/CopilotAdoption/cowork?from=2026-07-01&to=2026-07-31",
                    "api/CopilotAdoption/cowork/export?from=2026-07-01&to=2026-07-31",
                })
                {
                    var response = await host.Client.GetAsync(url);
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, url);
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual("copilotAdoption.error.pastRangeNamedListsHidden", (string)body["code"], url);
                }
            }
        }

        private static PortalTestHost CopilotAdoptionHost(CopilotAdoptionAnalysis analysis, IPrincipal principal, LeadershipComparisonProvider leadership = null)
        {
            return CopilotAdoptionHost(new FixedRunner(analysis), principal, leadership);
        }

        private static PortalTestHost CopilotAdoptionHost(FixedRunner runner, IPrincipal principal, LeadershipComparisonProvider leadership = null)
        {
            // Never the process-wide provider: that would read the test run's Storage account.
            leadership = leadership ?? new LeadershipComparisonProvider(() => new LeadershipCohortStore(new InMemoryKeyValueStore(), isDurable: true));
            var coordinator = new AdoptionCoordinator(
                runner,
                new DictionaryCache(),
                (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10));

            return new PortalTestHost(
                new[] { typeof(CopilotAdoptionAPIController) },
                principal,
                PortalAccessPolicy.Enforcing,
                _ => new CopilotAdoptionAPIController(coordinator) { LeadershipComparisons = leadership });
        }

        /// <summary>A provider whose configured leadership group holds SQL user ids 1..<paramref name="leaders"/>.</summary>
        private static async Task<LeadershipComparisonProvider> LeadershipProvider(int leaders)
        {
            var store = new LeadershipCohortStore(new InMemoryKeyValueStore(), isDurable: true);
            await store.SaveSettingsAsync(new LeadershipCohortSettings { GroupId = "00000000-0000-0000-0000-000000000654", Revision = "r1", UpdatedUtc = DateTime.UtcNow });
            await store.SaveMembersAsync(new LeadershipCohortSnapshot
            {
                Version = "v1",
                GroupId = "00000000-0000-0000-0000-000000000654",
                GroupDisplayName = "Contoso Leadership",
                SettingsRevision = "r1",
                Status = LeadershipCohortRefreshStatuses.Ready,
                AttemptedUtc = DateTime.UtcNow,
                RefreshedUtc = DateTime.UtcNow,
            }, Enumerable.Range(1, leaders).ToList());
            return new LeadershipComparisonProvider(() => store);
        }

        [TestMethod]
        public async Task CopilotAdoptionLeadership_IsAnAggregateForEveryReaderAndNamesNobody()
        {
            var analysis = SmallAnalysis(12);
            using (var host = CopilotAdoptionHost(analysis, PortalTestHost.SignedIn(), await LeadershipProvider(12)))
            {
                var body = await host.Client.GetStringAsync("api/CopilotAdoption/summary");
                var leadership = (JObject)JObject.Parse(body)["leadershipComparison"];
                Assert.AreEqual("ok", (string)leadership["status"], "Aggregates need no See PII.");
                Assert.AreEqual(12, (int)leadership["licensedLeaders"]);
                Assert.AreEqual(10, (int)leadership["minimumCohort"]);
                var text = leadership.ToString(Formatting.None);
                Assert.IsFalse(text.Contains("@contoso.com") || text.Contains("Contoso Leadership") || text.Contains("000000000654"), text);

                var workbook = WorkbookText(await (await host.Client.GetAsync("api/CopilotAdoption/export/workbook")).Content.ReadAsByteArrayAsync());
                StringAssert.Contains(workbook, "leadership.licensedLeaders");
                Assert.IsFalse(workbook.Contains("Contoso Leadership"), "The group's name is configuration, not report content.");
            }
            Assert.IsNull(analysis.Summary.LeadershipComparison, "The cached summary must not be modified.");
        }

        [TestMethod]
        public async Task CopilotAdoptionLeadership_SmallCohortIsSuppressedEvenForSeePii()
        {
            using (var host = CopilotAdoptionHost(SmallAnalysis(), PortalTestHost.SignedIn(PortalRoles.SeePii), await LeadershipProvider(6)))
            {
                var leadership = (JObject)JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"))["leadershipComparison"];
                Assert.AreEqual("suppressed", (string)leadership["status"]);
                Assert.AreEqual(JTokenType.Null, leadership["licensedLeaders"].Type);
                Assert.AreEqual(JTokenType.Null, leadership["leaderAdoptionRatePct"].Type);
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionLeadership_SmallComplementIsSuppressedEvenForSeePii()
        {
            // 12 leaders of 15 licensed users: the cohort clears the minimum, but tenant minus leaders would describe 3 people.
            using (var host = CopilotAdoptionHost(SmallAnalysis(15), PortalTestHost.SignedIn(PortalRoles.SeePii, PortalRoles.Administration), await LeadershipProvider(12)))
            {
                var leadership = (JObject)JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"))["leadershipComparison"];
                Assert.AreEqual("suppressed", (string)leadership["status"]);
                Assert.AreEqual("complementTooSmall", (string)leadership["reason"]);
                foreach (var field in new[] { "licensedLeaders", "activeLeaders", "habitualLeaders", "leaderAdoptionRatePct", "tenantAdoptionRatePct", "adoptionGapPts", "scoreGap" })
                    Assert.AreEqual(JTokenType.Null, leadership[field].Type, field);

                var workbook = WorkbookText(await (await host.Client.GetAsync("api/CopilotAdoption/export/workbook")).Content.ReadAsByteArrayAsync());
                StringAssert.Contains(workbook, "fewer than 10 licensed users are outside the leadership group");
                Assert.IsFalse(workbook.Contains("Contoso Leadership"));
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionLeadership_NarrowedViewIsNotCompared()
        {
            using (var host = CopilotAdoptionHost(SmallAnalysis(12), PortalTestHost.SignedIn(PortalRoles.SeePii), await LeadershipProvider(12)))
            {
                var leadership = (JObject)JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary?emailDomain=contoso.com"))["leadershipComparison"];
                Assert.AreEqual("scopedView", (string)leadership["status"]);
                Assert.AreEqual(JTokenType.Null, leadership["licensedLeaders"].Type);
            }
        }

        [TestMethod]
        public async Task CopilotAdoptionLeadership_IsNotConfiguredByDefault()
        {
            using (var host = CopilotAdoptionHost(SmallAnalysis(), PortalTestHost.SignedIn()))
            {
                var leadership = JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"))["leadershipComparison"];
                Assert.AreEqual("notConfigured", (string)leadership["status"]);
            }
        }

        /// <summary>Six seat holders reporting to one synthetic manager - enough to clear the roll-up's minimum.</summary>
        private static CopilotAdoptionAnalysis SmallAnalysis(int people = 6)
        {
            var now = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var options = CopilotAdoptionOptions.Default;
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.Options = options;
            summary.GeneratedUtc = now;
            summary.WindowDays = options.WindowDays;
            summary.FromUtc = now.AddDays(-options.WindowDays);
            summary.ToUtc = now;
            summary.DataSources.AuditAvailable = true;
            summary.DataSources.UserMetadataAvailable = true;

            for (var i = 0; i < people; i++)
            {
                analysis.LicensedUsers.Add(CopilotAdoptionScoring.Score(
                    new LicensedUserUsageRow
                    {
                        UserId = i + 1,
                        UserPrincipalName = "person" + i + "@contoso.com",
                        Department = "Finance",
                        ManagerUserPrincipalName = "manager@contoso.com",
                        Interactions = i * 3,
                        ActiveDays = i,
                        AppsUsed = i == 0 ? 0 : 1,
                        LastInteractionUtc = i == 0 ? (DateTime?)null : now.AddDays(-2),
                        FirstInteractionUtc = i == 0 ? (DateTime?)null : now.AddDays(-60),
                    },
                    summary.FromUtc,
                    now,
                    true,
                    options));
            }

            summary.LicensedUsers = analysis.LicensedUsers.Count;
            new CopilotAdoptionService().FinaliseSummary(analysis);
            return analysis;
        }

        private static string WorkbookText(byte[] bytes)
        {
            var text = new StringBuilder();
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    using (var part = entry.Open())
                    {
                        foreach (var node in XDocument.Load(part).Descendants().Where(e => !e.HasElements))
                        {
                            text.AppendLine(node.Value);
                        }
                    }
                }
            }

            return text.ToString();
        }

        private sealed class FixedRunner : AdoptionRunner
        {
            private readonly CopilotAdoptionAnalysis _analysis;
            internal FixedRunner(CopilotAdoptionAnalysis analysis) { _analysis = analysis; }

            /// <summary>The seat-licence override of every analysis started, empty for the automatic classification.</summary>
            internal List<List<int>> Overrides { get; } = new List<List<int>>();

            public Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc, bool usesExplicitDates, List<int> seatLicenceTypeIds, Common.Entities.CopilotAdoption.CopilotAdoptionEffectiveScoreSettings scoreSettings, ICopilotAdoptionRunTelemetry telemetry)
            {
                lock (Overrides) Overrides.Add(new List<int>(seatLicenceTypeIds ?? new List<int>()));
                return Task.FromResult(_analysis);
            }
        }

        private sealed class DictionaryCache : AdoptionCache
        {
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _entries = new Dictionary<string, CopilotAdoptionAnalysis>();

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                lock (_entries) return _entries.TryGetValue(key, out analysis);
            }

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
                lock (_entries) _entries[key] = analysis;
            }
        }

        private sealed class FakeTeamsStore : ITeamsExplorerStore
        {
            internal int MeetingsCalls;
            internal int CollaborationCalls;
            internal int ConversationsCalls;
            internal int PeopleCalls;
            internal int AdoptionCalls;
            internal int LastAdoptionTop;
            internal int LastPeopleTop;

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(1, 1));

            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources)
                => Task.FromResult(new TeamsOverview());

            /// <summary>
            /// Job titles held by 1, 4, 5 and 12 people, and an office of 30 with only two people active: the one-person
            /// title's messages per active user are that person's own. Cut to <c>top</c> in the SQL's order, as the SQL is.
            /// </summary>
            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref AdoptionCalls);
                LastAdoptionTop = query.Top;
                var adoption = new TeamsAdoption { GroupBy = query.GroupBy };
                adoption.Breakdown.AddRange(new[]
                    {
                        Row("Contoso Analysts", 12, 10, 40),
                        Row("Contoso Night Office", 30, 2, 90),
                        Row("Contoso Five Title", 5, 5, 20),
                        Row("Contoso Four Title", 4, 4, 30),
                        Row("Contoso Chief Explorer", 1, 1, 777),
                    }
                    .OrderByDescending(r => r.ActiveUsers).ThenByDescending(r => r.KnownUsers)
                    .Take(query.Top));
                return Task.FromResult(adoption);
            }

            private static TeamsDemographicRow Row(string name, int known, int active, double messagesPerActiveUser) =>
                new TeamsDemographicRow
                {
                    Name = name,
                    KnownUsers = known,
                    ActiveUsers = active,
                    ReachPct = TeamsExplorerScoring.Percentage(active, known),
                    MessagesPerActiveUser = messagesPerActiveUser,
                };

            public Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref MeetingsCalls);
                var meetings = new TeamsMeetings { WorkingDayStartHour = 42 };
                meetings.TopOrganisers.Add(new TeamsNamedCountRow { Name = "organiser@contoso.com", Count = 5 });
                meetings.TopAttendees.Add(new TeamsNamedCountRow { Name = "attendee@contoso.com", Count = 7 });
                // Twelve people in all; one week only two of them met, another seven; screen sharing was one person's.
                meetings.Kpis = new TeamsCallKpis { Calls = 45, Attendees = 12 };
                meetings.Trend.Add(new TeamsCallTrendPoint { WeekStart = new DateTime(2026, 10, 5), Calls = 20, Minutes = 600, Attendees = 12 });
                meetings.Trend.Add(new TeamsCallTrendPoint { WeekStart = new DateTime(2026, 10, 12), Calls = 10, Minutes = 300, Attendees = 2 });
                meetings.Trend.Add(new TeamsCallTrendPoint { WeekStart = new DateTime(2026, 10, 19), Calls = 15, Minutes = 450, Attendees = 7 });
                meetings.SizeDistribution.Add(new TeamsBucketRow { Key = "2", Label = "2", Count = 10, SharePct = 100 });
                meetings.ModalityMix.AddRange(new[]
                {
                    new TeamsBucketRow { Key = "audio", Label = "audio", Count = 50, People = 12 },
                    new TeamsBucketRow { Key = "video", Label = "video", Count = 20, People = 8 },
                    new TeamsBucketRow { Key = "screenSharing", Label = "screenSharing", Count = 2, People = 1 },
                });
                return Task.FromResult(meetings);
            }

            /// <summary>
            /// A team of 40, a team of 12 where one person made every reaction, a team of 8 and a team of two; a reaction one
            /// person used; and a tab whose name a member typed.
            /// </summary>
            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref CollaborationCalls);
                var collaboration = new TeamsCollaboration { ReadTop = query.Top };
                collaboration.Kpis = new TeamsCollaborationKpis { TotalTeams = 4, Reactions = 196 };
                collaboration.Teams.AddRange(new[]
                {
                    new TeamsTeamRow { Id = 1, Name = "Contoso Big Team", Members = 40, Messages = 900, Reactions = 120, ReactingUsers = 25 },
                    new TeamsTeamRow { Id = 3, Name = "Contoso Pair", Members = 2, Messages = 500, Reactions = 9, ReactingUsers = 2 },
                    new TeamsTeamRow { Id = 2, Name = "Contoso One Reactor", Members = 12, Messages = 300, Reactions = 37, ReactingUsers = 1 },
                    new TeamsTeamRow { Id = 4, Name = "Contoso Mid", Members = 8, Messages = 200, Reactions = 30, ReactingUsers = 9 },
                }.Take(query.Top));
                collaboration.Channels.AddRange(new[]
                {
                    new TeamsChannelRow { Id = 10, Name = "General", TeamName = "Contoso Big Team", TeamMembers = 40, Messages = 600, Reactions = 80, ReactingUsers = 20 },
                    new TeamsChannelRow { Id = 12, Name = "Πωλήσεις", TeamName = "Contoso Pair", TeamMembers = 2, Messages = 500, Reactions = 9, ReactingUsers = 2 },
                    new TeamsChannelRow { Id = 13, Name = "Plans", TeamName = "Contoso Mid", TeamMembers = 8, Messages = 200, Reactions = 30, ReactingUsers = 9 },
                    new TeamsChannelRow { Id = 11, Name = "Ideas", TeamName = "Contoso Big Team", TeamMembers = 40, Messages = 50, Reactions = 37, ReactingUsers = 1 },
                }.Take(query.Top));
                collaboration.ReactionMix.AddRange(new[]
                {
                    new TeamsBucketRow { Key = "like", Label = "like", Count = 100, SharePct = 68.5, People = 30 },
                    new TeamsBucketRow { Key = "heart", Label = "heart", Count = 40, SharePct = 27.4, People = 9 },
                    new TeamsBucketRow { Key = "angry", Label = "angry", Count = 6, SharePct = 4.1, People = 1 },
                });
                collaboration.TabUsage.Add(new TeamsNamedCountRow { Name = "Contoso private notes", Count = 1 });
                return Task.FromResult(collaboration);
            }

            /// <summary>Key phrases and a language nobody can say how many people wrote, and a team of two's sentiment.</summary>
            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable)
            {
                Interlocked.Increment(ref ConversationsCalls);
                var conversations = new TeamsConversations { CognitiveAvailable = true, ScoredChannelDays = 12, ReadTop = query.Top };
                conversations.Keywords.Add(new TeamsNamedCountRow { Name = "contoso secret project", Count = 3 });
                conversations.Languages.Add(new TeamsNamedCountRow { Name = "Ελληνικά", Count = 2 });
                conversations.SentimentTrend.Add(new TeamsSentimentPoint { WeekStart = new DateTime(2026, 10, 5), Sentiment = 0.6, Messages = 1600 });
                conversations.SentimentByTeam.AddRange(new[]
                {
                    new TeamsSentimentRow { Name = "Contoso Big Team", Sentiment = 0.7, Messages = 900, Members = 40 },
                    new TeamsSentimentRow { Name = "Contoso Pair", Sentiment = 0.1, Messages = 500, Members = 2 },
                    new TeamsSentimentRow { Name = "Contoso Mid", Sentiment = 0.5, Messages = 200, Members = 8 },
                }.Take(query.Top));
                conversations.SentimentByChannel.AddRange(new[]
                {
                    new TeamsSentimentRow { Name = "Contoso Big Team / General", Sentiment = 0.7, Messages = 600, Members = 40 },
                    new TeamsSentimentRow { Name = "Contoso Pair / Πωλήσεις", Sentiment = 0.1, Messages = 500, Members = 2 },
                    new TeamsSentimentRow { Name = "Contoso Mid / Plans", Sentiment = 0.5, Messages = 200, Members = 8 },
                }.Take(query.Top));
                return Task.FromResult(conversations);
            }

            /// <summary>
            /// Departments of 9, 7, 4 and 1 power users, cut to <c>top</c> as the SQL is - unless the query reads every group,
            /// as it does for a reader without See PII.
            /// </summary>
            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref PeopleCalls);
                LastPeopleTop = query.Top;
                var departmentTop = query.EveryGroup ? int.MaxValue : query.Top;
                var people = new TeamsPeople { ReadTop = departmentTop };
                people.Champions.Add(new TeamsPersonRow { UserPrincipalName = "champion@contoso.com", Department = "Ventes" });
                people.Dormant.Add(new TeamsPersonRow { UserPrincipalName = "dormant@contoso.com", Department = "Ventes" });
                people.ChampionsByDepartment.AddRange(new[]
                    {
                        new TeamsNamedCountRow { Name = "Πωλήσεις", Count = 9 },
                        new TeamsNamedCountRow { Name = "Contoso Seven", Count = 7 },
                        new TeamsNamedCountRow { Name = "Contoso Four", Count = 4 },
                        new TeamsNamedCountRow { Name = "Contoso One", Count = 1 },
                    }.Take(departmentTop));
                return Task.FromResult(people);
            }
        }

        #endregion
    }
}
