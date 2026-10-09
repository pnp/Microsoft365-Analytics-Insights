extern alias AnalyticsWeb;

using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Results;
using ApiErrorModel = AnalyticsWeb::Web.AnalyticsWeb.Models.ApiErrorModel;
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
using UserFilterAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.UserFilterAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The administrator's global filter on the web side: applied to every report request whatever the page
    /// sends, failing closed when it cannot be evaluated, switched off only with Administration and See PII,
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
        public async Task Bypass_IsHonouredForAnAdministratorWithSeePiiOnly()
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

            var ownFilter = UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Engineering\"]}]");
            var adminWithOwnFilter = await resolver.ResolveAsync(
                Request(bypass: true), Admin("rep@contoso.com"), ownFilter, CancellationToken.None);
            Assert.IsTrue(adminWithOwnFilter.Global.Bypassed);
            CollectionAssert.AreEqual(new[] { 5 }, adminWithOwnFilter.Sql.UserIds.ToArray(),
                "Bypassing the global filter does not remove the reader's own filter.");
        }

        [DataTestMethod]
        [DataRow(false, false, true)]
        [DataRow(true, false, true)]
        [DataRow(false, true, true)]
        [DataRow(true, true, true)]
        [DataRow(false, false, false)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(true, true, false)]
        public async Task Bypass_RequiresBothPermissions_InReportsAndCapabilityReporting(
            bool administration, bool seePii, bool enforced)
        {
            var roles = new[] { administration ? PortalRoles.Administration : null, seePii ? PortalRoles.SeePii : null }
                .Where(role => role != null).ToArray();
            var principal = Principal("person1@contoso.com", roles);
            var builder = new UserDirectorySnapshotBuilder();
            for (var id = 1; id <= 6; id++)
            {
                builder.AddUser(new UserDirectoryEntry
                {
                    UserId = id,
                    UserPrincipalName = "person" + id + "@contoso.com",
                    Department = id == 6 ? "Contoso Legal" : "Contoso Sales",
                });
            }
            var directory = new Directory(snapshot: builder.Build(DateTime.UtcNow));
            var resolver = new ReportScopeResolver(
                Provider(new MemoryStore("[{\"d\":\"department\",\"v\":[\"Contoso Sales\"]}]")), directory);
            var canBypass = !enforced || (administration && seePii);

            // Five included, one excluded: the existing small-scope floor permits the filtered report.
            // An Administration-only caller must not recover the excluded person's figures by differencing.
            var filtered = await resolver.ResolveAsync(Request(enforced: enforced), principal, null, CancellationToken.None);
            Assert.AreEqual(5, filtered.Sql.Count);
            var scope = await resolver.ResolveAsync(Request(bypass: true, enforced: enforced), principal, null, CancellationToken.None);
            Assert.AreEqual(canBypass, scope.Global.CanBypass);
            Assert.AreEqual(canBypass, scope.Global.Bypassed);
            Assert.AreEqual(!canBypass, scope.IsRestricted);
            Assert.AreEqual(canBypass, scope.Includes(6));
            Assert.AreEqual(canBypass ? "all" : filtered.Key, scope.Key, "Ignored bypasses share only the filtered cache key.");

            var described = await resolver.DescribeAsync(Request(bypass: true, enforced: enforced), principal, CancellationToken.None);
            Assert.AreEqual(canBypass, described.CanBypass);
            Assert.AreEqual(canBypass, described.Bypassed);
            Assert.AreEqual(!canBypass, described.Applied);

            var effective = Body<GlobalFilterEffectiveModel>(
                await Controller(resolver, principal, bypass: true, enforced: enforced).Effective(CancellationToken.None));
            Assert.AreEqual(canBypass, effective.CanBypass);
            Assert.AreEqual(canBypass, effective.Bypassed);
            Assert.AreEqual(!canBypass, effective.Applied);
            Assert.IsFalse(effective.TooFewPeople, "The existing five-person floor has not changed.");

            var noFilter = new ReportScopeResolver(GlobalFilterProviders.None, directory);
            var inactive = Body<GlobalFilterEffectiveModel>(
                await Controller(noFilter, principal, bypass: true, enforced: enforced).Effective(CancellationToken.None));
            Assert.IsFalse(inactive.Active);
            Assert.IsFalse(inactive.Bypassed);
            Assert.AreEqual(canBypass, inactive.CanBypass, "Capability reporting must also agree with no filter defined.");
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task Bypass_WithoutBothPermissions_CannotSkipAnInvalidFilterOrUnavailableDirectory(
            bool administration, bool seePii)
        {
            var roles = new[] { administration ? PortalRoles.Administration : null, seePii ? PortalRoles.SeePii : null }
                .Where(role => role != null).ToArray();
            var principal = Principal("rep@contoso.com", roles);
            var invalid = Resolver(new MemoryStore("[{\"d\":\"department\",\"v\":[],\"vu\":\"salary\"}]"));
            var invalidRefusal = await RefusalOf(() =>
                invalid.ResolveAsync(Request(bypass: true), principal, null, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, invalidRefusal.Item1);
            Assert.AreEqual("globalFilterInvalid", invalidRefusal.Item2);
            var effective = Body<GlobalFilterEffectiveModel>(
                await Controller(invalid, principal, bypass: true).Effective(CancellationToken.None));
            Assert.IsTrue(effective.Invalid);
            Assert.IsTrue(effective.Applied);
            Assert.IsFalse(effective.CanBypass);
            Assert.IsFalse(effective.Bypassed);

            var unavailable = new ReportScopeResolver(Provider(new MemoryStore(MyDepartment)),
                new Directory(failure: new TimeoutException("synthetic directory failure")));
            var unavailableRefusal = await RefusalOf(() =>
                unavailable.ResolveAsync(Request(bypass: true), principal, null, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailableRefusal.Item1);
            Assert.AreEqual("filterDirectoryUnavailable", unavailableRefusal.Item2);
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

            // Administration alone cannot switch off the filter or evade the existing floor.
            var adminWithoutPii = Principal("rep@contoso.com", PortalRoles.Administration);
            var adminRefused = await StatusOf(() => resolver.ResolveAsync(Request(bypass: true), adminWithoutPii, null, CancellationToken.None));
            Assert.AreEqual(HttpStatusCode.Forbidden, adminRefused.Item1);
            StringAssert.Contains(adminRefused.Item2, "seePii");
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
            Assert.AreEqual("revisionConflict", ((ApiErrorModel)((ObjectContent)response.Content).Value).Code);
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
            Assert.AreEqual("invalidFilter", ((ApiErrorModel)((ObjectContent)response.Content).Value).Code);
            Assert.AreEqual(0, store.Saves);
        }

        [TestMethod]
        public async Task Save_SaysWhenTheDatabaseHasNotBeenUpgraded()
        {
            var store = new MemoryStore(null) { Missing = true };
            var controller = Controller(Resolver(store), Admin("rep@contoso.com"), method: HttpMethod.Post);

            var response = await Execute(await controller.Save(new GlobalFilterSaveRequest { Filter = MyDepartment }, CancellationToken.None));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual("storageUnavailable", ((ApiErrorModel)((ObjectContent)response.Content).Value).Code);
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

            var names = await controller.Values("userName", null, 200, CancellationToken.None) as OkNegotiatedContentResult<UserFilterValuePage>;
            Assert.IsNotNull(names);
            CollectionAssert.AreEquivalent(
                new[] { "director@contoso.com", "rep@contoso.com", "peer@contoso.com" },
                names.Content.Values.Select(v => v.Value).ToArray(),
                "A sign-in name is a person: the picker must not name anyone the reader's reports leave out.");

            var departments = await controller.Values("department", null, 200, CancellationToken.None) as OkNegotiatedContentResult<UserFilterValuePage>;
            CollectionAssert.AreEqual(new[] { "Sales" }, departments.Content.Values.Select(v => v.Value).ToArray());

            var dimensions = await controller.Dimensions(CancellationToken.None) as OkNegotiatedContentResult<UserFilterDimensionList>;
            Assert.AreEqual(3, dimensions.Content.People);
        }

        [TestMethod]
        public async Task Picker_OffersAnAdministratorEveryValue_SoTheEditorCanChooseThem()
        {
            var controller = Picker(new Directory(), Admin("rep@contoso.com"));

            var names = await controller.Values("userName", null, 200, CancellationToken.None) as OkNegotiatedContentResult<UserFilterValuePage>;

            Assert.AreEqual(5, names.Content.Values.Count);
        }

        private static UserFilterAPIController Picker(Directory directory, ClaimsPrincipal user)
        {
            var request = Request();
            return new UserFilterAPIController(directory, new ReportScopeResolver(Provider(new MemoryStore(MyDepartment)), directory))
            {
                Request = request,
                Configuration = request.GetConfiguration(),
                User = user,
            };
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

        private static HttpRequestMessage Request(bool bypass = false, HttpMethod method = null, bool enforced = true)
        {
            var configuration = new HttpConfiguration();
            configuration.Properties[typeof(PortalAccessPolicy)] = enforced ? PortalAccessPolicy.Enforcing : PortalAccessPolicy.NotEnforcing;

            var request = new HttpRequestMessage(method ?? HttpMethod.Get, "https://contoso.example/api/GlobalFilter");
            request.SetConfiguration(configuration);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (bypass) request.Headers.Add("Cookie", "GlobalFilterBypass=1");
            return request;
        }

        private static GlobalFilterAPIController Controller(ReportScopeResolver resolver, ClaimsPrincipal user, bool bypass = false, HttpMethod method = null, bool enforced = true)
        {
            var request = Request(bypass, method, enforced);
            return new GlobalFilterAPIController(resolver)
            {
                Request = request,
                Configuration = request.GetConfiguration(),
                User = user,
            };
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

        private static async Task<HttpResponseMessage> Execute(IHttpActionResult result)
        {
            return await result.ExecuteAsync(CancellationToken.None);
        }

        private static T Body<T>(IHttpActionResult result) where T : class
        {
            var response = result.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Expected 200 but got " + response.StatusCode);
            var body = ((ObjectContent)response.Content).Value as T;
            Assert.IsNotNull(body, "The response body was not a " + typeof(T).Name);
            return body;
        }

        private static async Task<Tuple<HttpStatusCode, string>> RefusalOf(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (HttpResponseException ex)
            {
                var body = (ex.Response.Content as ObjectContent)?.Value as ApiErrorModel;
                return Tuple.Create(ex.Response.StatusCode, body?.Code);
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
            catch (HttpResponseException ex)
            {
                var body = ex.Response.Content == null ? string.Empty : await ex.Response.Content.ReadAsStringAsync();
                return Tuple.Create(ex.Response.StatusCode, body);
            }

            throw new AssertFailedException("The request was answered rather than refused.");
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
            private readonly UserDirectorySnapshot _snapshot;
            private readonly Exception _failure;

            public Directory(Exception failure = null, UserDirectorySnapshot snapshot = null)
            {
                _failure = failure;
                _snapshot = snapshot ?? Snapshot();
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
