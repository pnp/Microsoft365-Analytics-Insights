extern alias AnalyticsWeb;

using Common.Entities.UserFilters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ApiErrorModel = AnalyticsWeb::Web.AnalyticsWeb.Models.ApiErrorModel;
using ApiReplyException = AnalyticsWeb::Web.AnalyticsWeb.ApiReplyException;
using ApiReplyExceptionFilterAttribute = AnalyticsWeb::Web.AnalyticsWeb.ApiReplyExceptionFilterAttribute;
using CachedGlobalFilterProvider = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.CachedGlobalFilterProvider;
using GlobalFilterAdminModel = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterAdminModel;
using GlobalFilterAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.GlobalFilterAPIController;
using GlobalFilterEffectiveModel = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterEffectiveModel;
using GlobalFilterPreviewRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterPreviewRequest;
using GlobalFilterProviders = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterProviders;
using GlobalFilterSaveRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterSaveRequest;
using GlobalFilterState = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterState;
using IGlobalFilterProvider = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IGlobalFilterProvider;
using IUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IUserDirectorySource;
using PortalAccessPolicy = AnalyticsWeb::Web.AnalyticsWeb.Security.PortalAccessPolicy;
using PortalPermissionDeniedModel = AnalyticsWeb::Web.AnalyticsWeb.Security.PortalPermissionDeniedModel;
using PortalRoles = AnalyticsWeb::Web.AnalyticsWeb.Security.PortalRoles;
using ReportScopeResolver = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScopeResolver;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;
using UserFilterAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.UserFilterAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The administrator's global filter on the web side: applied to every report request whatever the page
    /// sends, failing closed when it cannot be evaluated, switched off only for an administrator who asks,
    /// and edited only with a revision check. No database: an in-memory store and directory.
    /// </summary>
    [TestClass]
    public class GlobalFilterWebTests
    {
        private const string MyDepartment = "[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]";

        #region Applying it to a report request

        [TestMethod]
        public async Task Resolve_NarrowsAReaderToTheFilter_AndTheirOwnFilterWithinIt()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));
            var userFilter = UserFilterCodec.Parse("[{\"d\":\"manager\",\"v\":[\"director@contoso.com\"]}]");

            var scope = await resolver.ResolveAsync(Request(), PiiReader("rep@contoso.com"), userFilter, CancellationToken.None);

            Assert.IsTrue(scope.IsRestricted);
            CollectionAssert.AreEqual(new[] { 3, 4 }, scope.Sql.UserIds.ToArray());
            Assert.IsTrue(scope.Includes(3));
            Assert.IsFalse(scope.Includes(5), "An engineer matches neither the department nor the reader's filter.");
            Assert.IsNotNull(scope.GlobalEcho);
            Assert.AreEqual("Sales", scope.GlobalEcho.Clauses[0].ViewerValue);
        }

        [TestMethod]
        public async Task Resolve_RecognisesTheReaderByObjectIdBeforeSignInName()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));
            var reader = PrincipalWithObjectId("someone-else@contoso.com", "00000000-0000-0000-0000-000000000005", PortalRoles.SeePii);

            var scope = await resolver.ResolveAsync(Request(), reader, null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 5 }, scope.Sql.UserIds.ToArray());
        }

        [TestMethod]
        public async Task Resolve_AReaderTheDirectoryDoesNotHold_SeesNobody()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));

            var scope = await resolver.ResolveAsync(Request(), Reader("stranger@contoso.com"), null, CancellationToken.None);

            Assert.IsTrue(scope.Sql.IsRestricted);
            Assert.AreEqual(0, scope.Sql.Count);
            Assert.IsFalse(scope.GlobalEcho.ViewerFound);
        }

        [TestMethod]
        public async Task Resolve_WithNoFilterAnywhere_NeverReadsTheDirectory()
        {
            var resolver = new ReportScopeResolver(GlobalFilterProviders.None, new Directory(failure: new InvalidOperationException("must not be read")));

            var scope = await resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None);

            Assert.IsFalse(scope.IsRestricted);
            Assert.AreSame(ReportUserScope.Everyone, scope.Sql);
        }

        [TestMethod]
        public async Task Bypass_IsHonouredForAnAdministratorOnly()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));

            var reader = await resolver.ResolveAsync(Request(bypass: true), PiiReader("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsTrue(reader.IsRestricted, "A reader cannot switch the filter off by setting the cookie themselves.");

            var admin = await resolver.ResolveAsync(Request(bypass: true), Admin("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsFalse(admin.IsRestricted);
            Assert.IsTrue(admin.Global.Bypassed);
            Assert.IsNull(admin.GlobalEcho);

            var adminWithoutCookie = await resolver.ResolveAsync(Request(), Admin("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsTrue(adminWithoutCookie.IsRestricted, "The filter applies to administrators too until they switch it off.");
        }

        [TestMethod]
        public async Task Resolve_FailsClosed_WhenTheFilterCannotBeRead()
        {
            var resolver = new ReportScopeResolver(new BrokenProvider(), new Directory());

            var refusal = await RefusalOf(() => resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refusal.Item1);
            Assert.AreEqual("globalFilterUnavailable", refusal.Item2);
        }

        [TestMethod]
        public async Task Resolve_FailsClosed_WhenTheStoredFilterCannotBeParsed_UnlessAnAdministratorSwitchedItOff()
        {
            var resolver = Resolver(new MemoryStore("[{\"d\":\"department\",\"v\":[],\"vu\":\"salary\"}]"));

            var refusal = await RefusalOf(() => resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refusal.Item1);
            Assert.AreEqual("globalFilterInvalid", refusal.Item2);

            var admin = await resolver.ResolveAsync(Request(bypass: true), Admin("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsFalse(admin.IsRestricted);
        }

        [TestMethod]
        public async Task Resolve_FailsClosed_WhenTheDirectoryCannotBeRead()
        {
            var resolver = new ReportScopeResolver(Provider(new MemoryStore(MyDepartment)), new Directory(failure: new TimeoutException("Execution Timeout Expired")));

            var refusal = await RefusalOf(() => resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refusal.Item1);
            Assert.AreEqual("filterDirectoryUnavailable", refusal.Item2);
        }

        #endregion

        #region Readers without See PII (#680)

        [TestMethod]
        public async Task ReaderWithoutSeePii_IsRefusedAScopeOfAHandfulOfPeople_ButNotOneOfNobody()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));

            // Rep's department holds three people: under the floor of five.
            var refused = await StatusOf(() => resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.Item1);
            StringAssert.Contains(refused.Item2, PortalPermissionDeniedModel.ErrorCode);
            StringAssert.Contains(refused.Item2, "seePii", "Refused with the portal's own See PII refusal, so the page says which permission would show it.");

            // The same scope for a reader who may see individuals.
            var allowed = await resolver.ResolveAsync(Request(), PiiReader("rep@contoso.com"), null, CancellationToken.None);
            Assert.AreEqual(3, allowed.Sql.Count);

            // Nobody at all is an empty report, which shows nobody's activity.
            var nobody = await resolver.ResolveAsync(Request(), Reader("stranger@contoso.com"), null, CancellationToken.None);
            Assert.AreEqual(0, nobody.Sql.Count);

            // An administrator who switched the filter off has no scope to be too small.
            var adminWithoutPii = Principal("rep@contoso.com", PortalRoles.Administration);
            var bypassed = await resolver.ResolveAsync(Request(bypass: true), adminWithoutPii, null, CancellationToken.None);
            Assert.IsFalse(bypassed.IsRestricted);
        }

        [TestMethod]
        public async Task ReaderWithoutSeePii_IsRefusedEvenOnlyTheirOwnFigures()
        {
            // "Only my own figures" leaves one person. The user import moves a row to whoever holds its sign-in
            // name now, so a reused address carries its former holder's activity - even into a row the
            // reader's own object id finds. One person's record is See PII's to show, whoever that may be.
            var resolver = Resolver(new MemoryStore("[{\"d\":\"userName\",\"v\":[],\"vu\":\"userName\"}]"));
            var engineer = PrincipalWithObjectId("engineer@contoso.com", "00000000-0000-0000-0000-000000000005");

            var refusal = await RefusalOf(() => resolver.ResolveAsync(Request(), engineer, null, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.Forbidden, refusal.Item1);

            var effective = Body<GlobalFilterEffectiveModel>(await Controller(resolver, engineer).Effective(CancellationToken.None));
            Assert.IsTrue(effective.TooFewPeople, "The bar says why.");

            var withPii = await resolver.ResolveAsync(
                Request(), PrincipalWithObjectId("engineer@contoso.com", "00000000-0000-0000-0000-000000000005", PortalRoles.SeePii), null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 5 }, withPii.Sql.UserIds.ToArray());
        }

        [TestMethod]
        public async Task ReaderWithoutSeePii_IsRefusedTheirOwnFiguresAlongsideAnotherPersons()
        {
            var resolver = Resolver(new MemoryStore(
                "[{\"d\":\"userName\",\"v\":[\"ceo@contoso.com\"],\"vu\":\"userName\"}]"));
            var engineer = PrincipalWithObjectId("engineer@contoso.com", "00000000-0000-0000-0000-000000000005");

            var refusal = await RefusalOf(() => resolver.ResolveAsync(Request(), engineer, null, CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.Forbidden, refusal.Item1, "Two people, one of them someone else, is a handful.");
        }

        [TestMethod]
        public async Task ReaderWithoutSeePii_IsNotRefusedAScopeOfFiveOrMore()
        {
            const string fiveNamed =
                "[{\"d\":\"userName\",\"v\":[\"ceo@contoso.com\",\"director@contoso.com\",\"rep@contoso.com\",\"peer@contoso.com\",\"engineer@contoso.com\"]}]";
            var resolver = Resolver(new MemoryStore(fiveNamed));

            var scope = await resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None);

            Assert.AreEqual(5, scope.Sql.Count);
        }

        [TestMethod]
        public async Task Effective_WithholdsSignInNamesFromAReaderWithoutSeePii()
        {
            const string people =
                "[{\"d\":\"userName\",\"v\":[\"ceo@contoso.com\",\"director@contoso.com\",\"rep@contoso.com\",\"peer@contoso.com\",\"engineer@contoso.com\"]},"
                + "{\"j\":\"or\",\"d\":\"manager\",\"v\":[],\"vu\":\"manager\"},"
                + "{\"j\":\"or\",\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]";
            var resolver = Resolver(new MemoryStore(people));

            var reader = Body<GlobalFilterEffectiveModel>(await Controller(resolver, Reader("rep@contoso.com")).Effective(CancellationToken.None));
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(reader);
            Assert.IsFalse(json.Contains("@contoso.com"), "No sign-in name may reach a reader without See PII: " + json);

            var named = reader.Filter.Clauses[0];
            Assert.AreEqual(0, named.Values.Count);
            Assert.AreEqual(5, named.HiddenValues);

            var manager = reader.Filter.Clauses[1];
            Assert.IsNull(manager.ViewerValue);
            Assert.IsTrue(manager.ViewerValueHidden);
            Assert.IsFalse(manager.Unresolved, "Withheld is not the same as missing: the condition still matches people.");

            Assert.AreEqual("Sales", reader.Filter.Clauses[2].ViewerValue, "A department is not a person, so it is shown.");

            // The editor, and a reader with See PII, see the names.
            var withPii = Body<GlobalFilterEffectiveModel>(await Controller(resolver, PiiReader("rep@contoso.com")).Effective(CancellationToken.None));
            Assert.AreEqual(5, withPii.Filter.Clauses[0].Values.Count);
            Assert.AreEqual("director@contoso.com", withPii.Filter.Clauses[1].ViewerValue);
        }

        [TestMethod]
        public async Task Effective_SaysWhenTheFilterLeavesAReaderWithoutSeePiiTooFewPeople()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));

            var reader = Body<GlobalFilterEffectiveModel>(await Controller(resolver, Reader("rep@contoso.com")).Effective(CancellationToken.None));
            Assert.IsTrue(reader.TooFewPeople);
            Assert.AreEqual(5, reader.MinimumPeople);

            var withPii = Body<GlobalFilterEffectiveModel>(await Controller(resolver, PiiReader("rep@contoso.com")).Effective(CancellationToken.None));
            Assert.IsFalse(withPii.TooFewPeople);
        }

        [TestMethod]
        public async Task Resolve_CompilesTheFilterOncePerDirectoryRead_ForReadersItTreatsAlike()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));

            var rep = await resolver.ResolveAsync(Request(), PiiReader("rep@contoso.com"), null, CancellationToken.None);
            var peer = await resolver.ResolveAsync(Request(), PiiReader("peer@contoso.com"), null, CancellationToken.None);
            var engineer = await resolver.ResolveAsync(Request(), PiiReader("engineer@contoso.com"), null, CancellationToken.None);

            Assert.AreSame(rep.Restriction, peer.Restriction, "Two people in the same department resolve the filter alike.");
            Assert.AreNotSame(rep.Restriction, engineer.Restriction);
        }

        #endregion

        #region The filter bar

        [TestMethod]
        public async Task Effective_WithNoFilter_SaysSo()
        {
            var controller = Controller(new ReportScopeResolver(GlobalFilterProviders.None, new Directory()), Admin("rep@contoso.com"));

            var model = Body<GlobalFilterEffectiveModel>(await controller.Effective(CancellationToken.None));

            Assert.IsFalse(model.Active);
            Assert.IsFalse(model.Applied);
            Assert.IsTrue(model.CanBypass);
            Assert.IsNull(model.Filter);
        }

        [TestMethod]
        public async Task Effective_ShowsAReaderTheConditionsWithTheirOwnValues()
        {
            var controller = Controller(Resolver(new MemoryStore(MyDepartment)), Reader("rep@contoso.com"));

            var model = Body<GlobalFilterEffectiveModel>(await controller.Effective(CancellationToken.None));

            Assert.IsTrue(model.Active);
            Assert.IsTrue(model.Applied);
            Assert.IsFalse(model.CanBypass);
            Assert.AreEqual(3, model.Filter.MatchedPeople);
            Assert.AreEqual("Sales", model.Filter.Clauses[0].ViewerValue);
        }

        [TestMethod]
        public async Task Effective_StillShowsAnAdministratorWhatEveryoneElseSees_WhenTheySwitchedItOff()
        {
            var controller = Controller(Resolver(new MemoryStore(MyDepartment)), Admin("rep@contoso.com"), bypass: true);

            var model = Body<GlobalFilterEffectiveModel>(await controller.Effective(CancellationToken.None));

            Assert.IsTrue(model.Active);
            Assert.IsTrue(model.Bypassed);
            Assert.IsFalse(model.Applied);
            Assert.IsNotNull(model.Filter);
        }

        #endregion

        #region Editing it

        [TestMethod]
        public async Task Save_StoresTheCanonicalForm_AndAppliesItAtOnce()
        {
            var store = new MemoryStore(null);
            var resolver = Resolver(store);
            var controller = Controller(resolver, Admin("rep@contoso.com"), method: HttpMethod.Post);

            // Prime this process's cache with "no filter", as a running site would have.
            var before = await resolver.ResolveAsync(Request(), Reader("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsFalse(before.IsRestricted);

            var saved = Body<GlobalFilterAdminModel>(await controller.Save(
                new GlobalFilterSaveRequest { Filter = "[{\"d\":\"department\",\"vu\":\"department\"}]", Revision = 0 }, CancellationToken.None));

            Assert.AreEqual(1, saved.Revision);
            Assert.AreEqual(MyDepartment, saved.Filter);
            Assert.AreEqual(MyDepartment, store.Record.FilterJson);
            Assert.AreEqual("rep@contoso.com", store.Record.ModifiedBy);

            var after = await resolver.ResolveAsync(Request(), PiiReader("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsTrue(after.IsRestricted, "The process that saved the filter applies it from the next request.");
        }

        [TestMethod]
        public async Task Save_RefusesAChangeMadeOverSomeoneElses()
        {
            var store = new MemoryStore(MyDepartment);
            var controller = Controller(Resolver(store), Admin("rep@contoso.com"), method: HttpMethod.Post);

            var response = await Execute(await controller.Save(new GlobalFilterSaveRequest { Filter = "", Revision = 0 }, CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("revisionConflict", ((ApiErrorModel)response.Value).Code);
            Assert.AreEqual(MyDepartment, store.Record.FilterJson, "The filter someone else saved is untouched.");
        }

        [TestMethod]
        public async Task Save_RefusesAFilterTheServerCannotRead()
        {
            var store = new MemoryStore(null);
            var controller = Controller(Resolver(store), Admin("rep@contoso.com"), method: HttpMethod.Post);

            var response = await Execute(await controller.Save(
                new GlobalFilterSaveRequest { Filter = "[{\"d\":\"department\",\"op\":\"contains\",\"v\":[\"x\"],\"vu\":\"department\"}]" },
                CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("invalidFilter", ((ApiErrorModel)response.Value).Code);
            Assert.AreEqual(0, store.Saves);
        }

        [TestMethod]
        public async Task Save_SaysWhenTheDatabaseHasNotBeenUpgraded()
        {
            var store = new MemoryStore(null) { Missing = true };
            var controller = Controller(Resolver(store), Admin("rep@contoso.com"), method: HttpMethod.Post);

            var response = await Execute(await controller.Save(new GlobalFilterSaveRequest { Filter = MyDepartment }, CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual("storageUnavailable", ((ApiErrorModel)response.Value).Code);
        }

        [TestMethod]
        public async Task Get_GivesTheEditorTheDefinitionWithoutAnyonesValues()
        {
            var controller = Controller(Resolver(new MemoryStore(MyDepartment, revision: 7)), Admin("rep@contoso.com"));

            var model = Body<GlobalFilterAdminModel>(await controller.Get(CancellationToken.None));

            Assert.AreEqual(MyDepartment, model.Filter);
            Assert.AreEqual(7, model.Revision);
            Assert.IsTrue(model.StorageAvailable);
            Assert.IsFalse(model.Invalid);
            Assert.AreEqual("department", model.Clauses[0].ViewerAttribute);
            Assert.IsNull(model.Clauses[0].ViewerValue);
        }

        [TestMethod]
        public async Task Preview_EvaluatesADraftForTheAdministrator_AndSavesNothing()
        {
            var store = new MemoryStore(null);
            var controller = Controller(Resolver(store), Admin("engineer@contoso.com"), method: HttpMethod.Post);

            var model = Body<GlobalFilterEffectiveModel>(await controller.Preview(new GlobalFilterPreviewRequest { Filter = MyDepartment }, CancellationToken.None));

            Assert.AreEqual(1, model.Filter.MatchedPeople);
            Assert.AreEqual("Engineering", model.Filter.Clauses[0].ViewerValue);
            Assert.AreEqual(0, store.Saves);
        }

        #endregion

        #region The picker

        [TestMethod]
        public async Task Picker_OffersAReaderOnlyTheValuesOfPeopleWithinTheFilter()
        {
            var directory = new Directory();
            var controller = Picker(directory, PiiReader("rep@contoso.com"));

            var names = OkValue<UserFilterValuePage>(await controller.Values("userName", null, 200, CancellationToken.None));
            Assert.IsNotNull(names);
            CollectionAssert.AreEquivalent(
                new[] { "director@contoso.com", "rep@contoso.com", "peer@contoso.com" },
                names.Values.Select(v => v.Value).ToArray(),
                "A sign-in name is a person: the picker must not name anyone the reader's reports leave out.");

            var departments = OkValue<UserFilterValuePage>(await controller.Values("department", null, 200, CancellationToken.None));
            CollectionAssert.AreEqual(new[] { "Sales" }, departments.Values.Select(v => v.Value).ToArray());

            var dimensions = OkValue<UserFilterDimensionList>(await controller.Dimensions(CancellationToken.None));
            Assert.AreEqual(3, dimensions.People);
        }

        [TestMethod]
        public async Task Picker_OffersAnAdministratorEveryValue_SoTheEditorCanChooseThem()
        {
            var controller = Picker(new Directory(), Admin("rep@contoso.com"));

            var names = OkValue<UserFilterValuePage>(await controller.Values("userName", null, 200, CancellationToken.None));

            Assert.AreEqual(5, names.Values.Count);
        }

        private static UserFilterAPIController Picker(Directory directory, ClaimsPrincipal user)
        {
            return new UserFilterAPIController(directory, new ReportScopeResolver(Provider(new MemoryStore(MyDepartment)), directory))
            {
                ControllerContext = ContextFor(Request(), user),
            };
        }

        #endregion

        #region net10: refusals reach the reader through ASP.NET Core

        /// <summary>
        /// Web API 2 sent an <c>HttpResponseException</c>'s response from any controller. ASP.NET Core has no such
        /// thing, so a refusal is an <see cref="ApiReplyException"/> and each controller needs the filter that sends
        /// it - without it a reader would get the generic error page instead of the refusal the portal explains.
        /// </summary>
        [TestMethod]
        public void EveryControllerThatResolvesAReportScope_SendsItsRefusals()
        {
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var resolving = PortalTestHost.WebControllers()
                .Where(c => c.GetFields(members).Any(f => f.FieldType == typeof(ReportScopeResolver))
                            || c.GetProperties(members).Any(p => p.PropertyType == typeof(ReportScopeResolver)))
                .ToList();

            CollectionAssert.IsSubsetOf(
                new[]
                {
                    "AgentCostsAPIController", "CopilotAdoptionAPIController", "DlpAPIController", "GlobalFilterAPIController",
                    "LicenceActivityAPIController", "ReportsAPIController", "TeamsExplorerAPIController",
                    "UserFilterAPIController", "WebActivityAPIController",
                },
                resolving.Select(c => c.Name).ToList(),
                "The controllers known to apply the global filter must be found, or this test proves nothing.");

            var missing = resolving
                .Where(c => c.GetCustomAttribute<ApiReplyExceptionFilterAttribute>(inherit: true) == null)
                .Select(c => c.Name)
                .ToList();
            Assert.AreEqual(0, missing.Count, "Missing [ApiReplyExceptionFilter]: " + string.Join(", ", missing));
        }

        [TestMethod]
        public async Task ARefusal_IsSentAsJsonToAScript_AndAsTextToAPageLoad_AndNeverCached()
        {
            var resolver = new ReportScopeResolver(new BrokenProvider(), new Directory());

            using (var host = new PortalTestHost(
                new[] { typeof(GlobalFilterAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing,
                _ => new GlobalFilterAPIController(resolver)))
            {
                var script = await host.Client.GetAsync("api/GlobalFilter/effective");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, script.StatusCode);
                Assert.AreEqual("globalFilterUnavailable", (string)JObject.Parse(await script.Content.ReadAsStringAsync())["code"]);
                Assert.IsTrue(script.Headers.CacheControl?.NoStore == true, "A refusal depends on the reader and must not be cached.");

                var navigation = new HttpRequestMessage(HttpMethod.Get, "api/GlobalFilter/effective");
                navigation.Headers.Add("Sec-Fetch-Mode", "navigate");
                var page = await host.Client.SendAsync(navigation);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, page.StatusCode);
                Assert.AreEqual("text/plain", page.Content.Headers.ContentType?.MediaType);
                StringAssert.StartsWith(await page.Content.ReadAsStringAsync(), "The administrator's report filter could not be read");
            }
        }

        /// <summary>
        /// The bypass cookie arrives among the browser's other cookies, and possibly twice when it was set at two
        /// paths. net10 reads the Cookie header itself, as Web API 2's <c>GetCookies</c> did, rather than
        /// <see cref="HttpRequest.Cookies"/>, which keeps only one value per name.
        /// </summary>
        [TestMethod]
        public async Task Bypass_IsFoundAmongTheBrowsersOtherCookies()
        {
            var resolver = Resolver(new MemoryStore(MyDepartment));

            foreach (var header in new[] { "theme=dark; GlobalFilterBypass=1", "GlobalFilterBypass=0; GlobalFilterBypass=1" })
            {
                var request = Request();
                request.Headers.Cookie = header;

                var admin = await resolver.ResolveAsync(request, Admin("rep@contoso.com"), null, CancellationToken.None);

                Assert.IsTrue(admin.Global.Bypassed, header);
                Assert.IsFalse(admin.IsRestricted, header);
            }

            var notSwitchedOff = Request();
            notSwitchedOff.Headers.Cookie = "theme=dark; GlobalFilterBypass=0";
            var stillFiltered = await resolver.ResolveAsync(notSwitchedOff, Admin("rep@contoso.com"), null, CancellationToken.None);
            Assert.IsTrue(stillFiltered.IsRestricted, "Only the value 1 switches the filter off.");
        }

        [TestMethod]
        public async Task ReaderWithoutSeePii_IsSentThePortalsOwnSeePiiRefusal_ByAReport()
        {
            // Three people, explicitly - nothing taken from the reader - so under the floor of five whoever reads it.
            var resolver = new ReportScopeResolver(Provider(new MemoryStore("[{\"d\":\"department\",\"v\":[\"Sales\"]}]")), new Directory());

            using (var host = new PortalTestHost(
                new[] { typeof(ReportsAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing,
                _ => new ReportsAPIController(resolver)))
            {
                var response = await host.Client.GetAsync("api/Reports/copilot");

                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(PortalPermissionDeniedModel.ErrorCode, (string)body["code"]);
                Assert.AreEqual("seePii", (string)body["permission"]);
            }
        }

        #endregion

        #region Fixtures

        private static ReportScopeResolver Resolver(MemoryStore store)
        {
            return new ReportScopeResolver(Provider(store), new Directory());
        }

        private static IGlobalFilterProvider Provider(MemoryStore store)
        {
            return new CachedGlobalFilterProvider(store, TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// A request to the portal's API, as ASP.NET Core hands it to a controller.
        /// </summary>
        /// <remarks>
        /// net10: the role policy is pinned where <c>PortalAccess</c> reads it on this host - the request's
        /// <see cref="HttpContext.Items"/> - rather than in Web API 2's <c>HttpConfiguration</c> properties.
        /// </remarks>
        private static HttpRequest Request(bool bypass = false, HttpMethod method = null)
        {
            var context = new DefaultHttpContext();
            context.Items[typeof(PortalAccessPolicy)] = PortalAccessPolicy.Enforcing;

            var request = context.Request;
            request.Method = (method ?? HttpMethod.Get).Method;
            request.Scheme = "https";
            request.Host = new HostString("contoso.example");
            request.Path = "/api/GlobalFilter";
            request.Headers["X-Requested-With"] = "XMLHttpRequest";
            if (bypass) request.Headers.Cookie = "GlobalFilterBypass=1";
            return request;
        }

        private static GlobalFilterAPIController Controller(ReportScopeResolver resolver, ClaimsPrincipal user, bool bypass = false, HttpMethod method = null)
        {
            return new GlobalFilterAPIController(resolver)
            {
                ControllerContext = ContextFor(Request(bypass, method), user),
            };
        }

        /// <summary>The controller context for <paramref name="request"/>, signed in as <paramref name="user"/>.</summary>
        private static ControllerContext ContextFor(HttpRequest request, ClaimsPrincipal user)
        {
            request.HttpContext.User = user;
            return new ControllerContext { HttpContext = request.HttpContext };
        }

        private static ClaimsPrincipal Reader(string upn) => Principal(upn);

        /// <summary>A reader who may add filters of their own - which, since #680, needs See PII.</summary>
        private static ClaimsPrincipal PiiReader(string upn) => Principal(upn, PortalRoles.SeePii);

        /// <summary>An administrator who may also edit the filter, which needs See PII as well (#680).</summary>
        private static ClaimsPrincipal Admin(string upn) => Principal(upn, PortalRoles.Administration, PortalRoles.SeePii);

        private static ClaimsPrincipal Principal(string upn, params string[] roles)
        {
            var identity = new ClaimsIdentity("Test");
            identity.AddClaim(new Claim("upn", upn));
            foreach (var role in roles) identity.AddClaim(new Claim("roles", role));
            return new ClaimsPrincipal(identity);
        }

        private static ClaimsPrincipal PrincipalWithObjectId(string upn, string objectId, params string[] roles)
        {
            var principal = Principal(upn, roles);
            ((ClaimsIdentity)principal.Identity).AddClaim(new Claim("oid", objectId));
            return principal;
        }

        private static Task<Reply> Execute(IActionResult result)
        {
            return Task.FromResult(Reply.Of(result));
        }

        private static T Body<T>(IActionResult result) where T : class
        {
            var response = Reply.Of(result);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Expected 200 but got " + response.StatusCode);
            var body = response.Value as T;
            Assert.IsNotNull(body, "The response body was not a " + typeof(T).Name);
            return body;
        }

        private static T OkValue<T>(IActionResult result) where T : class
        {
            return (result as OkObjectResult)?.Value as T;
        }

        private static async Task<Tuple<HttpStatusCode, string>> RefusalOf(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (ApiReplyException ex)
            {
                var reply = Reply.Of(ex.Result);
                return Tuple.Create(reply.StatusCode, (reply.Value as ApiErrorModel)?.Code);
            }

            throw new AssertFailedException("The request was answered rather than refused.");
        }

        /// <summary>The status and raw body of a refusal, whatever shape its body takes.</summary>
        private static async Task<Tuple<HttpStatusCode, string>> StatusOf(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (ApiReplyException ex)
            {
                var reply = Reply.Of(ex.Result);
                var body = reply.Value == null ? string.Empty
                    : reply.Value as string ?? Newtonsoft.Json.JsonConvert.SerializeObject(reply.Value);
                return Tuple.Create(reply.StatusCode, body);
            }

            throw new AssertFailedException("The request was answered rather than refused.");
        }

        /// <summary>
        /// net10: an action's result as the status and body it would send. ASP.NET Core results are inspected
        /// rather than executed; <c>Ok(x)</c> is an <see cref="OkObjectResult"/>, and Web API 2's
        /// <c>Content(status, x)</c> is ported as <c>StatusCode(status, x)</c>, an <see cref="ObjectResult"/>.
        /// </summary>
        private sealed class Reply
        {
            public HttpStatusCode StatusCode { get; private set; }

            public object Value { get; private set; }

            public static Reply Of(IActionResult result)
            {
                switch (result)
                {
                    case ObjectResult value: return new Reply { StatusCode = (HttpStatusCode)(value.StatusCode ?? 200), Value = value.Value };
                    case JsonResult json: return new Reply { StatusCode = (HttpStatusCode)(json.StatusCode ?? 200), Value = json.Value };
                    case ContentResult content: return new Reply { StatusCode = (HttpStatusCode)(content.StatusCode ?? 200), Value = content.Content };
                    case StatusCodeResult status: return new Reply { StatusCode = (HttpStatusCode)status.StatusCode };
                    default: throw new AssertFailedException("Unexpected result " + result?.GetType().Name);
                }
            }
        }

        private static UserDirectorySnapshot Snapshot()
        {
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "ceo@contoso.com" });
            builder.AddUser(new UserDirectoryEntry { UserId = 2, UserPrincipalName = "director@contoso.com", Department = "Sales", ManagerUserId = 1 });
            builder.AddUser(new UserDirectoryEntry { UserId = 3, UserPrincipalName = "rep@contoso.com", Department = "Sales", ManagerUserId = 2 });
            builder.AddUser(new UserDirectoryEntry { UserId = 4, UserPrincipalName = "peer@contoso.com", Department = "Sales", ManagerUserId = 2 });
            builder.AddUser(new UserDirectoryEntry
            {
                UserId = 5,
                UserPrincipalName = "engineer@contoso.com",
                Department = "Engineering",
                ManagerUserId = 1,
                EntraObjectId = "00000000-0000-0000-0000-000000000005",
            });
            return builder.Build(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
        }

        private sealed class Directory : IUserDirectorySource
        {
            private readonly UserDirectorySnapshot _snapshot = Snapshot();
            private readonly Exception _failure;

            public Directory(Exception failure = null)
            {
                _failure = failure;
            }

            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken)
            {
                if (_failure != null) throw _failure;
                return Task.FromResult(_snapshot);
            }

            public void Prefetch()
            {
            }

            public void Invalidate()
            {
            }
        }

        /// <summary>The store's contract - a revision check on every save - kept in memory.</summary>
        private sealed class MemoryStore : IGlobalFilterStore
        {
            public MemoryStore(string filterJson, int? revision = null)
            {
                Record = new GlobalFilterRecord
                {
                    StorageAvailable = true,
                    FilterJson = filterJson ?? string.Empty,
                    Revision = revision ?? (filterJson == null ? 0 : 1),
                };
            }

            public GlobalFilterRecord Record { get; private set; }

            public bool Missing { get; set; }

            public int Saves { get; private set; }

            public Task<GlobalFilterRecord> GetAsync(CancellationToken cancellationToken)
            {
                if (Missing) return Task.FromResult(new GlobalFilterRecord { StorageAvailable = false });
                return Task.FromResult(Record);
            }

            public Task<GlobalFilterRecord> SaveAsync(string filterJson, int expectedRevision, string modifiedBy, CancellationToken cancellationToken)
            {
                if (Missing) throw new GlobalFilterStorageMissingException();
                if (expectedRevision != Record.Revision) return Task.FromResult<GlobalFilterRecord>(null);

                Saves++;
                Record = new GlobalFilterRecord
                {
                    StorageAvailable = true,
                    FilterJson = filterJson ?? string.Empty,
                    Revision = Record.Revision + 1,
                    ModifiedUtc = DateTime.UtcNow,
                    ModifiedBy = modifiedBy,
                };
                return Task.FromResult(Record);
            }
        }

        private sealed class BrokenProvider : IGlobalFilterProvider
        {
            public Common.Entities.UserFilters.IGlobalFilterStore Store => throw new NotSupportedException();

            public Task<GlobalFilterState> GetAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("A network-related or instance-specific error occurred.");
            }

            public void Invalidate()
            {
            }
        }

        #endregion
    }
}
