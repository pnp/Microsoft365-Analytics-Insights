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
        private const string Any = "any signed-in user";
        private const string Public = "anonymous (not signed in)";
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

            // Microsoft Graph's change-notification webhook: Graph cannot sign in, so it checks clientState instead.
            ["CallRecordWebhookController.Post"] = Public,

            ["CopilotAdoptionAPIController.Availability"] = Any,
            ["CopilotAdoptionAPIController.Summary"] = Any,            // trims the manager roll-up
            ["CopilotAdoptionAPIController.LicenceTypes"] = Any,
            ["CopilotAdoptionAPIController.Sql"] = Any,
            ["CopilotAdoptionAPIController.Filters"] = Any,
            ["CopilotAdoptionAPIController.LicensedUsers"] = Pii,
            ["CopilotAdoptionAPIController.ExportLicensedUsers"] = Pii,
            ["CopilotAdoptionAPIController.Opportunities"] = Pii,
            ["CopilotAdoptionAPIController.ExportOpportunities"] = Pii,
            ["CopilotAdoptionAPIController.Cowork"] = Pii,
            ["CopilotAdoptionAPIController.ExportCowork"] = Pii,
            ["CopilotAdoptionAPIController.ExportWorkbook"] = Any,     // trims the per-person sheets

            ["DlpAPIController.Availability"] = Any,
            ["DlpAPIController.Summary"] = Any,                        // trims the top-users table

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
            ["LicenceActivityAPIController.Overview"] = Any,
            ["LicenceActivityAPIController.Users"] = Pii,
            ["LicenceActivityAPIController.Export"] = Any,             // refuses usersId without See PII

            ["PortalAccessAPIController.Get"] = Any,

            ["ProfilingStatusAPIController.Get"] = Admin,
            ["ProfilingStatusAPIController.TraceLogs"] = Admin,

            ["ReportsAPIController.Areas"] = Any,
            ["ReportsAPIController.Copilot"] = Any,
            ["ReportsAPIController.CopilotAgents"] = Any,
            ["ReportsAPIController.Usage"] = Any,
            ["ReportsAPIController.SpoAudit"] = Any,
            ["ReportsAPIController.WebTraffic"] = Any,
            ["ReportsAPIController.Calls"] = Any,
            ["ReportsAPIController.Emails"] = Any,
            ["ReportsAPIController.OfficeApps"] = Any,

            ["SiteTokenAPIController.Post"] = Admin,

            ["SystemStatusAPIController.Get"] = Any,                   // trims the admin-only fields

            ["TeamsAuthAPIController.Post"] = Admin,
            ["TeamsAuthAPIController.Put"] = Admin,

            ["TeamsExplorerAPIController.Availability"] = Any,
            ["TeamsExplorerAPIController.Overview"] = Any,
            ["TeamsExplorerAPIController.Adoption"] = Any,
            ["TeamsExplorerAPIController.Meetings"] = Any,             // trims the named leaderboards
            ["TeamsExplorerAPIController.Collaboration"] = Any,
            ["TeamsExplorerAPIController.Conversations"] = Any,
            ["TeamsExplorerAPIController.People"] = Any,               // trims the champion and dormant lists
            ["TeamsExplorerAPIController.Export"] = Any,               // refuses the people sections without See PII

            ["UpdateCheckAPIController.Get"] = Admin,

            ["UserDataLookupAPIController.Summary"] = AdminAndPii,
            ["UserDataLookupAPIController.Detail"] = AdminAndPii,

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

        private static string Classify(Type controller, MethodInfo method)
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

        /// <summary>The actions Web API itself would find: see <c>ApiControllerActionSelector</c>.</summary>
        private static IEnumerable<(string Key, Type Controller, MethodInfo Method)> WebApiActions()
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

        [TestMethod]
        public void ConnectTeams_MvcActionRequiresAdministration()
        {
            var action = typeof(AccountController).GetMethod(nameof(AccountController.ConnectTeams));
            var filter = action.GetCustomAttributes(typeof(RequirePortalMvcPermissionAttribute), true)
                .Cast<RequirePortalMvcPermissionAttribute>()
                .Single();
            Assert.AreEqual(PortalPermission.Administration, filter.Permission);

            var denied = ConnectTeamsAuthorization(PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing);
            filter.OnAuthorization(denied);
            Assert.AreEqual((int)HttpStatusCode.Forbidden, denied.HttpContext.Response.StatusCode);
            Assert.IsInstanceOfType(denied.Result, typeof(System.Web.Mvc.ContentResult));
            StringAssert.Contains(((System.Web.Mvc.ContentResult)denied.Result).Content, PortalRoles.Administration);

            var allowed = ConnectTeamsAuthorization(
                PortalTestHost.SignedIn(PortalRoles.Administration),
                PortalAccessPolicy.Enforcing);
            filter.OnAuthorization(allowed);
            Assert.IsNull(allowed.Result, "An administrator must reach the OIDC challenge.");

            var compatibilityMode = ConnectTeamsAuthorization(
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.NotEnforcing);
            filter.OnAuthorization(compatibilityMode);
            Assert.IsNull(compatibilityMode.Result, "EnforcePortalRoles=false must preserve the pre-role behaviour.");

            var anonymous = ConnectTeamsAuthorization(PortalTestHost.Anonymous(), PortalAccessPolicy.Enforcing);
            filter.OnAuthorization(anonymous);
            Assert.IsInstanceOfType(anonymous.Result, typeof(System.Web.Mvc.HttpUnauthorizedResult));
        }

        private static System.Web.Mvc.AuthorizationContext ConnectTeamsAuthorization(
            IPrincipal principal,
            PortalAccessPolicy policy)
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
                typeof(AccountController).GetMethod(nameof(AccountController.ConnectTeams)),
                nameof(AccountController.ConnectTeams),
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

            (HttpMethod.Get, "api/CopilotAdoption/licensed-users", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/licensed-users/export", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/opportunities", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/opportunities/export", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/cowork", "seePii"),
            (HttpMethod.Get, "api/CopilotAdoption/cowork/export", "seePii"),
            (HttpMethod.Get, "api/LicenceActivity/users?overviewId=synthetic&licenceTypeId=1", "seePii"),
            (HttpMethod.Get, "api/LicenceActivity/export?overviewId=synthetic&usersId=synthetic", "seePii"),
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
        public async Task TeamsPeople_WithoutSeePii_KeepTheDepartmentCounts_AndTheCachedSectionKeepsTheNames()
        {
            // Power users per department is a count, so every reader sees it; only the lists that name people go.
            var store = new FakeTeamsStore();
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow, TeamsExplorerQuery.DefaultGrouping, 37);
            MemoryCache.Default.Remove(query.CacheKey("people"));
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

                    // The next reader holds the permission and is served from the same cache entry.
                    host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/people?days=28&top=37"));
                    Assert.AreEqual("champion@contoso.com", (string)full["champions"][0]["userPrincipalName"]);
                    Assert.AreEqual("dormant@contoso.com", (string)full["dormant"][0]["userPrincipalName"]);
                    Assert.AreEqual(1, store.PeopleCalls, "The second reader must have been served from the cache.");
                }
            }
            finally
            {
                MemoryCache.Default.Remove(query.CacheKey("people"));
            }
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
            // Never the process-wide provider: that would read the test run's Storage account.
            leadership = leadership ?? new LeadershipComparisonProvider(() => new LeadershipCohortStore(new InMemoryKeyValueStore(), isDurable: true));
            var coordinator = new AdoptionCoordinator(
                new FixedRunner(analysis),
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

            public Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc, bool usesExplicitDates, List<int> seatLicenceTypeIds, ICopilotAdoptionRunTelemetry telemetry)
                => Task.FromResult(_analysis);
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
            internal int PeopleCalls;

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(1, 1));

            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources)
                => Task.FromResult(new TeamsOverview());

            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsAdoption());

            public Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref MeetingsCalls);
                var meetings = new TeamsMeetings { WorkingDayStartHour = 42 };
                meetings.TopOrganisers.Add(new TeamsNamedCountRow { Name = "organiser@contoso.com", Count = 5 });
                meetings.TopAttendees.Add(new TeamsNamedCountRow { Name = "attendee@contoso.com", Count = 7 });
                return Task.FromResult(meetings);
            }

            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query)
                => Task.FromResult(new TeamsCollaboration());

            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable)
                => Task.FromResult(new TeamsConversations());

            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref PeopleCalls);
                var people = new TeamsPeople();
                people.Champions.Add(new TeamsPersonRow { UserPrincipalName = "champion@contoso.com", Department = "Ventes" });
                people.Dormant.Add(new TeamsPersonRow { UserPrincipalName = "dormant@contoso.com", Department = "Ventes" });
                people.ChampionsByDepartment.Add(new TeamsNamedCountRow { Name = "Πωλήσεις", Count = 9 });
                return Task.FromResult(people);
            }
        }

        #endregion
    }
}
